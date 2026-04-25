# Negative test fixture: trap DEBUG is not supported in ps-bash
# This should produce a warning when evaluated

trap 'echo "DEBUG trap fired"' DEBUG

echo "after debug trap"
