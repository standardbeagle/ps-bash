## Imported from the Oils spec tests (builtin-trap.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### traps are not active inside subshells $() ()  trap | cat

# TODO: should we change this?  We're not compatible with bash or busybox ash

trap 'echo bye' EXIT

# NOT a subshell
trap > traps.txt
wc -l traps.txt

echo '( )'
( trap )

echo '$(trap)'
echo $(trap)

echo 'trap | cat'
trap | cat

1 traps.txt
( )
$(trap)
trap -- 'echo bye' EXIT
trap | cat
bye
## STDOUT:
1 traps.txt
( )
trap -- 'echo bye' EXIT
$(trap)
trap -- 'echo bye' EXIT
trap | cat
trap -- 'echo bye' EXIT
bye
## END
## status: 2
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap accepts/ignores --
trap -- 'echo hi' EXIT
echo ok
## STDOUT:
ok
hi
## END
## status: 0
## timeout: 10

#### Register invalid trap, remove invalid trap
trap 'foo' SIGINVALID
if test $? -ne 0; then
  echo ok
fi

trap - SIGINVALID
if test $? -ne 0; then
  echo ok
fi
## STDOUT:
ok
ok
## END
## status: 0
## timeout: 10

#### trap foo gives non-zero error
trap 'foo'
if test $? -ne 0; then
  echo ok
fi
## stdout: ok
## status: 0
## timeout: 10

#### trap 'echo hi' KILL (regression test, caught by smoosh suite)
trap 'echo hi' 9
echo status=$?

trap 'echo hi' KILL
echo status=$?

trap 'echo hi' STOP
echo status=$?

trap 'echo hi' TERM
echo status=$?
## STDOUT:
status=0
status=0
status=0
status=0
## END
## status: 0
## timeout: 10

#### exit 1 when trap code string is invalid
# All shells spew warnings to stderr, but don't actually exit!  Bad!
trap 'echo <' EXIT
echo status=$?
## stdout: status=0
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap EXIT calling exit
cleanup() {
  echo "cleanup [$@]"
  exit 42
}
trap 'cleanup x y z' EXIT
## stdout: cleanup [x y z]
## status: 42
## timeout: 10

#### trap EXIT return status ignored
cleanup() {
  echo "cleanup [$@]"
  return 42
}
trap 'cleanup x y z' EXIT
## stdout: cleanup [x y z]
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap EXIT with PARSE error
trap 'echo FAILED' EXIT
for
## stdout: FAILED
## status: 2
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap EXIT with PARSE error and explicit exit
trap 'echo FAILED; exit 0' EXIT
for
## stdout: FAILED
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap EXIT with explicit exit
trap 'echo IN TRAP; echo $stdout' EXIT 
stdout=FOO
exit 42
## STDOUT:
IN TRAP
FOO
## END
## status: 42
## timeout: 10

#### trap EXIT sees exit status in $?
trap 'echo status=$?' EXIT
exit 42
## stdout: status=42
## status: 42
## timeout: 10

#### trap EXIT saves $?, clears trap, runs a command, then exits with it
trap 'st=$?; trap - EXIT; true; exit $st' EXIT
exit 7
## stdout:
## status: 7
## timeout: 10

#### bare exit in trap EXIT uses the status from before the trap
trap 'true; exit' EXIT
exit 3
## stdout:
## status: 3
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### bare exit in a function called from trap EXIT
cleanup() {
  echo cleanup
  true
  exit
}
trap cleanup EXIT
false
## stdout: cleanup
## status: 1
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap EXIT with command sub / subshell / pipeline
trap 'echo EXIT TRAP' EXIT 

echo $(echo command sub)

( echo subshell )

echo pipeline | cat
## STDOUT:
command sub
subshell
pipeline
EXIT TRAP
## END
## status: 0
## timeout: 10

#### eval in the exit trap (regression for issue #293)
trap 'eval "echo hi"' 0
## stdout: hi
## status: 0
## timeout: 10

#### exit codes for traps are isolated

trap 'echo USR1 trap status=$?; ( exit 42 )' USR1

echo before=$?

# Equivalent to 'kill -USR1 $$' except OSH doesn't have "kill" yet.
# /bin/kill doesn't exist on Debian unless 'procps' is installed.
sh -c "kill -USR1 $$"
echo after=$?
## STDOUT:
before=0
USR1 trap status=0
after=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### traps are cleared in subshell (started with &)

# Test with SIGURG because the default handler is SIG_IGN
#
# If we use SIGUSR1, I think the shell reverts to killing the process

# https://man7.org/linux/man-pages/man7/signal.7.html

trap 'echo SIGURG' URG

kill -URG $$

# Hm trap doesn't happen here
{ echo begin child; sleep 0.1; echo end child; } &
kill -URG $!
wait
echo "wait status $?"

