#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Pipeline-throughput bench for the fused-pipeline lane (PERF task
  01KXQ0KMG5C26BWXNVPZXBVA6H). Measures end-to-end wall-clock of representative
  all-mapped chains with the fused lane ON (PSBASH_FUSED unset/1) vs OFF
  (PSBASH_FUSED=0), so the win on the profile's dominant bottleneck — the
  per-output-line IPC framing back to the launcher — is directly visible.

.DESCRIPTION
  Each case runs `ps-bash -c '<chain>'` in its own isolated per-session IPC
  endpoint (so a shared installed daemon is never contended), warms the host
  once, then times $Runs invocations and reports the median wall-clock and
  derived lines/sec. Fused vs unfused differ ONLY in $env:PSBASH_FUSED.

.PARAMETER PsBash
  Path to the freshly-built ps-bash launcher. Defaults to the Debug build beside
  this repo's src/PsBash.Shell.

.PARAMETER Lines
  Producer size (default 100000).

.PARAMETER Runs
  Timed runs per case (median reported; default 3).
#>
[CmdletBinding()]
param(
    [string]$PsBash = "$PSScriptRoot/../src/PsBash.Shell/bin/Debug/net10.0/ps-bash.exe",
    [int]$Lines = 100000,
    [int]$Runs = 3
)

if (-not (Test-Path $PsBash)) {
    throw "ps-bash launcher not found at $PsBash — build it first: dotnet build src/PsBash.Shell -c Debug -f net10.0"
}
$PsBash = (Resolve-Path $PsBash).Path

# Isolated per-session endpoint so we spawn OUR freshly-built host, never a
# shared installed daemon (which would both skew timing and risk a hang).
$session = [Guid]::NewGuid().ToString('N')

function Invoke-Chain {
    param([string]$Chain, [bool]$Fused)
    $env:PSBASH_SESSION = "bench-$session"
    $env:PSBASH_IPC_ENDPOINT = "pipe:psbash-bench-$session"
    if ($Fused) { Remove-Item Env:PSBASH_FUSED -ErrorAction SilentlyContinue }
    else { $env:PSBASH_FUSED = '0' }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $PsBash
    $psi.ArgumentList.Add('-c'); $psi.ArgumentList.Add($Chain)
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    # Drain both streams so the launcher's per-line writes are actually consumed
    # (the return path we are measuring).
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    [void]$outTask.Result; [void]$errTask.Result
    return $p
}

function Measure-Case {
    param([string]$Name, [string]$Chain, [int]$OutLines)
    foreach ($fused in @($true, $false)) {
        $label = if ($fused) { 'FUSED ' } else { 'unfused' }
        # Warm the host once (cold start is a one-time ~seconds cost, not what we measure).
        Invoke-Chain -Chain $Chain -Fused $fused | Out-Null
        $times = @()
        for ($i = 0; $i -lt $Runs; $i++) {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            Invoke-Chain -Chain $Chain -Fused $fused | Out-Null
            $sw.Stop()
            $times += $sw.Elapsed.TotalSeconds
        }
        $median = ($times | Sort-Object)[[int]([math]::Floor($times.Count / 2))]
        $lps = if ($median -gt 0) { [int]($OutLines / $median) } else { 0 }
        '{0,-22} {1}  median={2,7:N3}s  out_lines={3,7}  {4,10} lines/s' -f `
            $Name, $label, $median, $OutLines, $lps | Write-Host
    }
    Write-Host ''
}

Write-Host "ps-bash: $PsBash"
Write-Host "producer lines: $Lines   timed runs: $Runs   (median reported)`n"

# Output-heavy cases (the profile showed wall-clock tracks OUTPUT-line count):
Measure-Case -Name 'output-heavy: seq|cat' -Chain "seq 1 $Lines | cat"                 -OutLines $Lines
Measure-Case -Name 'sed chain (heavy)'     -Chain "seq 1 $Lines | sed 's/1/X/'"        -OutLines $Lines
Measure-Case -Name 'grep chain (heavy)'    -Chain "seq 1 $Lines | grep 1"              -OutLines ([int]($Lines * 0.41))
# Reduce case (few output lines): the win is small here — proves the win is the
# return path, not the fusion of stages.
Measure-Case -Name 'sed|wc reduce'         -Chain "seq 1 $Lines | sed 's/1/X/' | wc -l" -OutLines 1

# Clean up the isolated env for the caller.
Remove-Item Env:PSBASH_FUSED, Env:PSBASH_SESSION, Env:PSBASH_IPC_ENDPOINT -ErrorAction SilentlyContinue
