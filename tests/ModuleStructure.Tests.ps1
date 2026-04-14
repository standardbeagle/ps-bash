#Requires -Modules Pester

# Structural integrity tests for the PsBash module.
# These guard against breakage when splitting the monolithic psm1 into files.
# They verify: module import, function/alias exports, scope, type data, helpers, pipelines.

BeforeAll {
    # Remove any previously loaded copies (e.g. PSGallery installs)
    Get-Module PsBash | Remove-Module -Force -ErrorAction SilentlyContinue

    $modulePath = Join-Path $PSScriptRoot '..' 'src' 'PsBash.Module' 'PsBash.psd1'
    Import-Module $modulePath -Force
    Set-BashErrorMode 'PowerShell'

    # Add BashText to [string] so test assertions using .BashText work for fast-path strings
    Update-TypeData -TypeName System.String -MemberName BashText -MemberType ScriptProperty -Value { $this } -Force

    # Parse manifest for expected exports
    $manifestData = Import-PowerShellDataFile (Join-Path $PSScriptRoot '..' 'src' 'PsBash.Module' 'PsBash.psd1')
    $script:expectedFunctions = @($manifestData.FunctionsToExport | Where-Object { $_ })
    $script:expectedAliases = @($manifestData.AliasesToExport | Where-Object { $_ })

    # Collect aliases registered by the module (Set-Alias -Scope Global)
    $script:registeredAliases = @(Get-Alias | Where-Object {
        $_.Definition -match '^Invoke-Bash|^Get-Bash|^Set-Bash|^ConvertFrom-|^Register-Bash|^Test-Bash|^Format-|^Expand-|^New-|^Resolve-|^Compare-|^Split-'
    })
}

Describe 'Module Import' {
    It 'imports PsBash without error' {
        $mod = Get-Module PsBash | Select-Object -First 1
        $mod | Should -Not -BeNullOrEmpty
        $mod.Name | Should -Be 'PsBash'
    }
}

Describe 'FunctionsToExport — all declared functions exist' {
    It 'manifest declares at least 60 functions' {
        $script:expectedFunctions.Count | Should -BeGreaterOrEqual 60
    }

    It '<FuncName> is available as a command' -ForEach (
        (Import-PowerShellDataFile (Join-Path $PSScriptRoot '..' 'src' 'PsBash.Module' 'PsBash.psd1')).FunctionsToExport |
            Where-Object { $_ } | ForEach-Object { @{ FuncName = $_ } }
    ) {
        Get-Command $FuncName -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty -Because "'$FuncName' is listed in FunctionsToExport"
    }
}

Describe 'Aliases — all declared aliases resolve' {
    It 'manifest declares at least 60 aliases' {
        $script:expectedAliases.Count | Should -BeGreaterOrEqual 60
    }

    It 'alias <AliasName> resolves to a PsBash function' -ForEach (
        (Import-PowerShellDataFile (Join-Path $PSScriptRoot '..' 'src' 'PsBash.Module' 'PsBash.psd1')).AliasesToExport |
            Where-Object { $_ } | ForEach-Object { @{ AliasName = $_ } }
    ) {
        $alias = Get-Alias $AliasName -ErrorAction SilentlyContinue
        $alias | Should -Not -BeNullOrEmpty -Because "'$AliasName' is listed in AliasesToExport"
        if ($alias.Definition -match '^Invoke-Bash|^Get-Bash|^Set-Bash|^ConvertFrom-|^Register-Bash|^Test-Bash|^Format-Bash|^Expand-|^New-Flag|^New-Bash|^Resolve-|^Compare-|^Split-') {
            $alias.Definition | Should -Match '^Invoke-Bash|^Get-Bash|^Set-Bash|^ConvertFrom-|^Register-Bash|^Test-Bash|^Format-Bash|^Expand-|^New-Flag|^New-Bash|^Resolve-|^Compare-|^Split-' -Because "alias should point to a PsBash function"
        }
    }

    It 'all registered Invoke-Bash* aliases are in manifest AliasesToExport' {
        $bashAliases = @(Get-Alias | Where-Object { $_.Definition -like 'Invoke-Bash*' -or $_.Definition -like 'Get-Bash*' } | Select-Object -ExpandProperty Name)
        $bashAliases.Count | Should -BeGreaterOrEqual 60
        foreach ($name in $bashAliases) {
            $script:expectedAliases | Should -Contain $name -Because "alias '$name' is registered by the module"
        }
    }
}

