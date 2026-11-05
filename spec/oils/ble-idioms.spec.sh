## Imported from the Oils spec tests (ble-idioms.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### recursive arith: one level
a='b=123'
echo $((a))
## stdout: 123
## status: 0
## timeout: 10

#### recursive arith: two levels
a='b=c' c='d=123'
echo $((a))
## stdout: 123
## status: 0
## timeout: 10

#### recursive arith: short circuit &&, ||
# Note: mksh R52 has a bug. Even though it supports a short circuit like
#   "echo $((cond&&(a=1)))", it doesn't work with "x=a=1; echo
#   $((cond&&x))". It is fixed at least in mksh R57.
# Note: "busybox sh" doesn't support short circuit.
a=b=123
echo $((1||a)):$((b))
echo $((0||a)):$((b))
c=d=321
echo $((0&&c)):$((d))
echo $((1&&c)):$((d))
## STDOUT:
1:0
1:123
0:0
1:321
## END
## status: 0
## timeout: 10

#### recursive arith: short circuit ?:
# Note: "busybox sh" behaves strangely.
y=a=123 n=a=321
echo $((1?(y):(n))):$((a))
echo $((0?(y):(n))):$((a))
## STDOUT:
123:123
321:321
## END
## status: 0
## timeout: 10

#### recursive arith: side effects
# In Zsh and Busybox sh, the side effect of inner arithmetic
# evaluations seems to take effect only after the whole evaluation.
a='b=c' c='d=123'
echo $((a,d)):$((d))
## stdout: 123:123
## status: 0
## timeout: 10

#### recursive arith: recursion
loop='i<=100&&(s+=i,i++,loop)' s=0 i=0
echo $((a=loop,s))
## stdout: 5050
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### recursive arith: array elements
text[1]='d=123'
text[2]='text[1]'
text[3]='text[2]'
echo $((a=text[3]))
## stdout: 123
## status: 0
## timeout: 10

#### dynamic arith varname: assign
vec2_set () {
  local this=$1 x=$2 y=$3
  : $(( ${this}_x = $2 ))
  : $(( ${this}_y = y ))
}
vec2_set a 3 4
vec2_set b 5 12
echo a_x=$a_x a_y=$a_y
echo b_x=$b_x b_y=$b_y
## STDOUT:
a_x=3 a_y=4
b_x=5 b_y=12
## END
## status: 0
## timeout: 10

#### dynamic arith varname: read

vec2_load() {
  local this=$1
  x=$(( ${this}_x ))
  : $(( y = ${this}_y ))
}
a_x=12 a_y=34
vec2_load a
echo x=$x y=$y
## stdout: x=12 y=34
## status: 0
## timeout: 10

#### dynamic arith varname: copy/add
shopt -s eval_unsafe_arith  # for RHS

vec2_copy () {
  local this=$1 rhs=$2
  : $(( ${this}_x = $(( ${rhs}_x )) ))
  : $(( ${this}_y = ${rhs}_y ))
}
vec2_add () {
  local this=$1 rhs=$2
  : $(( ${this}_x += $(( ${rhs}_x )) ))
  : $(( ${this}_y += ${rhs}_y ))
}
a_x=3 a_y=4
b_x=4 b_y=20
vec2_copy c a
echo c_x=$c_x c_y=$c_y
vec2_add c b
echo c_x=$c_x c_y=$c_y
## STDOUT:
c_x=3 c_y=4
c_x=7 c_y=24
## END
## status: 0
## timeout: 10

#### Sparse array with big index

# TODO: more InternalStringArray idioms / stress tests ?

a=()

if false; then
  # This takes too long!  # From Zulip
  i=$(( 0x0100000000000000 ))
else
  # smaller number that's OK
  i=$(( 0x0100000 ))
fi

a[i]=1

echo len=${#a[@]}
## stdout: len=1
## status: 0
## timeout: 10

#### Issue #1069 [49] BUG: \return 0 does not work
f0() { return 3;          echo unexpected; return 0; }
f1() { \return 3;         echo unexpected; return 0; }
f0; echo "status=$?"
f1; echo "status=$?"
## STDOUT:
status=3
status=3
## END
## status: 0
## timeout: 10

#### Issue #1069 [49] BUG: \return 0 does not work (other variations)
f2() { builtin return 3;  echo unexpected; return 0; }
f3() { \builtin return 3; echo unexpected; return 0; }
f4() { command return 3;  echo unexpected; return 0; }
f2; echo "status=$?"
f3; echo "status=$?"
f4; echo "status=$?"
# Note: zsh does not allow calling builtin through command
# Note: ash does not have "builtin"
## STDOUT:
status=3
status=3
status=3
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Issue #1069 [56] BUG: declare -p unset does not print any error message
typeset -p nonexistent
## stdout:
## status: 1
## timeout: 10

#### Issue #1069 [57] - Variable v should be visible after IFS= eval 'local v=...'

set -u

f() {
  # The temp env messes it up
  IFS= eval "local v=\"\$*\""

  # Bug does not appear with only eval
  # eval "local v=\"\$*\""

  #declare -p v
  echo v=$v

  # test -v v; echo "v defined $?"
}

f h e l l o
## stdout: v=hello
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### is-array with ${var@a}
case $SH in mksh|ash|dash|yash) exit 1 ;; esac

function ble/is-array { [[ ${!1@a} == *a* ]]; }

