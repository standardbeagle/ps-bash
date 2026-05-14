namespace PsBash.Host.Server;

/// <summary>
/// Cancels the host's <see cref="CancellationTokenSource"/> after a configurable
/// idle period (no in-flight connections). Thread-safe: ConnectionStarted and
/// ConnectionEnded may be called from concurrent tasks.
/// </summary>
/// <remarks>
/// <para>PTY-10: this idle timer governs the <b>framed-IPC</b> host only —
/// the host that <c>IpcWorker</c> talks to over a Unix socket / named pipe,
/// where <see cref="ConnectionStarted"/> / <see cref="ConnectionEnded"/> bracket
/// each request. An <b>interactive</b> host (spawned <c>--interactive</c> under
/// a PTY by <c>Program.RunHostUnderPtyAsync</c>) does not honor this timeout the
/// same way: it has no framed IPC connections to count, it is bound to exactly
/// one launcher's PTY, and it exits when that launcher disconnects — the PTY
/// master closes (stdin EOF) and <c>ParentDeathWatcher</c> (armed via
/// <c>--launcher-pid</c>) terminates it if the launcher dies abruptly. An
/// interactive host is therefore never a shared, idle-reaped daemon; its
/// lifetime is its single session.</para>
/// </remarks>
public sealed class IdleShutdown : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("PSBASH_HOST_IDLE_SECS"), out var s) && s > 0 ? s : 600);

    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private int _inFlight;
    private Timer? _timer;
    private int _disposed;

    public IdleShutdown(CancellationTokenSource cts, TimeSpan? timeout = null)
    {
        _cts = cts;
        _timeout = timeout ?? DefaultTimeout;
        // Host starts idle; schedule first fire.
        lock (_gate) ScheduleTimer();
    }

    public void ConnectionStarted()
    {
        lock (_gate)
        {
            _inFlight++;
            CancelTimer();
        }
    }

    public void ConnectionEnded()
    {
        lock (_gate)
        {
            _inFlight--;
            if (_inFlight == 0) ScheduleTimer();
        }
    }

    private void ScheduleTimer()
    {
        _timer?.Dispose();
        _timer = new Timer(_ => _cts.TryCancel(), null, _timeout, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_gate) CancelTimer();
        }
    }
}

internal static class CtsExtensions
{
    // Suppress ObjectDisposedException that may race with disposal.
    internal static void TryCancel(this CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}
