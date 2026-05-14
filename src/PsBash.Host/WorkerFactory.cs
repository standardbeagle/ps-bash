using PsBash.Core.Runtime;

namespace PsBash.Host;

/// <summary>
/// Resolves the host binary and starts an <see cref="IpcWorker"/> against it.
/// There is no fallback: if the host binary is missing or fails to come up,
/// <see cref="IpcWorker.StartAsync"/> throws and the caller exits non-zero.
/// </summary>
public static class WorkerFactory
{
    /// <summary>
    /// Spawn a private <c>ps-bash-host</c> and return a worker that proxies
    /// commands to it. REFACTOR-7: uses <see cref="Lifetime.PerInvocation"/> —
    /// the worker owns the host process and kills it on dispose. Callers that
    /// need the shared per-user daemon must call
    /// <see cref="IpcWorker.StartAsync"/> with <see cref="Lifetime.Daemon"/>
    /// directly.
    /// </summary>
    public static async Task<IWorker> CreateAsync(CancellationToken ct = default)
    {
        var hostBinary = ResolveHostBinary()
            ?? throw new HostUnavailableException(
                "ps-bash-host binary not found. Set PSBASH_HOST=<path> or install alongside ps-bash.");

        return await IpcWorker.StartAsync(
            hostBinary, lifetime: Lifetime.PerInvocation, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the host binary path: <c>PSBASH_HOST</c> override (returned even
    /// if missing so the caller can name the bad path), else side-by-side
    /// <c>ps-bash-host</c>, else null.
    /// </summary>
    public static string? ResolveHostBinary()
    {
        var overridePath = Environment.GetEnvironmentVariable("PSBASH_HOST");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;

        var sxs = Path.Combine(AppContext.BaseDirectory, IpcWorker.GetHostBinaryName());
        return File.Exists(sxs) ? sxs : null;
    }
}
