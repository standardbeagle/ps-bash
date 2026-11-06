## Imported from the Oils spec tests (builtin-special.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Non-special builtins CAN be redefined as functions
test -n "$BASH_VERSION" && set -o posix
true() {
  echo 'true func'
}
true hi
echo status=$?
## STDOUT:
true func
status=0
## END
## status: 0
## timeout: 10

#### true is not special; prefix assignments don't persist, it can be redefined
foo=bar true
echo foo=$foo

true() {
  echo true func
}
foo=bar true
echo foo=$foo



# POSIX rule about special builtins pointed at:
#
# https://www.reddit.com/r/oilshell/comments/5ykpi3/oildev_is_alive/
## STDOUT:
foo=
true func
foo=
## END
## status: 0
## timeout: 10

#### Prefix assignments persist after special builtins, like : (set -o posix)
case $SH in
  bash) set -o posix ;;
esac

foo=bar :
echo foo=$foo

# Not true when you use 'builtin'
z=Z builtin :
echo z=$Z
## STDOUT:
foo=bar
z=
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Prefix assignments persist after readonly, but NOT exported (set -o posix)

# Bash only implements it behind the posix option
case $SH in
  bash) set -o posix ;;
esac
foo=bar readonly spam=eggs
echo foo=$foo
echo spam=$spam

# should NOT be exported
printenv.py foo
printenv.py spam
## STDOUT:
foo=bar
spam=eggs
## END
## status: 127
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Prefix binding for exec is a special case (versus e.g. readonly)

pre1=pre1 readonly x=x
pre2=pre2 exec sh -c 'echo pre1=$pre1 x=$x pre2=$pre2'
## stdout: pre1= x= pre2=pre2
## status: 0
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### exec without args is a special case of the special case in some shells

FOO=bar exec >& 2
echo FOO=$FOO
#declare -p | grep FOO
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Which shells allow special builtins to be redefined?
eval() {
  echo 'eval func' "$@"
}
eval 'echo hi'

# we allow redefinition, but the definition is NOT used!

# we PREVENT redefinition

# should not allow redefinition
## stdout: eval func echo hi
## status: 0
## timeout: 10

#### Special builtins can't be redefined as shell functions (set -o posix)
case $SH in
  bash) set -o posix ;;
esac

eval 'echo hi'

eval() {
  echo 'sh func' "$@"
}

eval 'echo hi'
## stdout: hi
## status: 2
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Shift is special and fails whole script

# https://pubs.opengroup.org/onlinepubs/9699919799/utilities/V3_chap02.html#tag_18_14
#
# 2.8.1 - Consequences of shell errors
#
# Special built-ins should exit a non-interactive shell
# bash and busybox dont't implement this even with set -o posix, so it seems risky
# dash and mksh do it; so does AT&T ksh

$SH -c '
if test -n "$BASH_VERSION"; then
  set -o posix
fi
set -- a b
shift 3
echo status=$?
'
if test "$?" != 0; then
  echo 'non-zero status'
fi
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### set is special and fails whole script, even if using || true
$SH -c '
if test -n "$BASH_VERSION"; then
  set -o posix
fi

shopt -s invalid_ || true
echo ok
set -o invalid_ || true
echo should not get here
'
if test "$?" != 0; then
  echo 'non-zero status'
fi
## STDOUT:
ok
should not get here
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### bash 'type' gets confused - says 'function', but runs builtin
case $SH in dash|mksh|zsh|ash|yash) exit ;; esac

echo TRUE
type -t true  # builtin
true() { echo true func; }
type -t true  # now a function
echo ---

echo EVAL

type -t eval  # builtin
# define function before set -o posix
eval() { echo "shell function: $1"; }
# bash runs the FUNCTION, but OSH finds the special builtin
# OSH doesn't need set -o posix
eval 'echo before posix'

if test -n "$BASH_VERSION"; then
  # this makes the eval definition invisible!
  set -o posix
fi

eval 'echo after posix'  # this is the builtin eval
# bash claims it's a function, but it's a builtin
type -t eval

# it finds the function and the special builtin
#type -a eval
## STDOUT:
TRUE
builtin
function
---
EVAL
builtin
shell function: echo before posix
after posix
function
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### command, builtin - both can be redefined, not special (regression)
case $SH in dash|ash|yash) exit ;; esac

builtin echo b
command echo c

builtin() {
  echo builtin-redef "$@"
}

command() {
  echo command-redef "$@"
}

builtin echo b
command echo c
## STDOUT:
b
c
builtin-redef echo b
command-redef echo c
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-special builtin-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
