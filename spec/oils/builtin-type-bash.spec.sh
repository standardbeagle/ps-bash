## Imported from the Oils spec tests (builtin-type-bash.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### type -t -> function
f() { echo hi; }
type -t f
## stdout: function
## status: 0
## timeout: 10

#### type -t -> alias
shopt -s expand_aliases
alias foo=bar
type -t foo
## stdout: alias
## status: 0
## timeout: 10

#### type -t -> builtin
type -t echo read : [ declare local
## STDOUT:
builtin
builtin
builtin
builtin
builtin
builtin
## END
## status: 0
## timeout: 10

#### type -t -> keyword
type -t for time ! fi do {
## STDOUT:
keyword
keyword
keyword
keyword
keyword
keyword
## END
## status: 0
## timeout: 10

#### type -t control flow
type -t break continue return exit


# In OSH, tye are now keywords AND dynamic builtins
#
# We MAY make them builtins in OSH, and keywords in YSH
## STDOUT:
builtin
builtin
builtin
builtin
## END
## status: 0
## timeout: 10

#### type -t -> file
type -t find xargs
## STDOUT:
file
file
## END
## status: 0
## timeout: 10

#### type -t -> not found
type -t echo ZZZ find ==
echo status=$?
## STDOUT:
builtin
file
status=1
## END
## status: 0
## timeout: 10

#### type -p and -P builtin -> file
touch /tmp/{mv,tar,grep}
chmod +x /tmp/{mv,tar,grep}
PATH=/tmp:$PATH

type -p mv tar grep
echo --
type -P mv tar grep
## STDOUT:
/tmp/mv
/tmp/tar
/tmp/grep
--
/tmp/mv
/tmp/tar
/tmp/grep
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -a -P gives multiple files

touch _tmp/pwd
chmod +x _tmp/pwd
PATH="_tmp:/bin"

type -a -P pwd
## STDOUT:
_tmp/pwd
/bin/pwd
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -p builtin -> not found
type -p FOO BAR NOT_FOUND
## stdout:
## status: 1
## timeout: 10

#### type -p builtin -> not a file
type -p cd type builtin command
## stdout:
## status: 0
## timeout: 10

#### type -P builtin -> not found
type -P FOO BAR NOT_FOUND
## stdout:
## status: 1
## timeout: 10

#### type -P builtin -> not a file
type -P cd type builtin command
## stdout:
## status: 1
## timeout: 10

#### type -P builtin -> not a file but file found
touch _tmp/{mv,tar,grep}
chmod +x _tmp/{mv,tar,grep}
PATH=_tmp:$PATH

mv () { ls; }
tar () { ls; }
grep () { ls; }
type -P mv tar grep cd builtin command type
## STDOUT:
_tmp/mv
_tmp/tar
_tmp/grep
## END
## status: 1
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -f builtin -> not found
type -f FOO BAR NOT FOUND
## stdout:
## status: 1
## timeout: 10

#### type -f builtin -> function and file exists
touch /tmp/{mv,tar,grep}
chmod +x /tmp/{mv,tar,grep}
PATH=/tmp:$PATH

mv () { ls; }
tar () { ls; }
grep () { ls; }
type -f mv tar grep
## STDOUT:
mv is /tmp/mv
tar is /tmp/tar
grep is /tmp/grep
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type prints function source code
f () { echo; }
type -a f
echo

type f
## STDOUT:
f is a function
f () 
{ 
    echo
}

f is a function
f () 
{ 
    echo
}
## END
## status: 0
## timeout: 10

#### type -ap -> function
f () { :; }
type -ap f
## stdout:
## status: 0
## timeout: 10

#### type -a -> alias; prints alias definition
shopt -s expand_aliases
alias ll="ls -lha"
type -a ll
## stdout: ll is aliased to `ls -lha'
## status: 0
## timeout: 10

#### type -ap -> alias
shopt -s expand_aliases
alias ll="ls -lha"
type -ap ll
## stdout:
## status: 0
## timeout: 10

#### type -a -> builtin
type -a cd
## stdout: cd is a shell builtin
## status: 0
## timeout: 10

#### type -ap -> builtin
type -ap cd
## stdout:
## status: 0
## timeout: 10

#### type -a -> keyword
type -a while
## stdout: while is a shell keyword
## status: 0
## timeout: 10

#### type -a -> file
touch _tmp/date
chmod +x _tmp/date
PATH=/bin:_tmp  # control output

type -a date
## STDOUT:
date is /bin/date
date is _tmp/date
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -ap -> file; abbreviated
touch _tmp/date
chmod +x _tmp/date
PATH=/bin:_tmp  # control output

type -ap date
## STDOUT:
/bin/date
_tmp/date
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -a -> builtin and file
touch _tmp/pwd
chmod +x _tmp/pwd
PATH=/bin:_tmp  # control output

type -a pwd
## STDOUT:
pwd is a shell builtin
pwd is /bin/pwd
pwd is _tmp/pwd
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -a -> builtin and file and shell function
touch _tmp/pwd
chmod +x _tmp/pwd
PATH=/bin:_tmp  # control output

type -a pwd
echo ---

pwd () 
{ 
    echo function-too
}

osh-normalize() {
  sed 's/shell function/function/'
}

type -a pwd | osh-normalize
echo ---

type -a -f pwd | osh-normalize
## STDOUT:
pwd is a shell builtin
pwd is /bin/pwd
pwd is _tmp/pwd
---
pwd is a function
pwd () 
{ 
    echo function-too
}
pwd is a shell builtin
pwd is /bin/pwd
pwd is _tmp/pwd
---
pwd is a shell builtin
pwd is /bin/pwd
pwd is _tmp/pwd
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -ap -> builtin and file; doesn't print builtin or function
touch _tmp/pwd
chmod +x _tmp/pwd
PATH=/bin:_tmp  # control output

# Function is also ignored
pwd() { echo function-too; }

type -ap pwd
echo ---

type -p pwd
## STDOUT:
/bin/pwd
_tmp/pwd
---
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### type -a -> executable not in PATH
touch _tmp/executable
chmod +x _tmp/executable
type -a executable
## stdout:
## status: 1
## timeout: 10

#### type -P does not find directories (regression)

mkdir -p _tmp
PATH="_tmp:$PATH"
mkdir _tmp/cat

type -P _tmp/cat
echo status=$?
type -P cat
echo status=$?
## STDOUT:
status=1
/usr/bin/cat
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-type-bash builtin-type-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