# In the CI, mksh sometimes gives:
#
# USR1
# begin child
# done
# 
# leaving off 'end child'.  This seems like a BUG to me?
## STDOUT:
SIGURG
begin child
end child
wait status 0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap '' sets handler to empty string (SIG_IGN)

# Note: this doesn't actually test that it's SIG_IGN

trap '' USR1
trap
## stdout: trap -- '' SIGUSR1
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap '' with multiple signals

trap '' USR1 USR2
trap
## STDOUT:
trap -- '' SIGUSR1
trap -- '' SIGUSR2
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### SIGINT and INT are aliases
trap - SIGINT
echo $?
trap - INT
echo $?
## STDOUT:
0
0
## END
## status: 0
## timeout: 10

#### trap without args prints traps
trap 'echo exit' EXIT
echo status=$?

trap
echo status=$?
## STDOUT:
status=0
trap -- 'echo exit' EXIT
status=0
exit
## END
## status: 0
## timeout: 10

#### print trap handler with multiple lines
trap 'echo 1
echo 2
echo 3' INT

trap
## STDOUT:
trap -- 'echo 1
echo 2
echo 3' SIGINT
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap -p is like trap: it prints the handlers and full signal names
case $SH in dash) exit ;; esac
trap "echo INT" INT
trap "echo EXIT" EXIT
trap -p
## STDOUT:
trap -- 'echo EXIT' EXIT
trap -- 'echo INT' SIGINT
EXIT
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Register the same handler for multiple signals
trap 'echo test' TERM 2 EXIT
trap
## STDOUT:
trap -- 'echo test' EXIT
trap -- 'echo test' SIGINT
trap -- 'echo test' SIGTERM
test
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Remove multiple handlers with trap -
trap "echo int" INT
trap "echo e" EXIT
trap - int 0 3
trap

echo ---
trap "echo int" INT
trap "echo e" EXIT
trap - int 0 -99
if test $? -ne 0; then
  echo ok
fi
## STDOUT:
---
ok
## END
## status: 0
## timeout: 10

#### trap EXIT clears the EXIT trap
trap "echo INT" INT
trap "echo EXIT" EXIT
trap
echo ---
trap EXIT
trap
echo ---
trap INT
trap
## STDOUT:
trap -- 'echo EXIT' EXIT
trap -- 'echo INT' SIGINT
---
trap -- 'echo INT' SIGINT
---
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap 0 is equivalent to trap EXIT
trap "echo INT" INT
trap "echo EXIT" 0  # EXIT
trap
echo ---
trap 0
trap
## STDOUT:
trap -- 'echo EXIT' EXIT
trap -- 'echo INT' SIGINT
---
trap -- 'echo INT' SIGINT
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap 1 is equivalent to SIGHUP; HUP is equivalent to SIGHUP
trap 'echo HUP' SIGHUP
echo status=$?
trap 'echo HUP' HUP
echo status=$?
trap 'echo HUP' 1
echo status=$?
trap - HUP
echo status=$?
## STDOUT:
status=0
status=0
status=0
status=0
## END
## status: 0
## timeout: 10

#### trap 0 2 resets EXIT AND SIGINT

trap "echo EXIT" EXIT
echo ---
trap
echo ---
trap 0 2
trap
echo ---
trap "echo INT" INT
trap "echo EXIT" EXIT
trap 2 EXIT
trap
## STDOUT:
---
trap -- 'echo EXIT' EXIT
---
---
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap '' EXIT - printing state

trap 'echo exit' EXIT
trap
echo

trap '' EXIT
trap
echo

trap '# comment' EXIT
trap
## STDOUT:
trap -- 'echo exit' EXIT

trap -- '' EXIT

trap -- '# comment' EXIT
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### trap EXIT sees $? of the last command, with sh -c
$SH -c 'trap "echo status=\$?" EXIT; ( exit 5 )'
echo outer=$?
## STDOUT:
status=5
outer=5
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Remove trap with an unsigned integer

$SH -e -c '
trap "echo noprint" EXIT
trap 0 EXIT
echo ok0
'
echo

$SH -e -c '
trap "echo noprint" EXIT
trap " 42 " EXIT
echo ok42space
'
echo

# corner case: sometimes 07 is treated as octal, but not here
$SH -e -c '
trap "echo noprint" EXIT
trap 07 EXIT
echo ok07
'
echo

$SH -e -c '
trap "echo trap-exit" EXIT
trap -1 EXIT
echo bad
'
if test $? -ne 0; then
  echo failure
fi
## STDOUT:
ok0

ok42space

ok07

trap-exit
failure
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### trap with command.NoOp - check internal invariant

$SH -c 'trap "> zz" EXIT'
wc -l zz  # should exist
## stdout: 0 zz
## status: 0
## XFAIL ps-bash gap-oils-builtin-trap builtin-trap (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
