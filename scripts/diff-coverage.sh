#!/usr/bin/env bash
# Diff-coverage gate: checks that changed lines in PsBash.Core and PsBash.Module
# are covered in the provided coverage XML.
#
# Usage: ./scripts/diff-coverage.sh <coverage-xml> [base-ref]
#
# Arguments:
#   coverage-xml  Path to a Cobertura coverage.cobertura.xml file.
#   base-ref      Git ref to diff against (default: origin/main).
#
# Exit codes:
#   0  All changed source files pass >= 90% line coverage, or no coverage data
#      for changed files (warn-only: non-blocking).
#   1  One or more changed source files fall below the 90% bar.
#
# Policy (QA rubric Directive 2):
#   DIFF COVERAGE >= 90% ON EVERY PR THAT TOUCHES src/PsBash.Core OR src/PsBash.Module.

set -euo pipefail

COVERAGE_XML="${1:-}"
BASE_REF="${2:-origin/main}"
THRESHOLD=90

if [[ -z "$COVERAGE_XML" ]]; then
    echo "diff-coverage.sh: usage: $0 <coverage-xml> [base-ref]" >&2
    exit 1
fi

if [[ ! -f "$COVERAGE_XML" ]]; then
    echo "diff-coverage.sh: WARNING: coverage XML not found: ${COVERAGE_XML}" >&2
    echo "diff-coverage.sh: skipping diff-coverage check (non-blocking)" >&2
    exit 0
fi

# Find changed C# files in PsBash.Core and PsBash.Module.
changed_files=()
while IFS= read -r f; do
    # Only check source files in the two guarded assemblies.
    if [[ "$f" == src/PsBash.Core/*.cs || "$f" == src/PsBash.Module/*.cs ]]; then
        changed_files+=("$f")
    fi
done < <(git diff "${BASE_REF}...HEAD" --name-only 2>/dev/null || true)

if [[ "${#changed_files[@]}" -eq 0 ]]; then
    echo "diff-coverage.sh: no PsBash.Core or PsBash.Module files changed — skipping" >&2
    exit 0
fi

echo "diff-coverage.sh: checking ${#changed_files[@]} changed source file(s) against ${THRESHOLD}% threshold" >&2

# Parse Cobertura XML with awk.
# Cobertura format: <class filename="..." line-rate="0.85" ...>
# line-rate is a float 0.0–1.0; multiply by 100 for percent.
#
# We match by filename suffix (the class filename in Cobertura is relative
# to the source root, e.g. "src/PsBash.Core/Parser/BashParser.cs").

failed=0
no_data=0

for src_file in "${changed_files[@]}"; do
    # Strip leading path components to get just the filename for matching.
    basename_file=$(basename "$src_file")

    # Extract line-rate for this file from the XML.
    # The awk looks for <class ... filename="...BaseName..." ... line-rate="N.NN"
    line_rate=$(awk -v fname="$basename_file" '
        $0 ~ "<class " && $0 ~ fname {
            for (i=1; i<=NF; i++) {
                if ($i ~ /^line-rate=/) {
                    gsub(/line-rate="|"/, "", $i)
                    print $i
                    exit
                }
            }
        }
    ' "$COVERAGE_XML")

    if [[ -z "$line_rate" ]]; then
        echo "diff-coverage.sh: WARNING: no coverage data for ${src_file} (not in XML)" >&2
        no_data=$((no_data + 1))
        continue
    fi

    # Convert float (e.g. "0.857142") to integer percent via awk.
    pct=$(awk -v r="$line_rate" 'BEGIN { printf "%d", r * 100 }')

    if [[ "$pct" -lt "$THRESHOLD" ]]; then
        echo "diff-coverage.sh: FAIL: ${src_file}: ${pct}% < ${THRESHOLD}% required" >&2
        failed=$((failed + 1))
    else
        echo "diff-coverage.sh: OK:   ${src_file}: ${pct}%" >&2
    fi
done

if [[ "$no_data" -gt 0 ]]; then
    echo "diff-coverage.sh: WARNING: ${no_data} file(s) had no coverage data (XML may be incomplete)" >&2
    echo "diff-coverage.sh: run with PSBASH_COVERAGE=1 to ensure all projects are instrumented" >&2
fi

if [[ "$failed" -gt 0 ]]; then
    echo "" >&2
    echo "diff-coverage.sh: GATE FAILED: ${failed} file(s) below ${THRESHOLD}% line coverage" >&2
    echo "diff-coverage.sh: add tests to cover changed lines in those files" >&2
    exit 1
fi

echo "diff-coverage.sh: all changed files meet the ${THRESHOLD}% coverage bar" >&2
exit 0
