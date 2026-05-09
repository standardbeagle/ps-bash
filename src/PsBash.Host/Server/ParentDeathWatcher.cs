using System.Diagnostics;

namespace PsBash.Host.Server;

/// <summary>
/// Polls a launcher process PID every <see cref="PollInterval"/> (default 200 ms).
/// Cancels the host's <see cref="CancellationTokenSource"/> when the process has
/// exited, so the host self-terminates if its parent launcher dies.
/// </summary>
/// <remarks>
/// Uses polling because Windows has no SIGHUP / POSIX process-death signal.
/// 200 ms poll interval matches the host parent-death
/// detection.
/// </remarks>
public sealed class ParentDeathWatcher : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenSource _watcherCts = new();
    private readonly Task _watchTask;

    private ParentDeathWatcher(int launcherPid, CancellationTokenSource cts, TimeSpan pollInterval)
    {
        _cts = cts;
        _watchTask = WatchAsync(launcherPid, pollInterval, _watcherCts.Token);
    }

    /// <summary>
    /// Creates a watcher for <paramref name="launcherPid"/>. Returns null when
    /// <paramref name="launcherPid"/> is null (no-op mode, e.g. during tests or
    /// when the host is run standalone).
    /// </summary>
    public static ParentDeathWatcher? TryCreate(
        int? launcherPid,
        CancellationTokenSource cts,
        TimeSpan? pollInterval = null)
    {
        if (launcherPid is null) return null;
        return new ParentDeathWatcher(launcherPid.Value, cts, pollInterval ?? PollInterval);
    }

    private async Task WatchAsync(int pid, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                if (!IsAlive(pid))
                {
                    _cts.TryCancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    public void Dispose()
    {
        _watcherCts.Cancel();
        try { _watchTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _watcherCts.Dispose();
    }
}
