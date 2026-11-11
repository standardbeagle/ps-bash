## Imported from the Oils spec tests (builtin-trap-bash.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### trap -l
trap -l | grep INT >/dev/null
## stdout:
## status: 0
## timeout: 10

#### trap -p

trap 'echo exit' EXIT

trap -p > parent.txt

grep EXIT parent.txt >/dev/null
if test $? -eq 0; then
  echo shown
else
  echo not shown
fi
## STDOUT:
shown
exit
## END
## status: 0
## timeout: 10

#### trap -p in child is BUGGY in bash

# It shows the trap even though it doesn't execute it!

trap 'echo exit' EXIT

trap -p | cat > child.txt

grep EXIT child.txt >/dev/null
if test $? -eq 0; then
  echo shown
else
  echo not shown
fi
## STDOUT:
shown
exit
## END
## status: 0
## timeout: 10

#### trap DEBUG ignores $?
debuglog() {
  echo "  [$@]"
  return 42     # IGNORED FAILURE
}

trap 'debuglog $LINENO' DEBUG

echo status=$?
echo A
echo status=$?
echo B
echo status=$?
## STDOUT:
  [8]
status=0
  [9]
A
  [10]
status=0
  [11]
B
  [12]
status=0
## END
## status: 0
## timeout: 10

#### but trap DEBUG respects errexit
set -o errexit

debuglog() {
  echo "  [$@]"
  return 42
}

trap 'debuglog $LINENO' DEBUG

echo status=$?
echo A
echo status=$?
echo B
echo status=$?
## STDOUT:
  [10]
## END
## status: 42
## timeout: 10

#### trap DEBUG with 'return'

debuglog() {
  echo "  [$@]"
}

trap 'debuglog $LINENO; return 42' DEBUG

echo status=$?
echo A
echo status=$?
echo B
echo status=$?



# OSH doesn't ignore this
## STDOUT:
  [8]
status=0
  [9]
A
  [10]
status=0
  [11]
B
  [12]
status=0
## END
## status: 0
## timeout: 10

#### trap DEBUG with 'exit'
debuglog() {
  echo "  [$@]"
}

trap 'debuglog $LINENO; exit 42' DEBUG

echo status=$?
echo A
echo status=$?
echo B
echo status=$?
## STDOUT:
  [7]
## END
## status: 42
## timeout: 10

#### trap DEBUG and control flow

debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

while true; do
  echo hello
  break
done
## STDOUT:
  [7]
  [8]
hello
  [9]
## END
## status: 0
## timeout: 10

#### trap DEBUG not run in forked interpreter for first pipeline part

debuglog() {
  #echo "  PID=$$ BASHPID=$BASHPID LINENO=$1"
  echo "  LINENO=$1"
}
trap 'debuglog $LINENO' DEBUG

{ echo pipe1;
  echo pipe2; } \
  | cat
echo ok
## STDOUT:
  LINENO=10
pipe1
pipe2
  LINENO=11
ok
## END
## status: 0
## timeout: 10

#### One 'echo' in first pipeline part - why does bash behave differently from case above?

# TODO: bash runs the trap 3 times, and osh only twice.  I don't see why.  Is
# it because Process::Run() does trap_state.ClearForSubProgram()?  Probably
#echo top PID=$$ BASHPID=$BASHPID
#shopt -s lastpipe

debuglog() {
  #echo "  PID=$$ BASHPID=$BASHPID LINENO=$1"
  #echo "  LINENO=$1 $BASH_COMMAND"
  # LINENO=6 echo pipeline
  # LINENO=7 cat
  echo "  LINENO=$1"
}
trap 'debuglog $LINENO' DEBUG

echo pipeline \
  | cat
echo ok
## STDOUT:
  LINENO=16
  LINENO=17
pipeline
  LINENO=18
ok
## END
## status: 0
## timeout: 10

#### trap DEBUG and pipeline (lastpipe difference)
debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

# gets run for each one of these
{ echo a; echo b; }

# only run for the last one, maybe I guess because traps aren't inherited?
{ echo x; echo y; } | wc -l

# bash runs for all of these, but OSH doesn't because we have SubProgramThunk
# Hm.
date | cat | wc -l

date |
  cat |
  wc -l


# Marking OK due to lastpipe execution difference
## STDOUT:
  [7]
a
  [7]
b
  [10]
2
  [14]
  [14]
  [14]
