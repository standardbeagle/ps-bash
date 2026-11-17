## Absorbed from Differential.Tests/BackgroundJobDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### Differential_Background_TrueAndWait_ExitsZero
true & wait; echo done
## stdout: done
## status: 0

#### Differential_Wait_NoJobs_ExitsZero
wait; echo ok
## stdout: ok
## status: 0

#### Differential_Background_InCommandSub_DoesNotHang
x=$(sleep 0 & echo sub_done); echo $x
## stdout: sub_done
## status: 0

#### Differential_Jobs_NoProcesses_EmitsNothing
jobs; echo empty
## stdout: empty
## status: 0

#### Differential_Kill_ListSignals_Golden
kill -l | head -1
## STDOUT:
 1) SIGHUP	 2) SIGINT	 3) SIGQUIT	 4) SIGILL	 5) SIGTRAP
## END
## status: 0

#### Differential_Background_QuotedArg_Golden
sleep 0 & wait; echo quoting_ok
## stdout: quoting_ok
## status: 0

#### Differential_Background_LastPidIsNonEmpty
sleep 0 & pid=$!; if [ -n "$pid" ]; then echo got_pid; else echo no_pid; fi; wait
## stdout: got_pid
## status: 0

#### Differential_Background_WaitAll_ReturnsAfterCompletion
sleep 0 & sleep 0 & wait; echo all_done
## stdout: all_done
## status: 0

#### Differential_Background_WaitLastPid_Continues
sleep 0 & wait $!; echo after_wait
## stdout: after_wait
## status: 0

#### Differential_Background_ThreeJobsWaitAll_ExitsZero
sleep 0 & sleep 0 & sleep 0 & wait; echo three_done
## stdout: three_done
## status: 0
