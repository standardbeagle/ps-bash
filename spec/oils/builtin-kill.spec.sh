## Imported from the Oils spec tests (builtin-kill.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### kill -KILL kills the process with SIGKILL
sleep 0.1 & 
pid=$!
kill -KILL $pid 
echo kill=$?

wait $pid
echo wait=$?  # 137 is 128 + SIGKILL
## STDOUT:
kill=0
wait=137
## END
## status: 0
## timeout: 10

#### kill -s TERM specifies the signal name
sleep 0.1 &
pid=$!
kill -s TERM $pid
echo kill=$?

wait $pid
echo wait=$?
## STDOUT:
kill=0
wait=143
## END
## status: 0
## timeout: 10

#### kill -l with exit code
kill -l 134 # 128 + 6 (ABRT)
## stdout: ABRT
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### kill -l with 128 is invalid
kill -l 128
if [ $? -ne 0 ]; then
    echo "invalid"
fi
## stdout: invalid
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### kill -l 0 returns EXIT
kill -l 0
## stdout: EXIT
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### kill -l 0 INT lists both signals
kill -l 0 INT
## STDOUT:
EXIT
2
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### kill multiple pids at once
sleep 0.1 &
pid1=$!
sleep 0.1 &
pid2=$!
sleep 0.1 &
pid3=$!

kill $pid1 $pid2 $pid3
echo $?
## stdout: 0
## status: 0
## timeout: 10

#### kill pid and job at once
sleep 0.1 &
pid=$!
sleep 0.1 &
kill %2 $pid
echo $?
## stdout: 0
## status: 0
## timeout: 10

#### Numeric signal out of range - OSH may send it anyway

sleep 0.1 &

# OSH doesn't validate this, but that could be useful for non-portable signals,
# which we don't have a name for.

kill -s 9999 %%
echo kill=$?

wait
echo wait=$?
## STDOUT:
kill=1
wait=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### kill -15 kills the process with SIGTERM
case $SH in mksh) exit ;; esac  # mksh is flaky

sleep 0.1 &
pid=$!
kill -15 $pid
echo kill=$?

wait $pid
echo wait=$?  # 143 is 128 + SIGTERM
## STDOUT:
kill=0
wait=143
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill -n 9 specifies the signal number
#case $SH in mksh|dash) exit ;; esac

sleep 0.1 &
pid=$!
kill -n 9 $pid
echo kill=$?

wait $pid
echo wait=$?
## STDOUT:
kill=0
wait=137
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill -terM -SigterM isn't case sensitive
case $SH in mksh|dash|zsh) exit ;; esac

sleep 0.1 &
pid=$!
kill -SigterM $pid
echo kill=$?
wait $pid
echo wait=$?

sleep 0.1 &
pid=$!
kill -terM $pid
echo kill=$?
wait $pid
echo wait=$?
## STDOUT:
kill=0
wait=143
kill=0
wait=143
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill HUP pid gives the correct error
case $SH in dash) exit ;; esac
sleep 0.1 &
builtin kill HUP $pid
echo $?
## stdout: 1
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill -l shows signals
case $SH in dash) exit ;; esac

# Check if at least the HUP flag is reported.  The output format of all shells
# is different and the available signals may depend on your environment

builtin kill -l | grep HUP > /dev/null
echo $?
## stdout: 0
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill -L also shows signals
case $SH in mksh|dash|zsh) exit ;; esac

builtin kill -L | grep HUP > /dev/null
echo $?
## stdout: 0
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill -l 10 TERM translates between names and numbers
case $SH in mksh|dash) exit ;; esac

builtin kill -l 10 11 12
echo status=$?
echo

builtin kill -l SIGUSR1 SIGSEGV USR2
echo status=$?
echo

# mixed kind
builtin kill -l 10 SIGSEGV 12
echo status=$?
echo
## STDOUT:
USR1
SEGV
USR2
status=0

10
11
12
status=0

USR1
11
USR2
status=0

## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill -L checks for invalid input
case $SH in mksh|dash) exit ;; esac

builtin kill -L 10 BAD 12
echo status=$?
echo

builtin kill -L USR1 9999 USR2
echo status=$?
echo
## STDOUT:
USR1
USR2
status=1

10
12
status=1

## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill -9999 is an invalid signal
case $SH in dash)  exit ;; esac
sleep 0.1 &
pid=$!
kill -9999 $pid > /dev/null
echo kill=$?

wait $pid
echo wait=$?
## STDOUT:
kill=1
wait=0
## END
## status: 0
## timeout: 10
## real-launcher

#### kill -15 %% kills current job
#case $SH in mksh|dash) exit ;; esac

sleep 0.5 &
pid=$!
kill -15 %%
echo kill=$?

wait %%
echo wait=$?

# no such job
wait %%
echo wait=$?
## STDOUT:
kill=0
wait=143
wait=127
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### kill -15 %- kills previous job
#case $SH in mksh|dash) exit ;; esac

sleep 0.1 &  # previous job
sleep 0.2 &  # current job

kill -15 %-
echo kill=$?

wait %-
echo wait=$?

# what does bash define here as the previous job?  May be a bug
#wait %-
#echo wait=$?
## STDOUT:
kill=0
wait=143
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-kill builtin-kill (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
