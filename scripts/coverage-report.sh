#!/usr/bin/env bash
# Generate an HTML coverage report for all test projects.
# Usage: ./scripts/coverage-report.sh [--open]
#
# Runs dotnet test with XPlat Code Coverage, then invokes reportgenerator
# to produce an HTML report in coverage/report/.
# Pass --open to open the report in the default browser after generation.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

open_report=0
if [[ "${1:-}" == "--open" ]]; then
    open_report=1
fi

cd "$REPO_ROOT"

echo "coverage-report.sh: running tests with coverage collection..." >&2
PSBASH_COVERAGE=1 bash scripts/test.sh

echo "coverage-report.sh: checking for coverage output..." >&2

if ! find coverage/raw -name "coverage.cobertura.xml" >/dev/null 2>&1 || \
   [[ "$(find coverage/raw -name "coverage.cobertura.xml" 2>/dev/null | wc -l | tr -d ' ')" -eq 0 ]]; then
    echo "coverage-report.sh: ERROR: no coverage XML files found in coverage/raw/" >&2
    echo "coverage-report.sh: ensure test projects reference coverlet.collector package" >&2
    exit 1
fi

xml_count=$(find coverage/raw -name "coverage.cobertura.xml" 2>/dev/null | wc -l | tr -d ' ')
echo "coverage-report.sh: found ${xml_count} coverage XML file(s)" >&2

if ! command -v reportgenerator >/dev/null 2>&1; then
    echo "coverage-report.sh: reportgenerator not found" >&2
    echo "coverage-report.sh: install with: dotnet tool install -g dotnet-reportgenerator-globaltool" >&2
    echo "coverage-report.sh: raw XML available at coverage/coverage.xml" >&2
    exit 0
fi

reports=$(find coverage/raw -name "coverage.cobertura.xml" | paste -sd ';')
mkdir -p coverage/report

reportgenerator \
    "-reports:${reports}" \
    "-targetdir:coverage/report" \
    "-reporttypes:Html;Cobertura;TextSummary"

if [[ -f coverage/report/Cobertura.xml ]]; then
    cp coverage/report/Cobertura.xml coverage/coverage.xml
fi

echo "" >&2
echo "coverage-report.sh: === Coverage Summary ===" >&2
if [[ -f coverage/report/Summary.txt ]]; then
    cat coverage/report/Summary.txt >&2
fi
echo "" >&2
echo "coverage-report.sh: full HTML report: coverage/report/index.html" >&2

if [[ "$open_report" -eq 1 ]]; then
    if command -v xdg-open >/dev/null 2>&1; then
        xdg-open coverage/report/index.html
    elif command -v open >/dev/null 2>&1; then
        open coverage/report/index.html
    elif command -v start >/dev/null 2>&1; then
        start coverage/report/index.html
    fi
fi
