## Imported from the Oils spec tests (array-assoc.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Literal syntax ([x]=y)
declare -A a
a=([aa]=b [foo]=bar ['a+1']=c)
echo ${a["aa"]}
echo ${a["foo"]}
echo ${a["a+1"]}
## STDOUT:
b
bar
c
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set associative array to indexed array literal (very surprising bash behavior)
declare -A assoc=([k1]=foo [k2]='spam eggs')
declare -p assoc

# Bash 5.1 assoc=(key value). Bash 5.0 (including the currently tested 4.4)
# does not implement this.

assoc=(foo 'spam eggs')
declare -p assoc
## STDOUT:
declare -A assoc=([k1]="foo" [k2]="spam eggs" )
declare -A assoc=([foo]="spam eggs" )
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Can initialize assoc array with the "(key value ...)" sequence
declare -A A=(1 2 3)
echo status=$?
declare -p A

# bash-4.4 prints warnings to stderr but gives no indication of the problem
## STDOUT:
status=0
declare -A A=([3]="" [1]="2" )
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### create empty assoc array, put, then get
declare -A A  # still undefined
argv.py "${A[@]}"
argv.py "${!A[@]}"
A['foo']=bar
echo ${A['foo']}
## stdout: bar
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Empty value (doesn't use EmptyWord?)
declare -A A=(["k"]= )
argv.py "${A["k"]}"
## stdout:
## status: 127
## timeout: 10

#### retrieve keys with !
declare -A a
var='x'
a["$var"]=b
a['foo']=bar
a['a+1']=c
for key in "${!a[@]}"; do
  echo $key
done | sort
## STDOUT:
a+1
foo
x
## END
## status: 0
## timeout: 10

#### retrieve values with ${A[@]}
declare -A A
var='x'
A["$var"]=b
A['foo']=bar
A['a+1']=c
for val in "${A[@]}"; do
  echo $val
done | sort
## STDOUT:
b
bar
c
## END
## status: 0
## timeout: 10

#### coerce to string with ${A[*]}, etc.
declare -A A
A['X X']=xx
A['Y Y']=yy
argv.py "${A[*]}"
argv.py "${!A[*]}"

argv.py ${A[@]}
argv.py ${!A[@]}
## stdout:
## status: 127
## timeout: 10

#### ${A[@]/b/B}
# but ${!A[@]/b/B} doesn't work
declare -A A
A['aa']=bbb
A['bb']=ccc
A['cc']=ddd
for val in "${A[@]//b/B}"; do
  echo $val
done | sort
## STDOUT:
BBB
ccc
ddd
## END
## status: 0
## timeout: 10

#### ${A[@]#prefix}
declare -A A
A['aa']=one
A['bb']=two
A['cc']=three
for val in "${A[@]#t}"; do
  echo $val
done | sort
## STDOUT:
hree
one
wo
## END
## status: 0
## timeout: 10

#### ${assoc} is like ${assoc[0]}
declare -A a

a=([aa]=b [foo]=bar ['a+1']=c)
echo a="${a}"

a=([0]=zzz)
echo a="${a}"

a=(['0']=yyy)
echo a="${a}"
## STDOUT:
a=
a=zzz
a=yyy
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### length ${#a[@]}
declare -A a
a["x"]=1
a["y"]=2
a["z"]=3
echo "${#a[@]}"
## stdout: 3
## status: 0
## timeout: 10

#### lookup with ${a[0]} -- "0" is a string
declare -A a
a["0"]=a
a["1"]=b
a["2"]=c
echo 0 "${a[0]}" 1 "${a[1]}" 2 "${a[2]}"
## stdout: 0 a 1 b 2 c
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### lookup with double quoted strings "mykey"
declare -A a
a["aa"]=b
a["foo"]=bar
a['a+1']=c
echo "${a["aa"]}" "${a["foo"]}" "${a["a+1"]}"
## stdout: b bar c
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### lookup with single quoted string
declare -A a
a["aa"]=b
a["foo"]=bar
a['a+1']=c
echo "${a['a+1']}"
## stdout: c
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### readonly associative array can't be modified
declare -Ar A
A['x']=1
echo status=$?
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### associative array and brace expansion
declare -A A=([k1]=v [k2]=-{a,b}-)
echo ${A["k1"]}
echo ${A["k2"]}
## STDOUT:
v
-{a,b}-
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare -A A=() allowed
set -o nounset
shopt -s strict_arith || true

declare -A ASSOC=()
echo len=${#ASSOC[@]}

# Check that it really can be used like an associative array
ASSOC['k']='32'
echo len=${#ASSOC[@]}

# bash allows a variable to be an associative array AND unset, while OSH
# doesn't
set +o nounset
declare -A u
echo unset len=${#u[@]}
## STDOUT:
len=0
len=1
unset len=0
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### unset -v and assoc array
shopt -s eval_unsafe_arith || true

show-len() {
  echo len=${#assoc[@]}
}

declare -A assoc=(['K']=val)
show-len

unset -v 'assoc["K"]'
show-len

declare -A assoc=(['K']=val)
show-len
key=K
unset -v 'assoc[$key]'
show-len

declare -A assoc=(['K']=val)
show-len
unset -v 'assoc[$(echo K)]'
show-len

# ${prefix} doesn't work here, even though it does in arithmetic
#declare -A assoc=(['K']=val)
#show-len
#prefix=as
#unset -v '${prefix}soc[$key]'
#show-len
## STDOUT:
len=1
len=0
len=1
len=0
len=1
len=0
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### nameref and assoc array
show-values() {
  echo values: ${A[@]}
}

declare -A A=(['K']=val)
show-values

declare -n ref='A["K"]'
echo before $ref
ref='val2'
echo after $ref
show-values

echo ---

key=K
declare -n ref='A[$key]'
echo before $ref
ref='val3'
echo after $ref
show-values
## STDOUT:
values: val
before val
after val2
values: val2
---
before val2
after val3
values: val3
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${!ref} and assoc array

show-values() {
  echo values: ${A[@]}
}

declare -A A=(['K']=val)
show-values

declare ref='A["K"]'
echo ref ${!ref}

key=K
declare ref='A[$key]'
echo ref ${!ref}
## STDOUT:
values: val
ref val
ref val
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### printf -v and assoc array

show-values() {
  echo values: ${assoc[@]}
}

declare -A assoc=(['K']=val)
show-values

printf -v 'assoc["K"]' '/%s/' val2
show-values

key=K
printf -v 'assoc[$key]' '/%s/' val3
show-values

# Somehow bash doesn't allow this
#prefix=as
#printf -v '${prefix}soc[$key]' '/%s/' val4
#show-values
## STDOUT:
values: val
values: /val2/
values: /val3/
## END
## status: 0
## timeout: 10

#### bash bug: (( A["$key"] = 1 )) doesn't work
key='\'
declare -A A
#A["$key"]=1

# Works in both
#A["$key"]=42

# Works in bash only
#(( A[\$key] = 42 ))

(( A["$key"] = 42 ))

argv.py "${!A[@]}"
argv.py "${A[@]}"
## stdout:
## status: 127
## timeout: 10

#### Implicit increment of keys
declare -a arr=( [30]=a b [40]=x y)
argv.py "${!arr[@]}"
argv.py "${arr[@]}"
## stdout:
## status: 127
## timeout: 10

#### test -v assoc[key]

typeset -A assoc
assoc=([empty]='' [k]=v)

echo 'no quotes'

test -v assoc[empty]
echo empty=$?

test -v assoc[k]
echo k=$?

test -v assoc[nonexistent]
echo nonexistent=$?

echo

# Now with quotes
echo 'quotes'

test -v assoc["empty"]
echo empty=$?

test -v assoc['k']
echo k=$?

test -v assoc['nonexistent'] 
echo nonexistent=$?
## STDOUT:
no quotes
empty=0
k=0
nonexistent=1

quotes
empty=0
k=0
nonexistent=1
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### test -v with dynamic parsing

typeset -A assoc
assoc=([empty]='' [k]=v)

key=empty
test -v 'assoc[$key]'
echo empty=$?

key=k
test -v 'assoc[$key]'
echo k=$?

key=nonexistent
test -v 'assoc[$key]'
echo nonexistent=$?
## STDOUT:
empty=0
k=0
nonexistent=1
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [[ -v assoc[key] ]]

typeset -A assoc
assoc=([empty]='' [k]=v)

echo 'no quotes'

[[ -v assoc[empty] ]]
echo empty=$?

[[ -v assoc[k] ]]
echo k=$?

[[ -v assoc[nonexistent] ]]
echo nonexistent=$?

echo

# Now with quotes
echo 'quotes'

[[ -v assoc["empty"] ]]
echo empty=$?

[[ -v assoc['k'] ]]
echo k=$?

[[ -v assoc['nonexistent'] ]]
echo nonexistent=$?

echo

echo 'vars'

key=empty
[[ -v assoc[$key] ]]
echo empty=$?

key=k
[[ -v assoc[$key] ]]
echo k=$?

key=nonexistent
[[ -v assoc[$key] ]]
echo nonexistent=$?
## STDOUT:
no quotes
empty=0
k=0
nonexistent=1

quotes
empty=0
k=0
nonexistent=1

vars
empty=0
k=0
nonexistent=1
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [[ -v assoc[key] ]] syntax errors

typeset -A assoc
assoc=([empty]='' [k]=v)

[[ -v assoc[empty] ]]
echo empty=$?

[[ -v assoc[k] ]]
echo k=$?

[[ -v assoc[k]z ]]
echo typo=$?
## STDOUT:
empty=0
k=0
typo=1
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### lookup with unquoted $key and quoted "$i$i"
declare -A A
A["aa"]=b
A["foo"]=bar

key=foo
echo ${A[$key]}
i=a
echo ${A["$i$i"]}   # note: ${A[$i$i]} doesn't work in OSH
## STDOUT:
bar
b
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### lookup by unquoted string doesn't work in OSH because it's a variable
declare -A a
a["aa"]=b
a["foo"]=bar
a['a+1']=c
echo "${a[a+1]}"
## stdout: c
## status: 0
## timeout: 10

#### bash bug: "i+1" and i+1 are the same key

i=1
array=(5 6 7)
echo array[i]="${array[i]}"
echo array[i+1]="${array[i+1]}"

# arithmetic does NOT work here in bash.  These are unquoted strings!
declare -A assoc
assoc[i]=$i
assoc[i+1]=$i+1

assoc["i"]=string
assoc["i+1"]=string+1

echo assoc[i]="${assoc[i]}" 
echo assoc[i+1]="${assoc[i+1]}"

echo assoc[i]="${assoc["i"]}" 
echo assoc[i+1]="${assoc["i+1"]}"
## STDOUT:
array[i]=6
array[i+1]=7
assoc[i]=string
assoc[i+1]=string+1
assoc[i]=string
assoc[i+1]=string+1
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Array stored in associative array gets converted to string (without strict_array)

array=('1 2' 3)
declare -A d
d['key']="${array[@]}"
argv.py "${d['key']}"
## stdout:
## status: 127
## timeout: 10

#### Indexed array as key of associative array coerces to string (without shopt -s strict_array)

declare -a array=(1 2 3)
declare -A assoc
assoc[42]=43
assoc["${array[@]}"]=foo

echo "${assoc["${array[@]}"]}"
for entry in "${!assoc[@]}"; do
  echo $entry
done | sort
## STDOUT:
foo
1 2 3
42
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Append to associative array value A['x']+='suffix'
declare -A A
A['x']='foo'
A['x']+='bar'
A['x']+='bar'
argv.py "${A["x"]}"
## stdout:
## status: 127
## timeout: 10

#### Slice of associative array doesn't make sense in bash
declare -A a
a[xx]=1
a[yy]=2
a[zz]=3
a[aa]=4
a[bb]=5
#argv.py ${a["xx"]}
argv.py ${a[@]: 0: 3}
argv.py ${a[@]: 1: 3}
argv.py ${a[@]: 2: 3}
argv.py ${a[@]: 3: 3}
argv.py ${a[@]: 4: 3}
argv.py ${a[@]: 5: 3}
## stdout:
## status: 127
## timeout: 10

#### bash variable can have an associative array part and a string part

# and $assoc is equivalent to ${assoc[0]}, just like regular arrays
declare -A assoc
assoc[1]=1
assoc[2]=2
echo ${assoc[1]} ${assoc[2]} ${assoc}
assoc[0]=zero
echo ${assoc[1]} ${assoc[2]} ${assoc}
assoc=string
echo ${assoc[1]} ${assoc[2]} ${assoc}
## STDOUT:
1 2
1 2 zero
1 2 string
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Associative array expressions inside (( )) with keys that look like numbers
declare -A assoc
assoc[0]=42
(( var = ${assoc[0]} ))
echo $var
(( var = assoc[0] ))
echo $var
## STDOUT:
42
42
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### (( A[5] += 42 ))
declare -A A
(( A[5] = 10 ))
(( A[5] += 6 ))
echo ${A[5]}
## stdout: 16
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### (( A[5] += 42 )) with empty cell
shopt -u strict_arith  # default zero cell
declare -A A
(( A[5] += 6 ))
echo ${A[5]}
## stdout: 6
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### setting key to itself (from bash-bug mailing list)
declare -A foo
foo=(["key"]="value1")
echo ${foo["key"]}
foo=(["key"]="${foo["key"]} value2")
echo ${foo["key"]}
## STDOUT:
value1
value1 value2
## END
## status: 0
## timeout: 10

#### BashAssoc a+=()

declare -A a=([apple]=red [orange]=orange)
a+=([lemon]=yellow [banana]=yellow)
echo "apple is ${a['apple']}"
echo "orange is ${a['orange']}"
echo "lemon is ${a['lemon']}"
echo "banana is ${a['banana']}"
## STDOUT:
apple is red
orange is orange
lemon is yellow
banana is yellow
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### BashAssoc ${a[@]@Q}

declare -A a=()
a['symbol1']=\'\'
a['symbol2']='"'
a['symbol3']='()<>&|'
a['symbol4']='[]*?'
echo "[${a[@]@Q}]"
echo "[${a[*]@Q}]"
## STDOUT:
['[]*?' '()<>&|' '"' ''\'''\''']
['[]*?' '()<>&|' '"' ''\'''\''']
## END
## status: 0
## XFAIL ps-bash gap-oils-array-assoc array-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
