namespace PsBash.Host.Server;

/// <summary>
/// Cancels the host's <see cref="CancellationTokenSource"/> after a configurable
/// idle period (no in-flight connections). Thread-safe: ConnectionStarted and
/// ConnectionEnded may be called from concurrent tasks.
/// </summary>
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
