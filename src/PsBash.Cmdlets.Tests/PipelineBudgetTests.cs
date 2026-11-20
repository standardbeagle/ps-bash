using System.Diagnostics;
using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// timeout's budget (<see cref="PipelineBudget"/>) stops the nested pipeline even when it ends before the pipeline
/// starts. ps-bash-specific: the window is between the timer's start and Invoke reaching Running, which a loaded host
/// widens past a short budget. Runs alone: the bound is wall-clock.
/// </summary>
[Collection(WallClockBoundCollection.Name)]
public class PipelineBudgetTests
{
    /// <summary>REGRESSION (Windows fleet s23e): the budget's stop landed before Invoke started and did nothing, so
    /// <c>timeout 0.3 sleep 10</c> slept 10 s and exited 0. The held command lives 2x the bound.</summary>
    [Fact]
    public void Budget_EndsBeforeThePipelineStarts_StillStopsIt()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("Start-Sleep -Seconds 120");
        var budget = new PipelineBudget(pwsh, TimeSpan.FromMilliseconds(1));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => budget.Expired, TimeSpan.FromSeconds(30)), "the budget never ended");
            var clock = Stopwatch.StartNew();
            // A pipeline stopped as it starts may return normally rather than throw (s23g/s23h): timeout decides 124
            // from Expired, so only "it was stopped" is asserted here.
            var ex = Record.Exception(() => pwsh.Invoke());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"the pipeline ran {clock.Elapsed} after its budget ended");
            Assert.True(ex is null or PipelineStoppedException or InvalidPowerShellStateException, $"unexpected {ex}");
        }
        finally { budget.Dispose(); }
    }

    /// <summary>A budget that outlasts the command leaves it alone: the command finishes and the budget never ends.</summary>
    [Fact]
    public void Budget_OutlastsTheCommand_LeavesItsResult()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("'done'");
        var budget = new PipelineBudget(pwsh, TimeSpan.FromMinutes(10));
        try
        {
            var output = pwsh.Invoke();
            Assert.Equal("done", Assert.Single(output).ToString());
            Assert.False(budget.Expired);
        }
        finally { budget.Dispose(); }
    }
}
