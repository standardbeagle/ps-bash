dotnet publish src/PsBash.Shell -c Release -r win-x64 -p:PublishAot=false --self-contained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test src/PsBash.Core.Tests/PsBash.Core.Tests.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishDir = "src/PsBash.Shell/bin/Release/net10.0/win-x64/publish"
$destDir = "$env:USERPROFILE\.local\bin"

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
