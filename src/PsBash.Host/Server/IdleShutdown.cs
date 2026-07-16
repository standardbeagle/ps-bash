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
    // Bumped every time the scheduled timer is superseded or cancelled (ScheduleTimer /
    // CancelTimer). The timer callback captures the generation it was scheduled under and
    // re-checks both this and _inFlight under _gate before cancelling — closes the race where
    // Timer.Dispose() cannot abort a callback that has already been dequeued by the threadpool
    // (a callback queued just before ConnectionStarted() must not tear down a live connection).
    private int _generation;

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
        var generation = ++_generation;
        _timer = new Timer(_ => OnTimerFired(generation), null, _timeout, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
        // Invalidate any callback already queued/running for the timer just disposed —
        // Timer.Dispose() does not abort an in-flight callback.
        _generation++;
    }

    private void OnTimerFired(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return; // superseded — a newer schedule/cancel won the race
            if (_inFlight > 0) return;              // a connection started concurrently — do not tear down
            _cts.TryCancel();
        }
    }

    /// <summary>
    /// Test-only seam: invokes the timer callback logic directly with an explicit generation,
    /// so a race between a fired-but-not-yet-run callback and a concurrent ConnectionStarted()
    /// can be reproduced deterministically instead of racing a real <see cref="Timer"/>.
    /// </summary>
    internal void SimulateTimerFired(int generation) => OnTimerFired(generation);

    /// <summary>Test-only seam: the generation the currently scheduled/last-cancelled timer holds.</summary>
    internal int CurrentGenerationForTest
    {
        get { lock (_gate) return _generation; }
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