1
  [16]
  [17]
  [18]
1
## END
## status: 0
## timeout: 10

#### trap DEBUG function call
debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

f() {
  local mylocal=1
  for i in "$@"; do
    echo i=$i
  done
}

f A B  # executes ONCE here, but does NOT go into the function call

echo next

f X Y

echo ok
## STDOUT:
  [13]
i=A
i=B
  [15]
next
  [17]
i=X
i=Y
  [19]
ok
## END
## status: 0
## timeout: 10

#### trap DEBUG case
debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

name=foo.py

case $name in 
  *.py)
    echo python
    ;;
  *.sh)
    echo shell
    ;;
esac
echo ok
## STDOUT:
  [6]
  [8]
  [10]
python
  [16]
ok
## END
## status: 0
## timeout: 10

#### trap DEBUG for each

debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

for x in 1 2; do
  echo x=$x
done

echo ok


# NOT matching bash right now because 'while' loops don't have it
# And we have MORE LOOPS
#
# What we really need is a trap that runs in the main loop and TELLS you what
# kind of node it is?
## STDOUT:
  [7]
  [8]
x=1
  [7]
  [8]
x=2
  [11]
ok
## END
## status: 0
## timeout: 10

#### trap DEBUG for expr
debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

for (( i =3 ; i < 5; ++i )); do
  echo i=$i
done

echo ok
## STDOUT:
  [6]
  [6]
  [7]
i=3
  [6]
  [6]
  [7]
i=4
  [6]
  [6]
  [10]
ok
## END
## status: 0
## timeout: 10

#### trap DEBUG if while
debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

if test x = x; then
  echo if
fi 

while test x != x; do
  echo while
done
## STDOUT:
  [6]
  [7]
if
  [10]
## END
## status: 0
## timeout: 10

#### Compare trap DEBUG vs. trap ERR

# Pipelines and AndOr are problematic

# THREE each
trap 'echo dbg $LINENO' DEBUG

false | false | false

false || false || false

! true

trap - DEBUG


# ONE EACH
trap 'echo err $LINENO' ERR

false | false | false

false || false || false

! true  # not run

echo ok
## STDOUT:
dbg 7
dbg 7
dbg 7
dbg 9
dbg 9
dbg 9
dbg 11
dbg 13
err 19
err 21
ok
## END
## status: 0
## timeout: 10

#### trap DEBUG with non-compound commands
case $SH in dash|mksh) exit ;; esac

debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

echo a
echo b; echo c

echo d && echo e
echo f || echo g

(( h = 42 ))
[[ j == j ]]

var=value

readonly r=value
## STDOUT:
  [8]
a
  [9]
b
  [9]
c
  [11]
d
  [11]
e
  [12]
f
  [14]
  [15]
  [17]
  [19]
## END
## status: 0
## timeout: 10
## real-launcher

#### trap DEBUG and command sub / subshell
case $SH in dash|mksh) exit ;; esac

debuglog() {
  echo "  [$@]"
}
trap 'debuglog $LINENO' DEBUG

echo "result = $(echo command sub; echo two)"
( echo subshell
  echo two
)
echo done
## STDOUT:
  [8]
result = command sub
two
subshell
two
  [12]
done
## END
## status: 0
## timeout: 10
## real-launcher

#### Combine DEBUG trap and USR1 trap

case $SH in dash|mksh|ash) exit ;; esac

trap 'false; echo $LINENO usr1' USR1
trap 'false; echo $LINENO dbg' DEBUG

sh -c "kill -USR1 $$"
echo after=$?
## STDOUT:
7 dbg
1 dbg
1 dbg
1 usr1
8 dbg
after=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-bash builtin-trap-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Combine ERR trap and USR1 trap

case $SH in dash|mksh|ash) exit ;; esac

trap 'false; echo $LINENO usr1' USR1
trap 'false; echo $LINENO err' ERR

sh -c "kill -USR1 $$"
echo after=$?
## STDOUT:
1 err
1 usr1
after=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-bash builtin-trap-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Combine DEBUG trap and ERR trap

case $SH in dash|mksh|ash) exit ;; esac

trap 'false; echo $LINENO err' ERR
trap 'false; echo $LINENO debug' DEBUG

false
echo after=$?
## STDOUT:
7 err
7 debug
7 debug
7 debug
7 err
8 err
8 debug
after=1
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-bash builtin-trap-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
