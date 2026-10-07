# Explicit restore first. dotnet build/publish only do an *implicit* restore,
# which is incrementally skipped when obj/project.assets.json merely looks
# present — so a stale restore (most often after a .NET SDK update, which
# changes the assets stamp) surfaces as NETSDK1064 "Package <X> was not found"
# even though the package is in the NuGet cache. An explicit restore always
# re-evaluates the graph and rewrites the assets for the current SDK.
dotnet restore ps-bash.sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet clean src/PsBash.Core -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Version stamp. The committed <Version>/ModuleVersion values are placeholders —
# the publish workflow patches them at release time — so an unstamped local
# build reports whatever stale number was last committed (0.10.19 from Core,
# 0.10.23 from PsBash.psd1). Derive the real one from the nearest release tag:
# at the tag it is X.Y.Z; N commits past it the informational version (what
# `ps-bash --version` prints) is X.Y.Z-dev.N.<sha>.
$moduleVersion = $null
$versionArgs = @()
$describe = git describe --tags --match 'v[0-9]*' --long 2>$null
if ($describe -match '^v(\d+\.\d+\.\d+)-(\d+)-g([0-9a-f]+)$') {
    $moduleVersion = $Matches[1]
    $informational = if ([int]$Matches[2] -eq 0) { $moduleVersion } else { "$moduleVersion-dev.$($Matches[2]).$($Matches[3])" }
    $versionArgs = @("-p:Version=$moduleVersion", "-p:InformationalVersion=$informational")
    Write-Host "Stamping local build as $informational" -ForegroundColor DarkGray
} else {
    Write-Warning "git describe found no vX.Y.Z tag; building with the committed placeholder versions."
}

dotnet publish src/PsBash.Shell -c Release -r win-x64 -p:PublishAot=false --self-contained @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Release, not the default Debug: tman's build/test (and any drain running
# alongside this install) own the shared src/*/bin/Debug outputs, and a second
# writer there is the documented cause of half-written test bins. The flags
# mirror .tman.kdl so no MSBuild node or compiler server outlives the install.
dotnet test src/PsBash.Core.Tests/PsBash.Core.Tests.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishDir = "src/PsBash.Shell/bin/Release/net10.0/win-x64/publish"
$destDir = "$env:USERPROFILE\.local\bin"
$managementClient = Join-Path $publishDir "ps-bash.exe"

if (Test-Path $managementClient) {
    Write-Host "Requesting running ps-bash host shutdown..." -ForegroundColor DarkGray
    & $managementClient host shutdown --deadline-ms 5000
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "ps-bash host shutdown returned exit code $LASTEXITCODE; continuing with file replacement."
    }
}

# `host shutdown` only retires the ONE daemon answering the canonical endpoint.
# Stray ps-bash-host processes — orphans on isolated/per-invocation endpoints, or
# OLD-BUILD daemons left over from a previous install — survive it, and a daemon
# outlives its launcher by design. After a rebuild those leftovers POISON reuse:
# a new-build launcher sees an old-build host, treats it as obsolete, and does the
# slow retire-and-replace cycle on every -c (observed as 12-19s/call + exit-125
# "connection forcibly closed"). Force-kill the remaining DAEMON hosts so the
# freshly deployed build starts from a clean slate.
#
# Only hosts running from $destDir, and never an --interactive host: an
# interactive host IS a user's open terminal (it may even be the parent of the
# shell running this script, or of an agent session), and killing it kills that
# session. Dev-build hosts under the repo's src/*/bin belong to a test run that
# may be in progress and are not this install's business either.
$destHostPath = Join-Path "$env:USERPROFILE\.local\bin" 'ps-bash-host.exe'
$strays = @(Get-CimInstance Win32_Process -Filter "Name='ps-bash-host.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ExecutablePath -and
        [string]::Equals($_.ExecutablePath, $destHostPath, [StringComparison]::OrdinalIgnoreCase) -and
        $_.CommandLine -notmatch '--interactive'
    } |
    ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
