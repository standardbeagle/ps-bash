## Imported from the Oils spec tests (array-literal.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Tilde expansions in RHS of [k]=v (BashArray)
HOME=/home/user
a=([2]=~ [4]=~:~:~)
echo "${a[2]}"
echo "${a[4]}"
## STDOUT:
/home/user
/home/user:/home/user:/home/user
## END
## status: 0
## timeout: 10

#### Tilde expansions in RHS of [k]=v (BashAssoc)
# Note: bash-5.2 has a bug that the tilde doesn't expand on the right hand side
# of [key]=value.  This problem doesn't happen in bash-3.1..5.1 and bash-5.3.
HOME=/home/user
declare -A a
declare -A a=(['home']=~ ['hello']=~:~:~)
echo "${a['home']}"
echo "${a['hello']}"
## STDOUT:
~
~:~:~
## END
## status: 0
## timeout: 10

#### index increments without [k]= (BashArray)
a=([100]=1 2 3 4)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a=([100]=1 2 3 4 [5]=a b c d)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
## stdout-json: "keys: vals: keys: vals: "
## status: 127
## timeout: 10

#### [k]=$v and [k]="$@" (BashArray)
i=5
v='1 2 3'
a=($v [i]=$v)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"

x=(3 5 7)
a=($v [i]="${x[*]}")
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a=($v [i]="${x[@]}")
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a=($v [i]=${x[*]})
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a=($v [i]=${x[@]})
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
## stdout-json: "keys: vals: keys: vals: keys: vals: keys: vals: keys: vals: "
## status: 127
## timeout: 10

#### [k]=$v and [k]="$@" (BashAssoc)
i=5
v='1 2 3'
declare -A a
a=([i]=$v)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"

x=(3 5 7)
a=([i]="${x[*]}")
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a=([i]="${x[@]}")
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a=([i]=${x[*]})
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a=([i]=${x[@]})
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
## stdout-json: "keys: vals: keys: vals: keys: vals: keys: vals: keys: vals: "
## status: 127
## timeout: 10

#### append to element (BashArray)
hello=100
a=([hello]=1 [hello]+=2)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a+=([hello]+=:34 [hello]+=:56)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
## stdout-json: "keys: vals: keys: vals: "
## status: 127
## timeout: 10

#### append to element (BashAssoc)
declare -A a
hello=100
a=([hello]=1 [hello]+=2)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a+=([hello]+=:34 [hello]+=:56)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
# Bash >= 5.1 has a bug. Bash <= 5.0 is OK.
## stdout-json: "keys: vals: keys: vals: "
## status: 127
## timeout: 10

#### non-index forms of element (BashAssoc)
declare -A a
a=([j]=1 2 3 4)
echo "status=$?"
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
# Bash outputs warning messages and succeeds (exit status 0)
## stdout-json: "status=0\nkeys: vals: "
## status: 127
## timeout: 10

#### Evaluation order (1)
# RHS of [k]=v are expanded when the initializer list is instanciated.  For the
# indexed array, the array indices are evaluated when the array is modified.
i=1
a=([100+i++]=$((i++)) [200+i++]=$((i++)) [300+i++]=$((i++)))
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
## stdout-json: "keys: vals: "
## status: 127
## timeout: 10

#### Evaluation order (2)
# When evaluating the index, the modification to the array by the previous item
# of the initializer list is visible to the current item.
a=([0]=1+2+3 [a[0]]=10 [a[6]]=hello)
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
## stdout-json: "keys: vals: "
## status: 127
## timeout: 10

#### Evaluation order (3)
# RHS should be expanded before any modification to the array.
a=(old1 old2 old3)
a=("${a[2]}" "${a[0]}" "${a[1]}" "${a[2]}" "${a[0]}")
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
a=(old1 old2 old3)
old1=101 old2=102 old3=103
new1=201 new2=202 new3=203
a+=([0]=new1 [1]=new2 [2]=new3 [5]="${a[2]}" [a[0]]="${a[0]}" [a[1]]="${a[1]}")
printf 'keys: '; argv.py "${!a[@]}"
printf 'vals: '; argv.py "${a[@]}"
## stdout-json: "keys: vals: keys: vals: "
## status: 127
## timeout: 10

