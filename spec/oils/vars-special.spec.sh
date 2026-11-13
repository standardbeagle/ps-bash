## Imported from the Oils spec tests (vars-special.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### $PWD is set
# Just test that it has a slash for now.
echo $PWD | grep -q /
echo status=$?
## stdout: status=0
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### $PWD is not only set, but exported
env | grep -q PWD
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### $1 .. $9 are scoped, while $0 is not
fun() {
  case $0 in
    *sh)
      echo 'sh'
      ;;
    *sh-*)  # bash-4.4 is OK
      echo 'sh'
      ;;
  esac

  echo $1 $2
}
fun a b
## STDOUT:
sh
a b
## END
## status: 0
## timeout: 10

#### $?
echo $?  # starts out as 0
sh -c 'exit 33'
echo $?
## STDOUT:
0
33
## END
## status: 0
## timeout: 10

#### $#
set -- 1 2 3 4
echo $#
## stdout: 4
## status: 0
## timeout: 10

#### $$ looks like a PID
echo $$ | egrep -q '[0-9]+'  # Test that it has decimal digits
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### $$ doesn't change with subshell or command sub
# Just test that it has decimal digits
set -o errexit
die() {
  echo 1>&2 "$@"; exit 1
}
parent=$$
test -n "$parent" || die "empty PID in parent"
( child=$$
  test -n "$child" || die "empty PID in subshell"
  test "$parent" = "$child" || die "should be equal: $parent != $child"
  echo 'subshell OK'
)
echo $( child=$$
        test -n "$child" || die "empty PID in command sub"
        test "$parent" = "$child" || die "should be equal: $parent != $child"
        echo 'command sub OK'
      )
exit 3  # make sure we got here
## STDOUT:
subshell OK
command sub OK
## END
## status: 3
## timeout: 10

#### $BASHPID DOES change with subshell and command sub
set -o errexit
die() {
  echo 1>&2 "$@"; exit 1
}
parent=$BASHPID
test -n "$parent" || die "empty BASHPID in parent"
( child=$BASHPID
  test -n "$child" || die "empty BASHPID in subshell"
  test "$parent" != "$child" || die "should not be equal: $parent = $child"
  echo 'subshell OK'
)
echo $( child=$BASHPID
        test -n "$child" || die "empty BASHPID in command sub"
        test "$parent" != "$child" ||
          die "should not be equal: $parent = $child"
        echo 'command sub OK'
      )
exit 3  # make sure we got here

# mksh also implements BASHPID!
## STDOUT:
subshell OK
command sub OK
## END
## status: 3
## XFAIL ps-bash gap-oils-vars-special vars-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Background PID $! looks like a PID
sleep 0.01 &
pid=$!
wait
echo $pid | egrep '[0-9]+' >/dev/null
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### $PPID
# ps-bash: the recorded expectation was one run's PID; check the shape instead (deterministic).
echo $PPID | egrep -q '^[0-9]+$'
echo status=$?

# NOTE: There is also $BASHPID
## stdout: status=0
## status: 0
## timeout: 10

#### $PIPESTATUS
echo hi | sh -c 'cat; exit 33' | wc -l >/dev/null
argv.py "${PIPESTATUS[@]}"
## stdout:
## status: 127
## timeout: 10

#### $RANDOM
expr $0 : '.*/osh$' && exit 99  # Disabled because of spec-runner.sh issue
# ps-bash: the recorded expectation was one run's random value; check the shape instead (deterministic).
echo $RANDOM | egrep -q '^[0-9]+$'
echo status=$?
## STDOUT:
0
status=0
## END
## status: 0
## timeout: 10

#### $UID and $EUID
# These are both bash-specific.
set -o errexit
echo $UID | egrep -o '[0-9]+' >/dev/null
echo $EUID | egrep -o '[0-9]+' >/dev/null
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### $OSTYPE is non-empty
test -n "$OSTYPE"
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### $HOME is NOT set
case $SH in *zsh) echo 'zsh sets HOME'; exit ;; esac

