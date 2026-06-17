# ---------------------------------------------------------------------------
# Drive the browse workbench non-interactively (great for scripts and CI),
# then point you at the interactive form.
# Run:  Import-Module PsBash; ./examples/04-interactive-tui/browse-demo.ps1
# ---------------------------------------------------------------------------

Import-Module PsBash -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'PrWorkbench.psm1') -Force

Write-Host "== browse --list (line mode: just the rows) ==" -ForegroundColor Cyan
Get-DemoPullRequests | browse --list

Write-Host "`n== Inspect a single row by index ==" -ForegroundColor Cyan
Get-DemoPullRequests | browse --inspect 0

Write-Host "`n== Run a custom action on a selection (previews; destructive needs -Force) ==" -ForegroundColor Cyan
Get-DemoPullRequests | browse --select 0,2 --action approve

Write-Host "`n== Inline exec against the selection (`$_` / `$1` / `$items` bound) ==" -ForegroundColor Cyan
Get-DemoPullRequests | browse --select 1 --exec '"picked PR #" + $_.Number'

Write-Host "`n--- Interactive form (real terminal) -------------------------------" -ForegroundColor DarkGray
Write-Host "  Get-DemoPullRequests | browse"                                      -ForegroundColor DarkGray
Write-Host "  n/p move | s select | i inspect | a approve/close | q quit"         -ForegroundColor DarkGray
Write-Host "  CSS viewer:  Get-DemoPullRequests | Show-Styled (Join-Path '$PSScriptRoot' pr.css)" -ForegroundColor DarkGray
