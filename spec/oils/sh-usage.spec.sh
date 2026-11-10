## Imported from the Oils spec tests (sh-usage.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### exit with explicit arg
exit 42
## stdout:
## status: 42
## timeout: 10

#### exit with no args
false
exit
## stdout:
## status: 1
## timeout: 10

#### Other flag parsers are not affected by - rule

# -c is special, with quit_parsing_flags

echo 'foo-bar' | { read -d -; echo reply=$REPLY; }
## stdout: reply=foo
## status: 0
## timeout: 10

#### sh +c is accepted
$SH +c 'echo hi'
## stdout: hi
## status: 0
## XFAIL ps-bash gap-oils-sh-usage sh-usage (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### empty stdin
# had a bug here
echo -n '' | $SH
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### sh - and sh -- stop flag processing
case $SH in zsh) exit ;; esac

echo 'echo foo' > foo.sh

$SH -x -v -- foo.sh

echo -  
echo - >& 2

$SH -x -v - foo.sh


# I think it turns off -x -v with -
echo foo
+ echo foo
-

# set -o verbose not implemented for now
## STDOUT:
foo
-
foo
foo
## END
## status: 127
## XFAIL ps-bash gap-oils-sh-usage sh-usage (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### --rcfile in non-interactive shell prints warnings
echo 'echo rc' > rc

$SH --rcfile rc -i </dev/null 2>interactive.txt
grep -q 'warning' interactive.txt
echo warned=$? >&2

$SH --rcfile rc </dev/null 2>non-interactive.txt
grep -q 'warning' non-interactive.txt
echo warned=$? >&2

head *interactive.txt
## STDOUT:
rc
==> interactive.txt <==
beagle@beagle-ab2:/tmp/tmp.yKLKrlwxiE$ exit

==> non-interactive.txt <==
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-usage sh-usage (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### accepts --login flag (dash and mksh don't accept long flags)
$SH --login -c 'exit 0'
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### osh --eval
case $SH in bash|dash|mksh|zsh) exit ;; esac

echo 'echo one "$@"' > one.sh
echo 'echo fail "$@"; ( exit 42 )' > fail.sh

$SH --eval one.sh \
  -c 'echo status=$? flag -c "$@"' dummy x y z
echo

# Even though errexit is off, the shell exits if the last status of an --eval
# file was non-zero.

$SH --eval one.sh --eval fail.sh \
  -c 'echo status=$? flag -c "$@"' dummy x y z
echo status=$?
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### sh -c -- 'echo hi' does not run anything (#2637)

$SH -c -z 'echo z'
if test $? -ne 0; then
  echo 'z failed'
fi
echo

$SH -c --- 'echo three'
if test $? -ne 0; then
  echo three failed
fi
echo

$SH -c -- 'echo two'
echo two=$?
echo

$SH -c - 'echo one'
echo one=$?
echo

$SH -c '' 'echo zero'
echo zero=$?
echo

# odd
$SH -c 'echo aa' 'echo bb'
echo aa=$?
## STDOUT:
z failed

three failed

two
two=0

one
one=0

zero=0

aa
aa=0
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-usage sh-usage (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### sh -c with multiple -- args

# variant of above case

$SH -c -- -- 'echo two'
echo status=$?

$SH -c -- -- -- 'echo two'
echo status=$?

$SH -c -z 'echo z'
if test $? -ne 0; then
  echo 'z failed'
fi
## STDOUT:
status=127
status=127
z failed
## END
## status: 0
## timeout: 10
## real-launcher

#### weird flag parsing -oo errexit noglob

prog='
case $- in
  *e*) echo e ;;
esac

case $- in
  *f*) echo f ;;
esac
'

# normal way
$SH -o errexit -o noglob -c "$prog"

# odd way
$SH -oo errexit noglob -c "$prog"



e
## STDOUT:
e
f
e
f
## END
## status: 127
## XFAIL ps-bash gap-oils-sh-usage sh-usage (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### 'sh -c -z' does not try to run -z

$SH -c -z
case $? in
  1) echo flag-parsing-error ;;
  2) echo flag-parsing-error ;;
  *) echo fail ;;
esac

$SH -c 'echo 0=$0 1=$1' -z
echo status=$?

$SH -c 'echo 0=$0 1=$1' foo -z
echo status=$?
## STDOUT:
flag-parsing-error
0=-z 1=
status=0
0=foo 1=-z
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-usage sh-usage (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### sh -c -x 'echo hi' - order is reversed
case $SH in zsh) exit ;; esac  # different -x format

$SH -c -x 'echo hi'

# two flags before the command
$SH -c -x -e 'zz; true' 2> /dev/null
echo status=$?
## STDOUT:
hi
status=127
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-usage sh-usage (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
