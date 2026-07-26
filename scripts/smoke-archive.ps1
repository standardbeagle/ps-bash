#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Smoke-test an assembled ps-bash package directory by actually RUNNING it.

.DESCRIPTION
    The one execution test that stands between a packaging mistake and a shipped artifact.
    Every release from v0.9.1 to v0.10.22 shipped archives with NO
    System.Management.Automation.dll — 195 DLLs instead of 265 — because
    <PrivateAssets>all</PrivateAssets> on the Microsoft.PowerShell.SDK reference excluded the
    SDK's runtime assets from publish. Those archives start the launcher, fail to load
    PowerShell in the host, and report the misleading "ps-bash-host did not accept
    connections within 20s" (exit 125). Nothing caught it because CI only ever BUILT and
    ZIPPED: build.yml packaged and uploaded, publish.yml packaged and uploaded, and neither
    ran the result. A dev `dotnet build` cannot see it either — that resolves SMA from the
    NuGet cache via runtimeconfig.dev.json probing paths.

    Checks, in order of diagnostic value:
      1. launcher and host binaries are present;
      2. System.Management.Automation.dll is present — failing with a message that NAMES the
         PrivateAssets/ExcludeAssets cause, so a regression is diagnosed rather than
         surfacing as an opaque connection timeout;
      3. the package runs a real command end to end (launcher spawns host from the same
         directory) and produces the expected output with exit 0.

    See docs/bugs/publish-host-missing-sma.md.

.PARAMETER StageDir
    The assembled package directory to test (e.g. ./publish/stage, dist/slim/win-x64).

.EXAMPLE
    ./scripts/smoke-archive.ps1 -StageDir ./publish/stage
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StageDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $StageDir)) {
    throw "package directory not found: $StageDir"
}

# Infer the extension from what is actually there rather than taking it as a parameter, so
# callers on every RID invoke this identically.
$exe = Join-Path $StageDir 'ps-bash.exe'
$hostExe = Join-Path $StageDir 'ps-bash-host.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    $exe = Join-Path $StageDir 'ps-bash'
    $hostExe = Join-Path $StageDir 'ps-bash-host'
}

if (-not (Test-Path -LiteralPath $exe)) { throw "launcher missing from package: $StageDir" }
if (-not (Test-Path -LiteralPath $hostExe)) { throw "ps-bash-host missing from package: $StageDir" }

# Assert the managed assembly itself, not merely a deps.json entry — the assembly is what
# fails to load.
if (-not (Test-Path -LiteralPath (Join-Path $StageDir 'System.Management.Automation.dll'))) {
    throw ("package has no System.Management.Automation.dll, so ps-bash-host cannot start. " +
           "Check that the Microsoft.PowerShell.SDK PackageReference in " +
           "src/PsBash.Host/PsBash.Host.csproj carries no PrivateAssets/ExcludeAssets " +
           "(see docs/bugs/publish-host-missing-sma.md).")
}

if (-not $IsWindows) {
    chmod +x $exe
    chmod +x $hostExe
}

# Randomized endpoint: never adopt a host left behind by another step or a developer shell.
$env:PSBASH_IPC_ENDPOINT = 'pipe:psbash-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8)

$output = & $exe -c 'echo smoke-ok; seq 1 3 | wc -l' 2>&1
$code = $LASTEXITCODE
$text = $output | Out-String
Write-Host "smoke run exit=$code output:`n$text"

if ($code -ne 0) { throw "package failed to run a command (exit $code): $text" }
if ($text -notmatch 'smoke-ok') { throw "package produced no 'smoke-ok' marker: $text" }
if ($text -notmatch '3') { throw "package pipeline produced no line count: $text" }

Write-Host "Package smoke test passed: $StageDir"
