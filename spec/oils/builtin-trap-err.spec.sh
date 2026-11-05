## Imported from the Oils spec tests (builtin-trap-err.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### trap can use original $LINENO

trap 'echo line=$LINENO' ERR

false
false
echo ok
## STDOUT:
line=4
line=5
ok
## END
## status: 0
## timeout: 10

#### trap ERR and set -o errexit

trap 'echo line=$LINENO' ERR

false
echo a

set -o errexit

echo b
false   # trap executed, and executation also halts
echo c  # doesn't get here
## STDOUT:
line=4
a
b
line=10
## END
## status: 1
## timeout: 10

#### trap ERR and errexit disabled context

trap 'echo line=$LINENO' ERR

false
echo a

set -o errexit

echo b
if false; then
  echo xx
fi
echo c  # doesn't get here
## STDOUT:
line=4
a
b
c
## END
## status: 0
## timeout: 10

#### trap ERR and if statement

if test -f /nope; then echo file exists; fi

trap 'echo err' ERR
#trap 'echo line=$LINENO' ERR

if test -f /nope; then echo file exists; fi
## stdout:
## status: 0
## timeout: 10

#### trap ERR and || conditional

trap 'echo line=$LINENO' ERR

false || false || false
echo ok

false && false
echo ok
## STDOUT:
line=4
ok
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap ERR and pipeline

# mksh and bash have different line numbers in this case
#trap 'echo line=$LINENO' ERR
trap 'echo line=$LINENO' ERR

# it's run for the last 'false'
false | false | false

{ echo pipeline; false; } | false | false

# it's never run here
! true
! false
## STDOUT:
line=7
line=9
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap ERR pipelines without simple commands

trap 'echo assign' ERR
a=$(false) | a=$(false) | a=$(false)

trap 'echo dparen' ERR
(( 0 )) | (( 0 )) | (( 0 ))

trap 'echo dbracket' ERR
[[ a = b ]] | [[ a = b ]] | [[ a = b ]]

# bash anomaly - it gets printed twice?
trap 'echo subshell' ERR
(false) | (false) | (false) | (false)

# same bug
trap 'echo subshell2' ERR 
(false) | (false) | (false) | (false; false)

trap 'echo group' ERR
{ false; } | { false; } | { false; }

echo ok
## STDOUT:
assign
dparen
dbracket
subshell
subshell
subshell2
subshell2
group
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Pipeline group quirk

# Oh this is because it's run for the PIPELINE, not for the last thing!  Hmmm

trap 'echo group2' ERR
{ false; } | { false; } | { false; false; }

echo ok
## STDOUT:
group2
ok
## END
## status: 0
## timeout: 10

#### trap ERR does not run in errexit situations

trap 'echo line=$LINENO' ERR

if false; then
  echo if
fi

while false; do
  echo while
done

until false; do
  echo until
  break
done

false || false || false

false && false && false

false; false; false

echo ok
## STDOUT:
until
line=17
line=21
line=21
line=21
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap ERR doesn't run in subprograms - subshell, command sub, async

trap 'echo line=$LINENO' ERR

( false; echo subshell )

x=$( false; echo command sub )

false & wait

{ false; echo async; } & wait

false
echo ok
## STDOUT:
subshell
async
line=12
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap ERR doesn't run with &

trap 'echo line=$LINENO' ERR

false & wait

{ false; echo async; } & wait
## stdout: async
## status: 0
## timeout: 10

#### trap ERR not active in shell functions in (bash behavior)

trap 'echo line=$LINENO' ERR

f() {
  false 
  true
}

f
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set -o errtrace - trap ERR runs in shell functions

trap 'echo err' ERR

passing() {
  false  # line 4
  true
}

failing() {
  true
  false
}

passing
failing

set -o errtrace

echo 'now with errtrace'
passing
failing

echo ok
## STDOUT:
err
now with errtrace
err
err
err
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set -o errtrace - trap ERR runs in shell functions (LINENO)

trap 'echo line=$LINENO' ERR

passing() {
  false  # line 4
  true
}

failing() {
  true
  false
}

passing
failing

set -o errtrace

echo 'now with errtrace'
passing
failing

echo ok
## STDOUT:
line=15
now with errtrace
line=5
line=11
line=21
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap ERR with "atoms": assignment (( [[

trap 'echo line=$LINENO' ERR

x=$(false)

[[ a == b ]]

(( 0 ))
echo ok
## STDOUT:
line=4
line=6
line=8
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap ERR with for,  case, { }

trap 'echo line=$LINENO' ERR

for y in 1 2; do
  false
done

case x in
  x) false ;;
  *) false ;;
esac

{ false; false; false; }
echo ok
## STDOUT:
line=5
line=5
line=9
line=13
line=13
line=13
ok
## END
## status: 0
## timeout: 10

#### trap ERR with redirect

trap 'echo line=$LINENO' ERR

false

{ false 
  true
} > /zz  # error
echo ok


# doesn't update line for redirect
## STDOUT:
line=4
line=4
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap ERR
err() {
  echo "err [$@] $?"
}
trap 'err x y' ERR 

echo A

false
echo B

( exit 42 )
echo C

trap - ERR  # disable trap

false
echo D

trap 'echo after errexit $?' ERR 

set -o errexit

( exit 99 )
echo E
## STDOUT:
A
err [x y] 1
B
err [x y] 42
C
D
after errexit 99
## END
## status: 99
## timeout: 10

#### set -o errtrace: trap ERR runs in subprograms
case $SH in mksh) exit ;; esac

set -o errtrace
trap 'echo line=$LINENO' ERR

( false; echo subshell )

x=$( false; echo command sub )

false
echo ok


# ash doesn't reject errtrace, but doesn't implement it
## STDOUT:
line=7
subshell
line=11
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### set -o errtrace: trap ERR with &
case $SH in mksh) exit ;; esac

set -o errtrace
trap 'echo line=$LINENO' ERR

false & wait

{ false; echo async; } & wait
## STDOUT:
line=9
async
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### trap ERR and pipelines - PIPESTATUS difference
case $SH in ash) exit ;; esac

err() {
  echo "err [$@] status=$? [${PIPESTATUS[@]}]"
}
trap 'err' ERR 

echo A

false

# succeeds
echo B | grep B

# fails
echo C | grep zzz

echo D | grep zzz | cat

set -o pipefail
echo E | grep zzz | cat

trap - ERR  # disable trap

echo F | grep zz
echo ok


# we don't set PIPESTATUS unless we get a pipeline
## STDOUT:
A
err [] status=1 [1]
B
err [] status=1 [0 1]
err [] status=1 [0 1 0]
ok
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### error in trap ERR (recursive)
case $SH in dash) exit ;; esac

err() {
  echo err status $?
  false
  ( exit 2 )  # not recursively triggered
  echo err 2
}
trap 'err' ERR 

echo A
false
echo B

# Try it with errexit
set -e
false
echo C
## STDOUT:
A
err status 1
err 2
B
err status 1
## END
## status: 1
## XFAIL ps-bash gap-oils-builtin-trap-err builtin-trap-err (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
