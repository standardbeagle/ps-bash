#!/usr/bin/env bash
# skip-report.sh — Parse TRX test result files and emit a skip summary.
#
# Usage:
#   ./scripts/skip-report.sh <results-dir> [<suite-label>]
#
# Exits 0 if all skips have recognized reasons.
# Exits 1 if any skip has an unrecognized reason (fail CI).
#
# Allow-listed skip reasons (case-insensitive substring match):
#   - "ps-bash binary not found"
#   - "pwsh not found"
#   - "bash not available on this platform"
#   - "platform:windows"
#   - "platform:linux"
#   - "platform:macos"
#   - "oracle: no bash available"
#   - any reason containing both "skip" and a platform name
#   - "bash 4" (macOS ships bash 3.x)
#   - "wsl" (WSL not available)
#   - "build first"

set -euo pipefail

RESULTS_DIR="${1:-.}"
SUITE_LABEL="${2:-}"
EXIT_CODE=0

# Collect all TRX files
TRX_FILES=()
while IFS= read -r -d '' f; do
    TRX_FILES+=("$f")
done < <(find "$RESULTS_DIR" -name "*.trx" -print0 2>/dev/null)

if [[ ${#TRX_FILES[@]} -eq 0 ]]; then
    echo "[skip-report] No TRX files found in: $RESULTS_DIR"
    exit 0
fi

LABEL_PREFIX=""
if [[ -n "$SUITE_LABEL" ]]; then
    LABEL_PREFIX="[$SUITE_LABEL] "
fi

# Allowlist patterns (lowercase for case-insensitive substring match)
ALLOW_LIST=(
    "ps-bash binary not found"
    "pwsh not found"
    "bash not available"
    "platform:windows"
    "platform:linux"
    "platform:macos"
    "oracle: no bash"
    "no bash available"
    "bash 4"
    "bash version"
    "wsl"
    "build first"
)

# Regex patterns checked separately (ERE syntax via grep -E)
ALLOW_REGEX=(
    "skip.*(windows|linux|macos|mac)"
    "(windows|linux|macos|mac).*skip"
)

is_allowed() {
    local reason_lower
    reason_lower=$(echo "$1" | tr '[:upper:]' '[:lower:]')
    # Substring match
    for pattern in "${ALLOW_LIST[@]}"; do
        if [[ "$reason_lower" == *"$pattern"* ]]; then
            return 0
        fi
    done
    # Regex match
    for regex in "${ALLOW_REGEX[@]}"; do
        if echo "$reason_lower" | grep -qE "$regex" 2>/dev/null; then
            return 0
        fi
    done
    return 1
}

TOTAL_SKIPPED=0
UNRECOGNIZED=0

for trx in "${TRX_FILES[@]}"; do
    # Extract NotExecuted results with their messages using python (available everywhere)
    # Falls back to grep-based extraction if python3 not available
    if command -v python3 >/dev/null 2>&1; then
        mapfile -t SKIP_REASONS < <(python3 - "$trx" <<'PYEOF'
import sys, xml.etree.ElementTree as ET

trx_path = sys.argv[1]
try:
    tree = ET.parse(trx_path)
    root = tree.getroot()
    # Handle TRX namespace
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    for result in root.findall('.//t:UnitTestResult', ns):
        if result.get('outcome') == 'NotExecuted':
            output = result.find('t:Output', ns)
            msg = ''
            if output is not None:
                err = output.find('t:ErrorInfo', ns)
                if err is not None:
                    msg_el = err.find('t:Message', ns)
                    if msg_el is not None and msg_el.text:
                        msg = msg_el.text.strip().replace('\n', ' ')
            test_name = result.get('testName', 'unknown')
            print(f"{test_name}|||{msg}")
except Exception as e:
    print(f"(parse error: {e})|||", file=sys.stderr)
PYEOF
        )
    else
        # Fallback: grep for NotExecuted blocks (POSIX grep, no -P)
        mapfile -t SKIP_REASONS < <(grep 'outcome="NotExecuted"' "$trx" | \
            sed 's/.*testName="\([^"]*\)".*/\1|||unknown reason/' 2>/dev/null || true)
    fi

    for entry in "${SKIP_REASONS[@]}"; do
        [[ -z "$entry" ]] && continue
        test_name="${entry%%|||*}"
        reason="${entry##*|||}"
        TOTAL_SKIPPED=$((TOTAL_SKIPPED + 1))

        if is_allowed "$reason"; then
            echo "${LABEL_PREFIX}SKIP (allowed)  $test_name — ${reason:-no reason}"
        else
            echo "${LABEL_PREFIX}SKIP (UNRECOGNIZED)  $test_name — ${reason:-no reason}"
            UNRECOGNIZED=$((UNRECOGNIZED + 1))
            EXIT_CODE=1
        fi
    done
done

echo ""
echo "${LABEL_PREFIX}Skip summary: $TOTAL_SKIPPED skipped, $UNRECOGNIZED unrecognized."

if [[ $EXIT_CODE -ne 0 ]]; then
    echo "${LABEL_PREFIX}ERROR: $UNRECOGNIZED test(s) skipped without a recognized reason. Add [Trait(\"Platform\",\"...\")] + [SkippableFact] with a reason from the allow-list."
fi

exit $EXIT_CODE