#### [k1]=v1 (BashArray)
# Note: This and next tests have originally been in "spec/assign.test.sh" and
# compared the behavior of OSH's BashAssoc and Bash's indexed array.  After
# supporting "arr=([index]=value)" for indexed arrays, the test was adjusted
# and copied here. See also the corresponding tests in "spec/assign.test.sh"
a=([k1]=v1 [k2]=v2)
echo ${a["k1"]}
echo ${a["k2"]}
## STDOUT:
v2
v2
## END
## status: 0
## XFAIL ps-bash gap-oils-array-literal array-literal (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [k1]=v1 (BashAssoc)
declare -A a
a=([k1]=v1 [k2]=v2)
echo ${a["k1"]}
echo ${a["k2"]}
## STDOUT:
v1
v2
## END
## status: 0
## timeout: 10

#### [k1]=v1 looking like brace expansions (BashAssoc)
declare -A a
a=([k2]=-{a,b}-)
echo ${a["k2"]}
## stdout: -{a,b}-
## status: 0
## timeout: 10

#### [k1]=v1 looking like brace expansions (BashArray)
a=([k2]=-{a,b}-)
echo ${a["k2"]}
## stdout: [k2]=-a-
## status: 0
## XFAIL ps-bash gap-oils-array-literal array-literal (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### BashArray cannot be changed to BashAssoc and vice versa
declare -a a=(1 2 3 4)
eval 'declare -A a=([a]=x [b]=y [c]=z)'
echo status=$?
argv.py "${a[@]}"

declare -A A=([a]=x [b]=y [c]=z)
eval 'declare -a A=(1 2 3 4)'
echo status=$?
argv.py $(printf '%s\n' "${A[@]}" | sort)
## STDOUT:
status=1
status=1
## END
## status: 127
## XFAIL ps-bash gap-oils-array-literal array-literal (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### (strict_array) s+=()
case $SH in bash) ;; *) shopt --set strict_array ;; esac

s1=hello
s2=world

# Overwriting Str with a new BashArray is allowed
eval 's1=(1 2 3 4)'
echo status=$?
declare -p s1
# Promoting Str to a BashArray is disallowed
eval 's2+=(1 2 3 4)'
echo status=$?
declare -p s2
## STDOUT:
status=0
declare -a s1=([0]="1" [1]="2" [2]="3" [3]="4")
status=0
declare -a s2=([0]="world" [1]="1" [2]="2" [3]="3" [4]="4")
## END
## status: 0
## XFAIL ps-bash gap-oils-array-literal array-literal (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### (strict_array) declare -A s+=()
case $SH in bash) ;; *) shopt --set strict_array ;; esac

s1=hello
s2=world

# Overwriting Str with a new BashAssoc is allowed
eval 'declare -A s1=([a]=x [b]=y)'
echo status=$?
declare -p s1
# Promoting Str to a BashAssoc is disallowed
eval 'declare -A s2+=([a]=x [b]=y)'
echo status=$?
declare -p s2
## STDOUT:
status=0
declare -A s1=([b]="y" [a]="x" )
status=0
declare -A s2=([0]="world" [b]="y" [a]="x" )
## END
## status: 0
## XFAIL ps-bash gap-oils-array-literal array-literal (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### (strict_array) assoc=(key value ...) is not allowed
case $SH in bash) ;; *) shopt --set strict_array ;; esac

declare -A a=([a]=b)
eval "a=(1 2 3 4)"
declare -p a
## stdout: declare -A a=([3]="4" [1]="2" )
## status: 0
## XFAIL ps-bash gap-oils-array-literal array-literal (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