ble/is-array undef
echo undef $?

string=''
ble/is-array string
echo string $?

array=(one two three)
ble/is-array array
echo array $?
## STDOUT:
undef 1
string 1
array 0
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### shift unshift reverse

case $SH in mksh|ash) exit ;; esac

# https://github.com/akinomyoga/ble.sh/blob/79beebd928cf9f6506a687d395fd450d027dc4cd/src/util.sh#L578-L582

# @fn ble/array#unshift arr value...
function ble/array#unshift {
  builtin eval -- "$1=(\"\${@:2}\" \"\${$1[@]}\")"
}
# @fn ble/array#shift arr count
function ble/array#shift {
  # Note: Bash 4.3 以下では ${arr[@]:${2:-1}} が offset='${2'
  # length='-1' に解釈されるので、先に算術式展開させる。
  builtin eval -- "$1=(\"\${$1[@]:$((${2:-1}))}\")"
}
# @fn ble/array#reverse arr
function ble/array#reverse {
  builtin eval "
  set -- \"\${$1[@]}\"; $1=()
  local e$1 i$1=\$#
  for e$1; do $1[--i$1]=\"\$e$1\"; done"
}

a=( {1..6} )
echo "${a[@]}"

ble/array#shift a 1
echo "${a[@]}"

ble/array#shift a 2
echo "${a[@]}"

echo ---

ble/array#unshift a 99
echo "${a[@]}"

echo ---

# doesn't work in zsh!
ble/array#reverse a
echo "${a[@]}"




# Note: yash does not support calling function name with '#'
## STDOUT:
1 2 3 4 5 6
2 3 4 5 6
4 5 6
---
99 4 5 6
---
6 5 4 99
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### shopt -u expand_aliases and eval
case $SH in zsh|mksh|ash|dash|yash) exit ;; esac

alias echo=false

function f {
  shopt -u expand_aliases
  eval -- "$1"
  shopt -s expand_aliases
}

f 'echo hello'
## stdout: hello
## status: 0
## timeout: 10
## real-launcher

#### Issue #1069 [40] BUG: a=(declare v); "${a[@]}" fails
case $SH in ash|dash)  exit 99 ;; esac
a=(typeset v=1)
v=x
"${a[@]}"
echo "v=$v"
# Note: ash/dash does not have arrays
## stdout: v=1
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Issue #1069 [40] BUG: a=declare; "$a" v=1 fails
case $SH in ash|dash)  exit 99 ;; esac
a=typeset
v=x
"$a" v=1
echo "v=$v"
## stdout: v=1
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Issue #1069 [52] BUG: \builtin local v=1 fails
case $SH in ash|dash|yash) exit 99 ;; esac
v=x
case $SH in
mksh) f1() { \builtin typeset v=1; echo "l:v=$v"; } ;;
*)    f1() { \builtin local   v=1; echo "l:v=$v"; } ;;
esac
f1
echo "g:v=$v"
# Note: ash/dash/yash does not have "builtin"
## STDOUT:
l:v=1
g:v=x
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Issue #1069 [53] BUG: a[1 + 1]=2, etc. fails
case $SH in ash|dash|yash) exit 99 ;; esac
a=()

a[1]=x
eval 'a[5&3]=hello'
echo "status=$?, a[1]=${a[1]}"

a[2]=x
eval 'a[1 + 1]=hello'
echo "status=$?, a[2]=${a[2]}"

a[3]=x
eval 'a[1|2]=hello'
echo "status=$?, a[3]=${a[3]}"
# Note: ash/dash does not have arrays
# Note: yash does not support a[index]=value
## STDOUT:
status=0, a[1]=hello
status=0, a[2]=hello
status=0, a[3]=hello
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Issue #1069 [53] - LHS array parsing a[1 + 2]=3 (see spec/array-assign for more)
case $SH in zsh|ash) exit ;; esac

a[1 + 2]=7
a[3|4]=8
a[(1+2)*3]=9

typeset -p a

# Dynamic parsing
expr='1 + 2'
a[expr]=55

b=(42)
expr='b[0]'
a[3 + $expr - 4]=66

typeset -p a
## STDOUT:
declare -a a=([3]="7" [7]="8" [9]="9")
declare -a a=([3]="55" [7]="8" [9]="9" [41]="66")
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Issue #1069 [57] BUG: variable v is invisible after IFS= eval 'local v=...'
v=x
case $SH in
mksh) f() { IFS= eval 'typeset v=1'; echo "l:$v"; } ;;
*)    f() { IFS= eval 'local   v=1'; echo "l:$v"; } ;;
esac
f
echo "g:$v"
## STDOUT:
l:1
g:x
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Issue #1069 [59] N-I: arr=s should set RHS to arr[0]
case $SH in ash|dash) exit 99 ;; esac
a=(1 2 3)
a=v
argv.py "${a[@]}"
# Note: ash/dash does not have arrays
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### Issue #1069 [59] - Assigning Str to BashArray/BashAssoc should not remove BashArray/BashAssoc
case $SH in zsh|ash) exit ;; esac

a=(1 2 3)
a=99
typeset -p a

typeset -A A=([k]=v)
A=99
typeset -p A
## STDOUT:
declare -a a=([0]="99" [1]="2" [2]="3")
declare -A A=([0]="99" [k]="v" )
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-idioms ble-idioms (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
