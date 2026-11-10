## Imported from the Oils spec tests (strict-options.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### strict_arith option
shopt -s strict_arith
## stdout:
## status: 1
## timeout: 10

#### shopt -s strict_control_flow
shopt -s strict_control_flow || true
echo break
break
echo hi
## STDOUT:
break
hi
## END
## status: 0
## timeout: 10

#### continue at top level is NOT an error
# NOTE: bash and mksh both print warnings, but don't exit with an error.
continue
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### break at top level is NOT an error
break
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### empty argv WITHOUT strict_argv
x=''
$x
echo status=$?

if $x; then
  echo VarSub
fi

if $(echo foo >/dev/null); then
  echo CommandSub
fi

if "$x"; then
  echo VarSub
else
  echo VarSub FAILED
fi

if "$(echo foo >/dev/null)"; then
  echo CommandSub
else
  echo CommandSub FAILED
fi
## STDOUT:
status=0
VarSub
CommandSub
VarSub FAILED
CommandSub FAILED
## END
## status: 0
## XFAIL ps-bash gap-oils-strict-options strict-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### empty argv WITH strict_argv
shopt -s strict_argv || true
echo empty
x=''
$x
echo status=$?
## STDOUT:
empty
status=0
## END
## status: 0
## timeout: 10

#### Arrays are incorrectly compared, but strict_array prevents it

# NOTE: from spec/dbracket has a test case like this
# sane-array should turn this ON.
# bash and mksh allow this because of decay

a=('a b' 'c d')
b=('a' 'b' 'c' 'd')
echo ${#a[@]}
echo ${#b[@]}
[[ "${a[@]}" == "${b[@]}" ]] && echo EQUAL

shopt -s strict_array || true
[[ "${a[@]}" == "${b[@]}" ]] && echo EQUAL
## STDOUT:
2
4
EQUAL
EQUAL
## END
## status: 0
## timeout: 10

#### automatically creating arrays WITHOUT strict_array
undef[2]=x
undef[3]=y
argv.py "${undef[@]}"
## stdout:
## status: 127
## timeout: 10

#### automatically creating arrays are INDEXED, not associative
shopt -u strict_arith || true

undef[2]=x
undef[3]=y
x='bad'
# bad gets coerced to zero, but this is part of the RECURSIVE arithmetic
# behavior, which we want to disallow.  Consider disallowing in OSH.

undef[$x]=zzz
argv.py "${undef[@]}"
## stdout:
## status: 127
## timeout: 10

#### simple_eval_builtin
for i in 1 2; do
  eval  # zero args
  echo status=$?
  eval echo one
  echo status=$?
  eval 'echo two'
  echo status=$?
  shopt -s simple_eval_builtin
  echo ---
done
## STDOUT:
status=0
one
status=0
two
status=0
---
status=0
one
status=0
two
status=0
---
## END
## status: 0
## timeout: 10

#### shopt -s strict_binding: Persistent prefix bindings not allowed on special builtins

shopt --set strict:all

# This differs from what it means in a process
FOO=bar eval 'echo FOO=$FOO'
echo FOO=$FOO
## STDOUT:
FOO=bar
FOO=
## END
## status: 0
## XFAIL ps-bash gap-oils-strict-options strict-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### strict_parse_slice means you need explicit  length
case $SH in bash*|dash|mksh) exit ;; esac

$SH -c '
a=(1 2 3); echo /${a[@]::}/
'
echo status=$?

$SH -c '
shopt --set strict_parse_slice

a=(1 2 3); echo /${a[@]::}/
'
echo status=$?
## stdout:
## status: 0
## timeout: 10
## real-launcher
