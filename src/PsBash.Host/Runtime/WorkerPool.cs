using PsBash.Core.Runtime;

namespace PsBash.Host.Runtime;

/// <summary>
/// A warm pool of isolated <see cref="SdkWorker"/> runspaces, backing the daemon
/// host. Each framed connection (`-c`, stdin, script) acquires its own worker for
/// the duration of one command and releases it afterward; on release the worker's
/// runspace is <b>discarded</b> (never recycled) so the next command always runs
/// in a structurally clean PowerShell session — the isolation the per-invocation
/// host process used to provide, now without a per-command process/runspace
/// cold-start on the critical path.
///
/// <para><b>Why a pool, not one shared runspace.</b> The legacy single-worker host
/// serialized every command on one runspace and leaked user state (variables,
/// functions, cwd) between invocations — only the separate per-invocation host
/// <i>process</i> gave true isolation. A pool restores isolation (fresh runspace
/// per command) and adds concurrency (N launchers run in parallel on N runspaces)
/// while keeping <c>_warmTarget</c> spares hot so steady-state latency is near
/// zero.</para>
///
/// <para><b>Sizing.</b> <c>warmTarget</c> idle runspaces are kept hot; total live
/// runspaces are bounded by <c>warmTarget + max</c> (idle ≤ warmTarget, in-use ≤
/// max). The expensive psm1 import happens off the critical path in the warmer.</para>
///
/// <para><b>Threading.</b> In-use concurrency is bounded by the <c>_slots</c>
/// semaphore; all idle bookkeeping (<c>_idle</c>, <c>_idleCount</c>, <c>_warming</c>)
/// is guarded by <c>_warmGate</c>. A worker holds a slot only while in use; idle
/// spares do not. <see cref="SdkWorker"/> owns its own runspace + persistent
/// PowerShell instance, so distinct pooled workers never share runspace state
/// (which would trigger the empty-output gotcha documented on SdkWorker).</para>
/// </summary>
public sealed class WorkerPool<TWorker> : IAsyncDisposable where TWorker : class, IAsyncDisposable
{
    internal static void DiagLog(string msg)
    {
        if (Environment.GetEnvironmentVariable("PSBASH_POOL_DIAG") != "1") return;
        try
        {
            var p = Path.Combine(Path.GetTempPath(), "psbash-pool-diag.log");
            File.AppendAllText(p, $"{DateTime.UtcNow:HH:mm:ss.fff} t{Environment.CurrentManagedThreadId} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private readonly int _warmTarget;
    private readonly int _max;
    private readonly Func<TWorker> _factory;

    private readonly object _warmGate = new();
    private readonly Queue<TWorker> _idle = new();
    private int _idleCount;
    private int _warming;

    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _firstWarm =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Exception? _firstWarmError;
    private int _disposed;

    /// <param name="warmTarget">Idle workers kept hot for low-latency checkout.</param>
    /// <param name="max">Maximum concurrently in-use workers (concurrency cap).</param>
    /// <param name="factory">Worker factory (one warm worker per call).</param>
    public WorkerPool(int warmTarget, int max, Func<TWorker> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _warmTarget = Math.Max(0, warmTarget);
        _max = Math.Max(1, max);
        _factory = factory;
        _slots = new SemaphoreSlim(_max, _max);
        TopUpWarm();
    }

    /// <summary>Completes once at least one worker has finished warming.</summary>
    public Task WhenFirstWarm => _firstWarm.Task;

    /// <summary>True once at least one runspace is built (health-ready).</summary>
    public bool IsReady => _firstWarm.Task.IsCompletedSuccessfully;

    /// <summary>True while at least one runspace is mid-warm-up.</summary>
    public bool IsWarming { get { lock (_warmGate) return _warming > 0; } }

    /// <summary>
    /// The first warm-up failure, if any, observed before any runspace came up.
    /// Lets the host report a "worker failed" health state when runspace creation
    /// is persistently broken (e.g. a corrupt install) rather than hanging on
    /// "starting" forever. Cleared once a runspace successfully warms.
    /// </summary>
    public Exception? FirstWarmError => IsReady ? null : _firstWarmError;

    /// <summary>
    /// Check out an isolated worker. Returns a warm spare if available; otherwise
    /// creates one (bounded by <c>max</c> in-use). Blocks on the concurrency cap
    /// when <c>max</c> workers are already in use, released as one is returned.
    /// The caller MUST <see cref="Release"/> the worker exactly once.
    /// </summary>
    public async Task<TWorker> AcquireAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DiagLog("Acquire: waiting slot");
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryTakeIdle(out var warm))
            {
                DiagLog("Acquire: took warm idle worker");
                TopUpWarm();
                return warm;
            }

            // Pool empty: create one on the critical path for this caller, and
            // top the warm spares back up for the next one.
            DiagLog("Acquire: pool empty, creating fresh");
            var fresh = await CreateAsync(ct).ConfigureAwait(false);
            DiagLog("Acquire: fresh created");
            _firstWarm.TrySetResult();
            TopUpWarm();
            return fresh;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    /// <summary>
    /// Return a checked-out worker. The runspace is discarded (disposed in the
    /// background) so it is never reused — this is what guarantees the next
    /// command gets a clean session. Frees a concurrency slot and refills warmth.
    /// </summary>
    public void Release(TWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _ = DisposeWorkerAsync(worker);
        // If the pool was disposed while this worker was checked out, _slots is
        // already disposed and Release() would throw ObjectDisposedException on
        // the caller's cleanup path. The slot count no longer matters once the
        // pool is gone, so skip it (and the warm top-up) rather than throw.
        if (_disposed != 0) return;
        try { _slots.Release(); }
        catch (ObjectDisposedException) { return; }
        TopUpWarm();
    }

    private bool TryTakeIdle(out TWorker worker)
    {
        lock (_warmGate)
        {
            if (_idle.Count > 0)
            {
                worker = _idle.Dequeue();
                _idleCount--;
                return true;
            }
        }
        worker = null!;
        return false;
    }

    private void TopUpWarm()
    {
        if (_shutdown.IsCancellationRequested) return;
        lock (_warmGate)
        {
            while (_idleCount + _warming < _warmTarget && !_shutdown.IsCancellationRequested)
            {
                _warming++;
                _ = WarmOneAsync();
            }
        }
    }

    private async Task WarmOneAsync()
    {
        DiagLog("WarmOne: creating");
        try
        {
            var w = await CreateAsync(_shutdown.Token).ConfigureAwait(false);
            DiagLog("WarmOne: created, enqueuing");
            bool disposeNow;
            lock (_warmGate)
            {
                _warming--;
                if (_disposed != 0)
                {
                    disposeNow = true;
                }
                else
                {
                    _idle.Enqueue(w);
                    _idleCount++;
                    disposeNow = false;
                }
            }
            if (disposeNow) { try { await w.DisposeAsync().ConfigureAwait(false); } catch { } }
            else _firstWarm.TrySetResult();
        }
        catch (Exception ex)
        {
            _firstWarmError = ex;
            lock (_warmGate) _warming--;
        }
    }

    // Create a worker on a DEDICATED thread, never a thread-pool thread.
    // Runspace creation (psm1 import) is heavy and CPU-bound. Running it via
    // Task.Run would occupy thread-pool threads; with warmTarget spares warming
    // (and refilling) in the background, that competes with the async accept,
    // connection, and IPC writer work the command path needs. The old
    // single-worker host dodged this because its one worker finished warming
    // before any command (health-gated). A LongRunning task gets its own thread,
    // so warming never competes with command execution or the async server loop.
    private Task<TWorker> CreateAsync(CancellationToken ct)
        => Task.Factory.StartNew(
            _factory,
            ct,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private static async Task DisposeWorkerAsync(TWorker worker)
    {
        try { await worker.DisposeAsync().ConfigureAwait(false); }
        catch { /* teardown best-effort; process exit reclaims anything left */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();

        List<TWorker> drain;
        lock (_warmGate)
        {
            drain = new List<TWorker>(_idle);
            _idle.Clear();
            _idleCount = 0;
        }
        foreach (var w in drain)
        {
            try { await w.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _shutdown.Dispose();
        _slots.Dispose();
    }
}

/// <summary>
/// Non-generic factory for the concrete <see cref="SdkWorker"/> pool that backs
/// the host, plus environment-based sizing.
/// </summary>
public static class WorkerPool
{
    /// <summary>
    /// Build the host's <see cref="SdkWorker"/> pool, sized from the environment
    /// with daemon-appropriate defaults. <c>PSBASH_POOL_WARM</c> sets warm spares
    /// (default 2); <c>PSBASH_POOL_MAX</c> the concurrency cap (default: CPU count
    /// clamped to [2, 8]). Invalid values fall back to the default.
    /// </summary>
    public static WorkerPool<SdkWorker> FromEnvironment(Func<SdkWorker>? factory = null)
    {
        int warm = ReadIntEnv("PSBASH_POOL_WARM", 2, min: 0, max: 64);
        int defaultMax = Math.Clamp(Environment.ProcessorCount, 2, 8);
        int max = ReadIntEnv("PSBASH_POOL_MAX", defaultMax, min: 1, max: 64);
        return new WorkerPool<SdkWorker>(warm, max, factory ?? SdkWorker.Create);
    }

    private static int ReadIntEnv(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(raw, out var v) && v >= min && v <= max) return v;
        return fallback;
    }
}
