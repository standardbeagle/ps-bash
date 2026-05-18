<#
.SYNOPSIS
    Run the same Pester suite CI runs, locally, in one shot.

.DESCRIPTION
    Matches .github/workflows/ci.yml's `Run Pester Tests` step:
    builds PsBash.Cmdlets, stages PsBash.Cmdlets.dll next to the psd1,
    and runs Invoke-Pester against ./tests.

    Iteration is ~25–30s vs ~5 minutes for a push-and-wait-for-CI cycle.
    Use this as the inner loop and only push when this is green.

.PARAMETER Filter
    Substring of a fully-qualified test name to limit the run.

.PARAMETER Slow
    After the run, print the top N slowest tests (default 20 when set).
    Useful to spot regressions in expensive cmdlets (ps, find, recursive ls).

.PARAMETER FailedOnly
    Print only failure summaries; suppress pass-line output.

.PARAMETER NoBuild
    Skip the PsBash.Cmdlets build step (use when iterating tests only).

.EXAMPLE
    pwsh scripts/test-local.ps1

.EXAMPLE
    pwsh scripts/test-local.ps1 -Filter 'sed' -FailedOnly

.EXAMPLE
    pwsh scripts/test-local.ps1 -Slow 30
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [int]$Slow,
    [switch]$FailedOnly,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$psd1 = Join-Path $repo 'src/PsBash.Module/PsBash.psd1'
$dllStage = Join-Path $repo 'src/PsBash.Module/PsBash.Cmdlets.dll'

if (-not $NoBuild) {
    Write-Host '== build PsBash.Cmdlets (net8.0) ==' -ForegroundColor Cyan
    $proj = Join-Path $repo 'src/PsBash.Cmdlets/PsBash.Cmdlets.csproj'
    & dotnet build $proj -c Debug --framework net8.0 -nologo 2>&1 |
        ForEach-Object { $_ } | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
    $built = Get-ChildItem (Join-Path $repo 'src/PsBash.Cmdlets/bin/Debug') `
        -Filter 'PsBash.Cmdlets.dll' -Recurse |
        Where-Object { $_.FullName -match 'net8\.0' } |
        Select-Object -First 1
    if (-not $built) { throw 'PsBash.Cmdlets.dll not found after build' }
    Copy-Item $built.FullName $dllStage -Force
}

Write-Host '== run pester ==' -ForegroundColor Cyan

# Force a clean module load so we pick up the freshly-staged dll.
Get-Module PsBash, PsBash.Cmdlets -ErrorAction SilentlyContinue |
    Remove-Module -Force -ErrorAction SilentlyContinue

Import-Module $psd1 -Force

$cfg = New-PesterConfiguration
$cfg.Run.Path = Join-Path $repo 'tests'
$cfg.Run.PassThru = $true
$cfg.Output.Verbosity = if ($FailedOnly) { 'None' } else { 'Detailed' }
if ($Filter) {
    $cfg.Filter.FullName = "*$Filter*"
}

$start = Get-Date
$result = Invoke-Pester -Configuration $cfg
$elapsed = (Get-Date) - $start

Write-Host ''
Write-Host '== summary ==' -ForegroundColor Cyan
$color = if ($result.FailedCount -eq 0) { 'Green' } else { 'Red' }
Write-Host ("Passed:  {0}" -f $result.PassedCount) -ForegroundColor $color
Write-Host ("Failed:  {0}" -f $result.FailedCount) -ForegroundColor $color
Write-Host ("Skipped: {0}" -f $result.SkippedCount)
Write-Host ("Total:   {0} in {1:N1}s" -f $result.TotalCount, $elapsed.TotalSeconds)

if ($result.FailedCount -gt 0) {
    Write-Host ''
    Write-Host '== failures ==' -ForegroundColor Red
    $result.Failed | ForEach-Object {
        Write-Host ("[-] {0}" -f $_.ExpandedPath) -ForegroundColor Red
        $msg = ($_.ErrorRecord.Exception.Message -split "`n")[0]
        Write-Host ("    {0}" -f $msg) -ForegroundColor DarkGray
    }
}

if ($Slow) {
    Write-Host ''
    Write-Host "== top $Slow slowest tests ==" -ForegroundColor Cyan
    $result.Tests |
        Sort-Object -Property { $_.Duration } -Descending |
        Select-Object -First $Slow |
        ForEach-Object {
            '{0,8:N0}ms  {1}' -f $_.Duration.TotalMilliseconds, $_.ExpandedName
        }
}

if ($result.FailedCount -gt 0) { exit 1 } else { exit 0 }
