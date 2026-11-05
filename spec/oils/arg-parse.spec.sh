## Imported from the Oils spec tests (arg-parse.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### true extra
true extra
## stdout:
## status: 0
## timeout: 10

#### shift 1 extra
$SH -c '
set -- a b c
shift 1 extra
'
if test $? -eq 0; then
  echo fail
fi
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### continue 1 extra, break, etc.
$SH -c '
for i in foo; do
  continue 1 extra
done
echo status=$?
'
if test $? -eq 0; then
  echo fail
fi



status=1
fail
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-arg-parse arg-parse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