home=$(echo $HOME)
test "$home" = ""
echo status=$?

env | grep HOME
echo status=$?

# not in interactive shell either
$SH -i -c 'echo $HOME' | grep /
echo status=$?
## STDOUT:
status=1
HOME=/home/beagle
status=0
/home/beagle
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-vars-special vars-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Vars set interactively only: $HISTFILE
case $SH in dash|mksh|zsh) exit ;; esac

$SH --norc --rcfile /dev/null -c 'echo histfile=${HISTFILE:+yes}'
$SH --norc --rcfile /dev/null -i -c 'echo histfile=${HISTFILE:+yes}'
## STDOUT:
histfile=
histfile=yes
## END
## status: 0
## XFAIL ps-bash gap-oils-vars-special vars-special (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### UID EUID PPID can't be changed

# bash makes these 3 read-only
{
  UID=xx $SH -c 'echo uid=$UID'

  EUID=xx $SH -c 'echo euid=$EUID'

  PPID=xx $SH -c 'echo ppid=$PPID'

} > out.txt

# bash shows that vars are readonly
# zsh shows other errors
# cat out.txt
#echo

grep '=xx' out.txt
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10
## real-launcher

#### HOSTNAME OSTYPE can be changed
case $SH in zsh) exit ;; esac

#$SH -c 'echo hostname=$HOSTNAME'

HOSTNAME=x $SH -c 'echo hostname=$HOSTNAME'
OSTYPE=x $SH -c 'echo ostype=$OSTYPE'
echo

#PS4=x $SH -c 'echo ps4=$PS4'

# OPTIND is special
#OPTIND=xx $SH -c 'echo optind=$OPTIND'
## STDOUT:
hostname=x
ostype=x

## END
## status: 0
## timeout: 10
## real-launcher

#### $HOSTNAME
test "$HOSTNAME" = "$(hostname)"
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### $LINENO is the current line, not line of function call
echo $LINENO  # first line
g() {
  argv.py $LINENO  # line 3
}
f() {
  argv.py $LINENO  # line 6
  g
  argv.py $LINENO  # line 8
}
f
## stdout: 1
## status: 127
## timeout: 10

#### $LINENO in [[
echo one
[[ $LINENO -eq 2 ]] && echo OK
## STDOUT:
one
OK
## END
## status: 0
## timeout: 10

#### $LINENO in ((
echo one
(( x = LINENO ))
echo $x
## STDOUT:
one
2
## END
## status: 0
## timeout: 10

#### $LINENO in for loop
# hm bash doesn't take into account the word break.  That's OK; we won't either.
echo one
for x in \
  $LINENO zzz; do
  echo $x
done
## STDOUT:
one
3
zzz
## END
## status: 0
## timeout: 10

#### $LINENO in other for loops
set -- a b c
for x; do
  echo $LINENO $x
done
## STDOUT:
3 a
3 b
3 c
## END
## status: 0
## timeout: 10

#### $LINENO in for (( loop
# This is a real edge case that I'm not sure we care about.  We would have to
# change the span ID inside the loop to make it really correct.
echo one
for (( i = 0; i < $LINENO; i++ )); do
  echo $i
done
## STDOUT:
one
0
1
2
3
## END
## status: 0
## timeout: 10

#### $LINENO for assignment
a1=$LINENO a2=$LINENO
b1=$LINENO b2=$LINENO
echo $a1 $a2
echo $b1 $b2
## STDOUT:
1 1
2 2
## END
## status: 0
## timeout: 10

#### $LINENO in case
case $LINENO in
  1) echo 'got line 1' ;;
  *) echo line=$LINENO
esac
## stdout: got line 1
## status: 0
## timeout: 10

#### $_ with simple command and evaluation

name=world
echo "hi $name"
echo "$_"
## STDOUT:
hi world
hi world
## END
## status: 0
## timeout: 10

