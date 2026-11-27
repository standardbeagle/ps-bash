#!/usr/bin/env bash
# Runs inside VHS to simulate a PowerShell session with pre-computed output
# This avoids PSReadLine interference that breaks VHS recordings

set -euo pipefail

GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
DIM='\033[2m'
RESET='\033[0m'

prompt() { printf "${GREEN}PS>${RESET} "; }
comment() { printf "${GREEN}PS>${RESET} ${DIM}# %s${RESET}\n" "$1"; }
error() { printf "${RED}%s${RESET}\n" "$1"; }
output() { printf "%s\n" "$1"; }

# Generate live output from PsBash
DEMO_DATA=$(pwsh -NoProfile -NoLogo -Command '
$ErrorActionPreference = "SilentlyContinue"
Import-Module /home/beagle/work/experimental/ps-bash/src/PsBash.psd1 *>$null
Set-Location /home/beagle/work/experimental/ps-bash

$f = ls -la src/ | sort -k5 -rn
Write-Output "LS_LINES"
foreach ($item in $f) { Write-Output $item.BashText.TrimEnd() }
Write-Output "LS_NAME"
Write-Output $f[0].Name
Write-Output "LS_SIZE"
Write-Output $f[0].SizeBytes
Write-Output "LS_SUM"
$f.SizeBytes | Measure-Object -Sum | Select -ExpandProperty Sum
Write-Output "DU_LINES"
$d = du -sh src/ tests/ | sort -rh
foreach ($item in $d) { Write-Output $item.BashText.TrimEnd() }
Write-Output "WC_LINE"
$w = find . -name "*.ps1" -type f | wc -l
Write-Output $w.BashText.TrimEnd()
Write-Output "CAT_LINES"
$c = cat README.md | head -n 3
foreach ($item in $c) { Write-Output $item.BashText.TrimEnd() }
Write-Output "END"
' 2>/dev/null)

# Parse live data
parse_section() {
    local start="$1" end="$2"
    echo "$DEMO_DATA" | sed -n "/$start/,/$end/p" | grep -v "^$start$" | grep -v "^$end$"
}

LS_LINES=$(parse_section "LS_LINES" "LS_NAME")
LS_NAME=$(parse_section "LS_NAME" "LS_SIZE")
LS_SIZE=$(parse_section "LS_SIZE" "LS_SUM")
LS_SUM=$(parse_section "LS_SUM" "DU_LINES")
DU_LINES=$(parse_section "DU_LINES" "WC_LINE")
WC_LINE=$(parse_section "WC_LINE" "CAT_LINES")
CAT_LINES=$(parse_section "CAT_LINES" "END")

# Get live ps aux data
PS_DATA=$(pwsh -NoProfile -NoLogo -Command '
$ErrorActionPreference = "SilentlyContinue"
Import-Module /home/beagle/work/experimental/ps-bash/src/PsBash.psd1 *>$null
$p = Get-Process | Sort-Object CPU -Descending | Select-Object -First 3
foreach ($proc in $p) {
    $cpu = [math]::Round($proc.CPU, 1)
    $mem = [math]::Round($proc.WorkingSet64 / 1MB, 1)
    Write-Output ("{0,-15} {1,6} {2,5} {3,5}  {4}" -f $proc.ProcessName, $proc.Id, $cpu, $mem, (Get-Date $proc.StartTime -Format "HH:mm" -ErrorAction SilentlyContinue))
}
Write-Output "PS_NAME"
Write-Output $p[0].ProcessName
Write-Output "PS_CPU"
Write-Output ([math]::Round($p[0].CPU, 1))
Write-Output "PS_PID"
Write-Output $p[0].Id
Write-Output "END"
' 2>/dev/null)

PS_LINES=$(echo "$PS_DATA" | sed -n '1,/^PS_NAME$/p' | grep -v "^PS_NAME$")
PS_NAME=$(echo "$PS_DATA" | sed -n '/^PS_NAME$/,/^PS_CPU$/p' | grep -v "^PS_NAME$" | grep -v "^PS_CPU$")
PS_CPU=$(echo "$PS_DATA" | sed -n '/^PS_CPU$/,/^PS_PID$/p' | grep -v "^PS_CPU$" | grep -v "^PS_PID$")
PS_PID=$(echo "$PS_DATA" | sed -n '/^PS_PID$/,/^END$/p' | grep -v "^PS_PID$" | grep -v "^END$")

clear

# Scene 1: The problem
comment "PowerShell aliases are lies"
sleep 0.8

prompt
echo "rm -rf node_modules"
error "Remove-Item: A parameter cannot be found that matches parameter name 'rf'."
sleep 2

# Scene 2: Install + fix
prompt
echo "Install-Module PsBash"
sleep 1.5

prompt
echo "rm -rf node_modules   # now it works!"
sleep 1.5

# Scene 3: Typed objects
comment "Bash flags work — and return typed .NET objects"
sleep 0.8

prompt
echo 'ps aux | sort -k3 -rn | head 3'
echo "$PS_LINES"
sleep 1.5

prompt
echo '$top = ps aux | sort -k3 -rn | head 3'
sleep 0.5

prompt
printf '%s\n' '$top[0].ProcessName'
output "$PS_NAME"
sleep 0.8

prompt
printf '%s\n' '$top[0].CPU'
output "$PS_CPU"
sleep 0.8

prompt
printf '%s\n' '$top[0].PID'
output "$PS_PID"
sleep 1.5

# Scene 4: Pipeline bridge
comment "Objects survive grep, sort, head — the pipeline bridge"
sleep 0.8

prompt
echo '$files = ls -la src/ | sort -k5 -rn'
sleep 0.5

prompt
echo '$files | ForEach-Object { $_.BashText.TrimEnd() }'
echo "$LS_LINES"
sleep 1.5

prompt
printf '%s\n' '$files[0].Name'
output "$LS_NAME"
sleep 0.8

prompt
printf '%s\n' '$files[0].SizeBytes'
output "$LS_SIZE"
sleep 0.8

prompt
printf '%s\n' '$files.SizeBytes | Measure-Object -Sum | Select -ExpandProperty Sum'
output "$LS_SUM"
sleep 1.5

# Scene 5: Breadth
comment "68 commands. Zero dependencies."
sleep 0.8

prompt
echo 'du -sh src/ tests/ | sort -rh'
echo "$DU_LINES"
sleep 1.2

prompt
echo "find . -name '*.ps1' -type f | wc -l"
output "$WC_LINE"
sleep 1.2

prompt
echo 'cat README.md | head 3'
echo "$CAT_LINES"
sleep 2.5

prompt
