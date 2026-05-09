<#
.SYNOPSIS
    Assembles the slim distribution package from build artifacts.
.DESCRIPTION
    Creates the slim distribution containing the AOT launcher, SDK host,
    and PowerShell module.
.PARAMETER RID
    Runtime identifier (e.g., win-x64, linux-x64, osx-arm64).
.PARAMETER OutputDir
    Base output directory. Package is written to <OutputDir>/slim/<RID>/.
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

$launcherPublishDir = Join-Path $repoRoot 'dist' $RID
$hostPublishDir     = Join-Path $repoRoot 'dist' 'host' $RID
$packageDir         = Join-Path $OutputDir 'slim' $RID

# Determine binary names per platform
$launcherBinary = if ($RID -like 'win-*') { 'ps-bash.exe' } else { 'ps-bash' }
$hostBinary     = if ($RID -like 'win-*') { 'ps-bash-host.exe' } else { 'ps-bash-host' }

$launcherPath = Join-Path $launcherPublishDir $launcherBinary
if (-not (Test-Path $launcherPath)) {
    throw "AOT launcher not found at $launcherPath. Run 'dotnet publish src/PsBash.Shell' first."
}

$hostPath = Join-Path $hostPublishDir $hostBinary
if (-not (Test-Path $hostPath)) {
    throw "Host binary not found at $hostPath. Run 'dotnet publish src/PsBash.Host' first."
}

# Clean and create output directory (idempotent)
if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

# Copy AOT launcher (single self-contained exe, no side files needed)
Copy-Item $launcherPath $packageDir

# Copy host + all its self-contained runtime assets (SMA DLLs, runtimes/, deps.json, ...)
# so the launcher can find ps-bash-host alongside itself at runtime.
Copy-Item -Path (Join-Path $hostPublishDir '*') -Destination $packageDir -Recurse -Force

# Copy PowerShell module
$moduleSrc = Join-Path $repoRoot 'src' 'PsBash.Module'
if (-not (Test-Path $moduleSrc)) {
    throw "PowerShell module not found at $moduleSrc."
}
$moduleDest = Join-Path $packageDir 'Modules' 'ps-bash'
New-Item -ItemType Directory -Path $moduleDest -Force | Out-Null
Copy-Item (Join-Path $moduleSrc '*') $moduleDest -Recurse

Write-Host "Slim package assembled: $packageDir"
Write-Host "  Launcher:  $launcherBinary"
Write-Host "  Host:      $hostBinary (+ runtime assets)"
Write-Host "  Module:    Modules/ps-bash/"
