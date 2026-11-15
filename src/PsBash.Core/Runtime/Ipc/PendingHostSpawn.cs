using System.Diagnostics;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// The host a launcher started for a shared endpoint and is still waiting on, recorded in
/// <c>&lt;endpoint&gt;.spawning</c> the moment the process starts. A launcher holds the spawn lock
/// (<see cref="HostSpawnLock"/>) only while it lives, and a host shows itself (socket + sidecar)
/// only once it has bound. A launcher killed in between (Claude Code cancels a status-line
/// command that has not finished, a cold host takes 2-8 s) released the lock with its host still
/// starting, invisible: the next launcher saw no host and started another, and whichever bound
/// last unlinked the other's socket, which then idled for ten minutes at ~150 MB. The next lock
/// holder reads this record and waits for that host instead of starting a second one.
/// </summary>
internal static class PendingHostSpawn
{
    internal const string Suffix = ".spawning";

    internal enum Verdict
    {
        /// <summary>No record, or its process is gone: nothing to wait for.</summary>
        None,
        /// <summary>The recorded host is running and still inside its startup window.</summary>
        WaitForIt,
        /// <summary>The recorded host is running but never came up within the startup window.</summary>
        Abandon,
    }

    internal static string PathFor(string endpoint) => endpoint + Suffix;

    /// <summary>Record <paramref name="process"/> as the host starting on <paramref name="endpoint"/>. Best effort.</summary>
    public static void Record(string endpoint, Process process)
    {
        try
        {
            var id = new ProcessIdentity(process.Id, ProcessIdentity.StartTicksOf(process));
            File.WriteAllText(PathFor(endpoint), id.ToArgumentValue());
        }
        catch { /* advisory: without it a killed launcher's host is just not waited for */ }
    }

    public static ProcessIdentity? TryRead(string endpoint)
    {
        try
        {
            var path = PathFor(endpoint);
            if (!File.Exists(path)) return null;
            return ProcessIdentity.TryParse(File.ReadAllText(path).Trim(), out var id) ? id : null;
        }
        catch { return null; }
    }

    public static void Clear(string endpoint)
    {
        try { File.Delete(PathFor(endpoint)); } catch { /* best effort */ }
    }

    /// <summary>
    /// What a spawn-lock holder should do about a recorded pending host. Pure: liveness is injected.
    /// The window is measured from the host's own start time, so every later launcher agrees on when
    /// the host has had long enough, however many launchers were killed in between.
    /// </summary>
    internal static Verdict Judge(ProcessIdentity? pending, DateTime nowUtc, TimeSpan startupTimeout,
        Func<ProcessIdentity, bool> isRunning)
    {
        if (pending is not { } id || !isRunning(id)) return Verdict.None;
        var started = new DateTime(id.StartTimeUtcTicks, DateTimeKind.Utc);
        return nowUtc < started + startupTimeout ? Verdict.WaitForIt : Verdict.Abandon;
    }

    /// <summary>
    /// Kill a recorded host that never came up, so it cannot bind later and unlink the socket of the
    /// host started to replace it. The start time guards against a recycled pid.
    /// </summary>
    public static void TryKill(ProcessIdentity id)
    {
        if (id.Pid == Environment.ProcessId) return;
        try
        {
            using var proc = Process.GetProcessById(id.Pid);
            if (ProcessIdentity.StartTicksOf(proc) != id.StartTimeUtcTicks) return;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2_000);
        }
        catch { /* gone, or not ours to kill */ }
    }
}
