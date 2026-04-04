#Requires -Modules Pester

<#
.SYNOPSIS
    Integration tests validating ps-bash as a SHELL replacement for AI agents.

.DESCRIPTION
    These tests verify that the ps-bash shell binary behaves correctly when
    invoked the way AI coding agents (Claude Code, opencode, Gemini CLI) call
    a SHELL: via -c "command", with exit code propagation, stdout/stderr
    capture, and bash-to-pwsh transpilation.

    Tests use `dotnet run --project` to invoke PsBash.Shell, falling back
    gracefully if the project or pwsh is unavailable.
#>

BeforeAll {
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
    $script:ShellProject = Join-Path $script:RepoRoot 'src' 'PsBash.Shell'
    $script:WorkerScript = Join-Path $script:RepoRoot 'scripts' 'ps-bash-worker.ps1'

    $script:HasProject = Test-Path (Join-Path $script:ShellProject 'PsBash.Shell.csproj')
    $script:HasWorker = Test-Path $script:WorkerScript
    $script:HasPwsh = $null -ne (Get-Command pwsh -ErrorAction SilentlyContinue)

    function Invoke-PsBashShell {
        param(
            [string[]]$Arguments = @(),
            [string]$StdinContent,
            [hashtable]$ExtraEnv = @{},
            [int]$TimeoutSeconds = 30
        )

        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = 'dotnet'
        $psi.ArgumentList.Add('run')
        $psi.ArgumentList.Add('--no-build')
        $psi.ArgumentList.Add('--project')
        $psi.ArgumentList.Add($script:ShellProject)
        $psi.ArgumentList.Add('--')
        foreach ($arg in $Arguments) {
            $psi.ArgumentList.Add($arg)
        }

        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardInput = $true
        $psi.UseShellExecute = $false

        $psi.Environment['PSBASH_WORKER'] = $script:WorkerScript

        foreach ($key in $ExtraEnv.Keys) {
            $psi.Environment[$key] = $ExtraEnv[$key]
        }

        $process = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $process) {
            throw 'Failed to start dotnet run'
        }

        if ($StdinContent) {
            $process.StandardInput.Write($StdinContent)
        }
        $process.StandardInput.Close()

        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $exited = $process.WaitForExit($TimeoutSeconds * 1000)

        if (-not $exited) {
            $process.Kill($true)
            throw "Process timed out after ${TimeoutSeconds}s"
        }

        [PSCustomObject]@{
            ExitCode = $process.ExitCode
            Stdout   = $stdout
            Stderr   = $stderr
        }
    }

    function Skip-UnlessReady {
        if (-not $script:HasProject) { Set-ItResult -Skipped -Because 'PsBash.Shell project not found' }
        if (-not $script:HasPwsh) { Set-ItResult -Skipped -Because 'pwsh not available' }
        if (-not $script:BuildSucceeded) { Set-ItResult -Skipped -Because 'PsBash.Shell build failed' }
    }

    # Build once before all tests
    if ($script:HasProject) {
        & dotnet build $script:ShellProject --nologo -v q 2>&1 | Out-Null
        $script:BuildSucceeded = ($LASTEXITCODE -eq 0)
    } else {
        $script:BuildSucceeded = $false
    }
}

Describe 'Prerequisites' {
    It 'has PsBash.Shell project' {
        $script:HasProject | Should -BeTrue
    }

    It 'has ps-bash-worker.ps1 script' {
        $script:HasWorker | Should -BeTrue
    }

    It 'has pwsh available' {
        $script:HasPwsh | Should -BeTrue
    }

    It 'builds PsBash.Shell successfully' {
        $script:BuildSucceeded | Should -BeTrue
    }
}

Describe 'Simple Command Execution' {
    It 'executes a command via -c and returns stdout' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', 'Write-Host hello-world')
        $result.ExitCode | Should -Be 0
        $result.Stdout | Should -Match 'hello-world'
    }

    It 'captures multi-line stdout' {
        Skip-UnlessReady
        $cmd = 'Write-Host "line1"; Write-Host "line2"; Write-Host "line3"'
        $result = Invoke-PsBashShell -Arguments @('-c', $cmd)
        $result.ExitCode | Should -Be 0
        $result.Stdout | Should -Match 'line1'
        $result.Stdout | Should -Match 'line2'
        $result.Stdout | Should -Match 'line3'
    }

    It 'returns empty stdout for commands with no output' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', '$null')
        $result.ExitCode | Should -Be 0
        $result.Stdout.Trim() | Should -BeNullOrEmpty
    }
}

Describe 'Exit Code Propagation' {
    It 'returns exit code 0 on success' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', 'Write-Host ok')
        $result.ExitCode | Should -Be 0
    }

    It 'returns exit code 1 on thrown error' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', "throw 'fail'")
        $result.ExitCode | Should -Be 1
    }

    It 'returns non-zero for no command specified' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @()
        $result.ExitCode | Should -Not -Be 0
        $result.Stderr | Should -Match 'no command specified'
    }
}

Describe 'Bash-to-PowerShell Transpilation' {
    It 'transpiles echo to Write-Output' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', 'echo hello')
        $result.ExitCode | Should -Be 0
        $result.Stdout | Should -Match 'hello'
    }

    It 'transpiles /dev/null redirect' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', 'Write-Host visible 2> /dev/null')
        $result.ExitCode | Should -Be 0
        $result.Stdout | Should -Match 'visible'
    }

    It 'handles --login flag without error' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('--login', '-c', 'Write-Host login-ok')
        $result.ExitCode | Should -Be 0
        $result.Stdout | Should -Match 'login-ok'
    }
}

Describe 'Stdin Mode' {
    It 'reads and executes from stdin with -s flag' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-s') -StdinContent 'Write-Host stdin-works'
        $result.ExitCode | Should -Be 0
        $result.Stdout | Should -Match 'stdin-works'
    }
}

Describe 'Debug Mode' {
    It 'writes debug info to stderr when PSBASH_DEBUG=1' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', 'Write-Host ok') -ExtraEnv @{ 'PSBASH_DEBUG' = '1' }
        $result.ExitCode | Should -Be 0
        $result.Stderr | Should -Match '\[ps-bash\] input:'
        $result.Stderr | Should -Match '\[ps-bash\] transpiled:'
        $result.Stderr | Should -Match '\[ps-bash\] exit:'
    }
}

Describe 'Agent SHELL Contract' {
    It 'handles the typical agent invocation: SHELL -c "command"' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', 'Get-Date -Format yyyy')
        $result.ExitCode | Should -Be 0
        $result.Stdout.Trim() | Should -Match '^\d{4}$'
    }

    It 'handles agent invocation with --login: SHELL --login -c "command"' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('--login', '-c', 'Write-Host agent-ok')
        $result.ExitCode | Should -Be 0
        $result.Stdout | Should -Match 'agent-ok'
    }

    It 'stderr passthrough works for agent error capture' {
        Skip-UnlessReady
        $result = Invoke-PsBashShell -Arguments @('-c', '[Console]::Error.WriteLine("agent-error"); Write-Host ok')
        $result.ExitCode | Should -Be 0
        $result.Stderr | Should -Match 'agent-error'
        $result.Stdout | Should -Match 'ok'
    }
}
