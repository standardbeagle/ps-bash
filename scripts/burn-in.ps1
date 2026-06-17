<#
.SYNOPSIS
  Chaos + burn-in harness for ps-bash -c. Hammers the launcher under abusive
  conditions for a long duration to surface connection/process-lifecycle bugs.

.DESCRIPTION
  Spins up N concurrent worker runspaces that loop forever (until the deadline)
  invoking `ps-bash -c <scenario>` on a SHARED, ISOLATED daemon endpoint, so the
  daemon-reuse + concurrency paths are stressed without fighting other agents'
  Bash tools. A separate "saboteur" runspace periodically kills host processes
  and corrupts socket/lock/sidecar artifacts mid-flight. A monitor runspace
  samples system state, drains results to JSONL, runs a sequential recovery
  probe, and raises a WEDGE ALARM if the system fails to self-heal.

  Success model: under sabotage, the command whose host was just killed MAY fail
  with exit 125 — that is expected. The bug we hunt is a SUSTAINED wedge: the
  sequential recovery probe failing for several cycles with no new sabotage in
  between. That is the metric that separates "chaos noise" from "broken".

.EXAMPLE
  # Short baseline against the installed build to confirm the harness repros:
  pwsh -File scripts/burn-in.ps1 -Launcher "$env:USERPROFILE\.local\bin\ps-bash.exe" -DurationMinutes 12 -Tag baseline

.EXAMPLE
  # Overnight run against a freshly-built launcher:
  pwsh -File scripts/burn-in.ps1 -Launcher "src/PsBash.Shell/bin/Release/net10.0/win-x64/publish/ps-bash.exe" -DurationMinutes 480 -Workers 24 -Tag overnight
