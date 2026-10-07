using PsBash.Host.Server;

namespace PsBash.Host.Runtime;

/// <summary>
/// Backstop for a command that cannot be stopped. Commands run one at a time under the host's
/// process-wide exec gate (env vars and cwd are process-global), so a command that ignores
/// PowerShell's Stop — a cmdlet spinning inside a .NET call — after its launcher left would hold the
/// gate forever: the shared daemon stays alive but every later command on it hangs, and nothing
/// reports why. When a command's cancellation fires and it has still not returned after
/// <see cref="Grace"/>, the host is poisoned: it logs the reason and exits, so launchers see "host
/// process exited" (and respawn a fresh host) instead of hanging.
/// <para>Enabled only for the shared framed-IPC daemon (<see cref="Enabled"/>): an interactive shell or
/// a private host is not shared, and must not exit because one command ignored Ctrl-C.</para>
/// </summary>
internal static class StuckCommandWatchdog
{
    public static bool Enabled { get; set; }

    /// <summary>How long a cancelled command may take to stop. <c>PSBASH_STUCK_GRACE_SECS</c>.</summary>
    internal static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("PSBASH_STUCK_GRACE_SECS"), out var s) && s > 0 ? s : 20);

    /// <summary>What poisoning does. Test seam; production logs and exits (70 = EX_SOFTWARE).</summary>
    internal static Action<string> Terminate { get; set; } = reason =>
    {
        HostLog.SetExitReason(reason);
        HostLog.Write("poisoned: " + reason);
        Environment.Exit(70);
    };

    /// <summary>Called when <paramref name="run"/>'s cancellation fires; arms the grace timer. Returns the
    /// monitoring task (completes once the command stopped or the host was poisoned).</summary>
    public static Task OnCancelled(Task run, string label)
    {
        if (!Enabled || run.IsCompleted) return Task.CompletedTask;
        var grace = Grace;
        return Task.Run(async () =>
        {
            var first = await Task.WhenAny(run, Task.Delay(grace)).ConfigureAwait(false);
            if (first != run)
                Terminate($"a cancelled command did not stop within {grace.TotalSeconds:0}s ({label}); " +
                          "it would hold the exec gate and hang every later command on this host");
        });
    }
}
