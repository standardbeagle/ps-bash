## Imported from the Oils spec tests (builtin-bash.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### help
help
echo status=$? >&2
help help
echo status=$? >&2
help -- help
echo status=$? >&2
## STDOUT:
GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)
These shell commands are defined internally.  Type `help' to see this list.
Type `help name' to find out more about the function `name'.
Use `info bash' to find out more about the shell in general.
Use `man -k' or `info' to find out more about commands not in this list.

A star (*) next to a name means that the command is disabled.

 job_spec [&]                            history [-c] [-d offset] [n] or hist>
 (( expression ))                        if COMMANDS; then COMMANDS; [ elif C>
 . filename [arguments]                  jobs [-lnprs] [jobspec ...] or jobs >
 :                                       kill [-s sigspec | -n signum | -sigs>
 [ arg... ]                              let arg [arg ...]
 [[ expression ]]                        local [option] name[=value] ...
 alias [-p] [name[=value] ... ]          logout [n]
 bg [job_spec ...]                       mapfile [-d delim] [-n count] [-O or>
 bind [-lpsvPSVX] [-m keymap] [-f file>  popd [-n] [+N | -N]
 break [n]                               printf [-v var] format [arguments]
 builtin [shell-builtin [arg ...]]       pushd [-n] [+N | -N | dir]
 caller [expr]                           pwd [-LP]
 case WORD in [PATTERN [| PATTERN]...)>  read [-ers] [-a array] [-d delim] [->
 cd [-L|[-P [-e]] [-@]] [dir]            readarray [-d delim] [-n count] [-O >
 command [-pVv] command [arg ...]        readonly [-aAf] [name[=value] ...] o>
 compgen [-abcdefgjksuv] [-o option] [>  return [n]
 complete [-abcdefgjksuv] [-pr] [-DEI]>  select NAME [in WORDS ... ;] do COMM>
 compopt [-o|+o option] [-DEI] [name .>  set [-abefhkmnptuvxBCEHPT] [-o optio>
 continue [n]                            shift [n]
 coproc [NAME] command [redirections]    shopt [-pqsu] [-o] [optname ...]
 declare [-aAfFgiIlnrtux] [name[=value>  source filename [arguments]
 dirs [-clpv] [+N] [-N]                  suspend [-f]
 disown [-h] [-ar] [jobspec ... | pid >  test [expr]
 echo [-neE] [arg ...]                   time [-p] pipeline
 enable [-a] [-dnps] [-f filename] [na>  times
 eval [arg ...]                          trap [-lp] [[arg] signal_spec ...]
 exec [-cl] [-a name] [command [argume>  true
 exit [n]                                type [-afptP] name [name ...]
 export [-fn] [name[=value] ...] or ex>  typeset [-aAfFgiIlnrtux] name[=value>
 false                                   ulimit [-SHabcdefiklmnpqrstuvxPRT] [>
 fc [-e ename] [-lnr] [first] [last] o>  umask [-p] [-S] [mode]
 fg [job_spec]                           unalias [-a] name [name ...]
 for NAME [in WORDS ... ] ; do COMMAND>  unset [-f] [-v] [-n] [name ...]
 for (( exp1; exp2; exp3 )); do COMMAN>  until COMMANDS; do COMMANDS-2; done
 function name { COMMANDS ; } or name >  variables - Names and meanings of so>
 getopts optstring name [arg ...]        wait [-fn] [-p var] [id ...]
 hash [-lr] [-p pathname] [-dt] [name >  while COMMANDS; do COMMANDS-2; done
 help [-dms] [pattern ...]               { COMMANDS ; }
help: help [-dms] [pattern ...]
    Display information about builtin commands.
    
    Displays brief summaries of builtin commands.  If PATTERN is
    specified, gives detailed help on all commands matching PATTERN,
    otherwise the list of help topics is printed.
    
    Options:
      -d	output short description for each topic
      -m	display usage in pseudo-manpage format
      -s	output only a short usage synopsis for each topic matching
    		PATTERN
    
    Arguments:
      PATTERN	Pattern specifying a help topic
    
    Exit Status:
    Returns success unless PATTERN is not found or an invalid option is given.
help: help [-dms] [pattern ...]
    Display information about builtin commands.
    
    Displays brief summaries of builtin commands.  If PATTERN is
    specified, gives detailed help on all commands matching PATTERN,
    otherwise the list of help topics is printed.
    
    Options:
      -d	output short description for each topic
      -m	display usage in pseudo-manpage format
      -s	output only a short usage synopsis for each topic matching
    		PATTERN
    
    Arguments:
      PATTERN	Pattern specifying a help topic
    
    Exit Status:
    Returns success unless PATTERN is not found or an invalid option is given.
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bash builtin-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### mapfile
type mapfile >/dev/null 2>&1 || exit 0
printf '%s\n' {1..5..2} | {
  mapfile
  echo "n=${#MAPFILE[@]}"
  printf '[%s]\n' "${MAPFILE[@]}"
}
## STDOUT:
n=3
[1
]
[3
]
[5
]
## END
## status: 0
## timeout: 10

#### readarray (synonym for mapfile)
type readarray >/dev/null 2>&1 || exit 0
printf '%s\n' {1..5..2} | {
  readarray
  echo "n=${#MAPFILE[@]}"
  printf '[%s]\n' "${MAPFILE[@]}"
}
## STDOUT:
n=3
[1
]
[3
]
[5
]
## END
## status: 0
## timeout: 10

#### mapfile (array name): arr
type mapfile >/dev/null 2>&1 || exit 0
printf '%s\n' {1..5..2} | {
  mapfile arr
  echo "n=${#arr[@]}"
  printf '[%s]\n' "${arr[@]}"
}
## STDOUT:
n=3
[1
]
[3
]
[5
]
## END
## status: 0
## timeout: 10

#### mapfile (delimiter): -d delim
# Note: Bash-4.4+
type mapfile >/dev/null 2>&1 || exit 0
printf '%s:' {1..5..2} | {
  mapfile -d : arr
  echo "n=${#arr[@]}"
  printf '[%s]\n' "${arr[@]}"
}
## STDOUT:
n=3
[1:]
[3:]
[5:]
## END
## status: 0
## timeout: 10

#### mapfile (delimiter): -d '' (null-separated)
# Note: Bash-4.4+
type mapfile >/dev/null 2>&1 || exit 0
printf '%s\0' {1..5..2} | {
  mapfile -d '' arr
  echo "n=${#arr[@]}"
  printf '[%s]\n' "${arr[@]}"
}
## STDOUT:
n=3
[1]
[3]
[5]
## END
## status: 0
## timeout: 10

#### mapfile (truncate delim): -t
type mapfile >/dev/null 2>&1 || exit 0
printf '%s\n' {1..5..2} | {
  mapfile -t arr
  echo "n=${#arr[@]}"
  printf '[%s]\n' "${arr[@]}"
}
## STDOUT:
n=3
[1]
[3]
[5]
## END
## status: 0
## timeout: 10

#### mapfile -t doesn't remove \r
type mapfile >/dev/null 2>&1 || exit 0
printf '%s\r\n' {1..5..2} | {
  mapfile -t arr
  argv.py "${arr[@]}"
}
## stdout:
## status: 127
## timeout: 10

#### mapfile -t bugs (ble.sh)

# empty line
mapfile -t lines <<< $'hello\n\nworld'
echo len=${#lines[@]}
#declare -p lines

# initial newline
mapfile -t lines <<< $'\nhello'
echo len=${#lines[@]}
#declare -p lines

# trailing newline
mapfile -t lines <<< $'hello\n'
echo len=${#lines[@]}
#declare -p lines
## STDOUT:
len=3
len=2
len=2
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bash builtin-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### mapfile (store position): -O start
type mapfile >/dev/null 2>&1 || exit 0
printf '%s\n' a{0..2} | {
  arr=(x y z)
  mapfile -O 2 -t arr
  echo "n=${#arr[@]}"
  printf '[%s]\n' "${arr[@]}"
}
## STDOUT:
n=5
[x]
[y]
[a0]
[a1]
[a2]
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bash builtin-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### mapfile (input range): -s start -n count
type mapfile >/dev/null 2>&1 || exit 0
printf '%s\n' a{0..10} | {
  mapfile -s 5 -n 3 -t arr
  echo "n=${#arr[@]}"
  printf '[%s]\n' "${arr[@]}"
}
## STDOUT:
n=3
[a5]
[a6]
[a7]
## END
## status: 0
## timeout: 10

#### mapfile / readarray stdin  TODO: Fix me.
shopt -s lastpipe  # for bash

seq 2 | mapfile m
seq 3 | readarray r
echo ${#m[@]}
echo ${#r[@]}
## STDOUT:
2
3
## END
## status: 0
## timeout: 10
