# ---------------------------------------------------------------------------
# Format-Styled: theme your terminal with real CSS.
# Run:  Import-Module PsBash; ./examples/03-styled-output/format-styled.ps1
# Static ANSI output — works in -c, pipes, files, CI (honors NO_COLOR).
# ---------------------------------------------------------------------------

Import-Module PsBash -ErrorAction Stop

Write-Host "== Built-in 'ps' sheet ==" -ForegroundColor Cyan
ps aux | Select-Object -First 6 | Format-Styled -Style ps

Write-Host "`n== Built-in 'ls' sheet ==" -ForegroundColor Cyan
ls -la ../../src | Format-Styled -Style ls -Property Name

Write-Host "`n== Inline CSS + a computed class property ==" -ForegroundColor Cyan
# Tag each row with a 'class', then color by class. Busy processes go red.
Get-Process |
    Select-Object -First 8 *, @{ n = 'class'; e = { if ($_.CPU -gt 5) { 'busy' } else { 'idle' } } } |
    Format-Styled 'Process { color: grey }
                   .busy   { color: red; font-weight: bold }
                   .idle   { color: #777777 }' `
                  -Property Name, Id, CPU

Write-Host "`n== Load a theme from a .css file ==" -ForegroundColor Cyan
$theme = Join-Path $PSScriptRoot 'custom-theme.css'
Get-ChildItem ../../docs |
    Select-Object Name, Length, LastWriteTime,
        @{ n = 'class'; e = { if ($_.PSIsContainer) { 'dir' } else { 'file' } } } |
    Format-Styled -Css $theme -Property Name, Length, LastWriteTime
