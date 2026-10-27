## Imported from the Oils spec tests (var-ref.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### var ref ${!a}
a=b
b=c
echo ref ${!a} ${a}
## stdout: ref c b
## status: 0
## timeout: 10

#### ${!ref-default}
ref=x
echo x=${!ref-default}

x=''
echo x=${!ref-default}

x=foo
echo x=${!ref-default}
## STDOUT:
x=default
x=
x=foo
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${!undef:-}
# bash 4.4 gives empty string, but I feel like this could be an error
echo undef=${!undef-'default'}
echo undef=${!undef}

set -u
echo NOUNSET
echo undef=${!undef-'default'}
echo undef=${!undef}


# Bash 4.4 had been generating an empty string, but it was fixed in Bash 5.0.
#
# ## BUG bash STDOUT:
# undef=default
# undef=
# NOUNSET
# undef=default
# ## END
## stdout: NOUNSET
## status: 1
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### comparison to ${!array[@]} keys (similar SYNTAX)

declare -a a=(x y)
argv.py "${!a[@]}"
echo a_keys=$?

argv.py "${!a}"  # missing [] is equivalent to ${!a[0]} ?
echo a_nobrackets=$?

echo ---
declare -A A=([A]=a [B]=b)

argv.py $(printf '%s\n' ${!A[@]} | sort)
echo A_keys=$?

(argv.py "${!A}")  # missing [] is equivalent to ${!A[0]} ?
echo A_nobrackets=$?
## STDOUT:
a_keys=127
a_nobrackets=127
---
A_keys=127
A_nobrackets=127
## END
## status: 0
## timeout: 10

#### ${!a[@]-'default'} is legal but fails with more than one element

# bash allows this construct, but the indirection fails when the array has more
# than one element because the variable name contains a space.  OSH originally
# made it an error unconditionally because [@] implies it's an array, so the
# behavior has been different from Bash when the array has a single element.
# We now changed it to follow Bash even when the array has a single element.

(argv.py "${!a[@]-default}")
echo status=$?

a=(x y z)
(argv.py "${!a[@]-default}")
echo status=$?

# Bash 4.4 had been generating an empty string for ${!undef[@]-}, but this was
# fixed in Bash 5.0.
#
# ## BUG bash status: 0
# ## BUG bash STDOUT:
# ['default']
# status=0
# status=1
# ## END
## STDOUT:
status=1
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref to $@ with @
set -- one two
ref='@'
echo ref=${!ref}
## stdout: ref=one two
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref to $1 and $2 with 1 and 2
set -- one two
ref1='1'
echo ref1=${!ref1}
ref2='2'
echo ref2=${!ref2}
## STDOUT:
ref1=one
ref2=two
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref: 1, @, *
set -- x y
ref=1; argv.py "${!ref}"
ref=@; argv.py "${!ref}"
ref=*; argv.py "${!ref}"  # maybe_decay_array bug?
## stdout:
## status: 127
## timeout: 10

#### var ref to special var BASH_SOURCE
ref='LINENO'
echo lineno=${!ref}
## stdout: lineno=2
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref to $? with '?'
myfunc() {
  local ref=$1
  echo ${!ref}
}
myfunc FUNCNAME
myfunc '?'
## STDOUT:
myfunc
0
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Var ref, then assignment with ${ := }
z=zz
zz=
echo ${!z:=foo}
echo ${!z:=bar}
## STDOUT:
foo
foo
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Var ref, then error with ${ ? }
w=ww
ww=
echo ${!w:?'my message'}
echo done
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Indirect expansion, THEN suffix operators

check_eq() {
  [ "$1" = "$2" ] || { echo "$1 vs $2"; }
}
check_expand() {
  val=$(eval "echo \"$1\"")
  [ "$val" = "$2" ] || { echo "$1 -> expected $2, got $val"; }
}
check_err() {
  e="$1"
  msg=$(eval "$e" 2>&1) && echo "bad success: $e"
  if test -n "$2"; then 
    if [[ "$msg" != $2 ]]; then
      echo "Expected error: $e"
      echo "Got error     : $msg"
    fi
  fi
}
# Nearly everything in manual section 3.5.3 "Shell Parameter Expansion"
# is allowed after a !-indirection.
#
# Not allowed: any further prefix syntax.
x=xx; xx=aaabcc
xd=x
check_err '${!!xd}'
check_err '${!!x*}'
a=(asdf x)
check_err '${!!a[*]}'
check_err '${!#x}'
check_err '${!#a[@]}'
# And an array reference binds tighter in the syntax, so goes first;
# there's no way to spell "indirection, then array reference".
check_expand '${!a[1]}' xx
b=(aoeu a)
check_expand '${!b[1]}' asdf  # i.e. like !(b[1]), not (!b)[1]
#
# Allowed: apparently everything else.
y=yy; yy=
check_expand '${!y:-foo}' foo
check_expand '${!x:-foo}' aaabcc

