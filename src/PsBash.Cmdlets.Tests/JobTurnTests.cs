using System.Diagnostics;
using System.Management.Automation.Runspaces;
using Xunit;
using JobTurn = PsBash.Cmdlets.BashJobRunspaces.JobTurn;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Jobs launched from one runspace begin their bodies, and make their first writes, in launch order (<see cref="JobTurn"/>,
/// docs/specs/background-jobs.md section 12.5). Oracle note (qa-rubric Directive 1): the ordering primitive is
/// ps-bash plumbing; the bash-visible order is pinned by the oils case "a &amp; b &amp;" (shell-grammar.spec.sh).
/// Wall-clock bounds, and one test occupies the whole thread pool, so the class runs alone (<see cref="WallClockBoundCollection"/>).
/// </summary>
[Collection(WallClockBoundCollection.Name)]
public class JobTurnTests
{
    private static readonly Func<bool> NotStopping = static () => false;

    private static readonly TimeSpan NoGrace = TimeSpan.FromMinutes(5);

    [Fact]
    public void LaterJob_BeginsOnceTheEarlierOneHasBegun()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        var first = JobTurn.Next(launcher, NoGrace)!;
        var second = JobTurn.Next(launcher, NoGrace)!;
        using var entered = new ManualResetEventSlim();
        var t = new Thread(() => { second.Enter(NotStopping); entered.Set(); }) { IsBackground = true };
        t.Start();

        Assert.False(entered.Wait(150), "the second job began before the first");
        first.Enter(NotStopping);
        Assert.True(entered.Wait(1000), "the second job did not begin once the first had");
    }

    // REGRESSION (Windows fleet s22e): holding the next body until the earlier job's first write made a run of silent
    // jobs start one grace apart (the 3rd began 215 ms after its `&`). Bodies begin as soon as the earlier ones have.
    [Fact]
    public void SilentJobs_BeginWithoutWaitingForEachOthersWrites()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        var a = JobTurn.Next(launcher, NoGrace)!;
        var b = JobTurn.Next(launcher, NoGrace)!;
        var c = JobTurn.Next(launcher, NoGrace)!;
        var sw = Stopwatch.StartNew();
        a.Enter(NotStopping);
        b.Enter(NotStopping);
        c.Enter(NotStopping);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"three silent jobs took {sw.ElapsedMilliseconds} ms to begin");
    }

    // REGRESSION (Windows fleet s22a, `echo word_a & echo word_b &` printed word_b first 1 of 3 runs): both bodies had
    // begun in order, then their first writes raced. A job's first write now waits for the earlier job's.
    [Fact]
    public void FirstWrite_WaitsUntilTheEarlierJobHasWritten()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        var first = JobTurn.Next(launcher, NoGrace)!;
        var second = JobTurn.Next(launcher, NoGrace)!;
        first.Enter(NotStopping);
        second.Enter(NotStopping);
        using var wrote = new ManualResetEventSlim();
        var t = new Thread(() => { second.WaitToWrite(NotStopping); wrote.Set(); }) { IsBackground = true };
        t.Start();

        Assert.False(wrote.Wait(150), "the second job wrote before the first had");
        first.Pass();
        Assert.True(wrote.Wait(1000), "the second job could not write once the first had");
    }

    // An earlier job that begins but neither writes, sleeps nor ends (a CPU loop, output to a file) holds the next
    // one's first write only for its grace.
    [Fact]
    public void SilentEarlierJob_HoldsTheNextWriteOnlyForItsGrace()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        var first = JobTurn.Next(launcher, TimeSpan.FromMilliseconds(50))!;
        var second = JobTurn.Next(launcher, NoGrace)!;
        first.Enter(NotStopping);
        second.Enter(NotStopping);
        var sw = Stopwatch.StartNew();
        second.WaitToWrite(NotStopping);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"the second job's write waited {sw.ElapsedMilliseconds} ms");
    }

    // REGRESSION (Windows fleet s22k, "the second job's write waited 2014 ms"): the grace was a Timer, whose callback runs
    // on the thread pool. With every pool thread busy (a loaded host, many jobs, parallel test classes) it did not run,
    // and a 50 ms grace held the next write for the whole 2 s bounded wait. The waiter now enforces the deadline itself.
    [Fact]
    public void SilentEarlierJobsGrace_HoldsWithEveryPoolThreadBusy()
    {
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        var release = new ManualResetEventSlim();   // not disposed: queued items may still be waking on it
        Assert.True(ThreadPool.SetMaxThreads(minWorkers, maxIo), "could not cap the thread pool");
        try
        {
            for (int i = 0; i < minWorkers * 2; i++) ThreadPool.UnsafeQueueUserWorkItem(static e => ((ManualResetEventSlim)e!).Wait(), release);
            var saturating = Stopwatch.StartNew();
            while (ThreadPool.PendingWorkItemCount == 0 && saturating.Elapsed < TimeSpan.FromSeconds(10)) Thread.Yield();
            Assert.True(ThreadPool.PendingWorkItemCount > 0, "the pool never filled up, so this test proves nothing");

            using var launcher = RunspaceFactory.CreateRunspace();
            var first = JobTurn.Next(launcher, TimeSpan.FromMilliseconds(50))!;
            var second = JobTurn.Next(launcher, NoGrace)!;
            first.Enter(NotStopping);
            second.Enter(NotStopping);
            var sw = Stopwatch.StartNew();
            second.WaitToWrite(NotStopping);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"with the pool busy the second job's write waited {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        }
    }

    // A job's sleep passes its turn: in bash the next job would already be writing.
    [Fact]
    public void EarlierJobThatSleeps_LetsTheNextWrite()
    {
        using var launcher = RunspaceFactory.CreateRunspace();
        var first = JobTurn.Next(launcher, NoGrace)!;
        var second = JobTurn.Next(launcher, NoGrace)!;
        var clock = JobClock.Launch();
        clock.Turn = first;
        first.Enter(NotStopping);
        second.Enter(NotStopping);
        clock.Blocking();
        var sw = Stopwatch.StartNew();
        second.WaitToWrite(NotStopping);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"waited {sw.ElapsedMilliseconds} ms");
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