if ($strays.Count -gt 0) {
    Write-Host "Force-killing $($strays.Count) leftover ps-bash-host process(es) so the new build starts clean..." -ForegroundColor DarkGray
    $strays | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# NTFS trick: a locked file cannot be deleted or overwritten, but it CAN be
# renamed — existing handles keep pointing at the old file by its file record.
# Rename every ps-bash/PsBash file to .old.<n> so Copy-Item can write the new
# ones even when a live ps-bash is holding Core.dll / framework DLLs. The
# .old files get cleaned up on the next deploy where nobody holds them.
function Move-OutOfTheWay($path) {
    if (-not (Test-Path $path)) { return }
    $base = "$path.old"
    $n = 0
    while (Test-Path $base) {
        Remove-Item $base -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $base)) { break }
        $n++
        $base = "$path.old.$n"
    }
    Move-Item $path $base -Force -ErrorAction SilentlyContinue
}

if (Test-Path $destDir) {
    # Backups from earlier installs that nobody holds any more (Move-OutOfTheWay only clears the
    # one slot it is about to reuse, so .old.<n> siblings otherwise accumulate). ~/.local/bin is
    # shared with other tools: only backups of a file THIS install publishes are ours to delete.
    $ours = @{}
    Get-ChildItem $publishDir -File | ForEach-Object { $ours[$_.Name] = $true }
    $ours['bash.exe'] = $true
    Get-ChildItem $destDir -File -Filter '*.old*' | Where-Object {
        $ours.ContainsKey(($_.Name -replace '(\.old(\.\d+)?)+$', ''))
    } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
    Get-ChildItem $destDir -File -Filter 'ps-bash*' | ForEach-Object {
        if ($_.Name -like '*.old*') { return }
        Move-OutOfTheWay $_.FullName
    }
    Get-ChildItem $destDir -File -Filter 'PsBash.*' | ForEach-Object {
        # Skip already-renamed backups — otherwise each install re-renames
        # PsBash.Core.dll.old → .old.old → .old.old.old …, growing the suffix
        # unboundedly (the 'ps-bash*' and '*.dll' loops already guard this).
        if ($_.Name -like '*.old*') { return }
        Move-OutOfTheWay $_.FullName
    }
    # Framework/dependency DLLs published alongside the host (System.*.dll etc).
    # If our live shell has them open, the rename still succeeds.
    Get-ChildItem $destDir -File -Filter '*.dll' | Where-Object {
        $_.Name -notlike '*.old*'
    } | ForEach-Object {
        Move-OutOfTheWay $_.FullName
    }
}

Copy-Item "$publishDir\*" "$destDir\" -Force -Recurse

# bash.exe: the same launcher under the name agents accept as a shell. Claude
# Code ignores CLAUDE_CODE_GIT_BASH_PATH unless the file is named bash.exe /
# sh.exe (and CLAUDE_CODE_SHELL only takes a working bash/zsh), then silently
# falls back to Git Bash. The .NET apphost resolves its app by the ps-bash.dll
# name embedded at build time, so a plain copy placed beside the runtime works;
# a copy anywhere else would miss the self-contained runtime DLLs.
$bashShim = Join-Path $destDir 'bash.exe'
Move-OutOfTheWay $bashShim
Copy-Item (Join-Path $destDir 'ps-bash.exe') $bashShim -Force