check_expand '${!x:?oops}' aaabcc

check_expand '${!y:+foo}' ''
check_expand '${!x:+foo}' foo

check_expand '${!x:2}' abcc
check_expand '${!x:2:2}' ab

check_expand '${!x#*a}' aabcc
check_expand '${!x%%c*}' aaab
check_expand '${!x/a*b/d}' dcc

# ^ operator not fully implemented in OSH
#check_expand '${!x^a}' Aaabcc

p=pp; pp='\$ '
check_expand '${!p@P}' '$ '
echo ok
## stdout: ok
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref OF array var -- silent a[0] decay
declare -a a=(ale bean)
echo first=${!a}

ale=zzz
echo first=${!a}
## STDOUT:
first=
first=zzz
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### array ref

declare -a array=(ale bean)
ref='array[0]'
echo ${!ref}
## stdout: ale
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${!array_ref:-set} and ${!array_ref:=assign}

ref='a[@]'
a=('' '' '')

echo "==== check ===="

argv.py "${!ref:-set}"
argv.py "${a[@]:-set}"

echo "==== assign ===="

argv.py "${!ref:=assign}"
argv.py "${!ref}"
a=('' '' '') # revert the state in case it is modified

argv.py "${a[@]:=assign}"
argv.py "${a[@]}"
## STDOUT:
==== check ====
==== assign ====
## END
## status: 127
## timeout: 10

#### Array indirect expansion with suffix operators

declare -A ref=(['dummy']=v1)
function test-suffixes {
  echo "==== $1 ===="
  ref['dummy']=$1
  argv.py "${!ref[@]:2}"
  argv.py "${!ref[@]:1:2}"
  argv.py "${!ref[@]:-empty}"
  argv.py "${!ref[@]:+set}"
  argv.py "${!ref[@]:=assign}"
}

v1=value
test-suffixes v1
echo "v1=$v1"

v2=
test-suffixes v2
echo "v2=$v2"

a1=()
test-suffixes a1
argv.py "${a1[@]}"

a2=(element)
test-suffixes 'a2[0]'
argv.py "${a2[@]}"

a3=(1 2 3)
test-suffixes 'a3[@]'
argv.py "${a3[@]}"
## STDOUT:
==== v1 ====
v1=value
==== v2 ====
v2=assign
==== a1 ====
==== a2[0] ====
==== a3[@] ====
## END
## status: 127
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Array indirect expansion with replacements

declare -A ref=(['dummy']=v1)
function test-rep {
  echo "==== $1 ===="
  ref['dummy']=$1
  argv.py "${!ref[@]#?}"
  argv.py "${!ref[@]%?}"
  argv.py "${!ref[@]//[a-f]}"
  argv.py "${!ref[@]//[a-f]/x}"
}

v1=value
test-rep v1

v2=
test-rep v2

a1=()
test-rep a1

a2=(element)
test-rep 'a2[0]'

a3=(1 2 3)
test-rep 'a3[@]'
## STDOUT:
==== v1 ====
==== v2 ====
==== a1 ====
==== a2[0] ====
==== a3[@] ====
## END
## status: 127
## timeout: 10

#### Array indirect expansion with @? conversion

declare -A ref=(['dummy']=v1)
function test-op0 {
  echo "==== $1 ===="
  ref['dummy']=$1
  argv.py "${!ref[@]@Q}"
  argv.py "${!ref[@]@P}"
  argv.py "${!ref[@]@a}"
}

v1=value
test-op0 v1

v2=
test-op0 v2

a1=()
test-op0 a1

a2=(element)
test-op0 'a2[0]'

a3=(1 2 3)
test-op0 'a3[@]'


# Bash 4.4 had a bug in the section "==== a3[@] ====":
#
# ==== a3[@] ====
# []
# []
# []
## STDOUT:
==== v1 ====
==== v2 ====
==== a1 ====
==== a2[0] ====
==== a3[@] ====
## END
## status: 127
## timeout: 10

#### array ref with strict_array
shopt -s strict_array

declare -a array=(ale bean)
ref='array'
echo ${!ref}
## stdout: ale
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref TO array var
shopt -s compat_array

declare -a array=(ale bean)

ref='array'  # when compat_array is on, this is like array[0]
ref_AT='array[@]'

echo ${!ref}
echo ${!ref_AT}
## STDOUT:
ale
ale bean
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref TO array var, with subscripts
f() {
  argv.py "${!1}"
}
f 'nonexistent[0]'
array=(x y z)
f 'array[0]'
f 'array[1+1]'
f 'array[@]'
f 'array[*]'
# Also associative arrays.
## stdout:
## status: 127
## timeout: 10

