#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Body of the .tman.kdl build/test/test-proj jobs: ONE serialized unit that builds, then tests.

.DESCRIPTION
    Why a script and not `dotnet ...` args in the kdl:
      1. `tman test-proj` used to pass --no-build, so an edit followed by a re-run tested STALE
         binaries. Every test verb here builds the solution first, in the SAME job, and only
         then runs `dotnet test --no-build` (no other job can rebuild in between).
      2. tman rewrites `-m:1` as `-m: 1` (and `-nodeReuse:false` as `-nodeReuse: false`) on the
         `tman run -- cmd args` path, breaking MSBuild switches. The switches live here, out of
         tman's arg parser. User args (`--filter "A|B"`) still pass through tman untouched.
      3. tman buckets by alias NAME, so build/test/test-proj/pester would run concurrently.
         Every verb takes the same per-checkout file lock below, so they queue behind each
         other regardless of alias. (Output is emitted while waiting so tman's `stall` does not
         kill a queued job.)
      4. tman's max-parallel is per DIRECTORY, so every agent worktree had its own slot: 4 agents
         = 4 concurrent solution builds + test hosts, which filled the disk and RAM (2026-09-30).
         While holding the per-checkout lock, this script re-runs itself through a NESTED
         `tman run --max-parallel N` started from one shared directory, so all checkouts on the
         machine share N slots (tman buckets by executable@cwd — pwsh.exe@<gate dir>).
         N = $env:PSBASH_TMAN_MACHINE_SLOTS (default 2; 0 = no machine-wide gate). The inner run
         carries the real stall/max-time limits; the outer alias limits are loose because the
         outer run is silent while queued. tman's max-mem is NOT used: in tman 0.5.1 it only
         measures the direct child (pwsh), not dotnet/testhost below it, so it never fires.

    Only tman/dotnet kill-tree ends processes; nothing here kills by image name.

.EXAMPLE
    tman build
    tman test
    tman test-proj src/PsBash.Core.Tests --filter "FullyQualifiedName~Fused|FullyQualifiedName~Foo"
    tman pester -Detailed -Filter '*echo*'
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('build', 'test', 'test-proj', 'pester')]
    [string]$Verb,

    [Parameter(ValueFromRemainingArguments)]
    [string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repo
$isInner = $env:PSBASH_TMAN_GATE_INNER -eq '1'

# Flags every dotnet invocation carries (see the long rationale history in git: 94ab500, b32b2b2).
# -nodeReuse:false + UseSharedCompilation=false: process exit releases every handle on obj/bin.
# -m:1: solution-level test runs test PROJECTS serially (spawn-heavy suites contend otherwise).
$common = @('--nologo', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false')

# --- per-checkout mutual exclusion across ALL verbs -------------------------------------------
$hash = [BitConverter]::ToString([Security.Cryptography.SHA1]::HashData([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant()))).Replace('-', '').Substring(0, 12)
$lockPath = Join-Path ([IO.Path]::GetTempPath()) "psbash-tman-$hash.lock"
$lock = $null
$waited = 0
# The inner (gated) run inherits the lock its outer run holds; taking it again would deadlock.
while (-not $isInner) {
    try {
        $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
        break
    }
    catch [IO.IOException] {
        if ($waited % 30 -eq 0) { Write-Host "tman-job: waiting for another build/test in this checkout ($waited s)..." }
        Start-Sleep -Seconds 5
        $waited += 5
        if ($waited -gt 3000) { throw "tman-job: gave up waiting for $lockPath" }
    }
}

try {
    # --- machine-wide gate (see DESCRIPTION 4) ---------------------------------------------------
    $slots = if ($env:PSBASH_TMAN_MACHINE_SLOTS) { [int]$env:PSBASH_TMAN_MACHINE_SLOTS } else { 2 }
    if (-not $isInner -and $slots -gt 0 -and (Get-Command tman -ErrorAction SilentlyContinue)) {
        $gateDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'psbash-tman-gate'
        New-Item -ItemType Directory -Force $gateDir | Out-Null
        $env:PSBASH_TMAN_GATE_INNER = '1'
        # tman exempts NESTED runs (TMAN_RUN_ID set by the parent run) from max-parallel queueing —
        # sensible when parent and child share a bucket, but ours never do (alias@checkout vs
        # pwsh.exe@gate), and with the marker set the gate silently admitted every run. Clear it
        # so the inner run queues; kill-tree still works because the inner tman is our child.
        $parentRunId = $env:TMAN_RUN_ID
        Remove-Item Env:TMAN_RUN_ID -ErrorAction SilentlyContinue
        Write-Host "tman-job: taking a machine-wide build/test slot ($slots total; queue: tman ls --all)"
        Push-Location $gateDir
        try {
            & tman run --max-parallel $slots --queue-timeout 60m --stall 5m --max-time 45m -- `
                pwsh -NoProfile -File $PSCommandPath $Verb @Rest
            $code = $LASTEXITCODE
        }
        finally {
            Pop-Location
            Remove-Item Env:PSBASH_TMAN_GATE_INNER -ErrorAction SilentlyContinue
            if ($parentRunId) { $env:TMAN_RUN_ID = $parentRunId }
        }
        exit $code
    }

    function Invoke-Native([string]$Exe, [string[]]$Arguments) {
        Write-Host "tman-job: $Exe $($Arguments -join ' ')"
        & $Exe @Arguments
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    function Build-Solution {
        Invoke-Native dotnet (@('build', 'ps-bash.sln', '-c', 'Debug', '-f', 'net10.0') + $common)
    }

    switch ($Verb) {
        'build' {
            # Extra args (e.g. a project path) replace the solution.
            $target = if ($Rest) { $Rest } else { @('ps-bash.sln') }
            Invoke-Native dotnet (@('build') + $target + @('-c', 'Debug', '-f', 'net10.0') + $common)
        }
        'test' {
            Build-Solution
            Invoke-Native dotnet (@('test', 'ps-bash.sln', '-c', 'Debug', '-f', 'net10.0', '--no-build') + $common + $Rest)
        }
        'test-proj' {
            if (-not $Rest -or $Rest[0].StartsWith('-')) { throw 'usage: tman test-proj <project-dir-or-csproj> [dotnet test args, e.g. --filter "..."]' }
            # Whole solution, not just the project: suites spawn ps-bash.exe / ps-bash-host.exe
            # (built by PsBash.Shell / PsBash.Host), which a project-only build does not relink.
            Build-Solution
            Invoke-Native dotnet (@('test', $Rest[0], '-c', 'Debug', '-f', 'net10.0', '--no-build') + $common + @($Rest | Select-Object -Skip 1))
        }
        'pester' {
            # pester.ps1 does its own build (same flags) + DLL refresh; it must run under our lock.
            # Child pwsh so -Detailed / -Filter parse as real parameters (array splat would not).
            & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'pester.ps1') @Rest
            exit $LASTEXITCODE
        }
    }
}
finally {
    if ($lock) { $lock.Dispose() }
}
