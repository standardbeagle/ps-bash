using System.Diagnostics;

namespace PsBash.Host.Runtime;

/// <summary>
/// Stops the running command — not the host — when the host process outgrows its memory limit.
/// <para>
/// Everything a command buffers lives in the shared host (<c>yes | sort</c> grew ~1 GB a minute), so
/// one command could otherwise exhaust memory and take every session down with an OutOfMemory crash.
/// While a command runs, its usage is polled; past <see cref="LimitBytes"/> the command is cancelled
/// (the same Stop a disconnect uses) and reports the limit, exit 137 as for an OOM kill.
/// Limit: <c>PSBASH_HOST_MAX_MB</c> (<c>0</c> disables); default a quarter of the memory available to the
/// process (physical RAM or a container limit), at most 2 GB (a warm host plateaus near 840 MB; machines are shared).
/// </para>
/// </summary>
internal sealed class HostMemoryGuard : IDisposable
{
    internal static long LimitBytes { get; set; } = ResolveLimit(
        Environment.GetEnvironmentVariable("PSBASH_HOST_MAX_MB"), GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    /// Cheap first look, read on every poll: the process working set. A free-running GC keeps free pages
    /// resident (and private bytes only ever creeps up), so this alone proves nothing is wrong when it is
    /// UNDER the limit but cannot condemn a command when it is over. Test seam.
    /// </summary>
    internal static Func<long> ReadUsage { get; set; } = static () =>
    {
        using var self = Process.GetCurrentProcess();
        return self.WorkingSet64;
    };

    /// <summary>
    /// Runs when the cheap look is over the limit, before any verdict: a full blocking, compacting
    /// collection (finalizers included) so that only memory still reachable is left. Test seam.
    /// </summary>
    internal static Action Reclaim { get; set; } = static () =>
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    };

    /// <summary>
    /// The verdict measure, read after <see cref="Reclaim"/>: bytes of managed objects still live. This is the
    /// minimal working set the command truly needs (the buffers a runaway <c>yes | sort</c> keeps referencing),
    /// independent of how many free pages the GC chooses to keep. Test seam.
    /// </summary>
    internal static Func<long> ReadLive { get; set; } = static () => GC.GetTotalMemory(forceFullCollection: false);

    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// After a reclaim that found the live memory under the limit, the next one waits until the working set has
    /// grown by an eighth of the limit or this long has passed. Reclaiming on every poll kept a blocking full GC
    /// running back to back: <c>yes | sort</c> crawled at ~0.1 MB/s just under the limit and never reached it. Test seam.
    /// </summary>
    internal static TimeSpan ReclaimBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Clock for <see cref="ReclaimBackoff"/>, in milliseconds. Test seam.</summary>
    internal static Func<long> Now { get; set; } = static () => Environment.TickCount64;

    /// <summary>The exit status of a command stopped by the limit (128 + SIGKILL, like an OOM kill).</summary>
    public const int ExitCode = 137;

    /// <summary>Default limit never exceeds this, so a big-RAM machine still bounds a runaway command.</summary>
    private const long DefaultCeilingBytes = 2L << 30;

    internal static long ResolveLimit(string? envMb, long availableBytes)
    {
        if (long.TryParse(envMb, out var mb) && mb >= 0) return mb == 0 ? 0 : mb * 1024 * 1024;
        return availableBytes > 0 ? Math.Min(availableBytes / 4, DefaultCeilingBytes) : 0;
    }

    private readonly Timer? _timer;
    private readonly CancellationTokenSource _toCancel;
    private int _breached;
    private long _reclaimFloor;          // working set that justifies the next reclaim (0: none done yet)
    private long _lastReclaimAt;

    private HostMemoryGuard(CancellationTokenSource toCancel)
    {
        _toCancel = toCancel;
        if (LimitBytes > 0)
            _timer = new Timer(_ => Check(), null, PollInterval, PollInterval);
    }

    /// <summary>Watch while a command runs; a breach cancels <paramref name="toCancel"/>.</summary>
    public static HostMemoryGuard Watch(CancellationTokenSource toCancel) => new(toCancel);

    /// <summary>True once the limit stopped the command.</summary>
    public bool Breached => Volatile.Read(ref _breached) != 0;

    public string Message =>
        $"ps-bash: command stopped: the host exceeded its memory limit ({LimitBytes / (1024 * 1024)} MB; PSBASH_HOST_MAX_MB)";

    internal void Check()
    {
        if (Breached) return;
        long used;
        try { used = ReadUsage(); } catch { return; }
        if (used <= LimitBytes) return;
        long now = Now();
        if (_reclaimFloor != 0 && used < _reclaimFloor && now - _lastReclaimAt < (long)ReclaimBackoff.TotalMilliseconds) return;
        _reclaimFloor = used + LimitBytes / 8;
        _lastReclaimAt = now;
        // Over the limit: reclaim first. Only memory that survives a full collection is the command's.
        try { Reclaim(); used = ReadLive(); } catch { return; }
        if (used <= LimitBytes) return;
        if (Interlocked.Exchange(ref _breached, 1) != 0) return;
        try { _toCancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        // The command buffered a lot to get here; give it back before the next one starts.
        if (Breached) GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
