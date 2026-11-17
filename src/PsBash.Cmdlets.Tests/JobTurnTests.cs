using System.Diagnostics;
using System.Management.Automation.Runspaces;
using Xunit;
using JobTurn = PsBash.Cmdlets.BashJobRunspaces.JobTurn;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Jobs launched from one runspace begin their bodies in launch order (<see cref="JobTurn"/>,
/// docs/specs/background-jobs.md section 12.5). Oracle note (qa-rubric Directive 1): the ordering primitive is
/// ps-bash plumbing; the bash-visible order is pinned by the oils case "a &amp; b &amp;" (shell-grammar.spec.sh).
/// </summary>
public class JobTurnTests
{
    private static readonly Func<bool> NotStopping = static () => false;

    [Fact]
    public void LaterJob_WaitsForTheEarlierOneToBegin()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        var first = JobTurn.Next(launcher)!;
        var second = JobTurn.Next(launcher)!;
        using var entered = new ManualResetEventSlim();
        var t = new Thread(() => { second.Enter(NotStopping); entered.Set(); }) { IsBackground = true };
        t.Start();

        Assert.False(entered.Wait(150), "the second job began before the first");
        first.Enter(NotStopping);
        Assert.True(entered.Wait(1000), "the second job did not begin once the first had");
    }

    [Fact]
    public void EarlierJobsThatEndedOutOfOrder_DoNotHoldUpTheNext()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        var a = JobTurn.Next(launcher)!;
        var b = JobTurn.Next(launcher)!;
        var c = JobTurn.Next(launcher)!;
        b.Pass();
        a.Pass();
        var sw = Stopwatch.StartNew();
        c.Enter(NotStopping);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"waited {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AnEarlierJobThatNeverBegins_HoldsTheNextOnlyForTheBoundedWait()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        _ = JobTurn.Next(launcher)!; // refused start: never enters, never ends
        var second = JobTurn.Next(launcher)!;
        var third = JobTurn.Next(launcher)!;

        var sw = Stopwatch.StartNew();
        second.Enter(NotStopping);
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(30));

        sw.Restart();
        third.Enter(NotStopping);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"the third job waited {sw.ElapsedMilliseconds} ms for the abandoned first");
    }

    [Fact]
    public void AStoppedJob_StopsWaiting()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        _ = JobTurn.Next(launcher)!;
        var second = JobTurn.Next(launcher)!;
        var sw = Stopwatch.StartNew();
        second.Enter(static () => true);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"waited {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void JobsOfDifferentRunspaces_DoNotWaitForEachOther()
    {
        using var one = RunspaceFactory.CreateRunspace();
        using var other = RunspaceFactory.CreateRunspace();
        _ = JobTurn.Next(one)!;
        var elsewhere = JobTurn.Next(other)!;
        var sw = Stopwatch.StartNew();
        elsewhere.Enter(NotStopping);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"waited {sw.ElapsedMilliseconds} ms");
    }
}
