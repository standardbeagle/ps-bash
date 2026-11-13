using PsBash.Core.Runtime;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// How many background-job spare hosts to start. Host-internal (no bash equivalent); hand-written asserts.
/// </summary>
public class JobHostSparesTests
{
    private static Task<IpcWorker> Starting() => new TaskCompletionSource<IpcWorker>().Task;
    private static Task<IpcWorker> Ready() => Task.FromResult<IpcWorker>(null!);
    private static Task<IpcWorker> Failed() => Task.FromException<IpcWorker>(new TimeoutException("spawn timed out"));
    private static Task<IpcWorker> Canceled() => Task.FromCanceled<IpcWorker>(new CancellationToken(canceled: true));

    [Fact]
    public void PruneAndCountMissing_FailedSpare_IsDroppedAndReplaced()
    {
        var spares = new List<Task<IpcWorker>> { Failed() };

        int missing = JobHostSpares.PruneAndCountMissing(spares, target: 1);

        Assert.Empty(spares);
        Assert.Equal(1, missing);
    }

    [Fact]
    public void PruneAndCountMissing_CanceledSpare_IsDropped()
    {
        var spares = new List<Task<IpcWorker>> { Canceled(), Ready() };

        Assert.Equal(1, JobHostSpares.PruneAndCountMissing(spares, target: 2));
        Assert.Single(spares);
    }

    [Fact]
    public void PruneAndCountMissing_OneStartingOfTwo_StartsTheOther()
    {
        // A starting spare is in the list once and counts once.
        var spares = new List<Task<IpcWorker>> { Starting() };

        Assert.Equal(1, JobHostSpares.PruneAndCountMissing(spares, target: 2));
    }

    [Fact]
    public void PruneAndCountMissing_TargetMet_StartsNone()
    {
        var spares = new List<Task<IpcWorker>> { Starting(), Ready() };

        Assert.Equal(0, JobHostSpares.PruneAndCountMissing(spares, target: 2));
        Assert.Equal(2, spares.Count);
    }

    [Fact]
    public void PruneAndCountMissing_MoreThanTarget_NeverNegative()
    {
        var spares = new List<Task<IpcWorker>> { Ready(), Ready() };

        Assert.Equal(0, JobHostSpares.PruneAndCountMissing(spares, target: 1));
    }
}
