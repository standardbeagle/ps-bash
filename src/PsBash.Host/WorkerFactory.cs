using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Host;

/// <summary>
/// Centralized <see cref="IWorker"/> selection for the launcher. Returns an
/// <see cref="IpcWorker"/> connected to the running (or freshly spawned)
/// <c>ps-bash-host</c> daemon. Throws <see cref="HostUnavailableException"/>
/// if the host binary cannot be found or fails to start.
/// </summary>
public static class WorkerFactory
{
    /// <summary>
    /// Create an <see cref="IpcWorker"/> for the current invocation. Spawns
    /// <c>ps-bash-host</c> if no instance is already running.
    /// </summary>
    public static async Task<IWorker> CreateAsync(CancellationToken ct = default)
    {
        var hostBinary = ResolveHostBinary();
        if (hostBinary is null)
        {
            throw new HostUnavailableException(
                "ps-bash-host binary not found beside the launcher.");
        }

        if (!File.Exists(hostBinary))
        {
            throw new HostUnavailableException(
                $"PSBASH_HOST='{hostBinary}' not found.");
        }

        var sessionId = ResolveSessionId();
        var lockFile = HostLockFile.ForSession(sessionId);
        return await IpcWorker.StartAsync(lockFile, hostBinary, spawnIfMissing: true, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the host binary path. Returns the <c>PSBASH_HOST</c> override
    /// when set, or the side-by-side host binary when it exists, or null.
    /// </summary>
    public static string? ResolveHostBinary()
    {
        var overridePath = Environment.GetEnvironmentVariable("PSBASH_HOST");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;

        var sxs = Path.Combine(AppContext.BaseDirectory, IpcWorker.GetHostBinaryName());
        return File.Exists(sxs) ? sxs : null;
    }

    private static string ResolveSessionId()
    {
        var explicitId = Environment.GetEnvironmentVariable("PSBASH_SESSION_ID");
        if (!string.IsNullOrEmpty(explicitId)) return explicitId;
        return Environment.ProcessId.ToString();
    }
}
