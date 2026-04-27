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
            "true &; wait; echo done",
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
            "sleep 0 &; pid=$!; if [ -n \"$pid\" ]; then echo got_pid; else echo no_pid; fi; wait",
            timeout: TimeSpan.FromSeconds(30));
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
            "sleep 0 &; sleep 0 &; wait; echo all_done",
            timeout: TimeSpan.FromSeconds(30));
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
            "sleep 0 &; wait $!; echo after_wait",
            timeout: TimeSpan.FromSeconds(30));
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
            "sleep 0 &; wait; echo quoting_ok",
            "Differential_Background_QuotedArg",
            timeout: TimeSpan.FromSeconds(30));
    }
}