#### var ref TO assoc array a[key]
shopt -s compat_array

declare -A assoc=([ale]=bean [corn]=dip)
ref=assoc
#ref_AT='assoc[@]'

# UNQUOTED doesn't work with the OSH parser
#ref_SUB='assoc[ale]'
ref_SUB='assoc["ale"]'

ref_SUB_QUOTED='assoc["al"e]'

ref_SUB_BAD='assoc["bad"]'

echo ref=${!ref}  # compat_array: assoc is equivalent to assoc[0]
#echo ref_AT=${!ref_AT}
echo ref_SUB=${!ref_SUB}
echo ref_SUB_QUOTED=${!ref_SUB_QUOTED}
echo ref_SUB_BAD=${!ref_SUB_BAD}
## STDOUT:
ref=
ref_SUB=bean
ref_SUB_QUOTED=bean
ref_SUB_BAD=
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref TO array with arbitrary subscripts
shopt -s eval_unsafe_arith compat_array

f() {
  local val=$(echo "${!1}")
  if test "$val" = y; then 
    echo "works: $1"
  fi
}
# Warmup: nice plain array reference
a=(x y)
f 'a[1]'
#
# Not allowed:
# no brace expansion
f 'a[{1,0}]'  # operand expected
# no process substitution (but see command substitution below!)
f 'a[<(echo x)]'  # operand expected
# TODO word splitting seems interesting
aa="1 0"
f 'a[$aa]'  # 1 0: syntax error in expression (error token is "0")
# no filename globbing
f 'a[b*]'  # operand expected
f 'a[1"]'  # bad substitution
#
# Allowed: most everything else in section 3.5 "Shell Expansions".
# shell parameter expansion
b=1
f 'a[$b]'
f 'a[${c:-1}]'
# (... and presumably most of the other features there)
# command substitution, yikes!
f 'a[$(echo 1)]'
# arithmetic expansion
f 'a[$(( 3 - 2 ))]'

# All of these are undocumented and probably shouldn't exist,
# though it's always possible some will turn up in the wild and
# we'll end up implementing them.
## STDOUT:
works: a[1]
works: a[$b]
works: a[${c:-1}]
works: a[$(echo 1)]
works: a[$(( 3 - 2 ))]
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Bizarre tilde expansion in array index
a=(x y)
PWD=1
ref='a[~+]'
echo ${!ref}

# Bash 4.4 had a bug, which was fixed in Bash 5.0.
#
# ## BUG bash status: 0
# ## BUG bash STDOUT:
# y
# ## END
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Indirect expansion TO fancy expansion features bash disallows

check_indir() {
    result="${!1}"
    desugared_result=$(eval 'echo "${'"$1"'}"')
    [ "$2" = "$desugared_result" ] || { echo "$1 $desugared_result";  }
}
x=y
y=a
a=(x y)
declare -A aa
aa=([k]=r [l]=s)
# malformed array indexing
check_indir "a[0"
check_indir "aa[k"
# double indirection
check_indir "!x"      a
check_indir "!a[0]"   y
# apparently everything else in the manual under "Shell Parameter Expansion"
check_indir "x:-foo"  y
check_indir "x:=foo"  y
check_indir "x:?oops" y
check_indir "x:+yy"   yy
check_indir "x:0"     y
check_indir "x:0:1"   y
check_indir "!a@"    "a aa"
# (!a[@] is elsewhere)
check_indir "#x"      1
check_indir "x#y"
check_indir "x/y/foo" foo
check_indir "x@Q"     y
echo done
## stdout: done
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Bad var ref
a='bad var name'
echo ref ${!a}
echo status=$?
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Bad var ref 2
b='/'  # really bad
echo ref ${!b}
echo status=$?
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${!OPTIND} (used by bash completion
set -- a b c
echo ${!OPTIND}
f() {
  local OPTIND=1
  echo ${!OPTIND}
  local OPTIND=2
  echo ${!OPTIND}
}
f x y z
## STDOUT:
a
x
y
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### var ref doesn't need cycle detection
x=y
y=x
echo cycle=${!x}

typeset -n a=b
typeset -n b=a
echo cycle=${a}
## STDOUT:
cycle=x
cycle=
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Var Ref Code Injection $(tee PWNED)

typeset -a a
a=(42)

x='a[$(echo 0 | tee PWNED)]'

echo ${!x}

if test -f PWNED; then
  echo PWNED
  cat PWNED
else
  echo NOPE
fi
## STDOUT:
42
PWNED
0
## END
## status: 0
## XFAIL ps-bash gap-oils-var-ref var-ref (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
