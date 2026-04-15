dotnet publish src/PsBash.Shell -c Release -r win-x64 -p:PublishAot=false --self-contained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test src/PsBash.Core.Tests/PsBash.Core.Tests.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishDir = "src/PsBash.Shell/bin/Release/net10.0/win-x64/publish"
$destDir = "$env:USERPROFILE\.local\bin"

if (Test-Path "$destDir\ps-bash.exe") {
    Move-Item "$destDir\ps-bash.exe" "$destDir\ps-bash.old.exe" -Force -ErrorAction SilentlyContinue
}

# Remove old deployment files (keep ps-bash.old.exe)
Get-ChildItem "$destDir" -Filter "ps-bash.*" | Where-Object {
    $_.Name -notin @('ps-bash.old.exe')
} | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem "$destDir" -Filter "PsBash.*" | Remove-Item -Force -ErrorAction SilentlyContinue

# Copy entire publish output
Copy-Item "$publishDir\*" "$destDir\" -Force -Recurse

Remove-Item "$env:TEMP\ps-bash\module-*" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Deployed ps-bash to $destDir\ps-bash.exe" -ForegroundColor Green
