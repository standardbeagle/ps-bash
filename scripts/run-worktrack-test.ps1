[CmdletBinding()]
param(
    [string] $BashExecutable,
    [string] $TestScript = (Join-Path $PSScriptRoot 'test.sh'),
    [string] $LogDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/worktrack'),
    [string] $Timestamp = (Get-Date -Format 'yyyyMMdd-HHmmss')
)

$ErrorActionPreference = 'Stop'

if (-not $BashExecutable) {
    $gitBash = 'C:\Program Files\Git\bin\bash.exe'
    $BashExecutable = if ($IsWindows -and (Test-Path $gitBash)) { $gitBash } else { 'bash' }
}

New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$logPath = Join-Path $LogDirectory "test-all-$Timestamp.log"

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $BashExecutable
$startInfo.ArgumentList.Add($TestScript)
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
try {
    if (-not $process.Start()) {
        throw "Failed to start $BashExecutable"
    }

    # Start both drains before waiting so a noisy child cannot fill either pipe.
    $stdoutRead = $process.StandardOutput.ReadToEndAsync()
    $stderrRead = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutRead.GetAwaiter().GetResult()
    $stderr = $stderrRead.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
}
finally {
    $process.Dispose()
}

# Keep the caller-visible streams distinct. The workflow's former `2>&1 |
# Tee-Object` command irreversibly redirected native stderr into stdout.
[Console]::Out.Write($stdout)
[Console]::Error.Write($stderr)
# The combined diagnostic log intentionally groups stdout before stderr. The
# caller-visible streams remain distinct, but cross-stream temporal ordering is
# not represented in this file.
[System.IO.File]::WriteAllText($logPath, $stdout + $stderr, [System.Text.UTF8Encoding]::new($false))
[Console]::Out.WriteLine("worktrack-test-log=$logPath exit=$exitCode")
exit $exitCode
