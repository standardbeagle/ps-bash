<#
.SYNOPSIS
    Guarantees the test session loads exactly one PsBash module: the source tree.

.DESCRIPTION
    A PSGallery / dev install of PsBash (and its companion PsBash.Cmdlets) commonly
    sits on $env:PSModulePath. Simply Remove-Module'ing it once is not enough: the
    installed copy stays *discoverable*, so PowerShell module auto-loading silently
    pulls it back in the moment any Invoke-Bash* command resolves mid-run. That
    leaves TWO modules named 'PsBash' loaded, and Pester's InModuleScope then fails
    with "Multiple script or manifest modules named 'PsBash' are currently loaded".

    This script makes the cleanup durable:
      1. Removes any already-loaded PsBash / PsBash.Cmdlets modules.
      2. Strips every $env:PSModulePath entry that ships an installed PsBash (except
         our own src tree), so auto-loading can never resolve a second copy.
      3. Imports the source module by explicit path.
      4. Asserts exactly one PsBash module is loaded (fails loud otherwise).

    Dot-source this at the TOP of a test file (discovery time), before any
    Describe / InModuleScope block:

        . $PSScriptRoot/EnsureCleanRunspace.ps1

    It returns the resolved module manifest path for convenience.
#>

# 1. Drop any copies already in the session.
foreach ($name in 'PsBash.Cmdlets', 'PsBash') {
    while (Get-Module $name) {
        Get-Module $name | Remove-Module -Force -ErrorAction SilentlyContinue
    }
}

# 2. Make every installed copy undiscoverable for auto-loading. Keep only path
#    entries that do NOT contain an installed PsBash, plus anything under our own
#    source tree. Pester is already loaded, so dropping its directory here is safe
#    for the remainder of the run.
$srcRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' 'src')).Path
$sep = [System.IO.Path]::PathSeparator
$kept = foreach ($dir in ($env:PSModulePath -split $sep)) {
    if ([string]::IsNullOrWhiteSpace($dir)) { continue }
    $isOurs = $dir.StartsWith($srcRoot, [System.StringComparison]::OrdinalIgnoreCase)
    # Test-Path THROWS UnauthorizedAccessException on a dir that exists but is not
    # readable by the current user (e.g. a root-owned /root/.local/.../Modules/PsBash on
    # a CI runner whose Pester step runs as a different user). Such a copy is unreadable,
    # so PowerShell auto-load can't pull it in either — treat the probe failure as "no
    # shadowing copy here" and keep the entry rather than crashing discovery.
    $shipsPsBash = try { Test-Path (Join-Path $dir 'PsBash') -ErrorAction Stop } catch { $false }
    if ($shipsPsBash -and -not $isOurs) { continue }   # installed copy lives here -> drop
    $dir
}
$env:PSModulePath = ($kept | Where-Object { $_ }) -join $sep

# 3. Import the source module by explicit path (independent of auto-loading).
$modulePath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'src' 'PsBash.Module' 'PsBash.psd1')).Path
Import-Module $modulePath -Force

# 4. Fail loud if a stray second copy still slipped in.
$loaded = @(Get-Module PsBash)
if ($loaded.Count -ne 1) {
    $paths = ($loaded | ForEach-Object { $_.Path }) -join '; '
    throw "EnsureCleanRunspace: expected exactly one PsBash module loaded, found $($loaded.Count): $paths"
}

# Hand the resolved manifest path back to the caller.
$modulePath
