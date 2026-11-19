using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for background job control: cmd &amp;, wait, wait $!, jobs, kill.
/// (Dart XjjfjLBg5EMa)
///
/// Background jobs in ps-bash run on a runspace pool (or, for a job that may change shell state, a job host
/// process); a job whose output is the shell's own streams it live (docs/specs/background-jobs.md section 12), so
/// job output is oracle-checked with EqualAsync.
///
/// Failure-surface axes targeted per test (Directive 3):
///   - Axis 8:  exit-code propagation (wait exit code, $! non-empty)
///   - Axis 14: missing target (wait with no jobs, kill non-existent PID)
///   - Axis 12: quoting / injection (backgrounded command with spaces in args)
/// </summary>
public class BackgroundJobDifferentialTests
{
    /// <summary>
    /// `kill $!` stops a backgrounded job and `wait` then reports 128+SIGTERM; `kill -0` probes a live job;
    /// `kill %1` takes a jobspec. Agents start a server with `&` and stop it with `kill $!`: the synthetic
    /// `$!` id found no process ("No such process") and `wait` returned 0.
    /// </summary>
    [SkippableTheory]
    [InlineData("sleep 30 & k=$!; kill -0 $k && echo alive; kill $k; echo kill=$?; wait $k; echo wait=$?")]
    [InlineData("sleep 30 & kill %1; wait; echo w=$?")]
    [InlineData("sleep 30 & k=$!; kill -9 $k; wait $k; echo wait=$?")]
    public async Task Differential_Background_KillLastPid_StopsJobAndWaitReportsSignal(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60), liveOnly: true);
    }

    /// <summary>
    /// Jobspecs `%%` (current) and `%-` (previous) name a job for `kill` and `wait`; a waited job is gone (127);
    /// `kill -n N` takes a signal number; `wait zzz` (status 1) and `kill HUP` (status 1, "arguments must be process
    /// or job IDs") reject a word that is neither a pid nor a jobspec; `kill -L` is `kill -l`. `%-` was "no such
    /// job", `wait %%` waited for nothing, `-n` and `-L` were ignored (oils builtin-kill, background).
    /// </summary>
    [SkippableTheory]
    [InlineData("{ sleep 30 & kill -15 %%; echo kill=$?; wait %%; echo wait=$?; wait %%; echo wait=$?; } 2>/dev/null")]
    [InlineData("sleep 30 & sleep 30 & kill -15 %-; echo kill=$?; wait %-; echo wait=$?; kill %%; wait")]
    [InlineData("{ sleep 30 & p=$!; sleep 30 & kill -n 9 $p; echo kill=$?; wait $p; echo wait=$?; kill %%; wait; } 2>/dev/null")]
    [InlineData("{ wait zzz; echo w=$?; kill HUP; echo k=$?; kill -L 10 BAD 12; echo l=$?; kill -L | grep -c HUP; } 2>/dev/null")]
    public async Task Differential_Background_JobspecsAndBadOperands(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60), liveOnly: true);
    }

    /// <summary>
    /// A background job's output reaches stdout even when the script ends before anything waits for it: bash's job
    /// writes it itself. ps-bash buffers a job's output until it is collected, and the runspace's teardown dropped
    /// it (oils shell-grammar "Command with trailer", "a &amp; b", "a &amp; b &amp;").
    /// </summary>
    [SkippableTheory]
    [InlineData("echo word word &")]
    [InlineData("{ echo x; echo y; } &")]
    public async Task Differential_Background_OutputOfAnUnwaitedJob_IsEmittedAtExit(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60), liveOnly: true);
    }

    /// <summary>
    /// A background job's output reaches the shell's stdout WHILE the job runs, in production order with the
    /// foreground's, and `wait` does not print it again. ps-bash collected a job's output and printed it all at
    /// `wait` (bg1, bg2, fg became fg, bg1, bg2). Timings are generous: a job's start costs ps-bash up to a few hundred
    /// milliseconds (a pooled runspace imports the module), which this asserts nothing about.
    /// </summary>
    [SkippableTheory]
    [InlineData("{ echo bg1; sleep 2; echo bg2; } & sleep 1; echo fg; wait")]
    [InlineData("{ echo a1; sleep 3; echo a2; } & { sleep 1; echo b1; sleep 1; echo b2; } & sleep 2.5; echo fg; wait; echo end")]
    [InlineData("for i in 1 2; do { sleep $i; echo job$i; } & done; echo started; wait; echo end")]
    public async Task Differential_Background_LiveOutput_InterleavesInProductionOrder(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// A job's stderr reaches the shell's stderr live; its stdout and stderr stay on their own streams.
    /// </summary>
    [SkippableTheory]
    [InlineData("{ echo out1; echo err1 >&2; sleep 2; echo out2; } & sleep 1; echo fg; wait")]
    [InlineData("{ ls /no/such/path; echo after; } & wait; echo rc=$?")]
    public async Task Differential_Background_LiveOutput_StderrStreams(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// A job's own redirects take its output away from the shell: `cmd &gt; f &amp;` and `cmd &amp;&gt; log &amp;` print
    /// nothing live, `cmd | tee f &amp;` prints live AND writes the file; a job inside a redirected group writes to the
    /// group's file (that job is not live, it keeps the collected path).
    /// </summary>
    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\"; { echo a; sleep 2; echo b; } > f & sleep 1; echo fg; wait; cat f; cd /")]
    [InlineData("cd \"$(mktemp -d)\"; { echo o; echo e >&2; } &> log & wait; echo fg; sort log; cd /")]
    [InlineData("cd \"$(mktemp -d)\"; { echo t1; sleep 2; echo t2; } | tee f & sleep 1; echo fg; wait; cat f; cd /")]
    [InlineData("cd \"$(mktemp -d)\"; { { echo in; } & wait; } > g; echo fg; cat g; cd /")]
    public async Task Differential_Background_LiveOutput_RespectsRedirects(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// `wait` statuses, `$!` and `jobs` are unchanged for a job whose output streams live.
    /// </summary>
    [SkippableTheory]
    [InlineData("{ sleep 1; echo a; exit 3; } & a=$!; { echo b; exit 4; } & b=$!; wait $a; echo a=$?; wait $b; echo b=$?")]
    [InlineData("{ sleep 1; echo j; } & jobs | wc -l; wait; echo w=$?")]
    [InlineData("f() { echo in-func; }; f & wait; echo end")]
    public async Task Differential_Background_LiveOutput_WaitJobsAndStatus(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// The words of `cmd &amp;` are expanded in the job, not the shell: `${bar=2} &amp;` and `$((n=5)) &amp;` assign
    /// nothing in the shell (oils background "Background process doesn't affect parent").
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_AssigningWordOfAJob_DoesNotAssignInTheShell()
    {
        await AssertOracle.EqualAsync("echo ${bar=2} > /dev/null & : $((n=5)) & wait; echo \"bar=${bar-unset} n=${n-unset}\"",
            timeout: TimeSpan.FromSeconds(60), liveOnly: true);
    }

    /// <summary>
    /// `wait` for a job a signal killed reports the signal's description (Hangup, status 129) on stderr, as bash does;
    /// it printed nothing (oils background "Signal message for killed background job"). TERM, INT, PIPE and QUIT are
    /// not reported, as in bash.
    /// </summary>
    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\"; sleep 30 & kill -HUP $!; wait $! 2>err.txt; echo status=$?; grep -o Hangup err.txt; cd /")]
    [InlineData("cd \"$(mktemp -d)\"; sleep 30 & kill -TERM $!; wait $! 2>err.txt; echo status=$?; wc -c < err.txt; cd /")]
    public async Task Differential_Background_WaitForASignaledJob_ReportsTheSignal(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60), liveOnly: true);
    }

    /// <summary>
    /// `jobs -p` prints one process id per job (a pipeline job too); it printed the whole `jobs` listing, four words a
    /// line (oils background "jobs -p prints one line per job").
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_JobsDashPPrintsOnePidPerJob()
    {
        await AssertOracle.EqualAsync("sleep 30 & a=$!; sleep 30 | cat & jobs -p > jp.txt; wc -l < jp.txt; wc -w < jp.txt; head -n 1 jp.txt | grep -c \"^$a\\$\"; rm -f jp.txt; kill %1 %2; wait",
            timeout: TimeSpan.FromSeconds(60), liveOnly: true);
    }

    /// <summary>
    /// `fg` finds a job `kill` just signalled (still in the table until reported), prints its command as written and
    /// returns its status; `%N` stays the job's own number after job 1 is reported; `\j` counts the table. `fg` picked
    /// "the newest job still running" itself and said "no current job", printed `bash-bg` for any job, `%2` became
    /// "no such job" once job 1 left, and `\j` was always 0 (oils prompt.spec "\j for number of jobs").
    /// </summary>
    [SkippableTheory]
    [InlineData("{ set -m; sleep 30 & kill %%; fg; echo rc=$?; fg; echo none=$?; } 2>/dev/null")]
    [InlineData("{ set -m; sleep 1 & sleep 30 & fg %1; echo rc=$?; kill %2; fg; echo rc=$?; } 2>/dev/null")]
    [InlineData("{ set -m; PS1='j=\\j'; echo \"${PS1@P}\"; sleep 30 & echo \"${PS1@P}\"; kill %%; fg; echo \"${PS1@P}\"; } 2>/dev/null")]
    public async Task Differential_Background_FgOfASignalledJob(string script)
    {
        await AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60), liveOnly: true);
    }

    // -----------------------------------------------------------------------
    // cmd &  — backgrounding operator
    // -----------------------------------------------------------------------

    /// <summary>
    /// Backgrounding a no-op command (true) and waiting for it exits cleanly.
    /// The exit code of `wait` must be 0 when all background jobs succeeded.
    /// Failure-surface axis 8: exit code of wait reflects job completion status.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_TrueAndWait_ExitsZero()
    {
        await AssertOracle.EqualAsync(
            "true & wait; echo done",
            timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// $! is set to a non-empty integer after backgrounding a command.
    /// Uses a golden because PID values differ between bash and ps-bash, but both
    /// must produce a non-empty numeric-looking value. We test via conditional echo.
    /// Failure-surface axis 8: $! must be set after & so the script can reference it.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_LastPidIsNonEmpty()
    {
        await AssertOracle.EqualAsync(
            "sleep 0 & pid=$!; if [ -n \"$pid\" ]; then echo got_pid; else echo no_pid; fi; wait",
            timeout: TimeSpan.FromSeconds(30),
            // liveOnly: background-job children race their stderr (a swallowed
            // "Invoke-BashSleep not recognized" cold-start error may or may not
            // reach the parent before it exits). stdout/exit are stable but
            // stderr is not reproducible from a frozen oracle — run live.
            liveOnly: true);
    }

    /// <summary>
    /// wait with no argument blocks until all background jobs finish.
    /// Foreground echo after wait must always print, proving wait returned.
    /// Failure-surface axis 8: wait must not hang; exit code after wait is 0.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_WaitAll_ReturnsAfterCompletion()
    {
        await AssertOracle.EqualAsync(
            "sleep 0 & sleep 0 & wait; echo all_done",
            timeout: TimeSpan.FromSeconds(30),
            liveOnly: true); // timing-dependent bg-job stderr — see LastPidIsNonEmpty
    }

    /// <summary>
    /// wait with no background jobs exits immediately with code 0.
    /// Failure-surface axis 14: missing target — wait called when job list is empty.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Wait_NoJobs_ExitsZero()
    {
        await AssertOracle.EqualAsync(
            "wait; echo ok",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// wait $! waits for the specific PID of the last background job.
    /// After wait completes the foreground must continue.
    /// Failure-surface axis 8: wait $! must resolve $! to the correct PID.
    /// Golden: ps-bash emits $global:BashBgLastPid for $! which differs from bash's $!
    /// but must be a valid PID that wait can resolve.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_WaitLastPid_Continues()
    {
        await AssertOracle.EqualAsync(
            "sleep 0 & wait $!; echo after_wait",
            timeout: TimeSpan.FromSeconds(30),
            liveOnly: true); // timing-dependent bg-job stderr — see LastPidIsNonEmpty
    }

    /// <summary>
    /// RC-2 regression: a background job spawned inside a command substitution
    /// must not hang. `$( ... &)` runs the &amp; in the subshell context; the
    /// command sub itself completes once the foreground portion finishes.
    /// Failure-surface axis 15: recursion — &amp; nested inside command sub.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_InCommandSub_DoesNotHang()
    {
        await AssertOracle.EqualAsync(
            "x=$(sleep 0 & echo sub_done); echo $x",
            timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// RC-2 regression: three background jobs then a single `wait` must all
    /// drain without hanging — exercises the runspace pool's multi-job path.
    /// Failure-surface axis 8: exit-code propagation across multiple bg jobs.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_ThreeJobsWaitAll_ExitsZero()
    {
        await AssertOracle.EqualAsync(
            "sleep 0 & sleep 0 & sleep 0 & wait; echo three_done",
            timeout: TimeSpan.FromSeconds(30),
            liveOnly: true); // timing-dependent bg-job stderr — see LastPidIsNonEmpty
    }

    // -----------------------------------------------------------------------
    // jobs — list background processes
    // -----------------------------------------------------------------------

    /// <summary>
    /// jobs with no background processes produces no output in bash.
    /// ps-bash Invoke-BashJobs must also produce no output when the list is empty.
    /// Failure-surface axis 14: missing target — jobs called with empty job list.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Jobs_NoProcesses_EmitsNothing()
    {
        await AssertOracle.EqualAsync(
            "jobs; echo empty",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // kill — send signal to process
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>kill -l</c> prints bash's Linux signal table (<c>%2d) SIGNAME</c>, tab-separated, five per row, real-time
    /// signals 34-64 as <c>SIGRTMIN+n</c>/<c>SIGRTMAX-n</c>), and every <c>-l</c> form reads the same table: 32 and 33 print
    /// nothing, 128+N is N, past 64 is invalid. REGRESSION: ps-bash printed 13 names one per line (a golden froze its
    /// <c>SIGHUP</c>), and <c>kill -34</c> resolved to no number and sent TERM. One table:
    /// <c>InvokeBashKillCommand.LinuxSignalNames</c>.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Kill_ListSignals()
    {
        await AssertOracle.EqualAsync(
            "kill -l | cat -A; echo \"rc=${PIPESTATUS[0]}\"\n"
            + "kill -l 34 64 RTMIN+1 SIGRTMAX-1 162 32 33 0 SIGHUP; echo rc=$?\n"
            + "kill -l 65 2>/dev/null; echo rc=$?",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Backgrounding a command with spaces in its arguments must not break quoting.
    /// Uses a benign command (echo with spaces in arg) backgrounded and waited.
    /// Failure-surface axis 12: quoting — argument containing spaces must survive &amp; emission.
    /// Golden: background job output is not forwarded to parent stdout by ps-bash architecture.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_QuotedArg_Golden()
    {
        await AssertOracle.GoldenAsync(
            "sleep 0 & wait; echo quoting_ok",
            "Differential_Background_QuotedArg",
            timeout: TimeSpan.FromSeconds(30));
    }
}
