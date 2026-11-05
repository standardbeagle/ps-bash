## Imported from the Oils spec tests (builtin-completion.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### complete with no args and complete -p both print completion spec

set -e

complete

complete -W 'foo bar' mycommand

complete -p

complete -F myfunc other

complete
## STDOUT:
complete -W 'foo bar' mycommand
complete -W 'foo bar' mycommand
complete -F myfunc other
## END
## status: 0
## timeout: 10

#### complete -F f is usage error

#complete -F f cmd

# Alias for complete -p
complete > /dev/null  # ignore OSH output for now
echo status=$?

# But this is an error
complete -F f
echo status=$?
## STDOUT:
status=0
status=2
## END
## status: 0
## timeout: 10

#### complete with nonexistent function
complete -F invalidZZ -D
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### complete with no action
complete foo
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### -A function prints functions
add () { expr 4 + 4; }
div () { expr 6 / 2; }
ek () { echo hello; }
__ec () { echo hi; }
_ab () { expr 10 % 3; }
compgen -A function
echo --
compgen -A function _
## STDOUT:
__ec
_ab
add
div
ek
--
__ec
_ab
## END
## status: 0
## timeout: 10

#### Invalid syntax
compgen -A foo
echo status=$?
## stdout: status=2
## status: 0
## timeout: 10

#### how compgen calls completion functions
foo_complete() {
  # first, cur, prev
  argv.py argv "$@"
  argv.py COMP_WORDS "${COMP_WORDS[@]}"
  argv.py COMP_CWORD "${COMP_CWORD}"
  argv.py COMP_LINE "${COMP_LINE}"
  argv.py COMP_POINT "${COMP_POINT}"
  #return 124
  COMPREPLY=(one two three)
}
compgen -F foo_complete foo a b c
## STDOUT:
one
two
three
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### complete -o -F (git)
foo() { echo foo; }
wrapper=foo
complete -o default -o nospace -F $wrapper git
## stdout:
## status: 0
## timeout: 10

#### compopt with invalid syntax
compopt -o invalid
echo status=$?
## stdout: status=2
## status: 0
## timeout: 10

#### compopt fails when not in completion function
# NOTE: Have to be executing a completion function
compopt -o filenames +o nospace
## stdout:
## status: 1
## timeout: 10

#### compgen -f on invalid  dir
compgen -f /non-existing-dir/
## stdout:
## status: 1
## timeout: 10

#### compgen -v with local vars
v1_global=0
f() {
  local v2_local=0	 
  compgen -v v
}
f
## STDOUT:
v1_global
v2_local
## END
## status: 0
## timeout: 10

#### compgen -v on unknown var
compgen -v __nonexistent__
## stdout:
## status: 1
## timeout: 10

#### compgen -v P
cd > /dev/null  # for some reason in bash, this makes PIPESTATUS appear!
compgen -v P | grep -E '^PATH|PWD' | sort
## STDOUT:
PATH
PWD
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### compgen -e with global/local exported vars
export v1_global=0
f() {
  local v2_local=0
  export v2_local
  compgen -e v
}
f
## STDOUT:
v1_global
v2_local
## END
## status: 0
## timeout: 10

#### compgen -e on known, but unexported, var
unexported=0
compgen -e unexported
## stdout:
## status: 1
## XFAIL ps-bash triage differs from bash 5.2 (auto-marked; investigate)
## timeout: 10

#### compgen -e on unknown var
compgen -e __nonexistent__
## stdout:
## status: 1
## timeout: 10

#### compgen -e P
cd > /dev/null  # for some reason in bash, this makes PIPESTATUS appear!
compgen -e P | grep -E '^PATH|PWD' | sort
## STDOUT:
PATH
PWD
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### compgen with actions: alias, setopt
alias v_alias='ls'
alias v_alias2='ls'
alias a1='ls'
compgen -A alias -A setopt v
## STDOUT:
v_alias
v_alias2
verbose
vi
## END
## status: 0
## timeout: 10

#### compgen with actions: shopt
compgen -A shopt -P [ -S ] nu
## stdout: [nullglob]
## status: 0
## timeout: 10

#### compgen with action and suffix: helptopic
compgen -A helptopic -S ___ fal
## stdout: false___
## status: 0
## timeout: 10

#### compgen -A user
# no assertion because this isn't hermetic
compgen -A user
## STDOUT:
root
daemon
bin
sys
sync
games
man
lp
mail
news
uucp
proxy
www-data
backup
list
irc
_apt
nobody
systemd-network
systemd-timesync
dhcpcd
messagebus
syslog
systemd-resolve
uuidd
landscape
polkitd
beagle
redis
postgres
ansible
pipeline
_galera
mysql
sshd
earlyoom
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### compgen -A command completes external commands
# NOTE: this test isn't hermetic
compgen -A command xarg | uniq
echo status=$?
## STDOUT:
xargs
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### compgen -A command completes functions and aliases
our_func() { echo ; }
our_func2() { echo ; }
alias our_alias=foo

compgen -A command our_
echo status=$?

# Introduce another function.  Note that we're missing test coverage for
# 'complete', i.e. bug #1064.
our_func3() { echo ; }

