#!/usr/bin/env bash
# Run dotnet tests and clean up all spawned processes on exit.
# Usage: ./scripts/test.sh [dotnet test args...]
#
# Coverage: set PSBASH_COVERAGE=1 to collect XPlat Code Coverage.
# Coverlet places output under coverage/raw/<guid>/coverage.cobertura.xml.
# If reportgenerator is installed, an HTML report is generated in coverage/report/.
#
# Two-binary host environment variables:
#
#   PSBASH_HOST=/path/to/ps-bash-host
#       Override the host binary the launcher resolves. Useful when testing
#       a freshly-built host against an installed launcher, or when running
#       the differential suite against a non-default host build (e.g. a
#       debug publish in another worktree). Default resolution looks beside
#       the launcher executable.
#
# Pass either via the caller's environment, e.g.
#   PSBASH_HOST=$PWD/dist/bin/ps-bash-host ./scripts/test.sh

set -euo pipefail

cleanup() {
    # Graceful shutdown first.
    dotnet build-server shutdown 2>/dev/null || true

    # On Windows, `pkill` is not available (Git Bash does not ship it), so the
    # previous cleanup silently no-op'd on Windows. That left ps-bash.exe and
    # ps-bash-host.exe children running from src/PsBash.Shell/bin/Debug after
    # a test run; subsequent `dotnet build` invocations then failed copying
    # ps-bash.exe because the binary was locked. Use taskkill.exe with exact
    # image-name matching so we never touch unrelated user processes (e.g.
    # an interactive `pwsh` session would NOT be matched here).
    # Windows: kill ONLY this repo's leaked test processes, not every testhost /
    # vstest / ps-bash on the box. On a shared machine with many concurrent agents
    # (each in its own worktree) the old blanket `taskkill //IM testhost.exe`
    # aborted OTHER agents' in-flight test runs (#17). Scope by matching this
    # repo's path in the process ExecutablePath/CommandLine via a CIM query.
    if command -v pwsh >/dev/null 2>&1 && command -v taskkill.exe >/dev/null 2>&1; then
        PSBASH_CLEANUP_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -W 2>/dev/null || pwd)" \
        pwsh -NoProfile -Command '
            $root = $env:PSBASH_CLEANUP_ROOT.Replace("\","/").ToLower()
            if ([string]::IsNullOrWhiteSpace($root)) { return }
            Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -in @("testhost.exe","vstest.console.exe","ps-bash.exe","ps-bash-host.exe") } |
              Where-Object { "$($_.ExecutablePath) $($_.CommandLine)".Replace("\","/").ToLower().Contains($root) } |
              ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        ' 2>/dev/null || true
    elif command -v taskkill.exe >/dev/null 2>&1; then
        # Fallback (no pwsh): blanket image kill. Machine-wide — only reached on a
        # box without PowerShell, where the shared-agent hazard does not apply.
        taskkill.exe //F //IM ps-bash-host.exe //T 2>/dev/null || true
        taskkill.exe //F //IM ps-bash.exe      //T 2>/dev/null || true
        taskkill.exe //F //IM testhost.exe        //T 2>/dev/null || true
        taskkill.exe //F //IM vstest.console.exe  //T 2>/dev/null || true
    elif command -v pkill >/dev/null 2>&1; then
        # POSIX path: pkill -f matches the full command line. Patterns are
        # anchored to the on-disk binary path so a similarly-named binary in
        # the user's PATH (or an interactive pwsh) is left alone.
        pkill -f "MSBuild\.dll.*nodeReuse:true" 2>/dev/null || true
        pkill -f "testhost"                     2>/dev/null || true
        pkill -f "vstest"                       2>/dev/null || true
        pkill -f 'dotnet.*\btest\b'             2>/dev/null || true
        pkill -f '/ps-bash($|[[:space:]])'      2>/dev/null || true
        pkill -f '/ps-bash-host($|[[:space:]])' 2>/dev/null || true
    fi
}

trap cleanup EXIT
trap cleanup INT

# Wall-clock timeout for the whole `dotnet test` driver. Bounds blast radius
# if test discovery, MSBuild, or a spawned subprocess hangs (e.g. stdin-EOF
# waits). Override with PSBASH_TEST_TIMEOUT=<seconds> or `0` to disable.
# 1500s headroom: Differential ~8m + Shell ~7m + Canary ~7m + per-project
# build cost. 900s was too tight and consistently truncated Canary on a
# warm cache after a clean bin/obj.
timeout_secs="${PSBASH_TEST_TIMEOUT:-1500}"

# Bound the explicit pre-build separately. The test timeout below cannot help
# when MSBuild stalls before test dispatch, which previously left the suite
# with only the "pre-build solution" marker and no actionable failure.
# Override with PSBASH_BUILD_TIMEOUT=<seconds> or `0` to disable.
build_timeout_secs="${PSBASH_BUILD_TIMEOUT:-600}"

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

# Stress isolation. Host-startup stress tests (real ps-bash-host spawns and
# concurrent cold-start races) carry [Trait("Category","Stress")]. They are
# CPU-heavy and EXCLUDED from the default run so the fast suite does not
# thundering-herd the box (the exact saturation that trips the host's startup
# timeout). Run them explicitly:
#   ./scripts/test.sh --stress         # only the stress suite
#   PSBASH_STRESS=1 ./scripts/test.sh  # same, via env
# A caller-supplied --filter wins and disables this auto-filtering entirely.
run_stress="${PSBASH_STRESS:-0}"
forwarded_args=()
caller_has_filter=0
for a in "$@"; do
    case "$a" in
        --stress) run_stress=1 ;;
        --filter|--filter=*) caller_has_filter=1; forwarded_args+=("$a") ;;
        *) forwarded_args+=("$a") ;;
    esac
