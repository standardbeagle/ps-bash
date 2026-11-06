## Imported from the Oils spec tests (background.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### wait with nothing to wait for
wait
## stdout:
## status: 0
## timeout: 10

#### wait -n with nothing to wait for
# The 127 is STILL overloaded.  Copying bash for now.
wait -n
## stdout:
## status: 127
## timeout: 10

#### wait with jobspec syntax %nonexistent
wait %nonexistent
## stdout:
## status: 127
## timeout: 10

#### wait with invalid PID
wait 12345678
## stdout:
## status: 127
## timeout: 10

#### wait with invalid arg
wait zzz
# mksh confuses a syntax error with 'command not found'!
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### wait for N parallel jobs

for i in 3 2 1; do
  { sleep 0.0$i; exit $i; } &
done
wait

# status is lost
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### wait for N parallel jobs and check failure

set -o errexit

pids=''
for i in 3 2 1; do
  { sleep 0.0$i; echo $i; exit $i; } &
  pids="$pids $!"
done

for pid in $pids; do
  set +o errexit
  wait $pid
  status=$?
  set -o errexit

  echo status=$status
done
## STDOUT:
1
2
3
status=3
status=2
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Builtin in background
echo async &
wait
## stdout: async
## status: 0
## timeout: 10

#### External command in background
sleep 0.01 &
wait
## stdout:
## status: 0
## timeout: 10

#### Start background pipeline, wait $pid
echo hi | { exit 99; } &
echo status=$?
wait $!
echo status=$?
echo --

pids=''
for i in 3 2 1; do
  sleep 0.0$i | echo i=$i | ( exit $i ) &
  pids="$pids $!"
done
#echo "PIDS $pids"

for pid in $pids; do
  wait $pid
  echo status=$?
done

# Not cleaned up
if false; then
  echo 'DEBUG'
  jobs --debug
fi
## STDOUT:
status=0
status=99
--
status=3
status=2
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Wait for job and PIPESTATUS

# foreground
{ echo hi; exit 55; } | false
echo fore status=$? pipestatus=${PIPESTATUS[@]}

# background
{ echo hi; exit 44; } | false &
echo back status=$? pipestatus=${PIPESTATUS[@]}

# wait for pipeline
wait %+
#wait %1
#wait $!
echo wait status=$? pipestatus=${PIPESTATUS[@]}
## STDOUT:
fore status=1 pipestatus=55 1
back status=0 pipestatus=0
wait status=1 pipestatus=1
## END
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Wait for job and PIPESTATUS - cat

# foreground
exit 55 | ( cat; exit 99 )
echo fore status=$? pipestatus=${PIPESTATUS[@]}

# background
exit 44 | ( cat; exit 88 ) &
echo back status=$? pipestatus=${PIPESTATUS[@]}

# wait for pipeline
wait %+
#wait %1
#wait $!
echo wait status=$? pipestatus=${PIPESTATUS[@]}
echo

# wait for non-pipeline
( exit 77 ) &
wait %+
echo wait status=$? pipestatus=${PIPESTATUS[@]}
## STDOUT:
fore status=99 pipestatus=55 99
back status=0 pipestatus=0
wait status=88 pipestatus=88

wait status=77 pipestatus=77
## END
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Brace group in background, wait all
{ sleep 0.09; exit 9; } &
{ sleep 0.07; exit 7; } &
wait  # wait for all gives 0
echo "status=$?"
## stdout: status=0
## status: 0
## timeout: 10

#### Wait on background process PID
{ sleep 0.09; exit 9; } &
pid1=$!
{ sleep 0.07; exit 7; } &
pid2=$!
wait $pid2
echo "status=$?"
wait $pid1
echo "status=$?"
## STDOUT:
status=7
status=9
## END
## status: 0
## timeout: 10

#### Wait on multiple specific IDs returns last status
{ sleep 0.08; exit 8; } &
jid1=$!
{ sleep 0.09; exit 9; } &
jid2=$!
{ sleep 0.07; exit 7; } &
jid3=$!
wait $jid1 $jid2 $jid3  # NOTE: not using %1 %2 %3 syntax on purpose
echo "status=$?"  # third job I think
## stdout: status=7
## status: 0
## timeout: 10

#### Async for loop
for i in 1 2 3; do
  echo $i
  sleep 0.0$i
done &
wait
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### Background process doesn't affect parent
echo ${foo=1}
echo $foo
echo ${bar=2} &
wait
echo $bar  # bar is NOT SET in the parent process
## STDOUT:
1
1
2

## END
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Background process and then a singleton pipeline

# This was inspired by #416, although that symptom there was timing, so it's
# technically not a regression test.  It's hard to test timing.

{ sleep 0.1; exit 42; } &
echo begin
! true
echo end
wait $!
echo status=$?
## STDOUT:
begin
end
status=42
## END
## status: 0
## timeout: 10

#### jobs prints one line per job
sleep 0.1 & 
sleep 0.1 | cat & 

# dash doesn't print if it's not a terminal?
jobs | wc -l
## stdout: 2
## status: 0
## timeout: 10

#### jobs -p prints one line per job
sleep 0.1 &
sleep 0.1 | cat &

jobs -p > tmp.txt

cat tmp.txt | wc -l  # 2 lines, one for each job
cat tmp.txt | wc -w  # each line is a single "word"
## STDOUT:
2
2
## END
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### No stderr spew when shell is not interactive

# in interactive shell, this prints 'Process' or 'Pipeline'
sleep 0.01 &
sleep 0.01 | cat &
wait


 
## stdout:
## status: 0
## timeout: 10

#### wait -n with arguments - arguments are respected
case $SH in dash|mksh) exit ;; esac

echo x &

# here, you can't tell if it's -n or the other
wait -n $!
echo status=$?

# by the bash error, you can tell which is preferred
wait -n $! bad 2>err.txt
echo status=$?
echo

n=$(wc -l < err.txt)
if test "$n" -gt 0; then
  echo 'got error lines'
fi
## STDOUT:
x
status=0
status=127

got error lines
## END
## status: 0
## timeout: 10
## real-launcher

#### Start background pipeline, wait %job_spec
case $SH in mksh) exit ;; esac  # flakiness?

echo hi | { exit 99; } &
echo status=$?
wait %1
echo status=$?
## STDOUT:
status=0
status=99
## END
## status: 0
## timeout: 10
## real-launcher

#### wait -n
case $SH in dash|mksh) return ;; esac

{ sleep 0.09; exit 9; } &
{ sleep 0.03; exit 3; } &
wait -n
echo "status=$?"
wait -n
echo "status=$?"
## STDOUT:
status=3
status=9
## END
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### YSH wait --all
case $SH in dash|bash|mksh) exit ;; esac

sleep 0.01 &
(exit 55) &
true &
wait
echo wait $?

sleep 0.01 &
(exit 44) &
true &
wait --all
echo wait --all $?
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### YSH wait --verbose
case $SH in dash|bash|mksh) exit ;; esac

sleep 0.01 &
(exit 55) &
wait --verbose
echo wait $?

(exit 44) &
sleep 0.01 &
wait --all --verbose
echo wait --all $?
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Signal message for killed background job
case $SH in dash|mksh) exit ;; esac

sleep 1 &
kill -HUP $!
wait $! 2>err.txt
echo status=$?
grep -o "Hangup" err.txt
## STDOUT:
status=129
Hangup
## END
## status: 0
## XFAIL ps-bash gap-oils-background background (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