#>
[CmdletBinding()]
param(
    [string]$Launcher = "$env:USERPROFILE\.local\bin\ps-bash.exe",
    [int]$DurationMinutes = 30,
    [int]$Workers = 24,
    [double]$SabotageIntervalSec = 7,
    [string]$Tag = "burnin",
    [string]$OutDir = "$PSScriptRoot\..\artifacts\burn-in",
    # Sequential recovery-probe failures (consecutive monitor cycles, no new
    # sabotage between them) that constitute a WEDGE. 3 cycles ≈ 45-90s stuck.
    [int]$WedgeCycles = 3,
    [switch]$NoSabotage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Launcher = (Resolve-Path $Launcher).Path
$hostBinary = Join-Path (Split-Path $Launcher) 'ps-bash-host.exe'
if (-not (Test-Path $hostBinary)) { throw "ps-bash-host.exe not found beside launcher at $hostBinary" }

# Isolated, shared endpoint: a fixed unix socket under the ps-bash temp dir so
# (a) every worker shares ONE daemon (stresses reuse + concurrency), and
# (b) we never touch the canonical per-session daemon other agents use, and
# (c) the saboteur can delete the .sock / .lock / .host.json by path.
$tempPsbash = Join-Path $env:TEMP 'ps-bash'
New-Item -ItemType Directory -Path $tempPsbash -Force | Out-Null
$socketPath = Join-Path $tempPsbash "burnin-$Tag.sock"
$endpoint   = "unix:$socketPath"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$stamp      = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir     = Join-Path $OutDir "$Tag-$stamp"
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$jsonlPath  = Join-Path $runDir 'results.jsonl'
$summaryPath= Join-Path $runDir 'summary.txt'
$alarmPath  = Join-Path $runDir 'WEDGE-ALARM.txt'
$eventsPath = Join-Path $runDir 'events.log'

Write-Host "=== ps-bash burn-in ===" -ForegroundColor Cyan
Write-Host "  launcher : $Launcher"
Write-Host "  host     : $hostBinary"
Write-Host "  endpoint : $endpoint"
Write-Host "  workers  : $Workers   duration: $DurationMinutes min   sabotage: $(if($NoSabotage){'OFF'}else{"every ~$SabotageIntervalSec s"})"
Write-Host "  out      : $runDir"

# ---- clean slate on our endpoint only -------------------------------------
Get-Process ps-bash-host -ErrorAction SilentlyContinue |
    Where-Object { try { $_.CommandLine -like "*$socketPath*" } catch { $false } } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-ChildItem $tempPsbash -Filter "burnin-$Tag.sock*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $tempPsbash -Filter "spawn-*$Tag*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# ---- shared state ----------------------------------------------------------
$sync = [hashtable]::Synchronized(@{})
$sync.Results          = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()
$sync.Events           = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()
$sync.Stop             = $false
$sync.Deadline         = (Get-Date).AddMinutes($DurationMinutes)
$sync.Launcher         = $Launcher
$sync.HostBinary       = $hostBinary
$sync.Endpoint         = $endpoint
$sync.SocketPath       = $socketPath
$sync.TempPsbash       = $tempPsbash
$sync.Tag              = $Tag
$sync.LastSabotageUtc  = [DateTime]::UtcNow
$sync.SabotageCount    = 0

# ---- the launcher invoker (System.Diagnostics.Process for fine control) ----
$invokeBody = {
    param($sync, $bashCmd, $stdinBytes, $brokenPipe, $timeoutMs)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName  = $sync.Launcher
    $psi.ArgumentList.Add('-c'); $psi.ArgumentList.Add($bashCmd)
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.RedirectStandardInput  = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow  = $true
    $psi.EnvironmentVariables['PSBASH_IPC_ENDPOINT'] = $sync.Endpoint
    # keep idle timeout finite so a genuinely-hung host trips it (and so a
    # `sleep` scenario doesn't pin a worker forever)
    $psi.EnvironmentVariables['PSBASH_TIMEOUT'] = '30'

    $p = [System.Diagnostics.Process]::new()
    $p.StartInfo = $psi
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $null = $p.Start()

    # stdin
    try {
        if ($null -ne $stdinBytes) { $p.StandardInput.BaseStream.Write($stdinBytes, 0, $stdinBytes.Length) }
        $p.StandardInput.Close()
    } catch { }

    if ($brokenPipe) {
        # read a little then abort the launcher mid-stream -> RST to the host
        try { $buf = [char[]]::new(256); $null = $p.StandardOutput.Read($buf, 0, 256) } catch { }
        Start-Sleep -Milliseconds (Get-Random -Minimum 5 -Maximum 60)
        try { $p.Kill($true) } catch { }
        try { $p.WaitForExit(2000) | Out-Null } catch { }
        $sw.Stop()
        return [pscustomobject]@{ Exit = -999; OutLen = 0; Err = ''; Ms = $sw.ElapsedMilliseconds; Killed = $true }
    }

    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($timeoutMs)) {
        try { $p.Kill($true) } catch { }
        try { $p.WaitForExit(2000) | Out-Null } catch { }
        $sw.Stop()
        return [pscustomobject]@{ Exit = -124; OutLen = 0; Err = 'harness-timeout'; Ms = $sw.ElapsedMilliseconds; Killed = $true }
    }
    $sw.Stop()
    $out = ''; $err = ''
    try { $out = $outTask.GetAwaiter().GetResult() } catch { }
    try { $err = $errTask.GetAwaiter().GetResult() } catch { }
    if ($err.Length -gt 400) { $err = $err.Substring(0,400) }
    return [pscustomobject]@{ Exit = $p.ExitCode; OutLen = $out.Length; Err = $err; Ms = $sw.ElapsedMilliseconds; Killed = $false }
}

