using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A background job's sleeps count from its launch (<see cref="JobClock"/>, docs/specs/background-jobs.md section
/// 12.5). Oracle note (qa-rubric Directive 1): the clock is ps-bash plumbing for a start lag bash does not have; the
/// bash-visible result is pinned by the oils cases "wait for N parallel jobs and check failure" and "wait -n"
/// (background.spec.sh).
/// </summary>
public class JobClockTests
{
    private static long Ms(long ms) => ms * Stopwatch.Frequency / 1000;

    [Fact]
    public void Sleep_LongerThanTheStartLag_SleepsOnlyTheRest()
    {
        var clock = new JobClock(0);
        clock.Begin(Ms(15));
        Assert.Equal(15, clock.Settle(30, Ms(15)));
        Assert.Equal(0, clock.DebtMs);
    }

    [Fact]
    public void StartLag_LongerThanTheSleep_CarriesToTheNextSleep()
    {
        var clock = new JobClock(0);
        clock.Begin(Ms(25));
        Assert.Equal(0, clock.Settle(10, Ms(25)));
        Assert.Equal(15, clock.DebtMs);
        Assert.Equal(15, clock.Settle(30, Ms(26)));
        Assert.Equal(0, clock.DebtMs);
        Assert.Equal(40, clock.Settle(40, Ms(60)));
    }

    [Fact]
    public void TimeTheBodySpends_IsNotDebt()
    {
        // The body began 5 ms late and then worked 100 ms before sleeping: only the 5 ms is paid back.
        var clock = new JobClock(0);
        clock.Begin(Ms(5));
        Assert.Equal(45, clock.Settle(50, Ms(105)));
    }

    [Fact]
    public void JobThatStartedOnTime_SleepsInFull()
    {
        var clock = new JobClock(Ms(100));
        clock.Begin(Ms(100));
        Assert.Equal(20, clock.Settle(20, Ms(100)));
    }

    [Fact]
    public void Begin_OnlyTheFirstCallCounts()
    {
        var clock = new JobClock(0);
        clock.Begin(Ms(10));
        clock.Begin(Ms(500));
        Assert.Equal(10, clock.DebtMs);
    }

    [Fact]
    public void BodyThatNeverBegan_MeasuresTheLagAtItsFirstSleep()
    {
        var clock = new JobClock(0);
        Assert.Equal(-1, clock.DebtMs);
        Assert.Equal(12, clock.Settle(20, Ms(8)));
    }

    [Fact]
    public void ClockBehindTheLaunch_OwesNothing()
    {
        var clock = new JobClock(Ms(50));
        clock.Begin(Ms(40));
        Assert.Equal(0, clock.DebtMs);
        Assert.Equal(20, clock.Settle(20, Ms(40)));
    }

    [Fact]
    public void HugeSleep_IsNotSettled()
    {
        var clock = new JobClock(0);
        clock.Begin(Ms(30));
        var huge = long.MaxValue / 2;
        Assert.Equal(huge, clock.Settle(huge, Ms(30)));
    }

    [Theory]
    [InlineData(30, 10, 20, 0)]
    [InlineData(10, 30, 0, 20)]
    [InlineData(10, 10, 0, 0)]
    [InlineData(10, 0, 10, 0)]
    public void Pay_SleepsTheExcessAndKeepsTheRestOwed(long requested, long debt, long sleep, long left)
    {
        Assert.Equal((sleep, left), JobClock.Pay(requested, debt));
    }
}

/// <summary>
/// <c>Invoke-BashSleep</c> in a job whose body began late ends at launch + D; outside a job it sleeps D in full.
/// Wall-clock measurements, so the class runs alone (<see cref="WallClockBoundCollection"/>).
/// </summary>
[Collection(WallClockBoundCollection.Name)]
public class JobClockSleepTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public JobClockSleepTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Fact]
    public void JobThatStartedLate_SleepEndsAtLaunchPlusTheDuration()
    {
        var pwsh = _fixture.AcquireFresh();
        // The command compiled once, so the measured run is the sleep and an invocation of a few ms.
        CmdResult.Run(pwsh, "Invoke-BashSleep 0").AssertSuccess();
        // Launched 3 s ago, body beginning now: `sleep 4` sleeps 1 s and ends 4 s after the launch, not 7.
        // A wide gap between right (1 s) and the bug (4 s): loaded macOS runners overslept 100 ms by 131 and 1 s by 822.
        var launched = Stopwatch.GetTimestamp() - 3000 * Stopwatch.Frequency / 1000;
        var clock = new JobClock(launched);
        clock.Begin();
        pwsh.Runspace.SessionStateProxy.SetVariable(JobClock.VariableName, clock);
        try
        {
            var run = Stopwatch.StartNew();
            CmdResult.Run(pwsh, "Invoke-BashSleep 4").AssertSuccess();
            var sinceLaunchMs = (Stopwatch.GetTimestamp() - launched) * 1000 / Stopwatch.Frequency;
            Assert.Equal(0, clock.DebtMs);
            Assert.True(run.ElapsedMilliseconds is >= 1000 and < 3000, $"the sleep took {run.ElapsedMilliseconds} ms; owed 1000 of 4000");
            Assert.InRange(sinceLaunchMs, 4000, 6000);
        }
        finally
        {
            pwsh.Runspace.SessionStateProxy.PSVariable.Remove(JobClock.VariableName);
        }
    }

    [Fact]
    public void OutsideAJob_SleepsInFull()
    {
        var pwsh = _fixture.AcquireFresh();
        var started = Stopwatch.StartNew();
        CmdResult.Run(pwsh, "Invoke-BashSleep 0.1").AssertSuccess();
        Assert.True(started.ElapsedMilliseconds >= 100, $"slept {started.ElapsedMilliseconds} ms of 100");
    }
}
