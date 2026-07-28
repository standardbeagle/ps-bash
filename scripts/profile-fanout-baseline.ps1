#!/usr/bin/env pwsh
<#
.SYNOPSIS
  S0 baseline for the fan-out epic (worktrack 01KYKGJZ1G170JP69ZKVR5K4F9, epic
  01KYKGFBY4WCHRNZ10R650H57B). Measures the per-line PSObject fan-out cost and
  DEMONSTRATES the two structural gaps the epic exists to close.

.DESCRIPTION
  Complements scripts/bench-pipeline.ps1, which only benches all-mapped chains
  that already fuse. This script covers the three shapes the epic cares about:

    1. read/filter/sort  `cat f | grep x | sort`   - the ~90% real-world shape.
       Fusion (phase-2a batching) engages, but the phase-2b STREAMING lane
       declines because `sort` has no core, so every line is still a PSObject.
    2. pure producer      `cat f`                  - producer fan-out alone.
    3. foreign producer   `Get-ChildItem | grep x` - an unmapped PowerShell
       producer. Fusion requires EVERY stage allowlisted, so this shape never
       reaches Invoke-BashFusedPipeline at all and gets neither batching nor
       streaming.

  Three sections, each emitting evidence rather than a claim:

    A. REGISTRY COVERAGE - calls LineStreamRegistry.TryCreate for every name in
       PsEmitter.FusePipelineAllowlist and reports which DECLINE. This is the
       direct proof of which stage kills a chain's streaming lane.
    B. EMITTED POWERSHELL - transpiles each shape and shows whether
       Invoke-BashFusedPipeline appears (and whether it carries -Stages).
    C. THROUGHPUT - end-to-end wall clock via `ps-bash -c`, fused vs unfused,
       reported as median seconds and derived lines/sec.

  QUIET BOX REQUIRED. Concurrent builds sharing src/*/bin outputs are the
  documented root cause of this repo's suite flakiness and will corrupt every
  number here. The script refuses to run if it sees a build in progress.

.PARAMETER Lines
  Producer size for the throughput section (default 100000).

.PARAMETER Runs
  Timed runs per case; median reported (default 3).

.PARAMETER SkipQuietCheck
  Bypass the concurrent-build guard. Only for debugging the script itself -
  numbers gathered this way must NOT be recorded as a baseline.
#>
[CmdletBinding()]
param(
    [string]$PsBash    = "$PSScriptRoot/../src/PsBash.Shell/bin/Debug/net10.0/ps-bash.exe",
    [string]$BinDir    = "$PSScriptRoot/../src/PsBash.Cmdlets/bin/Debug/net10.0",
    [string]$TranspDir = "$PSScriptRoot/../src/PsBash.Transpiler/bin/Debug/net10.0",
    [int]$Lines = 100000,
    [int]$Runs  = 3,
    [switch]$SkipQuietCheck
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- quiet guard
if (-not $SkipQuietCheck) {
    # VBCSCompiler is deliberately NOT in this list: the Roslyn compiler server
    # lingers idle for minutes after any build, so treating it as "busy" would
    # make the guard fire on every quiet box right after a build. MSBuild and
    # testhost only exist while work is actually in flight.
    $busy = @(Get-Process -Name MSBuild, testhost -ErrorAction SilentlyContinue)
    if ($busy.Count -gt 0) {
        $names = ($busy | Select-Object -ExpandProperty Name -Unique) -join ', '
        throw ("Build/test processes are running ($names). Every number here would be " +
               "contaminated - see the concurrent-build flakiness root cause. " +
               "Wait for a quiet box, or pass -SkipQuietCheck for a throwaway run.")
    }
}

foreach ($p in @($PsBash, $BinDir, $TranspDir)) {
    if (-not (Test-Path $p)) {
        throw "Not found: $p - build first: dotnet build src/PsBash.Shell -c Debug -f net10.0"
    }
}
$PsBash = (Resolve-Path $PsBash).Path

Write-Host "=== S0 fan-out baseline ===" -ForegroundColor Cyan
Write-Host "host        : $env:COMPUTERNAME"
Write-Host "utc         : $([DateTime]::UtcNow.ToString('o'))"
Write-Host "ps-bash     : $PsBash"
Write-Host "producer    : $Lines lines, $Runs timed runs (median)"
Write-Host ""

# ------------------------------------------------- A. streaming-core coverage
Write-Host "--- A. streaming-core coverage (which stage kills the streaming lane) ---" -ForegroundColor Yellow

Add-Type -Path (Join-Path $BinDir       'PsBash.Cmdlets.dll')    | Out-Null
Add-Type -Path (Join-Path $TranspDir    'PsBash.Transpiler.dll') | Out-Null

# NOTE: the parser/emitter moved into the PsBash.Transpiler *project* but kept
# their original PsBash.Core.* namespaces - assembly name and namespace differ.
$asm       = [System.Reflection.Assembly]::LoadFrom((Join-Path $TranspDir 'PsBash.Transpiler.dll'))
$emitter   = $asm.GetType('PsBash.Core.Parser.PsEmitter', $true)
$allowField = $emitter.GetField('FusePipelineAllowlist',
                  [System.Reflection.BindingFlags]::NonPublic -bor
                  [System.Reflection.BindingFlags]::Static)
if (-not $allowField) { throw 'FusePipelineAllowlist not found on PsEmitter - did the field move? (S5 extracts it to FusedLane.cs)' }
$allow = @($allowField.GetValue($null)) | Sort-Object

# A representative benign argv per command, so a decline means "no core at all"
# rather than "argv outside the certified subset".
$argvFor = @{
    cat = @(); grep = @('x'); sed = @('s/a/b/'); head = @('-n','5'); tail = @('-n','5')
    wc = @('-l'); sort = @(); uniq = @(); tr = @('a','b'); cut = @('-f1')
    seq = @('1','3'); rev = @(); tac = @(); nl = @()
}

$registry = [PsBash.Cmdlets.LineStreamRegistry]
$withCore = @(); $declines = @()
foreach ($name in $allow) {
    $argv = [string[]]($argvFor[$name] ?? @())
    $stage = $null
    $ok = $false
    try { $ok = $registry::TryCreate($name, $argv, [ref]$stage) } catch { $ok = $false }
    if ($ok) { $withCore += $name } else { $declines += $name }
    '{0,-6} {1}' -f $name, $(if ($ok) { 'has core' } else { 'DECLINES -> whole chain falls back to per-line PSObject' }) | Write-Host
}
Write-Host ""
Write-Host ("allowlisted: {0}   with core: {1}   DECLINING: {2}" -f $allow.Count, $withCore.Count, $declines.Count)
Write-Host ("declining stages: {0}" -f ($declines -join ', ')) -ForegroundColor Red
Write-Host ""

# ------------------------------------------------------- B. emitted PowerShell
Write-Host "--- B. emitted PowerShell (does the shape reach the fused lane at all?) ---" -ForegroundColor Yellow

$transpiler = $asm.GetType('PsBash.Core.Transpiler.BashTranspiler', $true)
$shapes = [ordered]@{
    'read/filter/sort' = 'cat f.txt | grep x | sort'
    'pure producer'    = 'cat f.txt'
    'foreign producer' = 'Get-ChildItem | grep x'
}
foreach ($k in $shapes.Keys) {
    $ps = $transpiler::Transpile($shapes[$k])
    $fused   = $ps -match 'Invoke-BashFusedPipeline'
    $staged  = $ps -match '-Stages'
    Write-Host ("[{0}]  bash: {1}" -f $k, $shapes[$k])
    Write-Host ("   emitted : {0}" -f $ps.Trim())
    Write-Host ("   fused   : {0}   -Stages (streaming attempted): {1}" -f $fused, $staged)
    Write-Host ""
}

# ------------------------------------------------------------- C. throughput
Write-Host "--- C. end-to-end throughput ---" -ForegroundColor Yellow

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-s0-$([Guid]::NewGuid().ToString('N')).txt"
try {
    $sb = [System.Text.StringBuilder]::new()
    for ($i = 1; $i -le $Lines; $i++) { [void]$sb.AppendLine("line $i x") }
    [System.IO.File]::WriteAllText($tmp, $sb.ToString())
    $bashPath = $tmp -replace '\\', '/'

    $session = [Guid]::NewGuid().ToString('N')
    function Invoke-Chain {
        param([string]$Chain, [bool]$Fused)
        $env:PSBASH_SESSION      = "s0-$session"
        $env:PSBASH_IPC_ENDPOINT = "pipe:psbash-s0-$session"
        if ($Fused) { Remove-Item Env:PSBASH_FUSED -ErrorAction SilentlyContinue }
        else        { $env:PSBASH_FUSED = '0' }

        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $PsBash
        $psi.ArgumentList.Add('-c'); $psi.ArgumentList.Add($Chain)
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError  = $true
        $psi.UseShellExecute = $false
        $p = [System.Diagnostics.Process]::Start($psi)
        $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
        $p.WaitForExit()
        [void]$o.Result; [void]$e.Result
    }

    function Measure-Shape {
        param([string]$Name, [string]$Chain, [int]$OutLines)
        foreach ($fused in @($true, $false)) {
            $label = if ($fused) { 'FUSED  ' } else { 'unfused' }
            Invoke-Chain -Chain $Chain -Fused $fused   # warm
            $times = @()
            for ($i = 0; $i -lt $Runs; $i++) {
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                Invoke-Chain -Chain $Chain -Fused $fused
                $sw.Stop(); $times += $sw.Elapsed.TotalSeconds
            }
            $median = ($times | Sort-Object)[[int][math]::Floor($times.Count / 2)]
            $lps = if ($median -gt 0) { [int]($OutLines / $median) } else { 0 }
            '{0,-20} {1}  median={2,7:N3}s  out={3,7}  {4,10} lines/s' -f `
                $Name, $label, $median, $OutLines, $lps | Write-Host
        }
        Write-Host ''
    }

    Measure-Shape -Name 'read/filter/sort' -Chain "cat $bashPath | grep x | sort" -OutLines $Lines
    Measure-Shape -Name 'pure producer'    -Chain "cat $bashPath"                 -OutLines $Lines
    Measure-Shape -Name 'foreign producer' -Chain "Get-ChildItem | grep -c ''"    -OutLines 1
}
finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
    Remove-Item Env:PSBASH_FUSED, Env:PSBASH_SESSION, Env:PSBASH_IPC_ENDPOINT -ErrorAction SilentlyContinue
}

Write-Host "=== end ===" -ForegroundColor Cyan
