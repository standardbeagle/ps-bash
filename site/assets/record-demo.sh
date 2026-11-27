#!/usr/bin/env bash
# Record the PsBash demo in both gif and webp formats
# Run from project root: bash site/assets/record-demo.sh
set -euo pipefail

TAPE="site/assets/demo.tape"

# Record gif
sed -i 's|^Output .*|Output site/assets/demo.gif|' "$TAPE"
vhs "$TAPE"

# Record webp
sed -i 's|^Output .*|Output site/assets/demo.webp|' "$TAPE"
vhs "$TAPE"

# Restore gif as default output
sed -i 's|^Output .*|Output site/assets/demo.gif|' "$TAPE"

echo "Done: site/assets/demo.gif + site/assets/demo.webp"
