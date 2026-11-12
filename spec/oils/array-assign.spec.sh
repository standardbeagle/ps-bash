## Imported from the Oils spec tests (array-assign.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Indexed LHS without spaces, and +=
a[1]=x
echo status=$?
argv.py "${a[@]}"

a[0+2]=y
#a[2|3]=y  # zsh doesn't allow this
argv.py "${a[@]}"

# += does appending
a[0+2]+=z
argv.py "${a[@]}"
## stdout: status=0
## status: 127
## timeout: 10

#### Are quotes allowed?

# double quotes allowed in bash
a["1"]=2
echo status=$? len=${#a[@]}

a['2']=3
echo status=$? len=${#a[@]}

# allowed in bash
a[2 + "3"]=5
echo status=$? len=${#a[@]}

a[3 + '4']=5
echo status=$? len=${#a[@]}



# syntax errors are not fatal in bash
## STDOUT:
status=0 len=1
status=1 len=1
status=0 len=2
status=1 len=2
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assign array-assign (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Indexed LHS with spaces
case $SH in zsh|ash) exit ;; esac

a[1 * 1]=x
a[ 1 + 2 ]=z
echo status=$?

argv.py "${a[@]}"
## stdout: status=0
## status: 127
## timeout: 10
## real-launcher

#### Nested a[i[0]]=0
case $SH in zsh|ash) exit ;; esac

i=(0 1 2)

a[i[0]]=0
a[ i[1] ]=1
a[ i[2] ]=2
a[ i[1]+i[2] ]=3

argv.py "${a[@]}"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### Multiple LHS array words
case $SH in zsh|ash) exit ;; esac

a=(0 1 2)
b=(3 4 5)

#declare -p a b

HOME=/home/spec-test

# empty string, and tilde sub
a[0 + 1]=  b[2 + 0]=~/src

typeset -p a b

echo ---

# In bash, this bad prefix binding prints an error, but nothing fails
a[0 + 1]='foo' argv.py b[2 + 0]='bar'
echo status=$?

typeset -p a b
## STDOUT:
declare -a a=([0]="0" [1]="" [2]="2")
declare -a b=([0]="3" [1]="4" [2]="/home/spec-test/src")
---
status=127
declare -a a=([0]="0" [1]="" [2]="2")
declare -a b=([0]="3" [1]="4" [2]="/home/spec-test/src")
## END
## status: 0
## timeout: 10
## real-launcher

#### LHS array is protected with shopt -s eval_unsafe_arith, e.g. 'a[$(echo 2)]'
case $SH in zsh|ash) exit ;; esac

a=(0 1 2)
b=(3 4 5)
typeset -p b

expr='a[$(echo 2)]' 

echo 'get' "${b[expr]}"

b[expr]=zzz

echo 'set' "${b[expr]}"
typeset -p b
## STDOUT:
declare -a b=([0]="3" [1]="4" [2]="5")
get 5
set zzz
declare -a b=([0]="3" [1]="4" [2]="zzz")
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assign array-assign (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### file named a[ is  not executed
case $SH in zsh|ash) exit ;; esac

PATH=".:$PATH"

for name in 'a[' 'a[5'; do
  echo "echo hi from $name: \$# args: \$@" > "$name"
  chmod +x "$name"
done

# this does not executed a[5
a[5 + 1]=
a[5 / 1]=y
echo len=${#a[@]}

# Not detected as assignment because there's a non-arith character
# bash and mksh both give a syntax error
a[5 # 1]=
## stdout: len=2
## status: 1
## XFAIL ps-bash gap-oils-array-assign array-assign (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### More fragments like a[  a[5  a[5 +  a[5 + 3]

for name in 'a[' 'a[5'; do
  echo "echo hi from $name: \$# args: \$@" > "$name"
  chmod +x "$name"
done

# syntax error in bash
$SH -c 'a['
echo "a[ status=$?"

$SH -c 'a[5'
echo "a[5 status=$?"

# 1 arg +
$SH -c 'a[5 +'
echo "a[5 + status=$?"

# 2 args
$SH -c 'a[5 + 3]'
echo "a[5 + 3] status=$?"

$SH -c 'a[5 + 3]='
echo "a[5 + 3]= status=$?"

$SH -c 'a[5 + 3]+'
echo "a[5 + 3]+ status=$?"

$SH -c 'a[5 + 3]+='
echo "a[5 + 3]+= status=$?"

# mksh doesn't issue extra parse errors
# and it doesn't turn a[5 + 3] and a[5 + 3]+ into commands!



# in zsh, everything becomes "bad pattern"

a[ status=1
a[5 status=1
a[5 + status=1
a[5 + 3] status=1
a[5 + 3]= status=1
a[5 + 3]+ status=1
a[5 + 3]+= status=1

# ash behavior is consistent
## STDOUT:
a[ status=2
a[5 status=2
a[5 + status=2
a[5 + 3] status=127
a[5 + 3]= status=0
a[5 + 3]+ status=127
a[5 + 3]+= status=0
## END
## status: 2
## XFAIL ps-bash gap-oils-array-assign array-assign (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Tricky parsing - a[ a[0]=1 ]=X  a[ a[0]+=1 ]+=X
case $SH in zsh|mksh|ash) exit ;; esac

# the nested [] means we can't use regular language lookahead?

echo assign=$(( z[0] = 42 ))

a[a[0]=1]=X
declare -p a

a[ a[2]=3 ]=Y
declare -p a

echo ---

a[ a[0]+=1 ]+=X
declare -p a
## STDOUT:
assign=42
declare -a a=([0]="1" [1]="X")
declare -a a=([0]="1" [1]="X" [2]="3" [3]="Y")
---
declare -a a=([0]="2" [1]="X" [2]="3X" [3]="Y")
## END
## status: 0
## timeout: 10
## real-launcher

#### argv.py a[1 + 2]=
case $SH in zsh|ash) exit ;; esac

# This tests that the worse parser doesn't unconditinoally treat a[ as special

a[1 + 2]= argv.py a[1 + 2]=
echo status=$?

a[1 + 2]+= argv.py a[1 + 2]+=
echo status=$?

argv.py a[3 + 4]=

argv.py a[3 + 4]+=
## STDOUT:
status=127
status=127
## END
## status: 127
## timeout: 10
## real-launcher

#### declare builtin doesn't allow spaces
case $SH in zsh|mksh|ash) exit ;; esac

# OSH doesn't allow this
declare a[a[0]=1]=X
declare -p a

# neither bash nor OSH allow this
declare a[ a[2]=3 ]=Y
declare -p a
## STDOUT:
declare -a a=([0]="1" [1]="X")
declare -a a=([0]="1" [1]="X" [2]="3")
## END
## status: 0
## timeout: 10
## real-launcher
