## Imported from the Oils spec tests (builtin-set.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### can continue after unknown option
#
# TODO: this is the posix special builtin logic?
# dash and mksh make this a fatal error no matter what.

set -o errexit
set -o STRICT || true # unknown option
echo hello
## stdout: hello
## status: 0
## timeout: 10

#### set with both options and argv
set -o errexit a b c
echo "$@"
false
echo done
## stdout: a b c
## status: 1
## timeout: 10

#### nounset with "$@"
set a b c
set -u  # shouldn't touch argv
echo "$@"
## stdout: a b c
## status: 0
## timeout: 10

#### set -u -- clears argv
set a b c
set -u -- # shouldn't touch argv
echo "$@"
## STDOUT:

## END
## status: 0
## timeout: 10

#### set -u -- x y z
set a b c
set -u -- x y z
echo "$@"
## stdout: x y z
## status: 0
## timeout: 10

#### reset option with long flag
set -o errexit
set +o errexit
echo "[$unset]"
## stdout: []
## status: 0
## timeout: 10

#### reset option with short flag
set -u 
set +u
echo "[$unset]"
## stdout: []
## status: 0
## timeout: 10

#### set -eu (flag parsing)
set -eu 
echo "[$unset]"
echo status=$?
## stdout:
## status: 1
## timeout: 10

#### set -o lists options
# NOTE: osh doesn't use the same format yet.
set -o | grep -o noexec
## stdout: noexec
## status: 0
## timeout: 10

#### set - - and so forth
set a b
echo "$@"

set - a b
echo "$@"

set -- a b
echo "$@"

set - -
echo "$@"

set -- --
echo "$@"

# note: zsh is different, and yash is totally different
## STDOUT:
a b
a b
a b
-
--
## END
## status: 0
## timeout: 10

#### set - leading single dash is ignored, turns off xtrace verbose (#2364)

show_options() {
  case $- in
    *v*) echo verbose-on ;;
  esac
  case $- in
    *x*) echo xtrace-on ;;
  esac
}

set -x -v
show_options
echo

set - a b c
echo "$@"
show_options
echo

# dash that's not leading is not special
set x - y z
echo "$@"
## STDOUT:
verbose-on
xtrace-on

a b c

x - y z
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set - + and + -
set - +
echo "$@"

set + -
echo "$@"
## STDOUT:
+
+
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set -a exports variables
set -a
FOO=bar
BAZ=qux
printenv.py FOO BAZ
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set +a stops exporting
set -a
FOO=exported
set +a
BAR=not_exported
printenv.py FOO BAR
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set -o allexport (long form)
set -o allexport
VAR1=value1
set +o allexport
VAR2=value2
printenv.py VAR1 VAR2
## stdout:
## status: 127
## timeout: 10

#### set -u with undefined variable exits the interpreter

# non-interactive
$SH -c 'set -u; echo before; echo $x; echo after'
if test $? -ne 0; then
  echo OK
fi

# interactive
$SH -i -c 'set -u; echo before; echo $x; echo after'
if test $? -ne 0; then
  echo OK
fi
## STDOUT:
before
OK
before
OK
## END
## status: 0
## timeout: 10
## real-launcher

#### set -u with undefined var in interactive shell does NOT exit the interpreter

# In bash, it aborts the LINE only.  The next line is executed!

# non-interactive
$SH -c 'set -u; echo before; echo $x; echo after
echo line2
'
if test $? -ne 0; then
  echo OK
fi

# interactive
$SH -i -c 'set -u; echo before; echo $x; echo after
echo line2
'
if test $? -ne 0; then
  echo OK
fi
## STDOUT:
before
OK
before
line2
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### set -u error can break out of nested evals
$SH -c '
set -u
test_function_2() {
  x=$blarg
}
test_function() {
  eval "test_function_2"
}

echo before
eval test_function
echo after
'
# status must be non-zero: bash uses 1, ash/dash exit 2
if test $? -ne 0; then
  echo OK
fi
## STDOUT:
before
OK
## END
## status: 0
## timeout: 10
## real-launcher

#### set - stops option processing like set --
case $SH in zsh) exit ;; esac

show_options() {
  case $- in
    *v*) echo verbose-on ;;
  esac
  case $- in
    *x*) echo xtrace-on ;;
  esac
}

set -x - -v

show_options
echo argv "$@"
## stdout: argv -v
## status: 0
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### A single + is an ignored flag; not an argument
case $SH in zsh) exit ;; esac

show_options() {
  case $- in
    *v*) echo verbose-on ;;
  esac
  case $- in
    *x*) echo xtrace-on ;;
  esac
}

set +
echo plus "$@"

set -x + -v x y
show_options
echo plus "$@"
## STDOUT:
plus
verbose-on
xtrace-on
plus x y
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### variables set before set -a are not exported
BEFORE=before_value
set -a
AFTER=after_value
printenv.py BEFORE AFTER
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set -a exports local variables
set -a
f() {
  local ZZZ=zzz
  printenv.py ZZZ
}
f
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set -a exports declare variables
set -a
declare ZZZ=zzz
printenv.py ZZZ
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-builtin-set builtin-set (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
