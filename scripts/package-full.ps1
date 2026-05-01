<#
.SYNOPSIS
    Assembles the full distribution package.
.DESCRIPTION
    In the two-binary architecture (ps-bash launcher + ps-bash-host SDK daemon),
    the host binary ships its own self-contained PowerShell SDK runtime, so a
    separately bundled pwsh is no longer needed. The full package is identical
    to the slim package; this script exists for backward-compatibility with CI
    artifact naming conventions.
.PARAMETER RID
    Runtime identifier (e.g., win-x64, linux-x64, osx-arm64).
.PARAMETER OutputDir
    Base output directory. Package is written to <OutputDir>/full/<RID>/.
    Defaults to dist/ in the repository root.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string]$RID,

    [Parameter()]
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'dist'
}

$packageDir = Join-Path $OutputDir 'full' $RID

# Build the slim package first (which now includes ps-bash-host + SDK runtime assets).
$slimDir = Join-Path $OutputDir 'slim' $RID
$slimScript = Join-Path $PSScriptRoot 'package-slim.ps1'

& $slimScript -RID $RID -OutputDir $OutputDir

if (-not (Test-Path $slimDir)) {
    throw "Slim package assembly failed; $slimDir not found."
}

# Clean and create full output directory (idempotent)
if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

# Full = slim: host ships the SDK runtime, no separate pwsh bundle needed.
Copy-Item -Path (Join-Path $slimDir '*') -Destination $packageDir -Recurse

Write-Host "Full package assembled: $packageDir"
Write-Host "  (identical to slim — ps-bash-host ships its own SDK runtime)"
