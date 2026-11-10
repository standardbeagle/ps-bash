## Imported from the Oils spec tests (builtin-type.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### type -> keyword builtin

type while cd
## STDOUT:
while is a shell keyword
cd is a shell builtin
## END
## status: 0
## timeout: 10

#### type -> alias external

mkdir -p _tmp
shopt -s expand_aliases || true  # bash

alias ll='ls -l'

touch _tmp/date
chmod +x _tmp/date
PATH=_tmp:/bin

normalize() {
  # ignore quotes and backticks
  # bash prints a left backtick
  quotes='"`'\'
  sed \
    -e "s/[$quotes]//g" \
    -e 's/shell function/function/' \
    -e 's/is aliased to/is an alias for/'
}

type ll date | normalize

# Note: both procs and funcs go in var namespace?  So they don't respond to
# 'type'?
## STDOUT:
ll is an alias for ls -l
date is _tmp/date
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type builtin-type (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type of relative path

mkdir -p _tmp
touch _tmp/file _tmp/ex
chmod +x _tmp/ex

type _tmp/file _tmp/ex

# dash and ash don't care if it's executable
# mksh
## stdout: _tmp/ex is _tmp/ex
## status: 1
## XFAIL ps-bash gap-oils-builtin-type builtin-type (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -> not found

type zz 2>err.txt
echo status=$?

# for bash and OSH: print to stderr
fgrep -o 'zz: not found' err.txt || true

# zsh and mksh behave the same - status 1
# dash and ash behave the same - status 127
## STDOUT:
status=1
zz: not found
## END
## status: 0
## timeout: 10

#### special builtins are called out
type cd
type eval
type :
type true

echo
type export
## STDOUT:
cd is a shell builtin
eval is a shell builtin
: is a shell builtin
true is a shell builtin

export is a shell builtin
## END
## status: 0
## timeout: 10

#### more special builtins
case $SH in bash|zsh|dash) exit ;; esac

type .
type source

# no agreement here!
# type local
# type typeset
## stdout:
## status: 0
## timeout: 10
## real-launcher