Remove-Item "$env:TEMP\ps-bash\module-*" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Deployed ps-bash to $destDir\ps-bash.exe (agent shell alias: $bashShim)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Module install to PSModulePath
# ---------------------------------------------------------------------------
# Why: `Import-Module PsBash` in plain pwsh used to require hand-junctioning
# the source tree (Documents\PowerShell\Modules\PsBash → src\PsBash.Module,
# \PsBash.Cmdlets → src\PsBash.Cmdlets\bin\Debug\net8.0). The junctions made
# `dotnet build` collide with the live pwsh's file-mapped PsBash.Cmdlets.dll
# (MSB3027 "file is locked"), so every test session that started with a pwsh
# open hit a build wall.
#
# Fix: copy (not junction) into PSModulePath, and RENAME the binary cmdlet
# DLL to PsBash.Cmdlets.Runtime.dll. The user's pwsh now maps a path that
# `dotnet build` never writes to, so rebuilds stop racing the live process.
# psm1's probe loop (see PsBash.psm1) prefers the Runtime.dll variant.

$documentsDir = [Environment]::GetFolderPath('MyDocuments')
$moduleRoot   = Join-Path $documentsDir 'PowerShell\Modules'
$psBashDir    = Join-Path $moduleRoot   'PsBash'
$cmdletsDir   = Join-Path $moduleRoot   'PsBash.Cmdlets'

function Ensure-RealDirectory($dir) {
    $item = Get-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue
    if ($item -and $item.LinkType) {
        Write-Host "Replacing module-path junction with real directory: $dir" -ForegroundColor DarkGray
        if (($item.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
            $item.Attributes = $item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
        }
        Remove-Item -LiteralPath $dir -Force -Recurse -ErrorAction Stop
        $item = $null
    }

    if (-not $item) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $item = Get-Item -LiteralPath $dir -Force -ErrorAction Stop
    if ($item.LinkType) {
        throw "Refusing to stage module install through reparse point: $dir"
    }
}

# Build the binary cmdlets DLL (Release) for the module install. PsBash.Cmdlets
# multi-targets net8.0 + net10.0; PSGallery distribution and the user's stock
# pwsh 7.4 both expect net8.0.
$cmdletsBuildDir = Join-Path $env:TEMP 'ps-bash\module-build\PsBash.Cmdlets\net8.0'
Remove-Item $cmdletsBuildDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $cmdletsBuildDir -Force | Out-Null

dotnet build src/PsBash.Cmdlets/PsBash.Cmdlets.csproj -c Release -f net8.0 --nologo -o $cmdletsBuildDir @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path "$cmdletsBuildDir\PsBash.Cmdlets.dll")) {
    Write-Warning "Module install skipped: $cmdletsBuildDir\PsBash.Cmdlets.dll missing."
    return
}

# Stage both target dirs with the rename trick (any pwsh holding the previous
# install keeps its old handle pointing at the renamed file; we copy fresh
# bits over a clean path).
foreach ($dir in @($psBashDir, $cmdletsDir)) {
    Ensure-RealDirectory $dir
    if (Test-Path $dir) {
        # Backups from earlier installs: delete every one nobody still holds open. Then rename
        # only LIVE files — this loop used to re-rename the backups too (X.old → X.old.old …),
        # doubling them per install until names hit MAX_PATH (685 files, 255-char names).
        Get-ChildItem $dir -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '*.old*' } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
        Get-ChildItem $dir -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '*.old*' } |
            ForEach-Object { Move-OutOfTheWay $_.FullName }
    }
}

# PsBash module: script module + format file + flag specs.
Copy-Item 'src\PsBash.Module\PsBash.psd1'         $psBashDir -Force
Copy-Item 'src\PsBash.Module\PsBash.psm1'         $psBashDir -Force
Copy-Item 'src\PsBash.Module\PsBash.Format.ps1xml' $psBashDir -Force
Copy-Item 'src\PsBash.Module\BashFlagSpecs.json'  $psBashDir -Force

# PsBash.Cmdlets.psd1 declares PsBash.psd1 as a nested module, matching the
# package/build output layout, so keep those script-module files beside it.
Copy-Item 'src\PsBash.Module\PsBash.psd1'         $cmdletsDir -Force
Copy-Item 'src\PsBash.Module\PsBash.psm1'         $cmdletsDir -Force
Copy-Item 'src\PsBash.Module\PsBash.Format.ps1xml' $cmdletsDir -Force
Copy-Item 'src\PsBash.Module\BashFlagSpecs.json'  $cmdletsDir -Force

