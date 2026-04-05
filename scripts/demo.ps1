# PsBash Demo — bash syntax that doesn't work in PowerShell without transpiling
# Run: pwsh -NoProfile -File scripts/demo.ps1

$exe = "$PSScriptRoot/../src/PsBash.Shell/bin/Release/net10.0/win-x64/publish/ps-bash.exe"
$env:PSBASH_MODULE = "$PSScriptRoot/../src/PsBash.Module/PsBash.psd1"
$env:PSBASH_WORKER = "$PSScriptRoot/ps-bash-worker.ps1"
$env:PSBASH_PARSER = "v1"  # Use regex transpiler until parser-v2 is complete

$commands = @(
    # --- Redirects & env vars ---
    'echo "hello" > /dev/null && echo "stdout silenced"'
    'echo "no errors" 2>/dev/null'
    'echo "User: $USERNAME on $OS"'
    'export DEMO="it works"; echo $DEMO'

    # --- Bare assignment (no export) ---
    'X=hello; echo $X'

    # --- Path transforms ---
    'echo "temp is at /tmp/"'
    'ls ~/. | head -5'

    # --- Pipes ---
    'cat README.md | head -3'
    'echo -e "cherry\napple\nbanana" | sort -r'
    'echo -e "banana\napple\ncherry" | tr a-z A-Z'
    'echo "PsBash" | md5sum'

    # --- Here-string <<< ---
    'cat <<< "hello from a here-string"'

    # --- Backtick command substitution ---
    'echo "Today is `date +%Y-%m-%d`"'

    # --- Arithmetic expansion $(()) ---
    'echo "2 + 3 = $((2 + 3))"'

    # --- Input redirection < file ---
    'wc -l < README.md'

    # --- Stderr redirect &> ---
    'echo "both streams" &> /dev/null; echo "silenced both"'

    # --- Brace expansion ---
    'echo {1..5}'
    'echo file{A,B,C}.txt'

    # --- Parameter expansion (inside and outside quotes) ---
    'export NAME="PsBash"; echo "hello ${NAME:-unknown}"'

    # --- Extended test [[ ]] ---
    '[[ "abc" =~ ^[a-z]+$ ]] && echo "regex matched"'

    # --- if/elif/else/fi ---
    'if [ -f README.md ]; then echo "README found"; else echo "missing"; fi'

    # --- for loop ---
    'for i in alpha beta gamma; do echo "item: $i"; done'

    # --- while read (with pipe) ---
    'echo -e "one\ntwo\nthree" | while read line; do echo "got: $line"; done'

    # --- case/esac ---
    'MODE=start; case $MODE in start) echo "starting";; stop) echo "stopping";; *) echo "unknown";; esac'

    # --- Process substitution <() ---
    'cat <(echo "from process substitution")'
)

foreach ($cmd in $commands) {
    Write-Host ("`$ " + $cmd) -ForegroundColor Yellow
    & $exe -c $cmd
    Write-Host ""
}