compgen -A command our_
echo status=$?
## STDOUT:
our_alias
our_func
our_func2
status=0
our_alias
our_func
our_func2
our_func3
status=0
## END
## status: 0
## timeout: 10

#### compgen -A command completes builtins and keywords
compgen -A command eva
echo status=$?
compgen -A command whil
echo status=$?
## STDOUT:
eval
status=0
while
status=0
## END
## status: 0
## timeout: 10

#### compgen -k shows Oils keywords too

# YSH has a superset of keywords:
# const var
# setvar setglobal
# proc func typed
# call =   # hm = is not here

compgen -k | sort | egrep '^(const|var|setvar|setglobal|proc|func|typed|call|=)$'
echo --
## stdout: --
## status: 0
## timeout: 10

#### compgen -k completes reserved shell keywords
compgen -k do | sort
echo status=$?
compgen -k el | sort
echo status=$?
## STDOUT:
do
done
status=0
elif
else
status=0
## END
## status: 0
## timeout: 10

#### -o filenames and -o nospace have no effect with compgen
# they are POSTPROCESSING.
compgen -o filenames -o nospace -W 'bin build'
## STDOUT:
bin
build
## END
## status: 0
## timeout: 10

#### compgen -W evaluates code in $()
IFS=':%'
compgen -W '$(echo "spam:eggs%ham cheese")'
## STDOUT:
spam
eggs
ham cheese
## END
## status: 0
## timeout: 10

#### compgen -W uses IFS, and delimiters are escaped with \
IFS=':%'
compgen -W 'spam:eggs%ham cheese\:colon'
## STDOUT:
spam
eggs
ham cheese:colon
## END
## status: 0
## timeout: 10

#### Parse errors for compgen -W and complete -W
# bash doesn't detect as many errors because it lacks static parsing.
compgen -W '${'
echo status=$?
complete -W '${' foo
echo status=$?
## STDOUT:
status=1
status=0
## END
## status: 0
## timeout: 10

#### Runtime errors for compgen -W
compgen -W 'foo $(( 1 / 0 )) bar'
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### Runtime errors for compgen -F func
_foo() {
  COMPREPLY=( foo bar )
  COMPREPLY+=( $(( 1 / 0 )) )  # FATAL, but we still have candidates
}
compgen -F _foo foo
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### compgen -W '' cmd is not a usage error
# Bug fix due to '' being falsey in Python
compgen -W '' -- foo
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### compgen -A builtin
compgen -A builtin g
## stdout: getopts
## status: 0
## timeout: 10

#### complete -C vs. compgen -C

f() { echo foo; echo bar; }

# Bash prints warnings: -C option may not work as you expect
#                       -F option may not work as you expect
#
# https://unix.stackexchange.com/questions/117987/compgen-warning-c-option-not-working-as-i-expected
#
# compexport fixes this problem, because it invokves ShellFuncAction, whcih
# sets COMP_ARGV, COMP_WORDS, etc.
#
# Should we print a warning?

compgen -C f b
echo compgen=$?

complete -C f b
echo complete=$?
## STDOUT:
foo
bar
compgen=0
complete=0
## END
## status: 0
## timeout: 10

#### compgen -F with scalar COMPREPLY

_comp_cmd_test() {
  unset -v COMPREPLY
  COMPREPLY=hello
}
compgen -F _comp_cmd_test
## stdout: hello
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### compgen -k shows the same keywords as bash

# bash adds ]] and } and coproc

# Use bash as an oracle
bash -c 'compgen -k' | sort > bash.txt

# osh vs. bash, or bash vs. bash
$SH -c 'compgen -k' | sort > this-shell.txt

#comm bash.txt this-shell.txt

# show lines in both files
comm -12 bash.txt this-shell.txt | egrep -v 'coproc|select'
## STDOUT:
!
[[
]]
case
do
done
elif
else
esac
fi
for
function
if
in
then
time
until
while
{
}
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### compgen doesn't respect -X for user-defined functions
# WORKAROUND: wrap in bash -i -c because non-interactive bash behaves
# differently!
case $SH in
  *bash|*osh)
    $SH --rcfile /dev/null -i -c '
shopt -s extglob
fun() {
  COMPREPLY=(one two three bin)
}
compgen -X "@(two|bin)" -F fun
echo --
compgen -X "!@(two|bin)" -F fun
'
esac
## STDOUT:
one
three
--
two
bin
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### compgen -W words -X filter
# WORKAROUND: wrap in bash -i -c because non-interactive bash behaves
# differently!
case $SH in
  *bash|*osh)
      $SH --rcfile /dev/null -i -c 'shopt -s extglob; compgen -X "@(two|bin)" -W "one two three bin"'
esac
## STDOUT:
one
three
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### compadjust with empty COMP_ARGV
case $SH in bash) exit ;; esac

COMP_ARGV=()
compadjust words
argv.py "${words[@]}"
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### compadjust with sparse COMP_ARGV
case $SH in bash) exit ;; esac

COMP_ARGV=({0..9})
unset -v 'COMP_ARGV['{1,3,4,6,7,8}']'
compadjust words
argv.py "${words[@]}"
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-builtin-completion builtin-completion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
