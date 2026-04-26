#!/usr/bin/env bash
# Run dotnet tests and clean up all spawned processes on exit.
# Usage: ./scripts/test.sh [dotnet test args...]
#
# Coverage: set PSBASH_COVERAGE=1 to collect XPlat Code Coverage.
# Coverlet places output under coverage/raw/<guid>/coverage.cobertura.xml.
# If reportgenerator is installed, an HTML report is generated in coverage/report/.

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

# Coverage: opt-in via PSBASH_COVERAGE=1.
# Appends --collect and --results-directory to dotnet test args.
coverage_enabled="${PSBASH_COVERAGE:-0}"
coverage_args=()
if [[ "$coverage_enabled" == "1" ]]; then
    coverage_args=(
        "--collect" "XPlat Code Coverage"
        "--results-directory" "coverage/raw"
    )
    echo "test.sh: coverage collection enabled (XPlat Code Coverage)" >&2
fi

test_exit=0

if [[ "$timeout_secs" == "0" ]] || ! command -v timeout >/dev/null 2>&1; then
    dotnet test "$@" "${coverage_args[@]+"${coverage_args[@]}"}" || test_exit=$?
else
    # -k 10: if SIGTERM doesn't stop it in 10s, SIGKILL.
    timeout -k 10 "$timeout_secs" dotnet test "$@" "${coverage_args[@]+"${coverage_args[@]}"}" || test_exit=$?
    if [[ $test_exit -eq 124 ]]; then
        echo "test.sh: dotnet test exceeded PSBASH_TEST_TIMEOUT=${timeout_secs}s — killed." >&2
        exit 124
    fi
fi

# Post-test: collect coverage XMLs and generate HTML report if tools are present.
# Runs even when tests fail so partial coverage is captured.
if [[ "$coverage_enabled" == "1" ]]; then
    xml_count=$(find coverage/raw -name "coverage.cobertura.xml" 2>/dev/null | wc -l | tr -d ' ')
    echo "test.sh: found ${xml_count} coverage XML file(s)" >&2

    if [[ "$xml_count" -gt 0 ]]; then
        mkdir -p coverage
        first_xml=$(find coverage/raw -name "coverage.cobertura.xml" | head -1)
        cp "$first_xml" coverage/coverage.xml 2>/dev/null || true

        if command -v reportgenerator >/dev/null 2>&1; then
            reports=$(find coverage/raw -name "coverage.cobertura.xml" | paste -sd ';')
            reportgenerator \
                "-reports:${reports}" \
                "-targetdir:coverage/report" \
                "-reporttypes:Html;Cobertura" \
                2>/dev/null || echo "test.sh: reportgenerator failed (non-fatal)" >&2
            if [[ -f coverage/report/Cobertura.xml ]]; then
                cp coverage/report/Cobertura.xml coverage/coverage.xml
            fi
            echo "test.sh: HTML report generated in coverage/report/" >&2
        else
            echo "test.sh: reportgenerator not found; raw XML at coverage/coverage.xml" >&2
            echo "test.sh: install: dotnet tool install -g dotnet-reportgenerator-globaltool" >&2
        fi
    fi
fi

exit $test_exit