# PsBash.Cmdlets module: binary entrypoint renamed to PsBash.Cmdlets.Runtime.dll
# (assembly *identity* stays PsBash.Cmdlets; only the file name differs). The
# psd1's RootModule is rewritten to match.
Copy-Item (Join-Path $cmdletsBuildDir 'PsBash.Cmdlets.dll') `
          (Join-Path $cmdletsDir 'PsBash.Cmdlets.Runtime.dll') -Force

# Companion DLLs (PsBash.Transpiler.dll, Parlot.dll, …) keep their names —
# only the loader entrypoint needs the rename.
Get-ChildItem $cmdletsBuildDir -File -Filter '*.dll' |
    Where-Object { $_.Name -ne 'PsBash.Cmdlets.dll' } |
    ForEach-Object { Copy-Item $_.FullName $cmdletsDir -Force }

# Manifest: rewrite the single RootModule line. Done with a regex replace
# rather than a full re-serialize so we don't have to keep a PSD1 parser
# round-tripping in lockstep with the source manifest's evolving entries.
$psd1Src = 'src\PsBash.Cmdlets\PsBash.Cmdlets.psd1'
$psd1Dst = Join-Path $cmdletsDir 'PsBash.Cmdlets.psd1'
$psd1Content = Get-Content -Raw -LiteralPath $psd1Src
$psd1Patched = $psd1Content -replace `
    "RootModule\s*=\s*'PsBash\.Cmdlets\.dll'", `
    "RootModule = 'PsBash.Cmdlets.Runtime.dll'"
if ($psd1Patched -eq $psd1Content) {
    Write-Warning "Did not find RootModule = 'PsBash.Cmdlets.dll' to patch in $psd1Src — install may fail to auto-load cmdlets."
}
Set-Content -LiteralPath $psd1Dst -Value $psd1Patched -Encoding UTF8

# Same version stamp as the binaries: `bash --version` in plain pwsh reads the
# imported PsBash module's ModuleVersion. The Cmdlets psd1 also pins PsBash via
# RequiredModules, so every ModuleVersion in both installed copies moves together.
if ($moduleVersion) {
    foreach ($manifest in @(
            (Join-Path $psBashDir  'PsBash.psd1'),
            (Join-Path $cmdletsDir 'PsBash.psd1'),
            $psd1Dst)) {
        $text = Get-Content -Raw -LiteralPath $manifest
        $text = $text -replace "ModuleVersion\s*=\s*'[\d.]+'", "ModuleVersion = '$moduleVersion'"
        Set-Content -LiteralPath $manifest -Value $text -Encoding UTF8
    }
}

Write-Host "Installed PsBash + PsBash.Cmdlets modules to $moduleRoot" -ForegroundColor Green
Write-Host "  (binary cmdlet DLL renamed to PsBash.Cmdlets.Runtime.dll to dodge dev-build file locks)" -ForegroundColor DarkGray
Write-Host "  Restart any pwsh session that has Import-Module PsBash loaded to pick up the new copy." -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# Warm-load the daemon so the install ends with a hot host.
# ---------------------------------------------------------------------------
# `-c` defaults to the shared Daemon lifetime: the first invocation pays a ~2 s
# cold start to spawn the host and warm its runspace pool, then every later -c
# reuses it (~300 ms). Pre-warming here means the user's (or an agent's) very
# first -c after install is already fast. `host start` is idempotent and leaves
# the daemon running for subsequent launchers.
$deployedClient = Join-Path $destDir 'ps-bash.exe'
if (Test-Path $deployedClient) {
    Write-Host "Warm-loading ps-bash-host..." -ForegroundColor DarkGray
    & $deployedClient host start
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "ps-bash host start returned exit code $LASTEXITCODE; the daemon will warm on the first -c instead."
    }
}
