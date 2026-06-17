#!/usr/bin/env ps-bash
# ---------------------------------------------------------------------------
# Bash commands, real PowerShell objects.
# Run:  ps-bash examples/01-bash-commands/pipelines.sh
# These are the same commands you'd type in bash — same flags, same output —
# but each one is a native PsBash cmdlet emitting typed objects.
# ---------------------------------------------------------------------------

echo "== ls -la, sorted by size (human) =="
ls -la | sort -k5 -h | head -n 5

echo
echo "== grep across files, recursively =="
# Default-prunes .git/node_modules/bin/obj like ripgrep would.
grep -rn "Format-Styled" ../../src/PsBash.Cmdlets | head -n 5

echo
echo "== awk: a real interpreter, not a flag table =="
printf 'alice 90\nbob 75\ncarol 88\n' | awk '{ total += $2 } END { print "avg:", total/NR }'

echo
echo "== sed with backreferences (\\1 -> \$1 under the hood) =="
echo "2026-06-17" | sed -E 's/([0-9]+)-([0-9]+)-([0-9]+)/\3\/\2\/\1/'

echo
echo "== jq over JSON =="
echo '{"name":"ps-bash","stars":[1,2,3]}' | jq '.stars | length'

echo
echo "== cut / tr / uniq pipeline =="
printf 'a,b,a\nc,d,c\n' | cut -d, -f1 | tr 'a-z' 'A-Z' | sort | uniq -c
