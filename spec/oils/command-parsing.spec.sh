## Imported from the Oils spec tests (command-parsing.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Prefix env on assignment
f() {
  # NOTE: local treated like a special builtin!
  E=env local v=var
  echo $E $v
}
f
## stdout: var
## status: 0
## timeout: 10

#### Redirect on assignment (enabled 7/2019)
f() {
  # NOTE: local treated like a special builtin!
  local E=env > _tmp/r.txt
}
rm -f _tmp/r.txt
f
test -f _tmp/r.txt && echo REDIRECTED
## stdout: REDIRECTED
## status: 0
## timeout: 10

#### Prefix env on control flow
for x in a b c; do
  echo $x
  E=env break
done
## stdout: a
## status: 0
## timeout: 10

#### Redirect on control flow (ignored in OSH)
rm -f _tmp/r.txt
for x in a b c; do
  break > _tmp/r.txt
done
if test -f _tmp/r.txt; then
  echo REDIRECTED
else
  echo NO
fi
## stdout: REDIRECTED
## status: 0
## XFAIL ps-bash gap-oils-command-parsing command-parsing (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
