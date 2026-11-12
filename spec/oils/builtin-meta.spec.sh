## Imported from the Oils spec tests (builtin-meta.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### command -v
myfunc() { echo x; }
command -v echo
echo $?

command -v myfunc
echo $?

command -v nonexistent  # doesn't print anything
echo nonexistent=$?

command -v ''  # BUG FIX, shouldn't succeed
echo empty=$?

command -v for
echo $?
## STDOUT:
echo
0
myfunc
0
nonexistent=1
empty=1
for
0
## END
## status: 0
## timeout: 10

#### command -v executable, builtin

#command -v grep ls

command -v grep | egrep -o '/[^/]+$'
command -v ls | egrep -o '/[^/]+$'
echo

command -v true
command -v eval
## STDOUT:
/grep
/ls

true
eval
## END
## status: 0
## XFAIL ps-bash intentional-psbash-command a ps-bash command (grep, ls) runs ahead of a PATH file, so command -v prints its name, not /usr/bin/grep (intentional-differences.md: command -v ls / type ls)
## timeout: 10

#### command -v with multiple names
# ALL FOUR SHELLS behave differently here!
#
# bash chooses to swallow the error!  We agree with zsh if ANY word lookup
# fails, then the whole thing fails.

myfunc() { echo x; }
command -v echo myfunc ZZZ for
echo status=$?
## STDOUT:
echo
myfunc
for
status=0
## END
## status: 0
## timeout: 10

#### command -v doesn't find non-executable file
# PATH resolution is different

mkdir -p _tmp
PATH="_tmp:$PATH"
touch _tmp/non-executable _tmp/executable
chmod +x _tmp/executable

command -v _tmp/non-executable
echo status=$?

command -v _tmp/executable
echo status=$?
## STDOUT:
status=1
_tmp/executable
status=0
## END
## status: 0
## XFAIL ps-bash platform-windows-exec-bit Windows has no execute bit: chmod +x leaves no mark, so the two files cannot be told apart (intentional-differences.md, type -P/-a (Windows))
## timeout: 10

#### command -v doesn't find executable dir

mkdir -p _tmp
PATH="_tmp:$PATH"
mkdir _tmp/cat

command -v _tmp/cat
echo status=$?
command -v cat
echo status=$?
## STDOUT:
status=1
/usr/bin/cat
status=0
## END
## status: 0
## XFAIL ps-bash platform-windows-unix-bin expects /bin/date, /bin/pwd or /usr/bin/cat, which Windows does not have (intentional-differences.md, Platform)
## timeout: 10

#### command -V
myfunc() { echo x; }

shopt -s expand_aliases
alias ll='ls -l'

backtick=\`
command -V ll | sed "s/$backtick/'/g"
echo status=$?

command -V echo
echo status=$?

# Paper over insignificant difference
command -V myfunc | sed 's/shell function/function/'
echo status=$?

command -V nonexistent  # doesn't print anything
echo status=$?

command -V for
echo status=$?
## STDOUT:
ll is aliased to 'ls -l'
status=0
echo is a shell builtin
status=0
myfunc is a function
myfunc () 
{ 
    echo x
}
status=0
status=1
for is a shell keyword
status=0
## END
## status: 0
## timeout: 10

#### command -V nonexistent
command -V nonexistent 2>err.txt
echo status=$?
fgrep -o 'nonexistent: not found' err.txt || true
## STDOUT:
status=1
nonexistent: not found
## END
## status: 0
## timeout: 10

#### command skips function lookup
seq() {
  echo "$@"
}
command  # no-op
seq 3
command seq 3
# subshell shouldn't fork another process (but we don't have a good way of
# testing it)
( command seq 3 )
## STDOUT:
3
1
2
3
1
2
3
## END
## status: 0
## timeout: 10

#### command command seq 3
command command seq 3
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### command command -v seq
seq() {
  echo 3
}
command command -v seq
## stdout: seq
## status: 0
## timeout: 10

#### command -p (find hidden tool in default path)
export PATH=''
command -p ls
## stdout:
## status: 0
## timeout: 10

#### $(command type ls)
type() { echo FUNCTION; }
type
s=$(command type echo)
echo $s | grep builtin > /dev/null
echo status=$?
## STDOUT:
FUNCTION
status=0
## END
## status: 0
## timeout: 10

#### builtin
cd () { echo "hi"; }
cd
builtin cd / && pwd
unset -f cd
## STDOUT:
hi
/
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### builtin ls not found
builtin ls
## stdout:
## status: 1
## timeout: 10

#### builtin usage
 
builtin
echo status=$?

builtin --
echo status=$?

builtin -- false
echo status=$?
## STDOUT:
status=0
status=0
status=1
## END
## status: 0
## timeout: 10

#### builtin command echo hi
builtin command echo hi
## stdout: hi
## status: 0
## timeout: 10
