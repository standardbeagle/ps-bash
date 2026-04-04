<#
.SYNOPSIS
    Assembles the slim distribution package from build artifacts.
.DESCRIPTION
    Creates the slim distribution containing the AOT binary, worker script,
    and PowerShell module. Requires PowerShell 7+ on the target system.
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

$publishDir = Join-Path $repoRoot 'dist' $RID
$packageDir = Join-Path $OutputDir 'slim' $RID

# Determine binary name per platform
$binary = if ($RID -like 'win-*') { 'ps-bash.exe' } else { 'ps-bash' }

$binaryPath = Join-Path $publishDir $binary
if (-not (Test-Path $binaryPath)) {
    throw "AOT binary not found at $binaryPath. Run 'dotnet publish' first."
}

# Clean and create output directory (idempotent)
if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

# Copy AOT binary
Copy-Item $binaryPath $packageDir

# Copy worker script
$workerSrc = Join-Path $repoRoot 'scripts' 'ps-bash-worker.ps1'
if (-not (Test-Path $workerSrc)) {
    throw "Worker script not found at $workerSrc."
}
Copy-Item $workerSrc $packageDir

# Copy PowerShell module
$moduleSrc = Join-Path $repoRoot 'src' 'PsBash.Module'
if (-not (Test-Path $moduleSrc)) {
    throw "PowerShell module not found at $moduleSrc."
}
$moduleDest = Join-Path $packageDir 'Modules' 'ps-bash'
New-Item -ItemType Directory -Path $moduleDest -Force | Out-Null
Copy-Item (Join-Path $moduleSrc '*') $moduleDest -Recurse

Write-Host "Slim package assembled: $packageDir"
Write-Host "  Binary:  $binary"
Write-Host "  Worker:  ps-bash-worker.ps1"
Write-Host "  Module:  Modules/ps-bash/"
