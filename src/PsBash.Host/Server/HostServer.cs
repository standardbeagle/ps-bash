using System.IO.Pipes;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;

namespace PsBash.Host.Server;

/// <summary>
/// Accept loop that owns an <see cref="IIpcTransport"/> and dispatches each
/// incoming connection to a <see cref="Connection"/>. Exceptions at the
/// connection boundary are swallowed so the host stays alive after bad requests.
///
/// Accepts a <see cref="WorkerPool"/> so the transport can start listening and
/// write its lock file before the first runspace finishes warming. Each connection
/// checks out its own isolated worker from the pool (see <see cref="Connection"/>);
/// a connection that arrives before any runspace is warm waits inside
/// <see cref="WorkerPool.AcquireAsync"/> until one is ready.
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly IIpcTransport _transport;
    private readonly WorkerPool<SdkWorker> _pool;
    private readonly IdleShutdown? _idle;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _acceptStop = new();
    private readonly object _gate = new();
    private int _inFlight;
    private TaskCompletionSource? _drained;
    private int _disposed;

    public HostServer(IIpcTransport transport, WorkerPool<SdkWorker> pool, IdleShutdown? idle = null)
    {
        _transport = transport;
        _pool = pool;
        _idle = idle;
    }

    /// <summary>Completes once <see cref="RunAsync"/> has called ListenAsync and is ready to accept.</summary>
    public Task WhenListening => _ready.Task;

    /// <summary>
    /// Await readiness without deadlocking if <see cref="RunAsync"/> faults before
    /// it begins listening. <see cref="WhenListening"/> (the <c>_ready</c> TCS) is
    /// only completed AFTER <c>ListenAsync</c> succeeds, so a bind fault
    /// (EADDRINUSE / stale-socket TOCTOU / replace race) makes <see cref="RunAsync"/>
    /// throw <em>without ever completing <c>_ready</c></em>. A bare
    /// <c>await WhenListening</c> would then hang forever as a zombie — the idle
    /// timer and parent-death watcher only cancel the CTS, which does not complete
    /// <c>_ready</c>. This observes both tasks: if <paramref name="runTask"/>
    /// finishes first it is awaited so a fault propagates (or a clean pre-bind
    /// cancellation is observed) instead of deadlocking.
    /// </summary>
    /// <param name="runTask">The task returned by <see cref="RunAsync"/>.</param>
    /// <returns>
    /// <c>true</c> when the server reached the listening state; <c>false</c> when
    /// <see cref="RunAsync"/> returned before it began listening. In practice a
    /// pre-bind exit surfaces as a fault (bind error) or an
    /// <see cref="OperationCanceledException"/> (pre-bind cancellation) — both are
    /// rethrown by the awaited <paramref name="runTask"/>; <c>false</c> is the
    /// defensive path for a hypothetical clean early return with nothing to serve.
    /// </returns>
    /// <exception cref="Exception">Rethrows whatever <see cref="RunAsync"/> faulted with.</exception>
    public async Task<bool> WaitUntilListeningAsync(Task runTask)
    {
        ArgumentNullException.ThrowIfNull(runTask);

        var first = await Task.WhenAny(WhenListening, runTask).ConfigureAwait(false);
        if (first == runTask)
        {
            // RunAsync ended before it began listening. Await it so a fault throws
            // (surfacing EADDRINUSE etc. to the caller) or a clean early return is
            // observed. Either way the server is not listening.
            await runTask.ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>True once <see cref="RequestShutdownAsync"/> has been called.</summary>
    public bool ShutdownRequested => _acceptStop.IsCancellationRequested;

    public async Task RunAsync(CancellationToken ct = default)
    {
        await _transport.ListenAsync(ct);
        _ready.TrySetResult();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _acceptStop.Token);

        while (!linked.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                stream = await _transport.AcceptAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"accept error: {ex.Message}");
                // Small delay prevents a tight busy-loop if AcceptAsync keeps failing
                // (e.g., transport in a persistently broken state).
                try { await Task.Delay(10, linked.Token); } catch (OperationCanceledException) { break; }
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(stream, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        ConnectionStarted();
        _idle?.ConnectionStarted();
        try
        {
            await using (stream)
            {
                var conn = new Connection(stream, _pool, this);
                await conn.HandleAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Log($"connection error: {ex.Message}");
        }
        finally
        {
            _idle?.ConnectionEnded();
            ConnectionEnded();
        }
    }

    private void ConnectionStarted()
    {
        lock (_gate) _inFlight++;
    }

    private void ConnectionEnded()
    {
        lock (_gate)
        {
            _inFlight--;
            if (_inFlight <= 0 && _drained is { } d)
                d.TrySetResult();
        }
    }

    /// <summary>
    /// Stop accepting new connections, then wait for in-flight requests to
    /// finish or for <paramref name="deadlineMs"/> milliseconds to elapse,
    /// whichever comes first. Always returns a completed task; the caller
    /// inspects <see cref="ShutdownRequested"/> or awaits <see cref="RunAsync"/>
    /// to confirm the accept loop exited.
    /// </summary>
    /// <remarks>
    /// Idempotent — second and later calls are no-ops with respect to the
    /// accept-stop signal but still observe the drain wait. The transport
    /// remains owned by the host process and is disposed by
    /// <see cref="DisposeAsync"/>; on Windows named pipes there is no
    /// kernel-namespace endpoint to unlink, but the disposal closes the
    /// listening pipe handle so a replacement host can bind the same name.
    /// </remarks>
    public async Task RequestShutdownAsync(int deadlineMs)
    {
        try { _acceptStop.Cancel(); } catch (ObjectDisposedException) { }

        Task drainTask;
        lock (_gate)
        {
            if (_inFlight <= 0) return;
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drainTask = _drained.Task;
        }

        if (deadlineMs <= 0) return;

        var completed = await Task.WhenAny(drainTask, Task.Delay(deadlineMs)).ConfigureAwait(false);
        // Ignore deadline result: caller bounds the wait, it's not a failure
        // if drain didn't finish in time. The accept loop has already stopped;
        // the host process exits on its own CTS.
        _ = completed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _acceptStop.Cancel(); } catch { }
            try { _acceptStop.Dispose(); } catch { }
            await _transport.DisposeAsync();
        }
    }

    private static void Log(string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".psbash");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "host.log"),
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
