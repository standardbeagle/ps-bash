#!/usr/bin/env bash
# Run dotnet tests and clean up all spawned processes on exit.
# Usage: ./scripts/test.sh [dotnet test args...]

set -euo pipefail

cleanup() {
    # Graceful shutdown first
    dotnet build-server shutdown 2>/dev/null || true
    # Kill all MSBuild node-reuse workers (includes orphans from prior runs)
    pkill -f "MSBuild.dll.*nodeReuse:true" 2>/dev/null || true
    pkill -f "testhost" 2>/dev/null || true
    pkill -f "vstest"   2>/dev/null || true
    # Kill any stray dotnet test drivers, ps-bash shells, and pwsh workers
    # spawned by this run (or leftover from a prior aborted run).
    pkill -f 'dotnet.*\btest\b' 2>/dev/null || true
    pkill -f 'ps-bash\.exe'     2>/dev/null || true
    pkill -f ps-bash-worker     2>/dev/null || true
}

trap cleanup EXIT
trap cleanup INT

# Wall-clock timeout for the whole `dotnet test` driver. Bounds blast radius
# if test discovery, MSBuild, or a spawned subprocess hangs (e.g. stdin-EOF
# waits). Override with PSBASH_TEST_TIMEOUT=<seconds> or `0` to disable.
timeout_secs="${PSBASH_TEST_TIMEOUT:-900}"

if [[ "$timeout_secs" == "0" ]] || ! command -v timeout >/dev/null 2>&1; then
    dotnet test "$@"
else
    # -k 10: if SIGTERM doesn't stop it in 10s, SIGKILL.
    timeout -k 10 "$timeout_secs" dotnet test "$@"
    rc=$?
    if [[ $rc -eq 124 ]]; then
        echo "test.sh: dotnet test exceeded PSBASH_TEST_TIMEOUT=${timeout_secs}s — killed." >&2
    fi
    exit $rc
fi
