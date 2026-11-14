## Imported from the Oils spec tests (introspect.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### ${FUNCNAME[@]} array
g() {
  argv.py "${FUNCNAME[@]}"
}
f() {
  argv.py "${FUNCNAME[@]}"
  g
  argv.py "${FUNCNAME[@]}"
}
f
## stdout:
## status: 127
## timeout: 10

#### ${FUNCNAME} with prefix/suffix operators

check() {
  argv.py "${#FUNCNAME}"
  argv.py "${FUNCNAME::1}"
  argv.py "${FUNCNAME:1}"
}
check
## stdout:
## status: 127
## timeout: 10

#### operators on FUNCNAME
check() {
  argv.py "${FUNCNAME}"
  argv.py "${#FUNCNAME}"
  argv.py "${FUNCNAME::1}"
  argv.py "${FUNCNAME:1}"
}
check
## stdout:
## status: 127
## timeout: 10

#### ${FUNCNAME} and "set -u" (OSH regression)
set -u
argv.py "$FUNCNAME"
## stdout:
## status: 1
## timeout: 10

#### $((BASH_LINENO)) (scalar form in arith)
check() {
  echo $((BASH_LINENO))
}
check
## stdout: 4
## status: 0
## XFAIL ps-bash gap-bash-lineno-unimplemented BASH_LINENO is not tracked: $((BASH_LINENO)) in a function is 0, not the caller's line
## timeout: 10

#### ${BASH_LINENO[@]} is a stack of line numbers for function calls
# note: it's CALLS, not DEFINITIONS.
g() {
  argv.py G "${BASH_LINENO[@]}"
}
f() {
  argv.py 'begin F' "${BASH_LINENO[@]}"
  g  # line 6
  argv.py 'end F' "${BASH_LINENO[@]}"
}
argv.py ${BASH_LINENO[@]}
f  # line 9
## stdout:
## status: 127
## timeout: 10