#### $_ and ${_}
case $SH in dash|mksh) exit ;; esac

_var=value

: 42
echo $_ $_var ${_}var

: 'foo'"bar"
echo $_
## STDOUT:
42 value 42var
foobar
## END
## status: 0
## timeout: 10
## real-launcher

#### $_ with word splitting
case $SH in dash|mksh) exit ;; esac

setopt shwordsplit  # for ZSH

x='with spaces'
: $x
echo $_
## stdout: spaces
## status: 0
## timeout: 10
## real-launcher

#### $_ with pipeline and subshell
case $SH in dash|mksh) exit ;; esac

shopt -s lastpipe

seq 3 | echo last=$_

echo pipeline=$_

( echo subshell=$_ )
echo done=$_


# very weird semantics for zsh!
## STDOUT:
last=lastpipe
pipeline=last=lastpipe
subshell=pipeline=last=lastpipe
done=pipeline=last=lastpipe
## END
## status: 0
## timeout: 10
## real-launcher

#### $_ with && and ||
case $SH in dash|mksh) exit ;; esac

echo hi && echo last=$_
echo and=$_

echo hi || echo last=$_
echo or=$_
## STDOUT:
hi
last=hi
and=last=hi
hi
or=hi
## END
## status: 0
## timeout: 10
## real-launcher

#### $_ is not reset with (( and [[

# bash is inconsistent because it does it for pipelines and assignments, but
# not (( and [[

case $SH in dash|mksh) exit ;; esac

echo simple
(( a = 2 + 3 ))
echo "(( $_"

[[ a == *.py ]]
echo "[[ $_"
## STDOUT:
simple
(( simple
[[ (( simple
## END
## status: 0
## timeout: 10
## real-launcher

#### $_ with assignments, arrays, etc.
case $SH in dash|mksh) exit ;; esac

: foo
echo "colon [$_]"

s=bar
echo "bare assign [$_]"

# zsh uses declare; bash uses s=bar
declare s=bar
echo "declare [$_]"

# zsh remains s:declare, bash resets it
a=(1 2)
echo "array [$_]"

# zsh sets it to declare, bash uses the LHS a
declare a=(1 2)
echo "declare array [$_]"

declare -g d=(1 2)
echo "declare flag [$_]"
## STDOUT:
colon [foo]
bare assign []
declare [s=bar]
array []
declare array [a]
declare flag [d]
## END
## status: 0
## timeout: 10
## real-launcher

#### $_ with loop

case $SH in dash|mksh) exit ;; esac

# zsh resets it when in a loop

echo init
echo begin=$_
for x in 1 2 3; do
  echo prev=$_
done
## STDOUT:
init
begin=init
prev=begin=init
prev=prev=begin=init
prev=prev=prev=begin=init
## END
## status: 0
## timeout: 10
## real-launcher

#### $_ is not undefined on first use
set -e

x=$($SH -u -c 'echo prev=$_')
echo status=$?

# bash and mksh set $_ to $0 at first; zsh is empty
#echo "$x"
## stdout: status=0
## status: 0
## timeout: 10
## real-launcher

#### BASH_VERSION / OILS_VERSION
case $SH in
  bash*)
    # BASH_VERSION=zz

    echo $BASH_VERSION | egrep -o '4\.4\.0' > /dev/null
    echo matched=$?
    ;;
  *osh)
    # note: version string is mutable like in bash.  I guess that's useful for
    # testing?  We might want a strict mode to eliminate that?

    echo $OILS_VERSION | egrep -o '[0-9]+\.[0-9]+\.' > /dev/null
    echo matched=$?
    ;;
  *)
    echo 'no version'
    ;;
esac
## stdout: matched=1
## status: 0
## timeout: 10
## real-launcher

#### $SECONDS

# most likely 0 seconds, but in CI I've seen 1 second
echo $SECONDS | awk '/[0-9]+/ { print "ok" }'
## stdout: ok
## status: 0
## timeout: 10
