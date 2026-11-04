## Imported from the Oils spec tests (sh-func.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Locals don't leak
f() {
  local f_var=f_var
}
f
echo $f_var
## STDOUT:

## END
## status: 0
## timeout: 10

#### Globals leak
f() {
  f_var=f_var
}
f
echo $f_var
## stdout: f_var
## status: 0
## timeout: 10

#### Return statement
f() {
  echo one
  return 42
  echo two
}
f
## stdout: one
## status: 42
## timeout: 10

#### Dynamic Scope
f() {
  echo $g_var
}
g() {
  local g_var=g_var
  f
}
g
## stdout: g_var
## status: 0
## timeout: 10

#### Dynamic Scope Mutation (wow this is bad)
f() {
  g_var=f_mutation
}
g() {
  local g_var=g_var
  echo "g_var=$g_var"
  f
  echo "g_var=$g_var"
}
g
echo g_var=$g_var
## STDOUT:
g_var=g_var
g_var=f_mutation
g_var=
## END
## status: 0
## timeout: 10

#### Assign local separately
f() {
  local f
  f='new-value'
  echo "[$f]"
}
f
## stdout: [new-value]
## status: 0
## timeout: 10

#### Assign a local and global on same line
myglobal=
f() {
  local mylocal
  mylocal=L myglobal=G
  echo "[$mylocal $myglobal]"
}
f
echo "[$mylocal $myglobal]"
## STDOUT:
[L G]
[ G]
## END
## status: 0
## timeout: 10

#### Return without args gives previous
f() {
  ( exit 42 )
  return
}
f
echo status=$?
## stdout: status=42
## status: 0
## timeout: 10

#### return "" (a lot of disagreement)
f() {
  echo f
  return ""
}

f
echo status=$?
## STDOUT:
f
status=2
## END
## status: 0
## timeout: 10

#### return $empty
f() {
  echo f
  empty=
  return $empty
}

f
echo status=$?
## STDOUT:
f
status=0
## END
## status: 0
## timeout: 10

#### Subshell function

f() ( return 42; )
# BUG: OSH raises invalid control flow!  I think we should just allow 'return'
# but maybe not 'break' etc.
g() ( return 42 )
# bash warns here but doesn't cause an error
# g() ( break )

f
echo status=$?
g
echo status=$?
## STDOUT:
status=42
status=42
## END
## status: 0
## timeout: 10

#### Scope of global variable when sourced in function (Shell Functions aren't Closures)
set -u

echo >tmp.sh '
g="global"
local L="local"

test_func() {
  echo "g = $g"
  echo "L = $L"
}
'

main() {
  # a becomes local here
  # test_func is defined globally
  . ./tmp.sh
}

main

# a is not defined
test_func
## stdout: g = global
## status: 1
## XFAIL ps-bash gap-oils-sh-func sh-func (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
