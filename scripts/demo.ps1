# PsBash Demo — bash syntax that doesn't work in PowerShell without transpiling
# Run: pwsh -NoProfile -File scripts/demo.ps1

$exe = "$PSScriptRoot/../src/PsBash.Shell/bin/Release/net10.0/win-x64/publish/ps-bash.exe"
$env:PSBASH_MODULE = "$PSScriptRoot/../src/PsBash.Module/PsBash.psd1"
$env:PSBASH_WORKER = "$PSScriptRoot/ps-bash-worker.ps1"

$commands = @(
    'echo "hello" > /dev/null && echo "stdout silenced"'
    'echo "no errors" 2>/dev/null'
    'echo "User: $USERNAME on $OS"'
    'export DEMO="it works"; echo $DEMO'
    'echo "temp is at /tmp/"'
    'ls ~/. | head -5'
    'cat README.md | head -3'
    'echo -e "cherry\napple\nbanana" | sort -r'
    'echo -e "banana\napple\ncherry" | tr a-z A-Z'
    'echo "PsBash" | md5sum'
)

foreach ($cmd in $commands) {
    Write-Host ("`$ " + $cmd) -ForegroundColor Yellow
    & $exe -c $cmd
    Write-Host ""
}
