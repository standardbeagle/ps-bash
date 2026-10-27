BeforeAll {
    $script:installer = Join-Path $PSScriptRoot '..' 'install.ps1'
    $script:installerAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $script:installer, [ref]$null, [ref]$null)
    # Load the production promotion function without downloading or installing anything.
    $definition = $script:installerAst.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Install-PsBashWindowsPackage'
    }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}

Describe 'Installer safety' {
    It 'resolves Windows paths without HOME or INSTALL_ROOT' -Skip:(-not $IsWindows) {
        $savedHome = $env:HOME
        $savedRoot = $env:INSTALL_ROOT
        try {
            $env:HOME = $null
            $env:INSTALL_ROOT = $null
            $text = [IO.File]::ReadAllText($script:installer)
            $prefix = $text.Substring(0, $text.IndexOf('# --- Resolve version ---'))
            { & ([scriptblock]::Create($prefix)) -InstallDir (Join-Path $TestDrive 'package') } | Should -Not -Throw
        } finally {
            $env:HOME = $savedHome
            $env:INSTALL_ROOT = $savedRoot
        }
    }

    BeforeEach {
        $stage = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $target = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage | Out-Null
        Set-Content -LiteralPath (Join-Path $stage 'ps-bash.exe') -Value 'new'
    }

    It 'refuses a shared directory and preserves its existing backup' {
        New-Item -ItemType Directory -Path $target, "$target.previous" | Out-Null
        Set-Content -LiteralPath (Join-Path $target 'other-tool.exe') -Value 'valuable tool'
        Set-Content -LiteralPath (Join-Path "$target.previous" 'backup.txt') -Value 'valuable backup'

        { Install-PsBashWindowsPackage -Stage $stage -InstallDir $target } | Should -Throw '*unowned directory*'

        Get-Content -LiteralPath (Join-Path $target 'other-tool.exe') | Should -Be 'valuable tool'
        Get-Content -LiteralPath (Join-Path "$target.previous" 'backup.txt') | Should -Be 'valuable backup'
        Test-Path -LiteralPath $stage | Should -BeTrue
    }

    It 'installs fresh packages and retains the original on upgrade' {
        Install-PsBashWindowsPackage -Stage $stage -InstallDir ($target + [IO.Path]::DirectorySeparatorChar)
        [IO.File]::ReadAllText((Join-Path $target '.ps-bash-package')) | Should -Be 'ps-bash'
        New-Item -ItemType Directory -Path $stage | Out-Null
        Set-Content -LiteralPath (Join-Path $stage 'ps-bash.exe') -Value 'upgraded'

        Install-PsBashWindowsPackage -Stage $stage -InstallDir $target

        Get-Content -LiteralPath (Join-Path $target 'ps-bash.exe') | Should -Be 'upgraded'
        $backups = @(Get-ChildItem -Directory -LiteralPath $TestDrive | Where-Object Name -Like "$([IO.Path]::GetFileName($target)).previous-*")
        $backups.Count | Should -Be 1
        Get-Content -LiteralPath (Join-Path $backups[0].FullName 'ps-bash.exe') | Should -Be 'new'
    }

    It 'restores the original package when promotion fails' {
        New-Item -ItemType Directory -Path $target | Out-Null
        [IO.File]::WriteAllText((Join-Path $target '.ps-bash-package'), 'ps-bash')
        Set-Content -LiteralPath (Join-Path $target 'ps-bash.exe') -Value 'original'
        # Pester 6 throws for a filtered mock with no default: pass other moves (the backup) through.
        Mock Move-Item { [IO.Directory]::Move($LiteralPath, $Destination) }
        Mock Move-Item { throw 'promotion failed' } -ParameterFilter { $LiteralPath -eq $stage }

        { Install-PsBashWindowsPackage -Stage $stage -InstallDir $target } | Should -Throw '*promotion failed*'

        Get-Content -LiteralPath (Join-Path $target 'ps-bash.exe') | Should -Be 'original'
    }
}