# ---- scenario catalog: label, cmd, stdin, expectedExit, flags --------------
# expectedExit = $null  means "any exit except a transport failure (125/-124)"
$scenarioBody = {
    param($rng)
    $big = ([string]('x' * 1024))  # 1KB unit
    switch (Get-Random -Minimum 0 -Maximum 16) {
        0  { @{ L='trivial';     C='echo alive';                                 In=$null; Exp=0 } }
        1  { @{ L='nonzero';     C='exit 7';                                      In=$null; Exp=7 } }
        2  { @{ L='false';       C='false';                                       In=$null; Exp=1 } }
        3  { @{ L='bigstdout';   C='seq 1 200000';                                In=$null; Exp=0 } }
        4  { @{ L='hugestdout';  C='seq 1 1000000 | wc -l';                       In=$null; Exp=0 } }
        5  { @{ L='pipeline';    C='seq 1 100000 | grep 7 | wc -l';               In=$null; Exp=0 } }
        6  { @{ L='bigstdin';    C='wc -c';   In=[Text.Encoding]::UTF8.GetBytes($big * 10240); Exp=0 } }  # ~10MB
        7  { @{ L='binstdin';    C='wc -c';   In=[byte[]](1..4096 | ForEach-Object { [byte](Get-Random -Maximum 256) }); Exp=0 } }
        8  { @{ L='unicode';     C='cat';     In=[byte[]](([byte[]][Text.Encoding]::UTF8.GetPreamble()) + [Text.Encoding]::UTF8.GetBytes("hello world emoji " + [char]::ConvertFromUtf32(0x1F30D) + " combining " + [char]0x0065 + [char]0x0301 + "`r`n")); Exp=0 } }
        9  { @{ L='slowquiet';   C='sleep 2; echo done';                          In=$null; Exp=0 } }
        10 { @{ L='brokenpipe';  C='seq 1 5000000';                              In=$null; Exp=$null; Broken=$true } }
        11 { @{ L='parseerr';    C='if [ ; then echo x';                          In=$null; Exp=$null } }
        12 { @{ L='deepsub';     C='echo $(echo $(echo $(echo $(echo hi))))';     In=$null; Exp=0 } }
        13 { @{ L='env+redir';   C='X=1 Y=2; echo $X$Y > /dev/null; echo ok';     In=$null; Exp=0 } }
        14 { @{ L='manyargs';    C=('echo ' + (1..200 -join ' '));                In=$null; Exp=0 } }
        15 { @{ L='heredoc';     C="cat <<EOF`nline1`nline2 with `$notvar`nEOF";   In=$null; Exp=0 } }
    }
}

# ---- worker runspace -------------------------------------------------------
$workerScript = {
    param($sync, $id, $invokeStr, $scenarioStr)
    $invokeBody = [scriptblock]::Create($invokeStr)
    $scenarioBody = [scriptblock]::Create($scenarioStr)
    $rng = [Random]::new($id * 7919 + 13)
    while (-not $sync.Stop -and (Get-Date) -lt $sync.Deadline) {
        $s = & $scenarioBody $rng
        $broken = $s.ContainsKey('Broken') -and $s.Broken
        $r = & $invokeBody $sync $s.C $s.In $broken 35000
        # classify
        $ok = $false; $class = 'other'
        if ($broken) {
            # broken pipe: success = launcher was killed and host stayed up (no wedge fallout here)
            $ok = $true; $class = 'brokenpipe'
        } elseif ($r.Exit -eq 125) {
            $class = 'transport125'      # host comm failed (reset, no recovery)
        } elseif ($r.Exit -eq 124) {
            $class = 'idletimeout124'    # launcher waited the full idle timeout then gave up
        } elseif ($r.Exit -eq -124) {
            $class = 'harnesstimeout'    # launcher itself hung past the harness kill deadline
        } elseif ($null -eq $s.Exp) {
            $ok = $true; $class = 'tolerated'   # parse error etc: any non-transport exit is fine
        } elseif ($r.Exit -eq $s.Exp) {
            $ok = $true; $class = 'pass'
        } else {
            $class = 'wrongexit'
        }
        $sync.Results.Enqueue([pscustomobject]@{
            t = (Get-Date).ToString('o'); w = $id; scn = $s.L; exit = $r.Exit
            exp = $s.Exp; ms = $r.Ms; outLen = $r.OutLen; ok = $ok; class = $class
            err = ($r.Err -replace "`r?`n",' ¶ ')
        })
    }
}

