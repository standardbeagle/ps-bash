## Imported from the Oils spec tests (assign-extended.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### local -a
# nixpkgs setup.sh uses this (issue #26)
f() {
  local -a array=(x y z)
  argv.py "${array[@]}"
}
f
## stdout:
## status: 127
## timeout: 10

#### declare -a
# nixpkgs setup.sh uses this (issue #26)
declare -a array=(x y z)
argv.py "${array[@]}"
## stdout:
## status: 127
## timeout: 10

#### declare -f exit code indicates function existence
func2=x  # var names are NOT found
declare -f myfunc func2
echo $?

myfunc() { echo myfunc; }
declare -f myfunc func2 > /dev/null
echo $?

func2() { echo func2; }
declare -f myfunc func2 > /dev/null
echo $?
## STDOUT:
1
1
0
## END
## status: 0
## timeout: 10

#### declare -F prints function names
add () { expr 4 + 4; }
div () { expr 6 / 2; }
ek () { echo hello; }
__ec () { echo hi; }
_ab () { expr 10 % 3; }

declare -F
## STDOUT:
declare -f __ec
declare -f _ab
declare -f add
declare -f div
declare -f ek
## END
## status: 0
## timeout: 10

#### declare -p var (exit status)
var1() { echo func; }  # function names are NOT found.
declare -p var1 var2 >/dev/null
echo $?

var1=x
declare -p var1 var2 >/dev/null
echo $?

var2=y
declare -p var1 var2 >/dev/null
echo $?
## STDOUT:
1
1
0
## END
## status: 0
## timeout: 10

#### declare
test_var1=111
readonly test_var2=222
export test_var3=333
declare -n test_var4=test_var1
f1() {
  local test_var5=555
  {
    echo '[declare]'
    declare
    echo '[readonly]'
    readonly
    echo '[export]'
    export
    echo '[local]'
    local
  } | grep -E '^\[|^\b.*test_var.\b'
}
f1
## STDOUT:
[declare]
test_var1=111
test_var2=222
test_var3=333
test_var4=test_var1
test_var5=555
[readonly]
declare -r test_var2="222"
[export]
declare -x test_var3="333"
[local]
declare -- test_var5="555"
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -p
# BUG: bash doesn't output flags with "local -p", which seems to contradict
#   with manual.
test_var1=111
readonly test_var2=222
export test_var3=333
declare -n test_var4=test_var1
f1() {
  local test_var5=555
  {
    echo '[declare]'
    declare -p
    echo '[readonly]'
    readonly -p
    echo '[export]'
    export -p
    echo '[local]'
    local -p
  } | grep -E '^\[|^\b.*test_var.\b'
}
f1
## STDOUT:
[declare]
declare -- test_var1="111"
declare -r test_var2="222"
declare -x test_var3="333"
declare -n test_var4="test_var1"
declare -- test_var5="555"
[readonly]
declare -r test_var2="222"
[export]
declare -x test_var3="333"
[local]
declare -- test_var5="555"
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### typeset -f
# mksh implement typeset but not declare
typeset  -f myfunc func2
echo $?

myfunc() { echo myfunc; }
# This prints the source code.
typeset  -f myfunc func2 > /dev/null
echo $?

func2() { echo func2; }
typeset  -f myfunc func2 > /dev/null
echo $?
## STDOUT:
1
1
0
## END
## status: 0
## timeout: 10

#### myvar=typeset (another form of dynamic assignment)
myvar=typeset
x='a b'
$myvar x=$x
echo $x
## stdout: a
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### dynamic array parsing is not allowed
code='x=(1 2 3)'
typeset -a "$code"  # note: -a flag is required
echo status=$?
argv.py "$x"
# bash allows it
## stdout: status=0
## status: 127
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### dynamic flag in array in assign builtin
typeset b
b=(unused1 unused2)  # this works in mksh

a=(x 'foo=F' 'bar=B')
typeset -"${a[@]}"
echo foo=$foo
echo bar=$bar
printenv.py foo
printenv.py bar

# syntax error in mksh!  But works in bash and zsh.
#typeset -"${a[@]}" b=(spam eggs)
#echo "length of b = ${#b[@]}"
#echo "b[0]=${b[0]}"
#echo "b[1]=${b[1]}"
## STDOUT:
foo=F
bar=B
## END
## status: 127
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### typeset +x
export e=E
printenv.py e
typeset +x e=E2
printenv.py e  # no longer exported
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### typeset +r removes read-only attribute (TODO: documented in bash to do nothing)
readonly r=r1
echo r=$r

# clear the readonly flag.  Why is this accepted in bash, but doesn't do
# anything?
typeset +r r=r2 
echo r=$r

r=r3
echo r=$r


# mksh doesn't allow you to unset

# bash doesn't allow you to unset
## STDOUT:
r=r1
r=r1
r=r1
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### function name with /
ble/foo() { echo hi; }
declare -F ble/foo
echo status=$?
## STDOUT:
ble/foo
status=0
## END
## status: 0
## timeout: 10

