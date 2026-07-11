using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Regression tests for the "host hangs forever as zombie if ListenAsync fails
/// to bind" defect. <see cref="HostServer.RunAsync"/> only completes the
/// <c>WhenListening</c> readiness task AFTER <c>ListenAsync</c> succeeds, so a
/// bind fault (EADDRINUSE / stale-socket TOCTOU / endpoint-replace race) makes
/// RunAsync throw without ever signalling readiness. A bare
/// <c>await WhenListening</c> in the host entrypoint would then deadlock forever.
/// <see cref="HostServer.WaitUntilListeningAsync"/> must surface the fault
/// instead of hanging.
/// </summary>
public sealed class HostServerListenFaultTests
{
    /// <summary>Transport whose ListenAsync faults, modelling a failed bind.</summary>
    private sealed class FaultingListenTransport : IIpcTransport
    {
        private readonly Exception _fault;
        public FaultingListenTransport(Exception fault) => _fault = fault;

        public string Endpoint => "faulting-endpoint";
        public string Scheme => "pipe";

        public Task ListenAsync(CancellationToken ct = default) => Task.FromException(_fault);
        public Task<Stream> AcceptAsync(CancellationToken ct = default) => Task.FromException<Stream>(_fault);
        public Task<Stream> ConnectAsync(CancellationToken ct = default) => Task.FromException<Stream>(_fault);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Transport whose ListenAsync blocks until cancelled, then never binds.</summary>
    private sealed class BlockingListenTransport : IIpcTransport
    {
        public string Endpoint => "blocking-endpoint";
        public string Scheme => "pipe";

        public async Task ListenAsync(CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource();
            using (ct.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        public Task<Stream> AcceptAsync(CancellationToken ct = default) =>
            Task.FromException<Stream>(new InvalidOperationException("never listening"));
        public Task<Stream> ConnectAsync(CancellationToken ct = default) =>
            Task.FromException<Stream>(new InvalidOperationException("never listening"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // warmTarget 0 + a throwing factory: the pool must never build a runspace in
    // these tests (we fault/cancel before any connection is accepted).
    private static WorkerPool<SdkWorker> IdlePool() =>
        new(warmTarget: 0, max: 1, () => throw new InvalidOperationException("factory must not run"));

    [Fact]
    public async Task WaitUntilListening_WhenListenAsyncFaults_RethrowsInsteadOfHanging()
    {
        var fault = new InvalidOperationException("bind failed: address in use");
        await using var pool = IdlePool();
        await using var transport = new FaultingListenTransport(fault);
        await using var server = new HostServer(transport, pool);

        var runTask = server.RunAsync(CancellationToken.None);

        // Must NOT hang: the fault propagates out within the timeout. Before the
        // fix, this awaited WhenListening (never completed) and hung forever.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.WaitUntilListeningAsync(runTask).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(fault.Message, thrown.Message);
    }

    [Fact]
    public async Task WaitUntilListening_WhenCancelledBeforeBind_ReturnsFalseNotHang()
    {
        using var cts = new CancellationTokenSource();
        await using var pool = IdlePool();
        await using var transport = new BlockingListenTransport();
        await using var server = new HostServer(transport, pool);

        var runTask = server.RunAsync(cts.Token);
        cts.Cancel();

        // Cancelled pre-bind: RunAsync unwinds via OperationCanceledException, which
        // WaitUntilListeningAsync observes as "not listening" (false) — the caller
        // exits cleanly rather than deadlocking on readiness.
        var wait = server.WaitUntilListeningAsync(runTask).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var listened = await wait;
            Assert.False(listened);
        }
        catch (OperationCanceledException)
        {
            // Also acceptable: the pre-bind cancellation surfaced as OCE rather than
            // a clean false. The point is it did not hang — reaching here proves that.
        }
    }

    [Fact]
    public async Task WaitUntilListening_NullRunTask_Throws()
    {
        await using var pool = IdlePool();
        await using var transport = new FaultingListenTransport(new Exception("unused"));
        await using var server = new HostServer(transport, pool);

        await Assert.ThrowsAsync<ArgumentNullException>(() => server.WaitUntilListeningAsync(null!));
    }
}