# ---- saboteur runspace -----------------------------------------------------
$saboteurScript = {
    param($sync)
    $rng = [Random]::new(424242)
    while (-not $sync.Stop -and (Get-Date) -lt $sync.Deadline) {
        Start-Sleep -Milliseconds (([int]($sync.SabInterval * 1000)) + $rng.Next(-1500, 1500))
        if ($sync.Stop) { break }
        $hosts = @(Get-Process ps-bash-host -ErrorAction SilentlyContinue |
            Where-Object { try { $_.CommandLine -like "*$($sync.SocketPath)*" } catch { $false } })
        $act = $rng.Next(0, 6)
        $desc = ''
        try {
            switch ($act) {
                0 { if ($hosts.Count) { $h = $hosts[$rng.Next(0,$hosts.Count)]; $h | Stop-Process -Force -ErrorAction SilentlyContinue; $desc = "kill-one pid=$($h.Id)" } else { $desc = 'kill-one (none)' } }
                1 { $hosts | Stop-Process -Force -ErrorAction SilentlyContinue; $desc = "kill-all n=$($hosts.Count)" }
                2 { if (Test-Path $sync.SocketPath) { Remove-Item $sync.SocketPath -Force -ErrorAction SilentlyContinue; $desc = 'rm .sock' } else { $desc = 'rm .sock (absent)' } }
                3 { $sc = $sync.SocketPath + '.host.json'; if (Test-Path $sc) { Remove-Item $sc -Force -ErrorAction SilentlyContinue; $desc = 'rm sidecar' } else { $desc = 'rm sidecar (absent)' } }
                4 { Get-ChildItem $sync.TempPsbash -Filter 'spawn-*.lock' -ErrorAction SilentlyContinue | Get-Random -Count 1 -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue; $desc = 'rm a spawn lock' }
                5 { if (Test-Path $sync.SocketPath) { try { [IO.File]::WriteAllText($sync.SocketPath, 'garbage') } catch {}; $desc = 'corrupt .sock' } else { $desc = 'corrupt .sock (absent)' } }
            }
        } catch { $desc = "sabotage-error: $_" }
        $sync.LastSabotageUtc = [DateTime]::UtcNow
        $sync.SabotageCount++
        $sync.Events.Enqueue([pscustomobject]@{ t=(Get-Date).ToString('o'); kind='SABOTAGE'; desc=$desc })
    }
}

