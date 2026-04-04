#!/usr/bin/env bash
# Record the PsBash demo in both gif and webp formats
# Run from project root: bash docs/assets/record-demo.sh
set -euo pipefail

TAPE="docs/assets/demo.tape"

# Record gif
sed -i 's|^Output .*|Output docs/assets/demo.gif|' "$TAPE"
vhs "$TAPE"

# Record webp
sed -i 's|^Output .*|Output docs/assets/demo.webp|' "$TAPE"
vhs "$TAPE"

# Restore gif as default output
sed -i 's|^Output .*|Output docs/assets/demo.gif|' "$TAPE"

echo "Done: docs/assets/demo.gif + docs/assets/demo.webp"
