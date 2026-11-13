using PsBash.Core.Runtime;

namespace PsBash.Host.Runtime;

/// <summary>
/// Pre-started private hosts for background jobs. A job needs its own <c>ps-bash-host</c> process, and a
/// cold one costs seconds (process start + runspace creation) — far more than the foreground command it
/// runs beside (docs/specs/background-jobs.md §7). So after the FIRST job of this process a spare is started
/// in the background; the next job takes it (a connect, not a spawn) and a replacement is started. A miss
/// (no spare ready) raises the target so a burst of <c>&amp;</c> in a loop is served warm after the first few.
/// Spares are not created for scripts that never use <c>&amp;</c>, run below normal priority, and are torn
/// down after <see cref="IdleExpiry"/> without a job, so an idle daemon holds none.
/// </summary>
internal static class JobHostSpares
{
    private const int MaxSpares = 2;
    private static readonly TimeSpan IdleExpiry = TimeSpan.FromSeconds(60);

    private static readonly object Gate = new();
    private static readonly List<Task<IpcWorker>> Spares = [];
    private static int _target = 1;
    private static Timer? _expiry;

    /// <summary>A started host, from the spare list when one is ready, else spawned now. Never returns null.</summary>
    public static IpcWorker Take(string hostPath)
    {
        Task<IpcWorker>? ready = null;
        lock (Gate)
        {
            for (int i = 0; i < Spares.Count; i++)
            {
                if (Spares[i].IsCompletedSuccessfully) { ready = Spares[i]; Spares.RemoveAt(i); break; }
            }
            if (ready is null && _target < MaxSpares) _target++; // a miss: keep more warm next time
            ArmExpiry();
        }
        IpcWorker worker;
        if (ready is not null) worker = ready.Result;
        else worker = StartWarmAsync(hostPath).GetAwaiter().GetResult();
        Refill(hostPath);
        return worker;
    }

    /// <summary>Start a spare now if none is ready or starting (a command that uses <c>&amp;</c> is about to run).</summary>
    public static void Prewarm(int expectedJobs = 1)
    {
        var hostPath = Environment.ProcessPath;
        if (hostPath is null) return;
        lock (Gate)
        {
            // Several `&` in one command: warm that many (bounded) so they are served by connects, not spawns.
            _target = Math.Max(_target, Math.Min(expectedJobs, MaxSpares));
            ArmExpiry();
        }
        Refill(hostPath);
    }

    private static void Refill(string hostPath)
    {
        lock (Gate)
        {
            int missing = PruneAndCountMissing(Spares, _target);
            for (int i = 0; i < missing; i++)
                Spares.Add(Task.Run(() => StartWarmAsync(hostPath)));
        }
    }

    /// <summary>
    /// Drops the spares whose start failed and returns how many to start to reach <paramref name="target"/>.
    /// The list holds started and still-starting spares alike, so its count is the whole supply. A failed one
    /// is not supply: kept, it held its slot until the idle expiry and no replacement was started. A separate
    /// starting counter was subtracted on top of the list count, so a spare that was starting counted twice
    /// and a burst of jobs got no second spare.
    /// </summary>
    internal static int PruneAndCountMissing(List<Task<IpcWorker>> spares, int target)
    {
        spares.RemoveAll(static t => t.IsFaulted || t.IsCanceled);
        return Math.Max(0, target - spares.Count);
    }

    /// <summary>A started host whose runspace is already created: a no-op command pays that cost before a job needs the host.</summary>
    private static async Task<IpcWorker> StartWarmAsync(string hostPath)
    {
        var w = await IpcWorker.StartAsync(hostPath, lifetime: Lifetime.PerInvocation).ConfigureAwait(false);
        try { await w.ExecuteAsync("Invoke-BashShopt -p 2>$null | Out-Null", CancellationToken.None, []).ConfigureAwait(false); }
        catch { /* a failed warm-up still leaves a usable host; the job's own command reports real errors */ }
        return w;
    }

    private static void ArmExpiry()
    {
        _expiry ??= new Timer(static _ => Expire(), null, Timeout.Infinite, Timeout.Infinite);
        _expiry.Change(IdleExpiry, Timeout.InfiniteTimeSpan);
    }

    private static void Expire()
    {
        List<Task<IpcWorker>> drop;
        lock (Gate)
        {
            drop = [.. Spares];
            Spares.Clear();
            _target = 1;
        }
        foreach (var t in drop)
        {
            _ = t.ContinueWith(static x =>
            {
                if (x.IsCompletedSuccessfully) { try { x.Result.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { } }
            }, TaskScheduler.Default);
        }
    }
}
