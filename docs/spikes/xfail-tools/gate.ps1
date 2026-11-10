# Full local gate: Core.Tests, then spec slices until every spec file has run, then Pester.
param([string]$Tag = '1', [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'psbash-gate'))
$D = $OutDir; New-Item -ItemType Directory -Force $D | Out-Null
Set-Location C:\Users\andyb\work\core\ps-bash
$log = "$D\gate$Tag.log"; $tsv = "$D\spec-results$Tag.tsv"
'' | Set-Content $log; if (Test-Path $tsv) { Clear-Content $tsv }
foreach ($n in 'D', 'S', 'F') { [Environment]::SetEnvironmentVariable($n, [NullString]::Value) }
function Keep($o) { $o | ForEach-Object { "$_" } | Where-Object { $_ -notmatch 'MINIMAX|warning' } | Add-Content $log }
Keep (tman test-proj src/PsBash.Core.Tests 2>&1)
# Shell.Tests and Host.Tests are not release-blocking, but launcher end-to-end regressions only show up there.
Keep (tman test-proj src/PsBash.Shell.Tests 2>&1)
Keep (tman test-proj src/PsBash.Host.Tests 2>&1)
$env:PSBASH_SPEC_BUDGET_MIN = '8'; $env:PSBASH_SPEC_RESULTS = $tsv; $env:PSBASH_SPEC_RESUME = '1'
$env:PSBASH_SPEC_DUMP_XFAIL_JSONL = "$D\xfail$Tag.jsonl"
for ($slice = 1; $slice -le 16; $slice++) {
    Keep (tman test-proj src/PsBash.Spec.Tests --filter 'DisplayName~Spec_file_conforms' 2>&1)
    $rows = Get-Content $tsv | ForEach-Object { $p = $_ -split "`t"; [pscustomobject]@{ v = $p[0]; n = $p[1] } }
    $all = @($rows.n | Sort-Object -Unique).Count
    $ran = @(($rows | Where-Object v -ne 'SKIP').n | Sort-Object -Unique).Count
    Add-Content $log "slice $slice done; files run: $ran of $all"
    if ($all -gt 0 -and $ran -ge $all) { break }
}
foreach ($n in 'PSBASH_SPEC_BUDGET_MIN', 'PSBASH_SPEC_RESULTS', 'PSBASH_SPEC_RESUME', 'PSBASH_SPEC_DUMP_XFAIL_JSONL') { [Environment]::SetEnvironmentVariable($n, [NullString]::Value) }
Keep (tman pester 2>&1 | Select-Object -Last 12)
Add-Content $log 'FINISHED'
