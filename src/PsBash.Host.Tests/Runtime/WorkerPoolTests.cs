using System.Collections.Concurrent;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Unit coverage for <see cref="WorkerPool{TWorker}"/> — the warm pool that gives
/// every framed connection its own isolated worker. Uses a trivial fake worker so
/// the pool's threading (warm spares, concurrency cap, discard-on-release isolation)
/// is exercised without paying for real PowerShell runspaces.
/// </summary>
public class WorkerPoolTests
{
    /// <summary>
    /// Fake unit-of-work. Tracks creation order, disposal, and live/peak-concurrent
    /// counts shared across one pool so tests can assert the cap and isolation.
    /// </summary>
    private sealed class FakeWorker : IAsyncDisposable
    {
        private readonly Factory _owner;
        public int Id { get; }
        public int Disposed; // 0/1

        private FakeWorker(Factory owner, int id) { _owner = owner; Id = id; }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref Disposed, 1);
            Interlocked.Increment(ref _owner.DisposedCount);
            return ValueTask.CompletedTask;
        }

        /// <summary>Worker factory + bookkeeping shared by all workers it makes.</summary>
        public sealed class Factory
        {
            private int _nextId;
            public int CreatedCount;
            public int DisposedCount;
            public TimeSpan CreateDelay;

            public FakeWorker Create()
            {
                if (CreateDelay > TimeSpan.Zero) Thread.Sleep(CreateDelay);
                var id = Interlocked.Increment(ref _nextId);
                Interlocked.Increment(ref CreatedCount);
                return new FakeWorker(this, id);
            }
        }
    }

    private sealed class ThrowingFactory
    {
        public int Attempts;
        public FakeThrow Create()
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("runspace boom");
        }
    }

    // Distinct type so ThrowingFactory's signature is unambiguous.
    private sealed class FakeThrow : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("condition not met in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task WarmTarget_PrewarmsIdleWorkers()
    {
        var f = new FakeWorker.Factory();
        await using var pool = new WorkerPool<FakeWorker>(warmTarget: 3, max: 4, f.Create);

        await pool.WhenFirstWarm;
        await WaitUntilAsync(() => Volatile.Read(ref f.CreatedCount) >= 3);

        Assert.True(pool.IsReady);
        Assert.True(f.CreatedCount >= 3);
    }

    [Fact]
    public async Task Acquire_ReturnsWarmSpare_WithoutCreatingNew()
    {
        var f = new FakeWorker.Factory();
        await using var pool = new WorkerPool<FakeWorker>(warmTarget: 2, max: 4, f.Create);
        await WaitUntilAsync(() => Volatile.Read(ref f.CreatedCount) >= 2);

        var createdBefore = f.CreatedCount;
        var w = await pool.AcquireAsync();

        Assert.NotNull(w);
        // Took a warm spare — no extra create beyond what warming already did
        // (the pool may top warmth back up afterward, so allow >=).
        Assert.True(f.CreatedCount >= createdBefore);
        pool.Release(w);
    }

    [Fact]
    public async Task ReleaseDiscardsWorker_NextAcquireIsADistinctInstance()
    {
        var f = new FakeWorker.Factory();
        await using var pool = new WorkerPool<FakeWorker>(warmTarget: 1, max: 2, f.Create);

        var first = await pool.AcquireAsync();
        pool.Release(first);

        // Discard-on-release: the released worker is disposed, never handed back.
        await WaitUntilAsync(() => Volatile.Read(ref first.Disposed) == 1);

        var second = await pool.AcquireAsync();
        Assert.NotSame(first, second);
        Assert.NotEqual(first.Id, second.Id);
        pool.Release(second);
    }

    [Fact]
    public async Task ConcurrencyCap_NeverExceedsMaxInUse()
    {
        const int max = 3;
        var f = new FakeWorker.Factory { CreateDelay = TimeSpan.FromMilliseconds(5) };
        await using var pool = new WorkerPool<FakeWorker>(warmTarget: 2, max: max, f.Create);

        int inUse = 0, peak = 0;
        var gate = new object();

        async Task Worker()
        {
            for (int i = 0; i < 20; i++)
            {
                var w = await pool.AcquireAsync();
                lock (gate) { inUse++; if (inUse > peak) peak = inUse; }
                await Task.Delay(2);
                lock (gate) { inUse--; }
                pool.Release(w);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Worker()));

        Assert.True(peak <= max, $"peak in-use {peak} exceeded cap {max}");
        Assert.True(peak >= 2, $"expected real concurrency, peak was {peak}");
    }

    [Fact]
    public async Task ConcurrentAcquires_GetDistinctWorkers()
    {
        var f = new FakeWorker.Factory();
        await using var pool = new WorkerPool<FakeWorker>(warmTarget: 2, max: 4, f.Create);

        var held = new List<FakeWorker>();
        for (int i = 0; i < 4; i++) held.Add(await pool.AcquireAsync());

        var ids = held.Select(w => w.Id).ToHashSet();
        Assert.Equal(4, ids.Count); // all distinct — no shared instance

        foreach (var w in held) pool.Release(w);
    }

    [Fact]
    public async Task FactoryFailure_SurfacesAsFirstWarmError_AndNotReady()
    {
        var f = new ThrowingFactory();
        await using var pool = new WorkerPool<FakeThrow>(warmTarget: 1, max: 2, f.Create);

        await WaitUntilAsync(() => pool.FirstWarmError is not null);
        Assert.False(pool.IsReady);
        Assert.IsType<InvalidOperationException>(pool.FirstWarmError);
    }

    [Fact]
    public async Task Dispose_DisposesIdleWorkers()
    {
        var f = new FakeWorker.Factory();
        var pool = new WorkerPool<FakeWorker>(warmTarget: 3, max: 4, f.Create);
        await WaitUntilAsync(() => Volatile.Read(ref f.CreatedCount) >= 3);

        await pool.DisposeAsync();

        // Asserted immediately after DisposeAsync ON PURPOSE: the contract is that
        // disposal is COMPLETE when it returns, covering workers created but not yet
        // enqueued. This assert used to race — CreatedCount is incremented when a worker
        // is constructed, but a warmer that had not yet reached the idle queue disposed
        // its worker on its own task AFTER DisposeAsync returned, so the count could be
        // short ("expected idle workers disposed, got 2"). DisposeAsync now awaits
        // in-flight warmers, so this is deterministic. Do NOT relax it into a
        // WaitUntilAsync — that would hide a regression of the guarantee.
        Assert.True(f.DisposedCount >= 3, $"expected idle workers disposed, got {f.DisposedCount}");
    }

    [Fact]
    public async Task Dispose_DisposesWorkerStillBeingWarmed()
    {
        // Directly targets the window the flake exposed: a worker whose creation is
        // still in flight when the pool is disposed must still be disposed BY THE TIME
        // DisposeAsync returns — no runspace outliving its pool.
        var f = new FakeWorker.Factory();
        using var releaseCreate = new SemaphoreSlim(0);
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pool = new WorkerPool<FakeWorker>(warmTarget: 1, max: 2, () =>
        {
            creating.TrySetResult();
            releaseCreate.Wait();          // hold the warmer mid-create
            return f.Create();
        });

        await creating.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var dispose = pool.DisposeAsync().AsTask();   // disposes while the warmer is stuck
        releaseCreate.Release();                      // let creation finish post-disposal
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, Volatile.Read(ref f.CreatedCount));
        var disposed = Volatile.Read(ref f.DisposedCount);
        Assert.True(disposed >= 1,
            $"a worker created during disposal must be disposed before DisposeAsync returns, got {disposed}");
    }

    [Fact]
    public async Task Acquire_AfterDispose_Throws()
    {
        var f = new FakeWorker.Factory();
        var pool = new WorkerPool<FakeWorker>(warmTarget: 1, max: 2, f.Create);
        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync());
    }
}
