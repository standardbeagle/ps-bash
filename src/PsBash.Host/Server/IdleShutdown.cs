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
    /// <summary>The window from <c>PSBASH_HOST_IDLE_SECS</c> alone (no <c>--idle-secs</c>): see <see cref="TimeoutFor"/>.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeoutFor([]);

    /// <summary>
    /// This host's window: its <c>--idle-secs=N</c> argument (a nested daemon's launcher passes 60), else
    /// <c>PSBASH_HOST_IDLE_SECS</c>, else 600 s (<see cref="PsBash.Core.Runtime.Ipc.HostIdleWindow"/>).
    /// </summary>
    public static TimeSpan TimeoutFor(string[] args)
    {
        string? argValue = null;
        var prefix = PsBash.Core.Runtime.Ipc.HostIdleWindow.Arg + "=";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == PsBash.Core.Runtime.Ipc.HostIdleWindow.Arg && i + 1 < args.Length) argValue = args[i + 1];
            else if (args[i].StartsWith(prefix, StringComparison.Ordinal)) argValue = args[i][prefix.Length..];
        }
        return PsBash.Core.Runtime.Ipc.HostIdleWindow.Resolve(
            argValue, Environment.GetEnvironmentVariable(PsBash.Core.Runtime.Ipc.HostIdleWindow.EnvVar));
    }

    private readonly CancellationTokenSource _cts;
    private TimeSpan _timeout;
    private string? _shortenedReason;
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

    private readonly Action<string> _recordExitReason;

    /// <param name="recordExitReason">Where the "idle timeout" exit reason goes (default
    /// <see cref="HostLog.SetExitReason"/>); a seam so tests need not share the process-wide one.</param>
    public IdleShutdown(CancellationTokenSource cts, TimeSpan? timeout = null, Action<string>? recordExitReason = null)
    {
        _cts = cts;
        _timeout = timeout ?? DefaultTimeout;
        _recordExitReason = recordExitReason ?? HostLog.SetExitReason;
        // Host starts idle; schedule first fire.
        lock (_gate) ScheduleTimer();
    }

    /// <summary>
    /// Lower the idle timeout to <paramref name="grace"/> (never raises it) and restart the
    /// countdown if no connection is in flight. In-flight connections still finish: the host
    /// exits <paramref name="grace"/> after the last one ends. Used when the session that keys
    /// this daemon is gone (<see cref="SessionAnchorWatcher"/>), so nothing new can arrive.
    /// </summary>
    public void ShortenTimeout(TimeSpan grace, string reason)
    {
        lock (_gate)
        {
            if (grace >= _timeout) return;
            _timeout = grace;
            _shortenedReason = reason;
            if (_inFlight == 0 && Volatile.Read(ref _disposed) == 0) ScheduleTimer();
        }
    }

    /// <summary>Connections in flight now: a command, or a request waiting for one.</summary>
    public int InFlight
    {
        get { lock (_gate) return _inFlight; }
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
            _recordExitReason(_shortenedReason is { } why
                ? $"{why}; idle {_timeout.TotalSeconds:0}s with no connection"
                : $"idle timeout ({_timeout.TotalSeconds:0}s with no connection)");
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
