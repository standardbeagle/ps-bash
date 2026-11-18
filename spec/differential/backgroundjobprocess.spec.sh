## Absorbed from Differential.Tests/BackgroundJobProcessTests.cs (scripts only; expectations recorded from bash 5.2).

#### RunAsync
export PSBASH_BG_MODE=process
for i in 1 2 3 4 5 6 7 8 9 10; do true & echo $! >> pids; done; wait; echo "rc=$?"
grep -cE '^[0-9]+$' pids; sort -u pids | wc -l
## STDOUT:
rc=0
10
10
## END
## status: 0
## timeout: 120

#### RunAsync row 2
export PSBASH_BG_MODE=process
x=before; { sleep 1; echo "job:$x"; } & x=after; wait; echo "parent:$x"
## STDOUT:
job:before
parent:after
## END
## status: 0
## timeout: 120

#### RunAsync row 3
export PSBASH_BG_MODE=process
for i in 1 2 3; do sleep 120 >/dev/null 2>&1 & echo $! >> pids; done
grep -cE '^[0-9]+$' pids; sort -u pids | wc -l; kill $(cat pids); wait; echo done
## STDOUT:
3
3
done
## END
## status: 0
## timeout: 120

#### RunAsync row 4
export PSBASH_BG_MODE=process
{ echo out1; echo err1 >&2; echo out2; } & wait; echo next
## STDOUT:
out1
out2
next
## END
## status: 0
## timeout: 120

#### RunAsync row 5
export PSBASH_BG_MODE=process
false & wait $!; echo "rc=$?"; (exit 3) & wait $!; echo "rc=$?"
## STDOUT:
rc=1
rc=3
## END
## status: 0
## timeout: 120

#### RunAsync row 6
export PSBASH_BG_MODE=process
sleep 60 & p=$!; kill -0 $p && echo alive; kill $p; wait $p; echo "rc=$?"; kill -0 $p 2>/dev/null && echo still-alive || echo gone
## STDOUT:
alive
rc=143
gone
## END
## status: 0
## timeout: 120

#### RunAsync row 8
export PSBASH_BG_MODE=process
f() { echo "f:$1"; }; arr=(a b c); f hi & wait; { echo "arr=${arr[1]} n=${#arr[@]}"; } & wait
## STDOUT:
f:hi
arr=b n=3
## END
## status: 0
## timeout: 120

#### RunAsync row 9
export PSBASH_BG_MODE=process
x=parent; { x=child; echo in-job; } & wait $!; echo "x=$x"
## STDOUT:
in-job
x=parent
## END
## status: 0
## timeout: 120
