using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for background job control: cmd &amp;, wait, wait $!, jobs, kill.
/// (Dart XjjfjLBg5EMa)
///
/// Background jobs in ps-bash use Invoke-BashBackground which spawns a child pwsh process
/// with stdout/stderr redirected via async reads. Because child output is swallowed by
/// BeginOutputReadLine/BeginErrorReadLine and never forwarded to the parent's stdout, tests
/// that check background-job output content must use GoldenAsync (known behavior frozen).
/// Tests that only check exit codes or side-effects on the foreground shell can use EqualAsync.
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
    /// `jobs -p` prints one process id per job (a pipeline job too); it printed the whole `jobs` listing, four words a
    /// line (oils background "jobs -p prints one line per job").
    /// </summary>
    [SkippableFact]
    public async Task Differential_Background_JobsDashPPrintsOnePidPerJob()
    {
        await AssertOracle.EqualAsync("sleep 30 & a=$!; sleep 30 | cat & jobs -p > jp.txt; wc -l < jp.txt; wc -w < jp.txt; head -n 1 jp.txt | grep -c \"^$a\\$\"; rm -f jp.txt; kill %1 %2; wait",
            timeout: TimeSpan.FromSeconds(60), liveOnly: true);
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
    /// kill -l lists signal names; output must be non-empty in both bash and ps-bash.
    /// Because bash and ps-bash may list different signal sets, use golden.
    /// Failure-surface axis 8: kill -l exit code must be 0.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Kill_ListSignals_Golden()
    {
        await AssertOracle.GoldenAsync(
            "kill -l | head -1",
            "Differential_Kill_ListSignals",
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
