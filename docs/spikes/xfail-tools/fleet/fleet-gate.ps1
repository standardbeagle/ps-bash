# Run the test gate on a fleet machine so this one stays free to build.
#
#   fleet-gate.ps1 [-HostName beagle-ab] [-Side both|win|linux] [-Tag t]   snapshot, ship, start (returns at once)
#   fleet-gate.ps1 -Tag t -Status                                          print both summaries
#   fleet-gate.ps1 -Tag t -Fetch                                           copy the result folders back here
#
# The snapshot is the working tree as a commit on refs/fleet/snapshot (tracked changes plus untracked files
# under src/ docs/ spec/ tests/, .gitignore applies), made through a temporary index: the real index, HEAD
# and branches are untouched. It travels as a git bundle, and each remote checkout is moved to it DETACHED.
# A remote checkout with tracked changes is refused, never overwritten.
#
# Host layout (see memory beagle-ab-remote-test-host): `ssh <host>` lands in WSL; Windows is reached
# through interop. Windows checkout C:\Users\andyb\work\core\ps-bash (tman, dotnet, pwsh), Linux checkout
# ~/work/core/ps-bash (dotnet, bash 5.2). Results: C:\Users\andyb\fleet\<tag>\win and ~/fleet/<tag>/linux.
param(
    [string]$HostName = 'beagle-ab',
    [ValidateSet('both', 'win', 'linux')] [string]$Side = 'both',
    [string]$Tag = (Get-Date -Format 'MMdd-HHmm'),
    [switch]$Status,
    [switch]$Fetch
)
$ErrorActionPreference = 'Stop'
$ssh = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15')
$winDir = "/mnt/c/Users/andyb/fleet/$Tag"          # the same folder seen from WSL
$winDirW = "C:\Users\andyb\fleet\$Tag"            # ... and from Windows
$pwshW = '"/mnt/c/Program Files/PowerShell/7/pwsh.exe"'

function Remote([string]$cmd) { ssh @ssh $HostName $cmd; if ($LASTEXITCODE -ne 0) { throw "ssh $HostName failed ($LASTEXITCODE): $cmd" } }

if ($Status) {
    $cmd = 'for f in {0}/win/summary.txt ~/fleet/{1}/linux/summary.txt; do echo "== $f"; if [ -f "$f" ]; then tr -d "\r" < "$f"; else echo "(not started)"; fi; done' -f $winDir, $Tag
    ssh @ssh $HostName $cmd
    return
}
if ($Fetch) {
    $local = Join-Path ([IO.Path]::GetTempPath()) "psbash-fleet\$Tag"
    New-Item -ItemType Directory -Force $local | Out-Null
    scp @ssh -r -q "${HostName}:$winDir/win" "${HostName}:~/fleet/$Tag/linux" $local 2>&1 | Out-Null
    Write-Output "fetched to $local"
    return
}

$repo = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Set-Location $repo

# 1. Snapshot through a temporary index.
$idx = Join-Path ([IO.Path]::GetTempPath()) "psbash-fleet-$Tag.idx"
$env:GIT_INDEX_FILE = $idx
try {
    git read-tree HEAD
    git -c core.safecrlf=false add -u -- .
    git -c core.safecrlf=false add -- src docs spec tests
    $tree = (git write-tree).Trim()
} finally {
    Remove-Item Env:GIT_INDEX_FILE
    Remove-Item $idx -ErrorAction SilentlyContinue
}
$commit = (git commit-tree $tree -p HEAD -m "fleet snapshot $Tag").Trim()
git update-ref refs/fleet/snapshot $commit
Write-Output "snapshot $($commit.Substring(0, 10)) on $(git log --oneline -1 HEAD)"

# 2. Bundle: only what the remote checkouts lack. ONE bundle feeds every side, so its base must be a commit
#    every receiving checkout has: the common ancestor of HEAD and all their HEADs. Unknown HEAD -> full bundle.
$heads = @()
if ($Side -ne 'linux') { $heads += (ssh @ssh $HostName 'git -C /mnt/c/Users/andyb/work/core/ps-bash rev-parse HEAD' 2>$null) }
if ($Side -ne 'win') { $heads += (ssh @ssh $HostName 'git -C ~/work/core/ps-bash rev-parse HEAD' 2>$null) }
$base = $null
$known = $heads.Count -gt 0
foreach ($h in $heads) {
    if (-not $h) { $known = $false; break }
    git cat-file -e "$($h.Trim())^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { $known = $false; break }
}
if ($known) { $base = (git merge-base --octopus HEAD @($heads | ForEach-Object { $_.Trim() })) }
$bundle = Join-Path ([IO.Path]::GetTempPath()) "psbash-fleet-$Tag.bundle"
if ($base) { git bundle create -q $bundle refs/fleet/snapshot --not $base.Trim() } else { git bundle create -q $bundle refs/fleet/snapshot }
Write-Output ("bundle {0:N1} MB ({1})" -f ((Get-Item $bundle).Length / 1MB), ($base ? "since $($base.Trim().Substring(0, 10))" : 'full'))

# 3. Ship the bundle and the runners.
Remote "mkdir -p $winDir"
scp @ssh -q $bundle "${HostName}:$winDir/snapshot.bundle"
scp @ssh -q (Join-Path $PSScriptRoot 'fleet-run-win.ps1') (Join-Path $PSScriptRoot 'fleet-run-linux.sh') "${HostName}:$winDir/"
Remove-Item $bundle

# 4. Start detached runners. Quoting goes through a generated .ps1, never inline across ssh -> bash -> pwsh.
if ($Side -ne 'linux') {
    $launch = Join-Path ([IO.Path]::GetTempPath()) "psbash-fleet-launch-$Tag.ps1"
    @"
Start-Process pwsh -WindowStyle Hidden -ArgumentList @('-NoProfile', '-File', '$winDirW\fleet-run-win.ps1', '-Bundle', '$winDirW\snapshot.bundle', '-Tag', '$Tag', '-Out', '$winDirW\win')
"@ | Set-Content -Encoding utf8 $launch
    scp @ssh -q $launch "${HostName}:$winDir/launch-win.ps1"
    Remove-Item $launch
    Remote "cd /mnt/c/Users/andyb && $pwshW -NoProfile -File '$winDirW\launch-win.ps1'"
    Write-Output "windows runner started -> $winDirW\win"
}
if ($Side -ne 'win') {
    Remote "mkdir -p ~/fleet/$Tag/linux && tr -d '\r' < $winDir/fleet-run-linux.sh > ~/fleet/$Tag/run.sh && setsid -f bash ~/fleet/$Tag/run.sh $winDir/snapshot.bundle $Tag ~/fleet/$Tag/linux > /dev/null 2>&1 < /dev/null"
    Write-Output "linux runner started -> ~/fleet/$Tag/linux"
}
Write-Output "poll: fleet-gate.ps1 -HostName $HostName -Tag $Tag -Status"
