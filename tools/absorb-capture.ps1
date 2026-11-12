# Merges scripts captured from the Differential.Tests oracle calls (PSBASH_ORACLE_CAPTURE, see OracleCapture.cs) into
# spec/differential/*.spec.sh. Existing cases (and their recorded expectations) are kept; only unseen scripts are appended.
param([Parameter(Mandatory)][string]$Capture, [string]$Root = (Resolve-Path "$PSScriptRoot/..").Path)
$out = Join-Path $Root 'spec/differential'
New-Item -ItemType Directory -Force $out | Out-Null
$rows = Get-Content $Capture -Encoding utf8 | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }
$added = 0; $dups = 0; $hostBound = 0
foreach ($g in $rows | Group-Object class) {
    $cls = ($g.Name -replace 'DifferentialTests$|Tests$', '').ToLower()
    $file = Join-Path $out "$cls.spec.sh"
    $existing = if (Test-Path $file) { [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8) } else { "## Absorbed from Differential.Tests/$($g.Name).cs (scripts only; expectations recorded from bash 5.2).`n" }
    $seen = @{}; $new = [System.Text.StringBuilder]::new(); $n = @{}
    foreach ($r in $g.Group) {
        $script = $r.script.TrimEnd("`n")
        # A Windows drive path is a file the TEST wrote before running the script (NativeStdioBytesTests' helper under
        # %TEMP%\psb_nsb_<pid>): a spec case cannot recreate it, bash under WSL cannot run it, and the path names one
        # process on one machine. Such a case recorded pwsh's usage text as "bash's" output and XPASSed elsewhere.
        if ($script -cmatch '(?<![\w/])[A-Z]:[\\/]') { $hostBound++; continue }
        if ($seen.ContainsKey($script) -or $script -match '(?m)^#### |^## ' -or $existing.Contains("`n$script`n") -or $existing.Contains("`n$script`r`n")) { $dups++; continue }
        $seen[$script] = 1
        $n[$r.method] = 1 + [int]$n[$r.method]
        $name = if ($n[$r.method] -gt 1) { "$($r.method) row $($n[$r.method])" } else { $r.method }
        [void]$new.Append("`n#### $name`n$script`n"); $added++
    }
    if ($new.Length -gt 0) { [IO.File]::WriteAllText($file, $existing + $new.ToString(), [Text.UTF8Encoding]::new($false)) }
}
"captured: $($rows.Count), added: $added, already present/dup: $dups, skipped (host path): $hostBound"
