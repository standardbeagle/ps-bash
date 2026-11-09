## Imported from the Oils spec tests (builtin-vars.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Export sets a global variable
# Even after you do export -n, it still exists.
f() { export GLOBAL=X; }
f
echo $GLOBAL
printenv.py GLOBAL
## stdout: X
## status: 127
## timeout: 10

#### Export sets a global variable that persists after export -n
f() { export GLOBAL=X; }
f
echo $GLOBAL
printenv.py GLOBAL
export -n GLOBAL
echo $GLOBAL
printenv.py GLOBAL
## STDOUT:
X
X
## END
## status: 127
## timeout: 10

#### export -n undefined is ignored
set -o errexit
export -n undef
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### export -n foo=bar not allowed
foo=old
export -n foo=new
echo status=$?
echo $foo
## STDOUT:
status=0
new
## END
## status: 0
## timeout: 10

#### Export a global variable and unset it
f() { export GLOBAL=X; }
f
echo $GLOBAL
printenv.py GLOBAL
unset GLOBAL
echo g=$GLOBAL
printenv.py GLOBAL
## STDOUT:
X
g=
## END
## status: 127
## timeout: 10

#### Export existing global variables
G1=g1
G2=g2
export G1 G2
printenv.py G1 G2
## stdout:
## status: 127
## timeout: 10

#### Export existing local variable
f() {
  local L1=local1
  export L1
  printenv.py L1
}
f
printenv.py L1
## stdout:
## status: 127
## timeout: 10

#### Export a local that shadows a global
V=global
f() {
  local V=local1
  export V
  printenv.py V
}
f
printenv.py V  # exported local out of scope; global isn't exported yet
export V
printenv.py V  # now it's exported
## stdout:
## status: 127
## timeout: 10

#### Export a variable before defining it
export U
U=u
printenv.py U
## stdout:
## status: 127
## timeout: 10

#### Unset exported variable, then define it again.  It's NOT still exported.
export U
U=u
printenv.py U
unset U
printenv.py U
U=newvalue
echo $U
printenv.py U
## stdout: newvalue
## status: 127
## timeout: 10

#### Exporting a parent func variable (dynamic scope)
# The algorithm is to walk up the stack and export that one.
inner() {
  export outer_var
  echo "inner: $outer_var"
  printenv.py outer_var
}
outer() {
  local outer_var=X
  echo "before inner"
  printenv.py outer_var
  inner
  echo "after inner"
  printenv.py outer_var
}
outer
## STDOUT:
before inner
inner: X
after inner
## END
## status: 127
## timeout: 10

#### Dependent export setting
# FOO is not respected here either.
export FOO=foo v=$(printenv.py FOO)
echo "v=$v"
## stdout: v=
## status: 0
## timeout: 10

#### Exporting a variable doesn't change it
old=$PATH
export PATH
new=$PATH
test "$old" = "$new" && echo "not changed"
## stdout: not changed
## status: 0
## timeout: 10

#### can't export array (strict_array)
shopt -s strict_array

typeset -a a
a=(1 2 3)

export a
printenv.py a
## stdout:
## status: 127
## timeout: 10

#### can't export associative array (strict_array)
shopt -s strict_array

typeset -A a
a["foo"]=bar

export a
printenv.py a
## stdout:
## status: 127
## timeout: 10

#### assign to readonly variable
# bash doesn't abort unless errexit!
readonly foo=bar
foo=eggs
echo "status=$?"  # nothing happens
## stdout: status=1
## status: 0
## timeout: 10

#### Make an existing local variable readonly
f() {
	local x=local
	readonly x
	echo $x
	eval 'x=bar'  # Wrap in eval so it's not fatal
	echo status=$?
}
x=global
f
echo $x

# mksh aborts the function, weird
## STDOUT:
local
status=1
global
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-vars builtin-vars (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### assign to readonly variable - errexit
set -o errexit
readonly foo=bar
foo=eggs
echo "status=$?"  # nothing happens
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-builtin-vars builtin-vars (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Unset a variable
foo=bar
echo foo=$foo
unset foo
echo foo=$foo
## STDOUT:
foo=bar
foo=
## END
## status: 0
## timeout: 10

#### Unset exit status
V=123
unset V
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### Unset nonexistent variable
unset ZZZ
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### Unset readonly variable
# dash and zsh abort the whole program.   OSH doesn't?
readonly R=foo
unset R
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### Unset a function without -f
f() {
  echo foo
}
f
unset f
f
## stdout: foo
## status: 127
## timeout: 10

#### Unset has dynamic scope
f() {
  unset foo
}
foo=bar
echo foo=$foo
f
echo foo=$foo
## STDOUT:
foo=bar
foo=
## END
## status: 0
## timeout: 10

#### Local without variable is still unset!
set -o nounset
f() {
  local foo
  echo "[$foo]"
}
f
# zsh doesn't support nounset?
## stdout:
## status: 1
## timeout: 10

#### local after readonly
f() { 
  readonly y
  local x=1 y=$(( x ))
  echo y=$y
}
f
echo y=$y
## STDOUT:
y=
y=
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-vars builtin-vars (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### unset a[-1] (bf.bash regression)
case $SH in dash|zsh) exit ;; esac

a=(1 2 3)
unset a[-1]
echo len=${#a[@]}

echo last=${a[-1]}
(( last = a[-1] ))
echo last=$last

(( a[-1] = 42 ))
echo "${a[@]}"
## STDOUT:
len=2
last=2
last=2
1 42
## END
## status: 0
## timeout: 10
## real-launcher

#### unset a[-1] in sparse array (bf.bash regression)
case $SH in dash|zsh) exit ;; esac

a=(0 1 2 3 4)
unset a[1]
unset a[4]
echo len=${#a[@]} a=${a[@]}
echo last=${a[-1]} second=${a[-2]} third=${a[-3]}

echo ---
unset a[3]
echo len=${#a[@]} a=${a[@]}
echo last=${a[-1]} second=${a[-2]} third=${a[-3]}
## STDOUT:
len=3 a=0 2 3
last=3 second=2 third=
---
len=2 a=0 2
last=2 second= third=0
## END
## status: 0
## timeout: 10
## real-launcher
