param([string]$Jsonl, [string]$Out)
# Classify every known-gap case by failure signature; writes a TSV and prints the cluster sizes.
$tier3 = 'builtin-completion|builtin-fc|builtin-history|builtin-bind|^interactive|prompt|history-expand|ble-|interactive-parse'
$rows = foreach ($line in [IO.File]::ReadLines($Jsonl)) {
    if (-not $line.Trim()) { continue }
    $r = $line | ConvertFrom-Json
    $f = $r.file -replace '^oils/|\.spec\.sh$', ''
    $err = [string]$r.actStderr
    $so = ([string]$r.actStdout) -replace "`r", ''
    $eo = ([string]$r.expStdout) -replace "`r", ''
    $stdoutOk = ($null -eq $r.expStdout) -or ($so.TrimEnd("`n") -eq $eo.TrimEnd("`n"))
    $statusOk = ($null -eq $r.expStatus) -or ($r.actStatus -eq $r.expStatus)
    $sig =
        if ($null -eq $r.actStatus) { 'no-result' }
        elseif ($r.actStatus -eq 124 -or $err -match 'timed out') { 'timeout' }
        elseif ($err -match 'parse error|ParseException|syntax error near|Unexpected token') { if ($r.expStatus -in 2, 1 -and $eo -eq '') { 'parse-error-wrong-shape' } else { 'parse-error-on-valid-bash' } }
        elseif ($err -match 'Cannot (convert|bind|find|index|process)|You cannot call a method|is not recognized as (a name|the name)|Exception|Index was outside|Object reference not set|The term |Unable to index|op_|ParameterBinding|at line:\d+') { 'powershell-error-leak' }
        elseif ($stdoutOk -and -not $statusOk) { "status-only(exp=$($r.expStatus),act=$($r.actStatus))" }
        elseif (-not $stdoutOk -and $statusOk) { if ($so -eq '') { 'stdout-empty' } elseif ($eo -eq '') { 'stdout-unexpected' } else { 'stdout-differs' } }
        elseif ($stdoutOk -and $statusOk) { 'stderr-or-flaky(now-matches)' }
        else { 'stdout+status' }
    $firstErr = (($err -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 1)
    if ($firstErr) { $firstErr = ($firstErr -replace '\d+', 'N' -replace "'[^']*'", "'X'" -replace '"[^"]*"', '"X"').Trim(); if ($firstErr.Length -gt 90) { $firstErr = $firstErr.Substring(0, 90) } }
    [pscustomobject]@{
        file = $f; case = $r.case; id = $r.id; tier3 = [bool]($f -match $tier3); sig = $sig
        expStatus = $r.expStatus; actStatus = $r.actStatus; err = $firstErr; lines = (($r.script -split "`n").Count)
    }
}
$rows | Export-Csv -Path $Out -Delimiter "`t" -NoTypeInformation -Encoding utf8
"cases: $(@($rows).Count)   tier3-by-file: $(@($rows | Where-Object tier3).Count)"
'--- by signature (non-tier3)'
$rows | Where-Object { -not $_.tier3 } | Group-Object { $_.sig -replace '\(.*', '' } | Sort-Object Count -Descending | ForEach-Object { '{0,4} {1}' -f $_.Count, $_.Name }
'--- top stderr first-lines (non-tier3)'
$rows | Where-Object { -not $_.tier3 -and $_.err } | Group-Object err | Sort-Object Count -Descending | Select-Object -First 45 | ForEach-Object { '{0,4} {1}' -f $_.Count, $_.Name }
