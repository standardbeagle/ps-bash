## Imported from the Oils spec tests (arith-context.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Multiple right brackets inside expression

a=(1 2 3)
echo ${a[a[0]]} ${a[a[a[0]]]}
## stdout: 2 3
## status: 0
## timeout: 10

#### Slicing of string with constants
s='abcd'
echo ${s:0} ${s:0:4} ${s:1:1}
## stdout: abcd abcd b
## status: 0
## timeout: 10

#### Slicing of string with variables
s='abcd'
zero=0
one=1
echo ${s:$zero} ${s:$zero:4} ${s:$one:$one}
## stdout: abcd abcd b
## status: 0
## timeout: 10

#### Array index on LHS of assignment
a=(1 2 3)
zero=0
a[zero+5-4]=X
echo ${a[@]}
## stdout: 1 X 3
## status: 0
## timeout: 10

#### Array index on LHS with indices
a=(1 2 3)
a[a[1]]=X
echo ${a[@]}
## stdout: 1 2 X
## status: 0
## timeout: 10

#### Slicing of string with expressions
# mksh accepts ${s:0} and ${s:$zero} but not ${s:zero}
# zsh says unrecognized modifier 'z'
s='abcd'
zero=0
echo ${s:zero} ${s:zero+0} ${s:zero+1:zero+1}
## stdout: abcd abcd b
## status: 0
## timeout: 10

#### Ambiguous colon in slice
s='abcd'
echo $(( 0 < 1 ? 2 : 0 ))  # evaluates to 2
echo ${s: 0 < 1 ? 2 : 0 : 1}  # 2:1 -- TRICKY THREE COLONS
## STDOUT:
2
c
## END
## status: 0
## XFAIL ps-bash gap-oils-arith-context arith-context (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Triple parens should be disambiguated
# The first paren is part of the math, parens 2 and 3 are a single token ending
# arith sub.
((a=1 + (2*3)))
echo $a $((1 + (2*3)))
## stdout: 7 7
## status: 0
## timeout: 10

#### Quadruple parens should be disambiguated
((a=1 + (2 * (3+4))))
echo $a $((1 + (2 * (3+4))))
## stdout: 15 15
## status: 0
## timeout: 10

#### Empty expression for (( ))

for (( ; ; )); do
  echo one
  break
done
## stdout: one
## status: 0
## timeout: 10

#### Empty expression in ${a[@]: : }

a=(a b c d e f)

# space required here -- see spec/var-op-slice
echo slice ${a[@]: }
echo status=$?
echo

echo slice ${a[@]: : }
echo status=$?
echo

# zsh doesn't accept this
echo slice ${a[@]:: }
echo status=$?
## STDOUT:
slice a b c d e f
status=0

slice
status=0

slice
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-arith-context arith-context (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Empty expression a[]

a=(1 2 3)

a[]=42
echo status=$?
echo ${a[@]}

echo ${a[]}
echo status=$?



# runtime failures




# Others 
# [ 1+2 -eq 3 ]
# [[ 1+2 -eq 3 ]]
# unset a[]
# printf -v a[]
## STDOUT:
status=1
1 2 3
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-arith-context arith-context (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