# ---- monitor runspace: drain results, sample, recovery probe, wedge alarm --
$monitorScript = {
    param($sync, $invokeStr, $jsonlPath, $summaryPath, $alarmPath, $eventsPath, $wedgeCycles)
    $invokeBody = [scriptblock]::Create($invokeStr)
    # counters live in a hashtable so the inline drain mutates members by
    # reference — no $script: scope games inside a runspace-created scriptblock.
    $st = @{ total = 0; ok = 0; counts = @{} }
    $probeFailStreak = 0
    $sabAtLastProbe = -1
    $wedged = $false
    $start = Get-Date
    $drain = {
        try {
            $sb = [System.Text.StringBuilder]::new()
            $item = $null
            while ($sync.Results.TryDequeue([ref]$item)) {
                [void]$sb.AppendLine(($item | ConvertTo-Json -Compress))
                $st.total++
                if ($item.ok) { $st.ok++ }
                $c = [string]$item.class
                if (-not $st.counts.ContainsKey($c)) { $st.counts[$c] = 0 }
                $st.counts[$c]++
            }
            if ($sb.Length) { Add-Content -Path $jsonlPath -Value $sb.ToString().TrimEnd() }
            $ev = $null
            while ($sync.Events.TryDequeue([ref]$ev)) { Add-Content -Path $eventsPath -Value ($ev | ConvertTo-Json -Compress) }
        } catch {
            Add-Content -Path (Join-Path (Split-Path $jsonlPath) 'monitor-errors.log') "drain: $_ | $($_.ScriptStackTrace)"
        }
    }
    while (-not $sync.Stop -and (Get-Date) -lt $sync.Deadline) {
        Start-Sleep -Seconds 15
        & $drain
        $total = $st.total; $okTotal = $st.ok; $counts = $st.counts
        # sequential recovery probe: one clean echo, off the worker pool
        $probe = & $invokeBody $sync 'echo __probe__' $null $false 30000
        $probeOk = ($probe.Exit -eq 0)
        $newSabotage = ($sync.SabotageCount -ne $sabAtLastProbe)
        $sabAtLastProbe = $sync.SabotageCount
        if ($probeOk) {
            $probeFailStreak = 0
        } else {
            # only count toward a wedge if no fresh sabotage happened this cycle
            if (-not $newSabotage) { $probeFailStreak++ } else { $probeFailStreak = 1 }
        }
        $hostCount = @(Get-Process ps-bash-host -ErrorAction SilentlyContinue |
            Where-Object { try { $_.CommandLine -like "*$($sync.SocketPath)*" } catch { $false } }).Count
        $allHosts  = @(Get-Process ps-bash-host -ErrorAction SilentlyContinue)
        $rss = if ($allHosts.Count) { [math]::Round((($allHosts | Measure-Object WorkingSet64 -Sum).Sum)/1MB,1) } else { 0 }
        $sockN = @(Get-ChildItem $sync.TempPsbash -Filter '*.sock' -ErrorAction SilentlyContinue).Count
        $lockN = @(Get-ChildItem $sync.TempPsbash -Filter '*.lock' -ErrorAction SilentlyContinue).Count
        $sideN = @(Get-ChildItem $sync.TempPsbash -Filter '*.host.json' -ErrorAction SilentlyContinue).Count

        $elapsed = [int]((Get-Date) - $start).TotalSeconds
        $lines = @()
        $lines += "ps-bash burn-in  tag=$($sync.Tag)  elapsed=${elapsed}s  deadline=$($sync.Deadline.ToString('HH:mm:ss'))"
        $lines += "invocations=$total  ok=$okTotal  okRate=$(if($total){[math]::Round(100*$okTotal/$total,2)}else{0})%"
        $lines += "sabotage=$($sync.SabotageCount)  recoveryProbe=$(if($probeOk){'OK'}else{"FAIL(streak=$probeFailStreak)"})"
        $lines += "hosts(ours)=$hostCount  hosts(all)=$($allHosts.Count)  rssMB=$rss  artifacts: sock=$sockN lock=$lockN sidecar=$sideN"
        $lines += "by-class:"
        foreach ($k in ($counts.Keys | Sort-Object)) { $lines += ("  {0,-16} {1}" -f $k, $counts[$k]) }
        Set-Content -Path $summaryPath -Value ($lines -join "`n")

        if (-not $wedged -and $probeFailStreak -ge $wedgeCycles) {
            $wedged = $true
            $diag = @()
            $diag += "WEDGE DETECTED at $(Get-Date -Format o)"
            $diag += "recovery probe failed $probeFailStreak consecutive cycles with no fresh sabotage in between."
            $diag += "last probe exit=$($probe.Exit) err=$($probe.Err)"
            $diag += "hosts(ours)=$hostCount hosts(all)=$($allHosts.Count)"
            $diag += "--- host.log tail ---"
            try { $diag += (Get-Content "$env:USERPROFILE\.psbash\host.log" -Tail 25) } catch { $diag += "(no host.log)" }
            $diag += "--- our host processes ---"
            try { $diag += (Get-CimInstance Win32_Process -Filter "Name='ps-bash-host.exe'" | Where-Object { $_.CommandLine -like "*$($sync.SocketPath)*" } | ForEach-Object { "pid=$($_.ProcessId) $($_.CommandLine)" }) } catch {}
            Add-Content -Path $alarmPath -Value ($diag -join "`n")
            $sync.Events.Enqueue([pscustomobject]@{ t=(Get-Date).ToString('o'); kind='WEDGE'; desc="probeFailStreak=$probeFailStreak" })
        }
    }
    & $drain
}

# ---- launch all runspaces --------------------------------------------------
$sync.SabInterval = $SabotageIntervalSec
$pool = [runspacefactory]::CreateRunspacePool(1, $Workers + 4)
$pool.Open()
$handles = [System.Collections.Generic.List[object]]::new()

