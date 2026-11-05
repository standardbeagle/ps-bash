## Imported from the Oils spec tests (xtrace.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### set -o verbose prints unevaluated code
set -o verbose
x=foo
y=bar
echo $x
echo $(echo $y)
## STDOUT:
foo
bar
## END
## status: 0
## timeout: 10

#### xtrace with paths
set -o xtrace
echo my-dir/my_file.cc
## stdout: my-dir/my_file.cc
## status: 0
## timeout: 10

#### xtrace with whitespace, quotes, and backslash
set -o xtrace
echo '1 2' \' \" \\

# YSH is different because backslashes require $'\\' and not '\', but that's OK
## stdout: 1 2 ' " \
## status: 0
## timeout: 10

#### xtrace with newlines
# bash and dash trace this badly.  They print literal newlines, which I don't
# want.
set -x
echo $'[\n]'
# bash has ugly output that spans lines
## STDOUT:
[
]
## END
## status: 0
## timeout: 10

#### xtrace written before command executes
set -x
echo one >&2
echo two >&2
## stdout:
## status: 0
## timeout: 10

#### Assignments and assign builtins
set -x
x=1 x=2; echo $x; readonly x=3
## stdout: 2
## status: 0
## timeout: 10

#### PS4 is scoped
set -x
echo one
f() { 
  local PS4='- '
  echo func;
}
f
echo two
## STDOUT:
one
func
two
## END
## status: 0
## timeout: 10

#### xtrace with variables in PS4
PS4='+$x:'
set -o xtrace
x=1
echo one
x=2
echo two
## STDOUT:
one
two
## END
## status: 0
## timeout: 10

#### PS4 with unterminated ${
# osh shows inline error; maybe fail like dash/mksh?
x=1
PS4='+${x'
set -o xtrace
echo one
echo status=$?
# mksh and dash both fail.  bash prints errors to stderr.
## STDOUT:
one
status=0
## END
## status: 0
## timeout: 10

#### PS4 with unterminated $(
# osh shows inline error; maybe fail like dash/mksh?
x=1
PS4='+$(x'
set -o xtrace
echo one
echo status=$?
# mksh and dash both fail.  bash prints errors to stderr.
## STDOUT:
one
status=0
## END
## status: 0
## timeout: 10

#### PS4 with runtime error
# osh shows inline error; maybe fail like dash/mksh?
x=1
PS4='+oops $(( 1 / 0 )) \$'
set -o xtrace
echo one
echo status=$?
# mksh and dash both fail.  bash prints errors to stderr.
## STDOUT:
one
status=0
## END
## status: 0
## timeout: 10

#### Reading $? in PS4
PS4='[last=$?] '
set -x
false
echo ok
## stdout: ok
## status: 0
## timeout: 10

#### unset PS4
case $SH in dash) echo 'weird bug'; exit ;; esac

set -x
echo 1
unset PS4
echo 2
## STDOUT:
1
2
## END
## status: 0
## timeout: 10
## real-launcher

#### xtrace with unprintable chars
case $SH in dash) exit ;; esac

$SH >stdout 2>stderr <<'EOF'

s=$'a\x03b\004c\x00d'
set -o xtrace
echo "$s"
EOF

show_hex() { od -A n -t c -t x1; }

echo STDOUT
cat stdout | show_hex
echo

echo STDERR
grep 'echo' stderr 
## STDOUT:
STDOUT
   a 003   b 004   c  \n
  61  03  62  04  63  0a

STDERR
+ echo $'a\003b\004c'
## END
## status: 0
## XFAIL ps-bash gap-oils-xtrace xtrace (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### xtrace with unicode chars
case $SH in dash) exit ;; esac

mu1='[μ]'
mu2=$'[\u03bc]'

set -o xtrace
echo "$mu1" "$mu2"
## stdout: [μ] [μ]
## status: 0
## timeout: 10
## real-launcher

#### xtrace with tabs
case $SH in dash) exit ;; esac

set -o xtrace
echo $'[\t]'
# this is a bug because it's hard to see
## stdout: [	]
## status: 0
## timeout: 10
## real-launcher

#### [[ ]]
case $SH in dash|mksh) exit ;; esac

set -x

dir=/
if [[ -d $dir ]]; then
  (( a = 42 ))
fi
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### Regression: xtrace for "declare -a a+=(v)"
case $SH in dash|mksh) exit ;; esac

a=(1)
set -x
declare a+=(2)
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### Regression: xtrace for "a+=(v)"
case $SH in dash|mksh) exit ;; esac

a=(1)
set -x
a+=(2)
## stdout:
## status: 0
## timeout: 10
## real-launcher
