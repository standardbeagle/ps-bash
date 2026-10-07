using System.Diagnostics;

namespace PsBash.Host.Runtime;

/// <summary>
/// Stops the running command — not the host — when the host process outgrows its memory limit.
/// <para>
/// Everything a command buffers lives in the shared host (<c>yes | sort</c> grew ~1 GB a minute), so
/// one command could otherwise exhaust memory and take every session down with an OutOfMemory crash.
/// While a command runs, its usage is polled; past <see cref="LimitBytes"/> the command is cancelled
/// (the same Stop a disconnect uses) and reports the limit, exit 137 as for an OOM kill.
/// Limit: <c>PSBASH_HOST_MAX_MB</c> (<c>0</c> disables); default half the memory available to the
/// process (physical RAM or a container limit).
/// </para>
/// </summary>
internal sealed class HostMemoryGuard : IDisposable
{
    internal static long LimitBytes { get; set; } = ResolveLimit(
        Environment.GetEnvironmentVariable("PSBASH_HOST_MAX_MB"), GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>How usage is measured. Test seam; production reads the process's private bytes.</summary>
    internal static Func<long> ReadUsage { get; set; } = static () =>
    {
        using var self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64;
    };

    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The exit status of a command stopped by the limit (128 + SIGKILL, like an OOM kill).</summary>
    public const int ExitCode = 137;

    internal static long ResolveLimit(string? envMb, long availableBytes)
    {
        if (long.TryParse(envMb, out var mb) && mb >= 0) return mb == 0 ? 0 : mb * 1024 * 1024;
        return availableBytes > 0 ? availableBytes / 2 : 0;
    }

    private readonly Timer? _timer;
    private readonly CancellationTokenSource _toCancel;
    private int _breached;

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