done

filter_args=()
if [[ "$caller_has_filter" -eq 0 ]]; then
    if [[ "$run_stress" == "1" ]]; then
        echo "test.sh: running ONLY Category=Stress tests" >&2
        filter_args=("--filter" "Category=Stress")
    else
        filter_args=("--filter" "Category!=Stress")
    fi
elif [[ "$run_stress" == "1" ]]; then
    echo "test.sh: --stress ignored because an explicit --filter was provided" >&2
fi

# Build the whole solution explicitly BEFORE `dotnet test`. Rationale:
# `dotnet test` on a solution dispatches one vstest worker per project in
# parallel and only guarantees each project's *own* build is done before
# its tests start, not that downstream binaries the tests load at runtime
# (ps-bash.exe, ps-bash-host.exe) are fully linked. PsBash.Differential.Tests
# discovers ps-bash via PsBashLocator at fixture init; if PsBash.Shell hasn't
# finished linking by then, BashOracleFixture.PsBashPath is null and ~200
# tests Skip with "ps-bash binary not found". An explicit `dotnet build`
# first closes the race. We deliberately do NOT pass --no-build to the
# subsequent `dotnet test` — that flag suppresses test-project post-build
# steps that several suites depend on, regressing previously-green Host /
# Escalation / Shell. The redundant build is cheap (incremental no-ops).
echo "test.sh: pre-build solution to avoid fixture/build race..." >&2
if [[ "$build_timeout_secs" == "0" ]] || ! command -v timeout >/dev/null 2>&1; then
    if ! dotnet build ps-bash.sln -nologo; then
        echo "test.sh: pre-build failed — skipping test dispatch." >&2
        exit 1
    fi
else
    build_exit=0
    timeout -k 10 "$build_timeout_secs" dotnet build ps-bash.sln -nologo || build_exit=$?
    if [[ $build_exit -eq 124 ]]; then
        echo "test.sh: pre-build exceeded PSBASH_BUILD_TIMEOUT=${build_timeout_secs}s — killed before test dispatch." >&2
        exit 124
    elif [[ $build_exit -ne 0 ]]; then
        echo "test.sh: pre-build failed with exit ${build_exit} — skipping test dispatch." >&2
        exit "$build_exit"
    fi
fi

test_exit=0

if [[ "$timeout_secs" == "0" ]] || ! command -v timeout >/dev/null 2>&1; then
    dotnet test "${forwarded_args[@]+"${forwarded_args[@]}"}" "${filter_args[@]+"${filter_args[@]}"}" "${coverage_args[@]+"${coverage_args[@]}"}" || test_exit=$?
else
    # -k 10: if SIGTERM doesn't stop it in 10s, SIGKILL.
    timeout -k 10 "$timeout_secs" dotnet test "${forwarded_args[@]+"${forwarded_args[@]}"}" "${filter_args[@]+"${filter_args[@]}"}" "${coverage_args[@]+"${coverage_args[@]}"}" || test_exit=$?
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
