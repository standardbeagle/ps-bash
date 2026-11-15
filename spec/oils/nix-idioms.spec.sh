## Imported from the Oils spec tests (nix-idioms.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### var ref to array 'preHooks[@]'
#
# This idiom discussed on
# https://github.com/NixOS/nixpkgs/pull/147629

show() {
  echo show

  # These are actually different
  argv.py ${!hooksSlice}

  argv.py ${!hooksSlice+"${!hooksSlice}"}
}

hooksSlice='preHooks[@]'

preHooks=()
show

preHooks=('foo bar' baz)
show

# WTF this exposes a difference?  But not the test case below?

# What's happening here?
# Uncomment this and get an error in bash about hookSlice, even though we never
# undefined it.

#wtf=1
#
# line 6: !hooksSlice: unbound variable

if test -n "$wtf"; then
  # 4.4.0(1)-release
  # echo $BASH_VERSION

  set -u
  preHooks=()
  show

  preHooks=('foo bar' baz)
  show
fi
## STDOUT:
show
show
## END
## status: 0
## timeout: 10

#### Similar to above with set -u
show() {
  echo show

  # bash gives an error here - !hookSlice unbound, even though preHooks exists
  # OSH currently does the "logical" thing

  # NOT testing this -- I think this is WHAT NIX WORKS AROUND WITH
  #argv.py ${!hooksSlice}

  argv.py ${!hooksSlice+"${!hooksSlice}"}
}

hooksSlice='preHooks[@]'

set -u
preHooks=()
show

preHooks=('foo bar' baz)
show
## STDOUT:
show
show
## END
## status: 127
## timeout: 10

#### ${!ref} to undefined array

set -u
shopt -s eval_unsafe_arith || true 2>/dev/null

#preHooks=()
hookSlice="preHooks[@]"

argv.py ${!hookSlice+"${!hookSlice}"}

for element in ${!hookSlice+"${!hookSlice}"}; do
  echo $element
done
## stdout:
## status: 0
## timeout: 10

#### ${!ref} to undefined string var is fatal, INCONSISTENT with array
hookSlice='preHooks'

argv.py ${!hookSlice}

set -u

argv.py ${!hookSlice}

echo end
## stdout:
## status: 1
## timeout: 10

#### export with dynamic var name +=

orig() {
  export NIX_LDFLAGS${role_post}+=" -L$1/lib64"
}

new() {
  local var_name="NIX_LDFLAGS$role_post"
  local value=" -L$1/lib64"

  eval "$var_name"+='$value'
  export "$var_name"
}

role_post='_foo'

# set -u

if test -n "${BASH_VERSION:-}"; then
  orig one
fi

declare -p NIX_LDFLAGS_foo  # inspect it
unset NIX_LDFLAGS_foo

new one

declare -p NIX_LDFLAGS_foo  # inspect it
## STDOUT:
declare -x NIX_LDFLAGS_foo=" -Lone/lib64"
declare -x NIX_LDFLAGS_foo=" -Lone/lib64"
## END
## status: 0
## timeout: 10

#### let idiom can be written in POSIX shell - eval ": \$(( ))"

for i in 0 1 2; do
  echo i=$i

  NIX_ENFORCE_NO_NATIVE=$i

  outputVar=NIX_ENFORCE_NO_NATIVE_x86_64_unknown_linux_gnu
  inputVar=NIX_ENFORCE_NO_NATIVE

  # Original Nix idiom

  if test -n "$BASH_VERSION"; then
    let "${outputVar} |= ${!inputVar:-0}" "1"
  else
    # OSH alternative
    eval ": \$(( ${outputVar} |= ${!inputVar:-0} ))"
  fi

  echo NIX_ENFORCE_NO_NATIVE=$NIX_ENFORCE_NO_NATIVE
  echo NIX_ENFORCE_NO_NATIVE_x86_64_unknown_linux_gnu=$NIX_ENFORCE_NO_NATIVE_x86_64_unknown_linux_gnu
  echo

done
## STDOUT:
i=0
NIX_ENFORCE_NO_NATIVE=0
NIX_ENFORCE_NO_NATIVE_x86_64_unknown_linux_gnu=0

i=1
NIX_ENFORCE_NO_NATIVE=1
NIX_ENFORCE_NO_NATIVE_x86_64_unknown_linux_gnu=1

i=2
NIX_ENFORCE_NO_NATIVE=2
NIX_ENFORCE_NO_NATIVE_x86_64_unknown_linux_gnu=3

## END
## status: 0
## timeout: 10
