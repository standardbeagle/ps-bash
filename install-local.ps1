#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Build and install ps-bash from the local working tree.

.PARAMETER NoBuild
    Skip pack-local.ps1 and use whatever is already in dist/bin/.

.PARAMETER NoTest
    Skip dotnet test.

.EXAMPLE
    ./install-local.ps1
    ./install-local.ps1 -NoBuild
#>
param(
    [switch]$NoBuild,
    [switch]$NoTest
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path $PSScriptRoot

# ── 1. Build ──────────────────────────────────────────────────────────────────
if (-not $NoBuild) {
    & (Join-Path $root 'scripts' 'pack-local.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Error "pack-local.ps1 failed (exit $LASTEXITCODE)"; exit 1 }
}

# ── 2. Test ───────────────────────────────────────────────────────────────────
if (-not $NoTest) {
    dotnet test (Join-Path $root 'src' 'PsBash.Core.Tests' 'PsBash.Core.Tests.csproj')
    if ($LASTEXITCODE -ne 0) { Write-Error "Tests failed (exit $LASTEXITCODE)"; exit 1 }
}

# ── 3. Locate source artifacts ────────────────────────────────────────────────
$ext    = if ($IsWindows) { '.exe' } else { '' }
$binDir = Join-Path $root 'dist' 'bin'

foreach ($bin in @("ps-bash$ext", "ps-bash-host$ext")) {
    if (-not (Test-Path (Join-Path $binDir $bin))) {
        Write-Error "$bin not found in $binDir — run without -NoBuild to compile first."
        exit 1
    }
}

$workerScript = Join-Path $root 'scripts' 'ps-bash-worker.ps1'
$moduleSrc    = Join-Path $root 'src' 'PsBash.Module'

# ── 4. Install directory ──────────────────────────────────────────────────────
$destDir = "$env:USERPROFILE\.local\bin"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Write-Host "Installing to: $destDir"

# ── 5. NTFS locked-file rename trick ─────────────────────────────────────────
# A locked binary cannot be deleted or overwritten but CAN be renamed —
# existing handles keep pointing at the old file record. Rename stale files
# to .old so Copy-Item can write the new ones even while ps-bash is running.
function Move-OutOfTheWay([string]$path) {
    if (-not (Test-Path $path)) { return }
    $base = "$path.old"
    $n = 0
    while (Test-Path $base) {
        Remove-Item $base -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $base)) { break }
        $n++; $base = "$path.old.$n"
    }
    Move-Item $path $base -Force -ErrorAction SilentlyContinue
}

Get-ChildItem $destDir -File -Filter 'ps-bash*' | Where-Object { $_.Name -notlike '*.old*' } |
    ForEach-Object { Move-OutOfTheWay $_.FullName }
Get-ChildItem $destDir -File -Filter 'PsBash.*' |
    ForEach-Object { Move-OutOfTheWay $_.FullName }
Get-ChildItem $destDir -File -Filter '*.dll' | Where-Object { $_.Name -notlike '*.old*' } |
    ForEach-Object { Move-OutOfTheWay $_.FullName }

# ── 6. Copy binaries and runtime assets ──────────────────────────────────────
Copy-Item -Path (Join-Path $binDir '*') -Destination $destDir -Recurse -Force
Write-Host "Copied binaries and runtime assets."

# ── 7. Copy worker script ─────────────────────────────────────────────────────
Copy-Item $workerScript $destDir -Force
Write-Host "Copied ps-bash-worker.ps1."

# ── 8. Copy PowerShell module ─────────────────────────────────────────────────
$moduleDest = Join-Path $destDir 'Modules' 'ps-bash'
if (Test-Path $moduleDest) { Remove-Item $moduleDest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $moduleDest | Out-Null
Copy-Item -Path (Join-Path $moduleSrc '*') -Destination $moduleDest -Recurse -Force
Write-Host "Copied PowerShell module to $moduleDest."

# ── 9. Wipe cached extracted module so the new psm1 is picked up immediately ──
Remove-Item "$env:TEMP\ps-bash\module-*" -Recurse -Force -ErrorAction SilentlyContinue

# ── 10. Verify ────────────────────────────────────────────────────────────────
$binary = Join-Path $destDir "ps-bash$ext"
try {
    $ver = & $binary --version 2>&1
    Write-Host "Installed version: $ver"
} catch {
    Write-Warning "Binary installed but --version failed: $_"
}

Write-Host ""
Write-Host "Done. Run 'ps-bash --help' to get started." -ForegroundColor Green
