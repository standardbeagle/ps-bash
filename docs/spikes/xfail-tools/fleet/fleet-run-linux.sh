#!/usr/bin/env bash
# Linux (WSL) side of fleet-gate.ps1: check out the snapshot detached, build, run Core, the Security probes,
# Cmdlets and Differential, one project at a time like CI's build.yml. A dedicated fleet checkout, so plain
# `dotnet test` (no tman on Linux), with node reuse off so nothing outlives the run.
# summary.txt gets one line per step and ends with FINISHED.
BUNDLE=$1 TAG=$2 OUT=$3
mkdir -p "$OUT"; SUM="$OUT/summary.txt"
s() { echo "$(date +%H:%M:%S) $*" >> "$SUM"; }
s "start tag=$TAG"
cd ~/work/core/ps-bash || { s 'ABORT: no checkout at ~/work/core/ps-bash'; s FINISHED; exit 1; }
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then s 'ABORT: the checkout has tracked changes'; s FINISHED; exit 1; fi
if ! { git fetch -q "$BUNDLE" refs/fleet/snapshot && git checkout -q --detach FETCH_HEAD; } >> "$OUT/git.log" 2>&1; then
    s 'ABORT: fetch/checkout failed (git.log)'; s FINISHED; exit 1
fi
s "checked out $(git log --oneline -1)"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
NB=(-m:1 -nodeReuse:false -p:UseSharedCompilation=false)
if ! dotnet build ps-bash.sln -c Debug "${NB[@]}" > "$OUT/build.log" 2>&1; then
    s "BUILD FAILED: $(grep -m3 -E 'error [A-Z]+[0-9]+' "$OUT/build.log" | tr '\n' ' ')"; s FINISHED; exit 1
fi
s 'build ok'

run() { # name project [dotnet test args...]
    local name=$1 proj=$2; shift 2
    timeout 90m dotnet test "src/$proj" -c Debug --no-build "${NB[@]}" --blame-hang-timeout 5m --blame-hang-dump-type none "$@" > "$OUT/$name.log" 2>&1
    s "$name: $(grep -E 'Passed!|Failed!' "$OUT/$name.log" | sed 's/^ *//' | tr '\n' ' ')"
    grep -E '^\s+Failed ' "$OUT/$name.log" | sed 's/^ */    /' >> "$SUM"
}
run Core PsBash.Core.Tests
run Security PsBash.Escalation.Tests --filter Category=Security
run Cmdlets PsBash.Cmdlets.Tests
run Differential PsBash.Differential.Tests
dotnet build-server shutdown > /dev/null 2>&1
s FINISHED
