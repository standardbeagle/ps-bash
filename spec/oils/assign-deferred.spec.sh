## Imported from the Oils spec tests (assign-deferred.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### typeset a[3]=4
typeset a[3]=4 a[5]=6
echo status=$?
argv.py "${!a[@]}" "${a[@]}"
## stdout: status=0
## status: 127
## XFAIL ps-bash gap-oils-assign-deferred assign-deferred (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### typeset -a a[1]=a a[3]=c
# declare works the same way in bash, but not mksh.
# spaces are NOT allowed here.
typeset -a a[1*1]=x a[1+2]=z
argv.py "${a[@]}"
## stdout:
## status: 127
## timeout: 10

#### local a[3]=4
f() {
  local a[3]=4 a[5]=6
  echo status=$?
  argv.py "${!a[@]}" "${a[@]}"
}
f
## stdout: status=0
## status: 127
## timeout: 10

#### readonly a[7]=8
readonly b[7]=8
echo status=$?
argv.py "${!b[@]}" "${b[@]}"

# bash doesn't like this variable name!
## stdout: status=1
## status: 127
## XFAIL ps-bash gap-oils-assign-deferred assign-deferred (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### export a[7]=8
export a[7]=8
echo status=$?
argv.py "${!a[@]}" "${a[@]}"
printenv.py a
## stdout: status=1
## status: 127
## XFAIL ps-bash gap-oils-assign-deferred assign-deferred (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### 'builtin' prefix is allowed on assignments
builtin export e='E'
echo e=$e
## stdout: e=E
## status: 0
## timeout: 10

#### 'command' prefix is allowed on assignments
readonly r1='R1'  # zsh has this
command readonly r2='R2'  # but not this
echo r1=$r1
echo r2=$r2
## STDOUT:
r1=R1
r2=R2
## END
## status: 0
## timeout: 10

#### is 'builtin' prefix and array allowed?  OSH is smarter
builtin typeset a=(1 2 3)
echo len=${#a[@]}
## stdout:
## status: 2
## timeout: 10

#### is 'command' prefix and array allowed?  OSH is smarter
command typeset a=(1 2 3)
echo len=${#a[@]}
## stdout:
## status: 2
## timeout: 10
