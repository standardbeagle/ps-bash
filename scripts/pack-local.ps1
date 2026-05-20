#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Packs PsBash.Core and publishes ps-bash + ps-bash-host into dist/.

.DESCRIPTION
    Reads the version from PsBash.psd1, syncs it to PsBash.Core.csproj,
    then packs the NuGet package into dist/. Dependent projects (e.g.
    beagle-term) reference this directory as a local NuGet feed.

    Also publishes the launcher (ps-bash, AOT) and the host
    (ps-bash-host, non-AOT, self-contained) for the local platform and
    merges them into dist/bin/ — the same launcher + host side-by-side
    layout that ships in the per-platform release archives.

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

# Sync version into PsBash.Transpiler.csproj. PsBash.Core takes a
# ProjectReference to PsBash.Transpiler, so Core's nupkg depends on
# PsBash.Transpiler at this version; both must be packed into the local feed or
# a consumer's restore fails NU1101.
$transpilerCsprojPath = Join-Path $root 'src' 'PsBash.Transpiler' 'PsBash.Transpiler.csproj'
$transpilerCsproj = Get-Content $transpilerCsprojPath -Raw
$transpilerUpdated = $transpilerCsproj -replace '<Version>[^<]*</Version>', "<Version>$version</Version>"
if ($transpilerCsproj -ne $transpilerUpdated) {
    Set-Content $transpilerCsprojPath -Value $transpilerUpdated -NoNewline
    Write-Host "Updated PsBash.Transpiler.csproj version to: $version"
} else {
    Write-Host "PsBash.Transpiler.csproj already at version: $version"
}

# Pack into dist/
$distDir = Join-Path $root 'dist'
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

# Pack PsBash.Transpiler first so PsBash.Core's dependency resolves against the
# freshly versioned package in the feed.
dotnet pack $transpilerCsprojPath -c Release -o $distDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet pack PsBash.Transpiler failed"
    exit 1
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

# Publish launcher + host for the local platform alongside the Core nupkg so a
# developer's `dist/` mirrors the per-platform layout shipped from CI:
# launcher (ps-bash[.exe]) + host (ps-bash-host[.exe]) in the same folder.
$rid = if ($IsWindows) { 'win-x64' }
       elseif ($IsMacOS) { 'osx-arm64' }
       else { 'linux-x64' }
$ext = if ($IsWindows) { '.exe' } else { '' }
$launcherStage = Join-Path $distDir 'launcher'
$hostStage = Join-Path $distDir 'host'
$launcherProj = Join-Path $root 'src' 'PsBash.Shell' 'PsBash.Shell.csproj'
$hostProj = Join-Path $root 'src' 'PsBash.Host' 'PsBash.Host.csproj'

Write-Host ""
Write-Host "Publishing ps-bash launcher (AOT) for $rid into $launcherStage ..."
dotnet publish $launcherProj -c Release -r $rid --self-contained true /p:PublishAot=true -o $launcherStage
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish PsBash.Shell failed"
    exit 1
}

Write-Host ""
Write-Host "Publishing ps-bash-host for $rid into $hostStage ..."
dotnet publish $hostProj -c Release -r $rid --self-contained true -o $hostStage
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish PsBash.Host failed"
    exit 1
}

# Merge launcher + host into a single bin/ directory so ps-bash and
# ps-bash-host live side-by-side with the host's PowerShell SDK runtime
# assets — the layout the launcher's WorkerFactory expects when resolving
# the host binary.
$binDir = Join-Path $distDir 'bin'
if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

$launcherBinary = Join-Path $launcherStage "ps-bash$ext"
if (Test-Path $launcherBinary) {
    Copy-Item $launcherBinary $binDir -Force
    Write-Host "Copied: $binDir/ps-bash$ext"
} else {
    Write-Warning "Expected $launcherBinary not found after publish."
}

# Copy host binary AND its runtime dependencies (System.Management.Automation,
# runtimes/, deps.json, ...) — the host won't start without them.
if (Test-Path $hostStage) {
    Copy-Item -Path (Join-Path $hostStage '*') -Destination $binDir -Recurse -Force
    Write-Host "Copied host runtime assets into: $binDir/"
} else {
    Write-Warning "Host stage $hostStage not found."
}
