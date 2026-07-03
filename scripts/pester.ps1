#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run the release-blocking Pester gate (tests/PsBash.Tests.ps1) the easy way.

.DESCRIPTION
    The Pester suite imports the SOURCE module (src/PsBash.Module/PsBash.psd1), whose
    psm1 loads the binary cmdlets from DLLs that must sit BESIDE it. Those DLLs are
    gitignored (src/PsBash.Module/*.dll), so a fresh checkout / a stale copy makes the
    Pester run load old code and "reproduce nothing". This script does the whole dance:

      1. Builds PsBash.Cmdlets (Debug) — which also builds Transpiler + Parlot.
      2. Refreshes the beside-module DLLs the psm1 probes for:
         bin/Debug/<tfm>/{PsBash.Cmdlets,PsBash.Transpiler,Parlot}.dll -> src/PsBash.Module/
      3. Runs Invoke-Pester ./tests/ in a FRESH child pwsh (clean runspace, so no
         previously-imported module shadows the source copy).

    This is the same gate publish.yml blocks on. A green run here is the pre-tag check
    the /publish skill and the release-pester-gate-local memory call for.

.PARAMETER Detailed
    Use -Output Detailed (per-test) instead of the default Minimal summary.

.PARAMETER Filter
    Only run Describe/Context/It blocks whose full name matches this wildcard
    (Invoke-Pester -FullNameFilter). Speeds up iterating on one area.

.PARAMETER SkipBuild
    Skip the build + DLL refresh and just re-run Pester against the DLLs already
    beside the module. Use when iterating on the .ps1 tests only.

.EXAMPLE
    ./scripts/pester.ps1
    ./scripts/pester.ps1 -Detailed -Filter '*echo*'
    ./scripts/pester.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [string]$Filter,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$moduleDir = Join-Path $repo 'src/PsBash.Module'
$cmdletsProj = Join-Path $repo 'src/PsBash.Cmdlets/PsBash.Cmdlets.csproj'
$besideDlls = 'PsBash.Cmdlets.dll', 'PsBash.Transpiler.dll', 'Parlot.dll'

if (-not $SkipBuild) {
    Write-Host '==> Building PsBash.Cmdlets (Debug)…' -ForegroundColor Cyan
    dotnet build $cmdletsProj -c Debug --nologo -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Cmdlets build failed (exit $LASTEXITCODE)." }

    # Prefer net8.0 (the module's load target); fall back to whatever TFM has the DLLs.
    $binRoot = Join-Path $repo 'src/PsBash.Cmdlets/bin/Debug'
    $tfmDir = @('net8.0', 'net10.0', 'net9.0') |
        ForEach-Object { Join-Path $binRoot $_ } |
        Where-Object { Test-Path (Join-Path $_ 'PsBash.Cmdlets.dll') } |
        Select-Object -First 1
    if (-not $tfmDir) {
        $tfmDir = Get-ChildItem $binRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'PsBash.Cmdlets.dll') } |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $tfmDir) { throw "Could not find a built PsBash.Cmdlets.dll under $binRoot." }

    Write-Host "==> Refreshing beside-module DLLs from $tfmDir" -ForegroundColor Cyan
    foreach ($dll in $besideDlls) {
        $src = Join-Path $tfmDir $dll
        if (-not (Test-Path $src)) { throw "Expected build output missing: $src" }
        Copy-Item $src (Join-Path $moduleDir $dll) -Force
    }
}
else {
    foreach ($dll in $besideDlls) {
        if (-not (Test-Path (Join-Path $moduleDir $dll))) {
            throw "-SkipBuild but $dll is not beside the module. Run once without -SkipBuild first."
        }
    }
}

# Run in a FRESH pwsh, and — CRITICAL for local runs — make any INSTALLED PsBash
# (a dev/PSGallery copy under the user module path, e.g. OneDrive\…\PowerShell\Modules)
# undiscoverable BEFORE Pester runs. Otherwise module auto-loading pulls that stale copy
# in the moment a test resolves an Invoke-Bash* command, it can't find ITS companion
# Cmdlets DLL, and dozens of ls/cat/grep tests fail for reasons unrelated to the source
# tree (the stale-installed-psbash-shadows-tests footgun). tests/EnsureCleanRunspace.ps1
# does the same strip, but only at discovery time — doing it here first is the reliable
# belt-and-suspenders. Pester is imported by explicit path so stripping its dir is safe.
$outputMode = if ($Detailed) { 'Detailed' } else { 'Minimal' }
$testsDir = Join-Path $repo 'tests'
$srcModuleParent = Join-Path $repo 'src'

Write-Host "==> Invoke-Pester $testsDir (-Output $outputMode)$(if ($Filter) { " filter '$Filter'" })" -ForegroundColor Cyan
$pester = @"
`$ErrorActionPreference = 'Stop'
# 1. Import Pester by explicit path so we don't depend on the user module path staying intact.
`$pesterMod = Get-Module -ListAvailable Pester |
    Where-Object { `$_.Version -ge [version]'5.0' } |
    Sort-Object Version -Descending | Select-Object -First 1
if (-not `$pesterMod) { throw 'Pester 5+ is not installed. Install-Module Pester -Scope CurrentUser' }
Import-Module `$pesterMod.Path -Force

# 2. Drop any loaded PsBash and strip every module-path entry that ships an installed
#    PsBash (keep our own src tree), so auto-load can never resolve a stale copy.
foreach (`$n in 'PsBash.Cmdlets','PsBash') { while (Get-Module `$n) { Remove-Module `$n -Force -EA SilentlyContinue } }
`$sep = [IO.Path]::PathSeparator
`$src = '$srcModuleParent'
`$kept = foreach (`$dir in (`$env:PSModulePath -split `$sep)) {
    if ([string]::IsNullOrWhiteSpace(`$dir)) { continue }
    if (`$dir.StartsWith(`$src, [StringComparison]::OrdinalIgnoreCase)) { `$dir; continue }
    `$shipsPsBash = try { Test-Path (Join-Path `$dir 'PsBash') -EA Stop } catch { `$false }
    if (-not `$shipsPsBash) { `$dir }
}
`$env:PSModulePath = ((`$kept | Where-Object { `$_ }) -join `$sep)

# 3. Run the gate.
`$cfg = New-PesterConfiguration
`$cfg.Run.Path = '$testsDir'
`$cfg.Output.Verbosity = '$outputMode'
$(if ($Filter) { "`$cfg.Filter.FullName = '$Filter'" })
`$cfg.Run.Exit = `$true
Invoke-Pester -Configuration `$cfg
"@

pwsh -NoProfile -Command $pester
$code = $LASTEXITCODE
if ($code -eq 0) { Write-Host '==> Pester gate PASSED' -ForegroundColor Green }
else { Write-Host "==> Pester gate FAILED (exit $code)" -ForegroundColor Red }
exit $code
