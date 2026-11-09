## Imported from the Oils spec tests (dparen.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### (( )) with arrays
a=(4 5 6)
(( sum = a[0] + a[1] + a[2] ))
echo $sum
## stdout: 15
## status: 0
## timeout: 10

#### (( )) with error
(( a = 0 )) || echo false
(( b = 1 )) && echo true
(( c = -1 )) && echo true
echo $((a + b + c))
## STDOUT:
false
true
true
0
## END
## status: 0
## timeout: 10

#### bash and mksh: V in (( a[K] = V )) gets coerced to integer
shopt -u strict_arith || true
K=key
V=value
typeset -a a
(( a[K] = V ))

# not there!
echo a[\"key\"]=${a[$K]}

echo keys = ${!a[@]}
echo values = ${a[@]}
## STDOUT:
a["key"]=0
keys = 0
values = 0
## END
## status: 0
## timeout: 10

#### bash: K in (( A[K] = V )) is a constant string
K=5
V=42
typeset -A A
(( A[K] = V ))

echo A["5"]=${A["5"]}
echo keys = ${!A[@]}
echo values = ${A[@]}
## STDOUT:
A[5]=
keys = K
values = 42
## END
## status: 0
## XFAIL ps-bash gap-oils-dparen dparen (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### BUG: (( V = A[K] )) doesn't retrieve the right value
typeset -A A
K=5
V=42
A["$K"]=$V
A["K"]=oops
A[K]=oops2

# We don't neither 42 nor "oops".  Bad!
(( V = A[K] ))

echo V=$V
## stdout: V=0
## status: 0
## timeout: 10

#### bash: V in (( A["K"] = V )) gets coerced to integer
shopt -u strict_arith || true
K=key
V=value
typeset -A A || exit 1
(( A["K"] = V ))

# not there!
echo A[\"key\"]=${A[$K]}

echo keys = ${!A[@]}
echo values = ${A[@]}
## STDOUT:
A["key"]=
keys = K
values = 0
## END
## status: 0
## timeout: 10

#### literal strings inside (( ))
declare -A A
A['x']=42
(( x = A['x'] ))
(( A['y'] = 'y' ))  # y is a variable, gets coerced to 0
echo $x ${A['y']}
## stdout: 42
## status: 0
## XFAIL ps-bash gap-oils-dparen dparen (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Assigning whole raray (( b = a ))
a=(4 5 6)
(( b = a ))

echo "${a[@]}"

# OSH doesn't like this
echo "${b[@]}"
## STDOUT:
4 5 6
4
## END
## status: 0
## timeout: 10

#### set associative array
declare -A A=(['foo']=bar ['spam']=42)
(( x = A['spam'] ))
echo $x
## stdout: 42
## status: 0
## XFAIL ps-bash gap-oils-dparen dparen (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Example of incrementing associative array entry with var key (ble.sh)
declare -A A=(['foo']=42)
key='foo'

# note: in bash, (( A[\$key] += 1 )) works the same way.

set -- 1 2
(( A[$key] += $2 ))

echo foo=${A['foo']}
## stdout: foo=44
## status: 0
## timeout: 10

#### (( )) result
(( 1 )) && echo True
(( 0 )) || echo False
## STDOUT:
True
False
## END
## status: 0
## timeout: 10

#### negative number is true
(( -1 )) && echo True
## stdout: True
## status: 0
## timeout: 10

#### (( )) in if statement
if (( 3 > 2)); then
  echo True
fi
## stdout: True
## status: 0
## timeout: 10

#### (( ))
# What is the difference with this and let?  One difference: spaces are allowed.
(( x = 1 ))
(( y = x + 2 ))
echo $x $y
## stdout: 1 3
## status: 0
## timeout: 10
