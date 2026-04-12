#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Install ps-bash from GitHub releases.
.DESCRIPTION
    Detects platform (Windows, Linux, macOS) and architecture (x64, arm64),
    downloads the latest GitHub release, and installs ps-bash to a suitable
    location. Optionally adds it to your PATH.
.EXAMPLE
    iwr https://raw.githubusercontent.com/standardbeagle/ps-bash/main/install.ps1 | iex
#>
param(
    [string]$Version = "latest",
    [string]$InstallDir,
    [switch]$NoAddToPath
)

$ErrorActionPreference = 'Stop'

# --- Platform detection ---
$os = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
$arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
    ([System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' }
    ([System.Runtime.InteropServices.Architecture]::X64)   { 'x64' }
    default {
        Write-Error "Unsupported architecture: $_"
        exit 1
    }
}
$rid = "${os}-${arch}"
$ext = if ($IsWindows) { '.exe' } else { '' }

# --- Install path ---
if (-not $InstallDir) {
    $InstallDir = if ($IsWindows) {
        Join-Path $env:LOCALAPPDATA 'ps-bash'
    } else {
        Join-Path $env:HOME '.local/bin'
    }
}

$binaryName = "ps-bash${ext}"
$targetPath = Join-Path $InstallDir $binaryName

# --- Resolve version ---
if ($Version -eq 'latest') {
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/standardbeagle/ps-bash/releases/latest'
    $Version = $release.tag_name
} else {
    if (-not $Version.StartsWith('v')) { $Version = "v${Version}" }
}
Write-Host "Installing ps-bash $Version for $rid..."

# --- Download ---
$assetName = "ps-bash-${Version}-${rid}.zip"
$downloadUrl = "https://github.com/standardbeagle/ps-bash/releases/download/${Version}/${assetName}"
$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) $assetName

try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $tempZip -UseBasicParsing
} catch {
    Write-Error "Failed to download ${downloadUrl}: $($_.Exception.Message)"
    exit 1
}

# --- Extract ---
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ($IsWindows) {
    Expand-Archive -Path $tempZip -DestinationPath $InstallDir -Force
} else {
    $unzipDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetFileNameWithoutExtension($assetName))
    New-Item -ItemType Directory -Force -Path $unzipDir | Out-Null
    unzip -o $tempZip -d $unzipDir
    Copy-Item -Path (Join-Path $unzipDir $binaryName) -Destination $targetPath -Force
    chmod +x $targetPath
}

Remove-Item -Path $tempZip -Force -ErrorAction SilentlyContinue

Write-Host "Installed to: $targetPath"

# --- PATH ---
if (-not $NoAddToPath) {
    $alreadyInPath = $env:PATH -split [IO.Path]::PathSeparator | Where-Object { $_ -eq $InstallDir }
    if (-not $alreadyInPath) {
        if ($IsWindows) {
            $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
            if ($userPath -notlike "*${InstallDir}*") {
                [Environment]::SetEnvironmentVariable('Path', "${userPath};${InstallDir}", 'User')
                Write-Host "Added $InstallDir to your user PATH. Restart your terminal to use 'ps-bash'."
            }
        } else {
            $profileFiles = @(
                (Join-Path $env:HOME '.bashrc'),
                (Join-Path $env:HOME '.zshrc'),
                (Join-Path $env:HOME '.config/fish/config.fish')
            )
            $exportLine = "export PATH=`"\"$InstallDir`"\":`$PATH"
            foreach ($pf in $profileFiles) {
                if (Test-Path $pf) {
                    $content = Get-Content -Raw -Path $pf -ErrorAction SilentlyContinue
                    if ($content -notlike "*$InstallDir*") {
                        Add-Content -Path $pf -Value "`n# ps-bash`n$exportLine`n"
                        Write-Host "Added $InstallDir to PATH in $pf"
                    }
                }
            }
        }
    } else {
        Write-Host "$InstallDir is already in your PATH."
    }
}

# --- Verify ---
try {
    $installedVersion = & $targetPath --version 2>$null
    Write-Host "Installed version: $installedVersion"
} catch {
    Write-Warning "Installation succeeded but could not verify version."
}

Write-Host "Done. Run '$binaryName --help' to get started."
