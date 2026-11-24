using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// The daemon opens in-process job runspaces (docs/specs/background-jobs.md section 12.5) only for a command that can
/// start a job in this process. An <c>-Isolated</c> job runs in a host process of its own, so opening four runspaces for
/// it only slowed the commands beside it. The commands are the emitter's output shape
/// (<c>Invoke-BashBackground [-Isolated] [-Live] {...}</c>).
/// <para>Oracle note (qa-rubric Directive 1): where a job runs is ps-bash plumbing with no bash-visible output.</para>
/// </summary>
public class ConnectionJobRunspaceWarmTests
{
    [Theory]
    [InlineData("Invoke-BashBackground { Invoke-BashEcho x }", null, true)]
    [InlineData("Invoke-BashBackground -Live { Invoke-BashEcho x }", null, true)]
    [InlineData("Invoke-BashBackground -Isolated { $global:bar = 2 }", null, false)]
    [InlineData("Invoke-BashBackground -Isolated { $global:bar = 2 }; Invoke-BashBackground { Invoke-BashEcho x }", null, true)]
    [InlineData("Invoke-BashBackground -Isolated { $global:bar = 2 }", "inproc", true)]
    [InlineData("Invoke-BashBackground { Invoke-BashEcho x }", "process", false)]
    [InlineData("Invoke-BashEcho x", null, false)]
    [InlineData("Invoke-BashEcho x", "inproc", false)]
    public void UsesInProcessJobs_OnlyForAJobThatRunsInThisProcess(string command, string? bgMode, bool expected)
    {
        Assert.Equal(expected, Connection.UsesInProcessJobs(command, bgMode));
    }

    // REGRESSION (private-host churn): every command with a `&` pre-started a private host for its job, but a job runs
    // in this process unless it is -Isolated (or PSBASH_BG_MODE=process). The spare was never used and was torn down
    // 60 s later: one ~3 CPU-second cold start wasted per agent command that backgrounds something. Only jobs that run
    // in a host process of their own are warmed for.
    [Theory]
    [InlineData("Invoke-BashBackground { Invoke-BashEcho x }", null, 0)]
    [InlineData("Invoke-BashBackground -Live { Invoke-BashEcho x }", null, 0)]
    [InlineData("Invoke-BashBackground -Isolated { $global:bar = 2 }", null, 1)]
    [InlineData("Invoke-BashBackground -Isolated -Live { $global:bar = 2 }; Invoke-BashBackground { Invoke-BashEcho x }", null, 1)]
    [InlineData("Invoke-BashBackground -Isolated { $global:bar = 2 }", "inproc", 0)]
    [InlineData("Invoke-BashBackground { Invoke-BashEcho x }; Invoke-BashBackground { Invoke-BashEcho y }", "process", 2)]
    [InlineData("Invoke-BashEcho x", "process", 0)]
    public void ProcessJobCount_OnlyJobsThatRunInAHostProcessOfTheirOwn(string command, string? bgMode, int expected)
        => Assert.Equal(expected, Connection.ProcessJobCount(command, bgMode));
}
