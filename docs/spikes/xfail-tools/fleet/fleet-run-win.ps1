# Windows side of fleet-gate.ps1: check out the snapshot detached, build, run gate.ps1 (Core, Shell, Host, every
# spec file, Pester), then the full Differential and Cmdlets suites. summary.txt gets one line per step and
# ends with FINISHED; xpass.txt lists the spec cases that now pass (candidates for unmark.ps1).
param([string]$Bundle, [string]$Tag, [string]$Out)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $Out | Out-Null
$sum = Join-Path $Out 'summary.txt'
function S([string]$m) { "$(Get-Date -Format HH:mm:ss) $m" | Add-Content $sum }
function Totals([string[]]$lines) { ($lines | Select-String 'Passed!|Failed!' | ForEach-Object { $_.Line.Trim() }) -join ' / ' }
function Fails([string[]]$lines) { $lines | Select-String '^\s+Failed ' | ForEach-Object { '    ' + $_.Line.Trim() } | Add-Content $sum }

S "start tag=$Tag"
Set-Location C:\Users\andyb\work\core\ps-bash
if (git status --porcelain --untracked-files=no) { S 'ABORT: the checkout has tracked changes'; S 'FINISHED'; exit 1 }
git fetch -q $Bundle refs/fleet/snapshot 2>&1 | Add-Content (Join-Path $Out 'git.log')
if ($LASTEXITCODE -ne 0) { S 'ABORT: fetch failed (git.log)'; S 'FINISHED'; exit 1 }
git checkout -q --detach FETCH_HEAD 2>&1 | Add-Content (Join-Path $Out 'git.log')
if ($LASTEXITCODE -ne 0) { S 'ABORT: checkout failed (git.log)'; S 'FINISHED'; exit 1 }
S ('checked out ' + (git log --oneline -1))

# A dev-build host left by an earlier, killed run holds src/*/bin DLLs (MSB3021). Stop only hosts whose exe is
# under THIS checkout's src/ (never ~/.local/bin, which serves the machine's own shells).
$srcPrefix = (Join-Path (Get-Location) 'src').ToLowerInvariant() + '\'
Get-CimInstance Win32_Process -Filter "Name='ps-bash-host.exe' OR Name='ps-bash.exe' OR Name='testhost.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant().StartsWith($srcPrefix) } |
    ForEach-Object { S "stopping leftover $($_.Name) $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# The first build after a checkout can fail CS2012 (a locked obj file); one retry.
for ($i = 1; $i -le 2; $i++) {
    $b = (tman build 2>&1 | ForEach-Object { "$_" }) -join "`n"
    $b | Set-Content (Join-Path $Out "build$i.log")
    if ($b -match ' 0 Error\(s\)') { break }
}
if ($b -notmatch ' 0 Error\(s\)') { S 'BUILD FAILED (build2.log)'; S 'FINISHED'; exit 1 }
S 'build ok'

& .\docs\spikes\xfail-tools\gate.ps1 -Tag $Tag -OutDir $Out
$gate = Get-Content (Join-Path $Out "gate$Tag.log")
S ('gate: ' + (Totals $gate)); Fails $gate
$xp = $gate | Select-String 'xpass=[1-9]|\[XPass\]' | ForEach-Object { $_.Line.Trim() }
$xp | Set-Content (Join-Path $Out 'xpass.txt')
S "gate: $(@($gate | Select-String 'xpass=[1-9]').Count) spec files with cases that now pass (xpass.txt)"
$tsv = Join-Path $Out "spec-results$Tag.tsv"
if (Test-Path $tsv) { S ('spec files: ' + ((Get-Content $tsv | ForEach-Object { ($_ -split "`t")[0] } | Group-Object | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')) }

foreach ($p in 'Differential', 'Cmdlets') {
    $r = tman test-proj "src/PsBash.$p.Tests" 2>&1 | ForEach-Object { "$_" }
    $r | Set-Content (Join-Path $Out "$p.log")
    S "${p}: $(Totals $r)"; Fails $r
}
S 'FINISHED'
