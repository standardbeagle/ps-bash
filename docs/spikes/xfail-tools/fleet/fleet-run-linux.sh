#!/usr/bin/env bash
# Linux (WSL) side of fleet-gate.ps1: check out the snapshot detached, build, run Core, the Security probes,
# Cmdlets and Differential, one project at a time like CI's build.yml. A dedicated fleet checkout, so plain
# `dotnet test` (no tman on Linux), with node reuse off so nothing outlives the run.
# summary.txt gets one line per step and ends with FINISHED.
# Optional: PROJECTS (comma list of Core,Security,Cmdlets,Differential,Host,Shell; default the CI four) and
# FILTER (a dotnet test --filter applied to every project) — for smaller runs on the lighter fleet hosts.
# Every build and test step runs in the box's tman machine queue "default" when tman is there (every fleet box),
# so a gate waits for room instead of piling onto whatever else the box runs. Linux-only agent runs on build3 or
# beagle-ab use fleet-run (dev-setup) instead (own worktree, same queue).
BUNDLE=$1 TAG=$2 OUT=$3 PROJECTS=${4:-Core,Security,Cmdlets,Differential} FILTER=${5:-}
mkdir -p "$OUT"; SUM="$OUT/summary.txt"
s() { echo "$(date +%H:%M:%S) $*" >> "$SUM"; }
s "start tag=$TAG"
cd ~/work/core/ps-bash || { s 'ABORT: no checkout at ~/work/core/ps-bash'; s FINISHED; exit 1; }
# Tracked changes abort the run; changes that are only line endings (a CRLF blob under eol=lf, rewritten by a
# checkout: s23k) force the checkout instead.
force=
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    if ! git -c core.safecrlf=false diff --ignore-cr-at-eol --quiet 2>/dev/null; then s 'ABORT: the checkout has tracked changes'; s FINISHED; exit 1; fi
    s 'tracked changes are line endings only: forcing the checkout'; force=-f
fi
if ! { git fetch -q "$BUNDLE" refs/fleet/snapshot && git checkout -q $force --detach FETCH_HEAD; } >> "$OUT/git.log" 2>&1; then
    s 'ABORT: fetch/checkout failed (git.log)'; s FINISHED; exit 1
fi
s "checked out $(git log --oneline -1)"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
NB=(-m:1 -nodeReuse:false -p:UseSharedCompilation=false)
TM=~/.local/bin/tman Q=()
if [ -x "$TM" ] && "$TM" queue default > /dev/null 2>&1; then Q=("$TM" run --queue default --label "agent=fleet-$TAG" --stall 10m --max-time 95m --); fi
if ! "${Q[@]}" dotnet build ps-bash.sln -c Debug "${NB[@]}" > "$OUT/build.log" 2>&1; then
    s "BUILD FAILED: $(grep -m3 -E 'error [A-Z]+[0-9]+' "$OUT/build.log" | tr '\n' ' ')"; s FINISHED; exit 1
fi
s 'build ok'

run() { # name project [dotnet test args...]
    local name=$1 proj=$2; shift 2
    "${Q[@]}" timeout 90m dotnet test "src/$proj" -c Debug --no-build "${NB[@]}" --blame-hang-timeout 5m --blame-hang-dump-type none "$@" > "$OUT/$name.log" 2>&1
    s "$name: $(grep -E 'Passed!|Failed!' "$OUT/$name.log" | sed 's/^ *//' | tr '\n' ' ')"
    grep -E '^\s+Failed ' "$OUT/$name.log" | sed 's/^ */    /' >> "$SUM"
}
F=(); [ -n "$FILTER" ] && F=(--filter "$FILTER")
for p in ${PROJECTS//,/ }; do
    case $p in
        Security) run Security PsBash.Escalation.Tests --filter "Category=Security${FILTER:+&($FILTER)}" ;;
        *) run "$p" "PsBash.$p.Tests" "${F[@]}" ;;
    esac
done
dotnet build-server shutdown > /dev/null 2>&1
s FINISHED