#### invalid var name
typeset foo/bar
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### unset and shell funcs
foo() {
  echo bar
}

foo

declare -F
unset foo
declare -F

foo
## STDOUT:
bar
declare -f foo
## END
## status: 127
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -p doesn't print binary data, but can be loaded into bash

# bash prints binary data!
case $SH in bash*|mksh) exit ;; esac

unquoted='foo'
sq='foo bar'
bash1=$'\x1f'  # ASCII control char
bash2=$'\xfe\xff'  # Invalid UTF-8

s1=$unquoted
s2=$sq
s3=$bash1
s4=$bash2

declare -a a=("$unquoted" "$sq" "$bash1" "$bash2")
declare -A A=(["$unquoted"]="$sq" ["$bash1"]="$bash2")

#echo lengths ${#s1} ${#s2} ${#s3} ${#s4} ${#a[@]} ${#A[@]}

declare -p s1 s2 s3 s4 a A | tee tmp.bash

echo ---

bash -c 'source tmp.bash; echo "$s1 $s2"; echo -n "$s3" "$s4" | od -A n -t x1'
echo bash=$?
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### declare -p var
# BUG? bash doesn't output anything for 'local/readonly -p var', which seems to
#   contradict with manual.  Besides, 'export -p var' is not described in
#   manual
test_var1=111
readonly test_var2=222
export test_var3=333
declare -n test_var4=test_var1
f1() {
  local test_var5=555
  {
    echo '[declare]'
    declare -p test_var{0..5}
    echo '[readonly]'
    readonly -p test_var{0..5}
    echo '[export]'
    export -p test_var{0..5}
    echo '[local]'
    local -p test_var{0..5}
  } | grep -E '^\[|^\b.*test_var.\b'
}
f1
## STDOUT:
[declare]
declare -- test_var1="111"
declare -r test_var2="222"
declare -x test_var3="333"
declare -n test_var4="test_var1"
declare -- test_var5="555"
[readonly]
[export]
[local]
declare -rx test_var5="555"
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -p arr
test_arr1=()
declare -a test_arr2=()
declare -A test_arr3=()
test_arr4=(1 2 3)
declare -a test_arr5=(1 2 3)
declare -A test_arr6=(['a']=1 ['b']=2 ['c']=3)
test_arr7=()
test_arr7[3]=foo
declare -p test_arr{1..7}
## STDOUT:
declare -a test_arr1=()
declare -a test_arr2=()
declare -A test_arr3=()
declare -a test_arr4=([0]="1" [1]="2" [2]="3")
declare -a test_arr5=([0]="1" [1]="2" [2]="3")
declare -A test_arr6=([c]="3" [b]="2" [a]="1" )
declare -a test_arr7=([3]="foo")
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -p foo=bar doesn't make sense
case $SH in mksh) exit 0 ;; esac

declare -p foo=bar
echo status=$?

a=b
declare -p a foo=bar > tmp.txt
echo status=$?
sed 's/"//g' tmp.txt  # don't care about quotes
## STDOUT:
status=1
status=1
declare -- a=b
## END
## status: 0
## timeout: 10
## real-launcher

