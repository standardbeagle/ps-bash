## Imported from the Oils spec tests (parse-errors.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### $% is not a parse error
echo $%
## stdout: $%
## status: 0
## timeout: 10

#### Bad braced var sub -- not allowed
echo ${%}
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Bad var sub caught at parse time
if test -f /; then
  echo ${%}
else
  echo ok
fi
## stdout: ok
## status: 0
## timeout: 10

#### Incomplete while
echo hi; while
echo status=$?
## stdout:
## status: 2
## timeout: 10

#### Incomplete for
echo hi; for
echo status=$?
## stdout:
## status: 2
## timeout: 10

#### Incomplete if
echo hi; if
echo status=$?
## stdout:
## status: 2
## timeout: 10

#### do unexpected
do echo hi
## stdout:
## status: 2
## timeout: 10

#### } is a parse error
}
echo should not get here
## stdout:
## status: 2
## timeout: 10

#### { is its own word, needs a space
# bash and mksh give parse time error because of }
# dash gives 127 as runtime error
{ls; }
echo "status=$?"
## stdout:
## status: 2
## timeout: 10

#### } on the second line
set -o errexit
{ls;
}
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Invalid for loop variable name
for i.j in a b c; do
  echo hi
done
echo done
## stdout: done
## status: 0
## timeout: 10

#### bad var name globally isn't parsed like an assignment
# bash and dash disagree on exit code.
FOO-BAR=foo
## stdout:
## status: 127
## timeout: 10

#### bad var name in export
# bash and dash disagree on exit code.
export FOO-BAR=foo
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### bad var name in local
# bash and dash disagree on exit code.
f() {
  local FOO-BAR=foo
}
f
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### misplaced parentheses are not a subshell
echo a(b)
## stdout:
## status: 2
## timeout: 10

#### incomplete command sub
$(x
## stdout:
## status: 2
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### incomplete backticks
`x
## stdout:
## status: 2
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### misplaced ;;
echo 1 ;; echo 2
## stdout:
## status: 2
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### empty clause in [[

# regression test for commit 451ca9e2b437e0326fc8155783d970a6f32729d8
[[ || true ]]
## stdout:
## status: 2
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### array literal inside array is a parse error
a=( inside=() )
echo len=${#a[@]}
## stdout: len=0
## status: 0
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### array literal inside loop is a parse error
f() {
  for x in a=(); do
    echo x=$x
  done
  echo done
}
f
## stdout:
## status: 2
## timeout: 10

#### array literal in case
f() {
  case a=() in
    foo)
      echo hi
      ;;
  esac
}
f
## stdout:
## status: 2
## timeout: 10

#### %foo=() is parse error (regression)

# Lit_VarLike and then (, but NOT at the beginning of a word.

f() {
  %foo=()
}
## stdout:
## status: 2
## XFAIL ps-bash gap-oils-parse-errors parse-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### echo =word is allowed
echo =word
## stdout: =word
## status: 0
## timeout: 10

#### Long Token - 65535 bytes

python2 -c 'print("echo -n %s" % ("x" * 65535))' > tmp.sh
$SH tmp.sh > out
wc --bytes out
## stdout: 0 out
## status: 0
## timeout: 10
## real-launcher

#### Token that's too long for Oils - 65536 bytes

python2 -c 'print("echo -n %s" % ("x" * 65536))' > tmp.sh
$SH tmp.sh > out
echo status=$?
wc --bytes out
## STDOUT:
status=0
0 out
## END
## status: 0
## timeout: 10
## real-launcher

#### interactive parse error (regression)
flags=''
case $SH in
  bash*|*osh)
    flags='--rcfile /dev/null'
    ;;
esac  
$SH $flags -i -c 'var=)'
## stdout:
## status: 2
## timeout: 10
## real-launcher