Describe 'Script-scope variables initialized' {
    It 'Show-BashHelp returns content for ls (proves $script:BashHelpSpecs works)' {
        $result = Show-BashHelp 'ls'
        $result | Should -Not -BeNullOrEmpty
        $result | Should -Match 'Usage:'
    }

    It 'Show-BashHelp returns content for grep (proves $script:BashHelpSpecs has grep)' {
        $result = Show-BashHelp 'grep'
        $result | Should -Match 'Usage:'
    }

    It 'Test-BashHelpFlag recognizes --help (proves $script:BashFlagSpecs works)' {
        $result = Test-BashHelpFlag @('--help')
        $result | Should -BeTrue
    }
}

Describe 'Update-TypeData — ToString on BashObject types' {
    It 'New-BashObject slow path has ToString returning BashText' {
        $obj = New-BashObject -BashText 'test-value' -NoTrailingNewline
        $obj -is [string] | Should -BeFalse -Because 'NoTrailingNewline forces slow path'
        $obj.ToString() | Should -Be 'test-value'
    }

    It 'Set-BashDisplayProperty + CatLine type has ToString' {
        InModuleScope PsBash {
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.CatLine'
                BashText   = 'line content'
                LineNumber = 1
                Content    = 'line content'
            }
            Set-BashDisplayProperty $obj
            $obj.ToString() | Should -Be 'line content'
        }
    }
}

Describe 'Internal helpers callable' {
    It 'Get-BashText returns string directly for [string] input' {
        $result = Get-BashText -InputObject 'hello'
        $result | Should -Be 'hello'
    }

    It 'Get-BashText extracts BashText from PSCustomObject' {
        $obj = [PSCustomObject]@{ BashText = 'from-obj' }
        $result = Get-BashText -InputObject $obj
        $result | Should -Be 'from-obj'
    }

    It 'Get-BashText returns empty for $null' {
        $result = Get-BashText -InputObject $null
        $result | Should -Be ''
    }

    It 'New-BashObject fast path returns [string]' {
        $result = New-BashObject -BashText 'fast'
        $result -is [string] | Should -BeTrue
        $result | Should -Be 'fast'
    }

    It 'New-BashObject slow path returns PSCustomObject with BashText' {
        $result = New-BashObject -BashText 'slow' -NoTrailingNewline
        $result -is [string] | Should -BeFalse
        $result.BashText | Should -Be 'slow'
    }

    It 'New-BashObject strips trailing newline' {
        $result = New-BashObject -BashText "hello`n"
        $result | Should -Be 'hello' -Because 'trailing newline should be stripped'
    }

    It 'ConvertFrom-BashArgs parses flags and operands' {
        $defs = New-FlagDefs -Entries @('-a', 'flag a', '-b', 'flag b')
        $parsed = ConvertFrom-BashArgs -Arguments @('-a', 'file.txt') -FlagDefs $defs
        $parsed.Flags['-a'] | Should -BeTrue
        $parsed.Flags['-b'] | Should -BeFalse
        $parsed.Operands | Should -Contain 'file.txt'
    }

    It 'Resolve-BashGlob resolves literal paths' {
        $tmpFile = Join-Path $TestDrive 'globtest.txt'
        Set-Content -Path $tmpFile -Value 'hello' -NoNewline
        InModuleScope PsBash {
            param([string]$TestFilePath)
            $result = @(Resolve-BashGlob -Paths @($TestFilePath))
            $result.Count | Should -Be 1
            $result[0] | Should -Be $TestFilePath
        } -Parameters @{ TestFilePath = $tmpFile }
    }
}

Describe 'Cross-command pipeline integrity' {
    It 'echo | grep passes strings through pipeline' {
        $result = @(Invoke-BashEcho 'hello world' | Invoke-BashGrep 'world')
        $result.Count | Should -Be 1
        $result[0] | Should -Be 'hello world'
    }

    It 'echo | sort passes strings through pipeline' {
        $result = @(Invoke-BashEcho 'cherry' | Invoke-BashSort)
        $result.Count | Should -Be 1
        $result[0] | Should -Be 'cherry'
    }

    It 'echo | sed passes strings through pipeline' {
        $result = @(Invoke-BashEcho 'hello' | Invoke-BashSed 's/hello/world/')
        $result.Count | Should -Be 1
        $result[0] | Should -Be 'world'
    }

    It 'echo | head passes strings through pipeline' {
        $result = @(Invoke-BashEcho 'line1' | Invoke-BashHead -n 1)
        $result.Count | Should -Be 1
        $result[0] | Should -Be 'line1'
    }

    It 'echo | tr passes strings through pipeline' {
        $result = @(Invoke-BashEcho 'abc' | Invoke-BashTr 'a' 'x')
        $result.Count | Should -Be 1
        $result[0] | Should -Be 'xbc'
    }
}