function Start-RS([scriptblock]$sb, [object[]]$rsArgs) {
    $ps = [powershell]::Create(); $ps.RunspacePool = $pool
    [void]$ps.AddScript($sb.ToString())
    foreach ($a in $rsArgs) { [void]$ps.AddArgument($a) }
    $handles.Add([pscustomobject]@{ PS=$ps; Async=$ps.BeginInvoke() })
}

# monitor + saboteur first. Helper bodies are passed as STRINGS and rebuilt
# inside each runspace ([scriptblock]::Create) — invoking a parent-runspace
# scriptblock across a runspace boundary is unreliable.
$invokeStr = $invokeBody.ToString()
$scenarioStr = $scenarioBody.ToString()
Start-RS $monitorScript @($sync, $invokeStr, $jsonlPath, $summaryPath, $alarmPath, $eventsPath, $WedgeCycles)
if (-not $NoSabotage) { Start-RS $saboteurScript @($sync) }
for ($i = 1; $i -le $Workers; $i++) { Start-RS $workerScript @($sync, $i, $invokeStr, $scenarioStr) }

Write-Host "Running until $($sync.Deadline.ToString('yyyy-MM-dd HH:mm:ss')) ... (summary: $summaryPath)" -ForegroundColor Green

# wait for the deadline, then signal stop and reap
while ((Get-Date) -lt $sync.Deadline) { Start-Sleep -Seconds 5 }
$sync.Stop = $true
Start-Sleep -Seconds 3
$rsErrPath = Join-Path $runDir 'runspace-errors.log'
foreach ($h in $handles) {
    try { $h.PS.EndInvoke($h.Async) } catch { Add-Content $rsErrPath "EndInvoke threw: $_" }
    try {
        foreach ($e in $h.PS.Streams.Error) { Add-Content $rsErrPath ("ERR: " + $e.ToString() + " | " + $e.ScriptStackTrace) }
    } catch {}
    try { $h.PS.Dispose() } catch {}
}
$pool.Close(); $pool.Dispose()

# Final orchestrator-side drain: workers may have enqueued results after the
# monitor's last drain returned.
$sb = [System.Text.StringBuilder]::new(); $item = $null
while ($sync.Results.TryDequeue([ref]$item)) { [void]$sb.AppendLine(($item | ConvertTo-Json -Compress)) }
if ($sb.Length) { Add-Content -Path $jsonlPath -Value $sb.ToString().TrimEnd() }
$ev = $null
while ($sync.Events.TryDequeue([ref]$ev)) { Add-Content -Path $eventsPath -Value ($ev | ConvertTo-Json -Compress) }

# ---- final report ----------------------------------------------------------
$results = @(Get-Content $jsonlPath -ErrorAction SilentlyContinue | ForEach-Object { $_ | ConvertFrom-Json })
$byClass = $results | Group-Object class | Sort-Object Count -Descending
$report = @()
$report += "================ ps-bash burn-in FINAL REPORT ================"
$report += "tag=$Tag  launcher=$Launcher"
$report += "duration=${DurationMinutes}min  workers=$Workers  sabotageEvents=$($sync.SabotageCount)"
$report += "total invocations=$($results.Count)  ok=$(@($results|Where-Object ok).Count)"
$report += "transport125=$(@($results|Where-Object {$_.class -eq 'transport125'}).Count)  idletimeout124=$(@($results|Where-Object {$_.class -eq 'idletimeout124'}).Count)  wrongexit=$(@($results|Where-Object {$_.class -eq 'wrongexit'}).Count)  harnesstimeout=$(@($results|Where-Object {$_.class -eq 'harnesstimeout'}).Count)"
$report += "WEDGE ALARMS: $(if(Test-Path $alarmPath){'YES — see WEDGE-ALARM.txt'}else{'none'})"
$report += "by class:"
foreach ($g in $byClass) { $report += ("  {0,-16} {1}" -f $g.Name, $g.Count) }
$report += "=============================================================="
$reportText = $report -join "`n"
Set-Content -Path (Join-Path $runDir 'FINAL-REPORT.txt') -Value $reportText
Write-Host $reportText -ForegroundColor Cyan
