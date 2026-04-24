#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Packs PsBash.Core into the local dist/ feed for dependent projects.

.DESCRIPTION
    Reads the version from PsBash.psd1, syncs it to PsBash.Core.csproj,
    then packs the NuGet package into dist/. Dependent projects (e.g.
    beagle-term) reference this directory as a local NuGet feed.

.EXAMPLE
    ./scripts/pack-local.ps1
#>

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# Read version from module manifest
$manifestPath = Join-Path $root 'src' 'PsBash.Module' 'PsBash.psd1'
$manifestData = Import-PowerShellDataFile $manifestPath
$version = $manifestData.ModuleVersion
Write-Host "Module version from manifest: $version"

# Sync version into PsBash.Cmdlets.psd1
$cmdletsManifestPath = Join-Path $root 'src' 'PsBash.Cmdlets' 'PsBash.Cmdlets.psd1'
$cmdletsManifest = Get-Content $cmdletsManifestPath -Raw
$updatedCmdlets = $cmdletsManifest -replace "ModuleVersion = '[^']*'", "ModuleVersion = '$version'"
if ($cmdletsManifest -ne $updatedCmdlets) {
    Set-Content $cmdletsManifestPath -Value $updatedCmdlets -NoNewline
    Write-Host "Updated PsBash.Cmdlets.psd1 version to: $version"
} else {
    Write-Host "PsBash.Cmdlets.psd1 already at version: $version"
}

# Sync version into csproj
$csprojPath = Join-Path $root 'src' 'PsBash.Core' 'PsBash.Core.csproj'
$csproj = Get-Content $csprojPath -Raw
$updated = $csproj -replace '<Version>[^<]*</Version>', "<Version>$version</Version>"
if ($csproj -ne $updated) {
    Set-Content $csprojPath -Value $updated -NoNewline
    Write-Host "Updated PsBash.Core.csproj version to: $version"
} else {
    Write-Host "PsBash.Core.csproj already at version: $version"
}

# Pack into dist/
$distDir = Join-Path $root 'dist'
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

dotnet pack $csprojPath -c Release -o $distDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet pack failed"
    exit 1
}

$nupkg = "PsBash.Core.$version.nupkg"
Write-Host ""
Write-Host "Packed: $distDir/$nupkg"
Write-Host "Dependent projects can now restore this version from the local feed."
