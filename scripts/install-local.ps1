#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Build and install ps-bash from the local working tree.

.DESCRIPTION
    Runs pack-local.ps1 to compile the AOT launcher, host daemon, and NuGet
    package, then copies the resulting binaries, worker script, and PowerShell
    module into the install directory — the same layout that the GitHub release
    installer creates, but sourced entirely from the local build.

    Use this for rapid local iteration:
        ./scripts/install-local.ps1

    Use -NoBuild to skip the build when dist/ is already current:
        ./scripts/install-local.ps1 -NoBuild

.PARAMETER InstallDir
    Target directory.  Defaults to %LOCALAPPDATA%\ps-bash on Windows and
    ~/.local/bin/ps-bash on Linux / macOS.

.PARAMETER NoBuild
    Skip pack-local.ps1 and use whatever is already in dist/bin/.

.PARAMETER NoAddToPath
    Do not modify PATH.

.EXAMPLE
    ./scripts/install-local.ps1
    ./scripts/install-local.ps1 -NoBuild -InstallDir C:\tools\ps-bash
#>
param(
    [string]$InstallDir,
    [switch]$NoBuild,
    [switch]$NoAddToPath
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# ── 1. Build ──────────────────────────────────────────────────────────────────
if (-not $NoBuild) {
    Write-Host "Building ps-bash from source..."
    & (Join-Path $PSScriptRoot 'pack-local.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Error "pack-local.ps1 failed (exit $LASTEXITCODE)"
        exit 1
    }
}

# ── 2. Locate source artifacts ────────────────────────────────────────────────
$ext    = if ($IsWindows) { '.exe' } else { '' }
$binDir = Join-Path $root 'dist' 'bin'

$launcherBin = Join-Path $binDir "ps-bash$ext"
if (-not (Test-Path $launcherBin)) {
    Write-Error "Launcher not found at $launcherBin — run without -NoBuild to compile first."
    exit 1
}

$hostBin = Join-Path $binDir "ps-bash-host$ext"
if (-not (Test-Path $hostBin)) {
    Write-Error "Host binary not found at $hostBin — run without -NoBuild to compile first."
    exit 1
}

$workerScript = Join-Path $root 'scripts' 'ps-bash-worker.ps1'
$moduleSrc    = Join-Path $root 'src' 'PsBash.Module'

# ── 3. Resolve install directory ──────────────────────────────────────────────
if (-not $InstallDir) {
    $InstallDir = if ($IsWindows) {
        Join-Path $env:LOCALAPPDATA 'ps-bash'
    } else {
        Join-Path $env:HOME '.local' 'bin' 'ps-bash'
    }
}

Write-Host "Installing to: $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

# ── 4. Copy launcher + host runtime assets ────────────────────────────────────
# Wipe and repopulate so stale DLLs from a previous install don't linger.
Get-ChildItem -Path $InstallDir -File | Remove-Item -Force
Copy-Item -Path (Join-Path $binDir '*') -Destination $InstallDir -Recurse -Force
Write-Host "Copied binaries and runtime assets."

# ── 5. Copy worker script ─────────────────────────────────────────────────────
Copy-Item $workerScript $InstallDir -Force
Write-Host "Copied ps-bash-worker.ps1."

# ── 6. Copy PowerShell module ─────────────────────────────────────────────────
$moduleDest = Join-Path $InstallDir 'Modules' 'ps-bash'
if (Test-Path $moduleDest) { Remove-Item $moduleDest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $moduleDest | Out-Null
Copy-Item -Path (Join-Path $moduleSrc '*') -Destination $moduleDest -Recurse -Force
Write-Host "Copied PowerShell module to $moduleDest."

# ── 7. PATH management ────────────────────────────────────────────────────────
if (-not $NoAddToPath) {
    $inCurrentPath = $env:PATH -split [IO.Path]::PathSeparator |
                     Where-Object { $_ -eq $InstallDir }
    if (-not $inCurrentPath) {
        $env:PATH = "${InstallDir}$([IO.Path]::PathSeparator)$env:PATH"
    }

    if ($IsWindows) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath -notlike "*${InstallDir}*") {
            [Environment]::SetEnvironmentVariable('Path', "${InstallDir};${userPath}", 'User')
            Write-Host "Added $InstallDir to user PATH (restart terminal to take effect)."
        } else {
            Write-Host "$InstallDir is already in user PATH."
        }
    } else {
        $exportLine = "export PATH=`"$InstallDir`":`$PATH"
        foreach ($pf in @("$env:HOME/.bashrc", "$env:HOME/.zshrc", "$env:HOME/.config/fish/config.fish")) {
            if (Test-Path $pf) {
                $content = Get-Content -Raw -Path $pf -ErrorAction SilentlyContinue
                if ($content -notlike "*$InstallDir*") {
                    Add-Content -Path $pf -Value "`n# ps-bash`n$exportLine"
                    Write-Host "Added $InstallDir to PATH in $pf."
                }
            }
        }
        if (-not $IsWindows) {
            & chmod +x (Join-Path $InstallDir "ps-bash")
        }
    }
}

# ── 8. Verify ─────────────────────────────────────────────────────────────────
$binary = Join-Path $InstallDir "ps-bash$ext"
try {
    $ver = & $binary --version 2>&1
    Write-Host "Installed version: $ver"
} catch {
    Write-Warning "Binary installed but --version failed: $_"
}

Write-Host ""
Write-Host "Done. Run 'ps-bash --help' to get started."
