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
