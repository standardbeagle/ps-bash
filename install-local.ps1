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

dotnet publish src/PsBash.Shell -c Release -r win-x64 -p:PublishAot=false --self-contained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test src/PsBash.Core.Tests/PsBash.Core.Tests.csproj
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
# "connection forcibly closed"). Force-kill every remaining host so the freshly
# deployed build starts from a clean slate.
$strays = @(Get-Process ps-bash-host -ErrorAction SilentlyContinue)
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

Remove-Item "$env:TEMP\ps-bash\module-*" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Deployed ps-bash to $destDir\ps-bash.exe" -ForegroundColor Green

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

dotnet build src/PsBash.Cmdlets/PsBash.Cmdlets.csproj -c Release -f net8.0 --nologo -o $cmdletsBuildDir
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
        Get-ChildItem $dir -File -Recurse -ErrorAction SilentlyContinue |
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
