# ---------------------------------------------------------------------------
# The payoff of the typed pipeline: bash command in, real objects out.
# Run:  Import-Module PsBash; ./examples/01-bash-commands/typed-objects.ps1
#
# `ls -la | grep` does NOT hand you strings to re-parse — the LsEntry objects
# survive the grep, so you can reach for real properties afterward.
# ---------------------------------------------------------------------------

Import-Module PsBash -ErrorAction Stop

Write-Host "== ls returns LsEntry objects with real properties ==" -ForegroundColor Cyan
$files = ls -la ../../src | grep -v '/$'      # files only (drop dir rows)
$files | Select-Object -First 3 Name, SizeBytes, Mode | Format-Table

Write-Host "`n== Sort by a real numeric property, no -k gymnastics ==" -ForegroundColor Cyan
$biggest = $files | Sort-Object SizeBytes -Descending | Select-Object -First 1
"Largest: {0} ({1:N0} bytes)" -f $biggest.Name, $biggest.SizeBytes

Write-Host "`n== ps emits PsEntry objects you can filter as objects ==" -ForegroundColor Cyan
ps aux | Where-Object { $_.PSObject.Properties['CPU'] } |
    Sort-Object CPU -Descending | Select-Object -First 3 | Format-Table

Write-Host "`n== wc returns a WcResult, not a string to split ==" -ForegroundColor Cyan
$wc = Get-ChildItem ../../README.md | ForEach-Object { cat $_.FullName | wc -l }
"README line count object: $($wc.BashText.Trim())"
