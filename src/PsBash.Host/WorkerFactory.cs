using System.Runtime.InteropServices;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Host;

/// <summary>
/// Centralized <see cref="IWorker"/> selection for the launcher (T07). Decides
/// between out-of-process <see cref="IpcWorker"/> (host-backed) and in-process
/// <see cref="PwshWorker"/> (per-call pwsh) based on environment, host binary
/// availability, and host startup behavior.
/// </summary>
/// <remarks>
/// <para>Selection order:
/// <list type="number">
///   <item><description>If <c>PSBASH_DISABLE_HOST=1</c> is set, return PwshWorker (no warning — opt-out is intentional).</description></item>
///   <item><description>If <c>PSBASH_HOST</c> overrides the binary path, use that path; if missing, fall back with a single warning.</description></item>
///   <item><description>Otherwise resolve the side-by-side <c>ps-bash-host</c> next to the launcher; if missing, fall back silently (host adoption is opt-in during rollout — emit no warning to avoid noise on default installs).</description></item>
///   <item><description>Attempt <see cref="IpcWorker.StartAsync"/>. If it throws <see cref="HostUnavailableException"/>, fall back to PwshWorker with a single warning.</description></item>
/// </list></para>
///
/// <para>Soft-fallback warning is rate-limited to one stderr line per launcher
/// invocation via <see cref="_warned"/> so a stuck host does not flood stderr
/// across multiple worker creations.</para>
///
/// <para>Interactive mode (M4) does not yet support host-backed handoff — that
/// is T08a/T08b. Callers pass <c>forcePwsh: true</c> to stay on the in-process
/// PwshWorker until the interactive bridge lands.</para>
/// </remarks>
public static class WorkerFactory
{
    private static int _warned;

    /// <summary>
    /// Create an <see cref="IWorker"/> for the current invocation. Returns an
    /// <see cref="IpcWorker"/> when a host is reachable, otherwise a
    /// <see cref="PwshWorker"/>.
    /// </summary>
    /// <param name="pwshPath">Resolved pwsh binary path used by the PwshWorker fallback.</param>
    /// <param name="forcePwsh">When true, skip host detection and always return PwshWorker. Used by interactive mode pending T08.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<IWorker> CreateAsync(
        string pwshPath,
        bool forcePwsh = false,
        CancellationToken ct = default)
    {
        if (forcePwsh) return await StartPwshAsync(pwshPath).ConfigureAwait(false);

        if (Environment.GetEnvironmentVariable("PSBASH_DISABLE_HOST") == "1")
            return await StartPwshAsync(pwshPath).ConfigureAwait(false);

        var hostBinary = ResolveHostBinary();
        if (hostBinary is null)
        {
            // Side-by-side host binary missing — silent fallback. Distribution
            // for the host binary is opt-in during the migration rollout.
            return await StartPwshAsync(pwshPath).ConfigureAwait(false);
        }

        if (!File.Exists(hostBinary))
        {
            // PSBASH_HOST override pointed at a missing path — warn once.
            WarnFallback($"PSBASH_HOST='{hostBinary}' not found");
            return await StartPwshAsync(pwshPath).ConfigureAwait(false);
        }

        try
        {
            var sessionId = ResolveSessionId();
            var lockFile = HostLockFile.ForSession(sessionId);
            return await IpcWorker.StartAsync(lockFile, hostBinary, ct: ct).ConfigureAwait(false);
        }
        catch (HostUnavailableException ex)
        {
            WarnFallback($"host unavailable: {ex.Message}");
            return await StartPwshAsync(pwshPath).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolve the host binary path. Returns the <c>PSBASH_HOST</c> override
    /// when set (even if the path is missing — caller distinguishes "user
    /// asked for a path that doesn't exist" from "no host installed"), or the
    /// side-by-side host binary when it exists, or null otherwise.
    /// </summary>
    internal static string? ResolveHostBinary()
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

    private static async Task<IWorker> StartPwshAsync(string pwshPath)
    {
        var modulePath = Environment.GetEnvironmentVariable("PSBASH_MODULE")
            ?? ModuleExtractor.ExtractEmbedded();
        return await PwshWorker.StartAsync(
            pwshPath,
            workerScriptPath: Environment.GetEnvironmentVariable("PSBASH_WORKER"),
            modulePath: modulePath).ConfigureAwait(false);
    }

    private static void WarnFallback(string reason)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0) return;
        Console.Error.WriteLine($"[ps-bash] host unavailable, falling back to in-process pwsh: {reason}");
    }

    /// <summary>
    /// Reset the rate-limiter for tests that exercise multiple fallback paths
    /// in one process.
    /// </summary>
    internal static void ResetWarningStateForTests() => Interlocked.Exchange(ref _warned, 0);
}
