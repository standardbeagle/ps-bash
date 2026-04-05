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
}

trap cleanup EXIT

dotnet test "$@"
