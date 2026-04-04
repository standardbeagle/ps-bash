<#
.SYNOPSIS
    Assembles the full distribution package (slim + bundled pwsh).
.DESCRIPTION
    Creates the full distribution containing everything in the slim package
    plus a bundled PowerShell 7 directory. The bundled pwsh directory is
    created as a placeholder with instructions for CI population.
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

# Build the slim package first into a temp location, then copy
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

# Copy slim contents into full package
Copy-Item (Join-Path $slimDir '*') $packageDir -Recurse

# Create pwsh placeholder directory
$pwshDir = Join-Path $packageDir 'pwsh'
New-Item -ItemType Directory -Path $pwshDir -Force | Out-Null

# Map RID to PowerShell download archive name
$pwshArchive = switch ($RID) {
    'win-x64'   { 'PowerShell-<VERSION>-win-x64.zip' }
    'linux-x64' { 'PowerShell-<VERSION>-linux-x64.tar.gz' }
    'osx-arm64' { 'PowerShell-<VERSION>-osx-arm64.tar.gz' }
}

@"
# Bundled PowerShell 7

This directory should contain a self-contained PowerShell 7 installation.
The ps-bash binary looks for pwsh in this directory when system pwsh is
not available (PwshLocator priority 4: side-by-side bundled).

## Populating in CI

Download the PowerShell release for this platform from GitHub releases
and extract it here:

    https://github.com/PowerShell/PowerShell/releases

Expected archive: $pwshArchive

Example CI step (GitHub Actions):

    - name: Bundle pwsh
      shell: bash
      run: |
        PWSH_VERSION="7.5.1"
        curl -sL "https://github.com/PowerShell/PowerShell/releases/download/v`${PWSH_VERSION}/$pwshArchive" -o pwsh-archive
        mkdir -p dist/full/$RID/pwsh
        # For .zip: unzip pwsh-archive -d dist/full/$RID/pwsh
        # For .tar.gz: tar xzf pwsh-archive -C dist/full/$RID/pwsh

After extraction, this directory should contain:
- pwsh (or pwsh.exe on Windows)
- All supporting libraries and assemblies
"@ | Set-Content (Join-Path $pwshDir 'README.md')

Write-Host "Full package assembled: $packageDir"
Write-Host "  Slim contents copied"
Write-Host "  pwsh/ placeholder created with population instructions"