#### declare -pnrx
test_var1=111
readonly test_var2=222
export test_var3=333
declare -n test_var4=test_var1
f1() {
  local test_var5=555
  {
    echo '[declare -pn]'
    declare -pn
    echo '[declare -pr]'
    declare -pr
    echo '[declare -px]'
    declare -px
  } | grep -E '^\[|^\b.*test_var.\b'
}
f1
## STDOUT:
[declare -pn]
declare -n test_var4="test_var1"
[declare -pr]
declare -r test_var2="222"
[declare -px]
declare -x test_var3="333"
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -paA
declare -a test_var6=()
declare -A test_var7=()
f1() {
  {
    echo '[declare -pa]'
    declare -pa
    echo '[declare -pA]'
    declare -pA
  } | grep -E '^\[|^\b.*test_var.\b'
}
f1
## STDOUT:
[declare -pa]
declare -a test_var6=()
[declare -pA]
declare -A test_var7=()
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -pnrx var
# Note: Bash ignores other flags (-nrx) when variable names are supplied while
#   OSH uses other flags to select variables.  Bash's behavior is documented.
test_var1=111
readonly test_var2=222
export test_var3=333
declare -n test_var4=test_var1
f1() {
  local test_var5=555
  {
    echo '[declare -pn]'
    declare -pn test_var{0..5}
    echo '[declare -pr]'
    declare -pr test_var{0..5}
    echo '[declare -px]'
    declare -px test_var{0..5}
  } | grep -E '^\[|^\b.*test_var.\b'
}
f1
## STDOUT:
[declare -pn]
declare -- test_var1="111"
declare -r test_var2="222"
declare -x test_var3="333"
declare -n test_var4="test_var1"
declare -- test_var5="555"
[declare -pr]
declare -- test_var1="111"
declare -r test_var2="222"
declare -x test_var3="333"
declare -n test_var4="test_var1"
declare -- test_var5="555"
[declare -px]
declare -- test_var1="111"
declare -r test_var2="222"
declare -x test_var3="333"
declare -n test_var4="test_var1"
declare -- test_var5="555"
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -pg
test_var1=global
f1() {
  local test_var1=local
  {
    declare -pg
  } | grep -E '^\[|^\b[^"]*test_var.\b'
}
f1
## stdout: declare -- test_var1="local"
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -pg var
test_var1=global
f1() {
  local test_var1=local
  {
    declare -pg test_var1
  } | grep -E '^\[|^\b.*test_var.\b'
}
f1
## stdout: declare -- test_var1="local"
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ble.sh: eval -- "$(declare -p var arr)"
# This illustrates an example usage of "eval & declare" for exporting
# multiple variables from $().
eval -- "$(
  printf '%s\n' a{1..10} | {
    sum=0 i=0 arr=()
    while read line; do
      ((sum+=${#line},i++))
      arr[$((i/3))]=$line
    done
    declare -p sum arr
  })"
echo sum=$sum
for ((i=0;i<${#arr[@]};i++)); do
  echo "arr[$i]=${arr[i]}"
done
## STDOUT:
sum=21
arr[0]=a2
arr[1]=a5
arr[2]=a8
arr[3]=a10
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -p and value.Undef

# This is a regression for a crash
# But actually there is also an incompatibility -- we don't print anything

declare x
declare -p x

function f { local x; declare -p x; }
x=1
f
## STDOUT:
declare -- x
declare -- x
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### eval -- "$(declare -p arr)" (restore arrays w/ unset elements)
arr=(1 2 3)
eval -- "$(arr=(); arr[3]= arr[4]=foo; declare -p arr)"
for i in {0..4}; do
  echo "arr[$i]: ${arr[$i]+set ... [}${arr[$i]-unset}${arr[$i]+]}"
done
## STDOUT:
arr[0]: unset
arr[1]: unset
arr[2]: unset
arr[3]: set ... []
arr[4]: set ... [foo]
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -p UNDEF (and typeset) -- prints something to stderr

x=42
readonly x
export x

declare -p x undef1 undef2 2> de

typeset -p x undef1 undef2 2> ty

# readonly -p and export -p don't accept args!  They only print all
#
# These do not accept args
# readonly -p x undef1 undef2 2> re
# export -p x undef1 undef2 2> ex

f() {
  # it behaves weird with x
  #local -p undef1 undef2 2>lo
  local -p a b b>lo
  #local -p x undef1 undef2 2> lo
}
# local behaves differently in bash 4.4 and bash 5, not specifying now
# f
# files='de ty lo'

files='de ty'

wc -l $files
#cat $files
## STDOUT:
declare -rx x="42"
declare -rx x="42"
  2 de
  2 ty
  4 total
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### typeset -p
var1() { echo func; }  # function names are NOT found.
typeset -p var1 var2 >/dev/null
echo $?

var1=x
typeset -p var1 var2 >/dev/null
echo $?

var2=y
typeset -p var1 var2 >/dev/null
echo $?
## STDOUT:
1
1
0
## END
## status: 0
## timeout: 10

#### typeset -r makes a string readonly
typeset -r s1='12'
typeset -r s2='34'

s1='c'
echo status=$?
s2='d'
echo status=$?

s1+='e'
echo status=$?
s2+='f'
echo status=$?

unset s1
echo status=$?
unset s2
echo status=$?
## STDOUT:
status=1
status=1
status=1
status=1
status=1
status=1
## END
## status: 0
## timeout: 10

#### typeset -ar makes it readonly
typeset -a -r array1=(1 2)
typeset -ar array2=(3 4)

array1=('c')
echo status=$?
array2=('d')
echo status=$?

array1+=('e')
echo status=$?
array2+=('f')
echo status=$?

unset array1
echo status=$?
unset array2
echo status=$?
## STDOUT:
status=1
status=1
status=1
status=1
status=1
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### typeset -x makes it exported
typeset -rx PYTHONPATH=lib/
printenv.py PYTHONPATH
## stdout:
## status: 127
## timeout: 10

#### Multiple assignments / array assignments on a line
a=1 b[0+0]=2 c=3
echo $a ${b[@]} $c
## stdout: 1 2 3
## status: 0
## timeout: 10

#### Env bindings shouldn't contain array assignments
a=1 b[0]=2 c=3 printenv.py a b c
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### syntax error in array assignment
a=x b[0+]=y c=z
echo $a $b $c
## stdout: x
## status: 0
## XFAIL ps-bash gap-oils-assign-extended assign-extended (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -g (bash-specific; bash-completion uses it)
f() {
  declare -g G=42
  declare L=99

  declare -Ag dict
  dict["foo"]=bar

  declare -A localdict
  localdict["spam"]=Eggs

  # For bash-completion
  eval 'declare -Ag ev'
  ev["ev1"]=ev2
}
f
argv.py "$G" "$L"
argv.py "${dict["foo"]}" "${localdict["spam"]}"
argv.py "${ev["ev1"]}"
## stdout:
## status: 127
## timeout: 10
