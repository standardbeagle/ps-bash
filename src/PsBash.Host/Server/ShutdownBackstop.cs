using System.Diagnostics;

namespace PsBash.Host.Server;

/// <summary>
/// Once a host starts shutting down, its process ends within <see cref="Deadline"/>, whatever teardown does.
/// Shutdown disposes the worker pool and the server, then calls <c>Environment.Exit</c>; any of those can wait on a
/// command or a runspace that will not let go, and the host then runs on with nothing serving it. That is what the
/// host log showed for daemons whose session ended: "exiting 5s after the last command", then no exit line at all.
/// </summary>
internal static class ShutdownBackstop
{
    /// <summary>
    /// Longer than <c>StuckCommandWatchdog.Grace</c> (20 s), so a command that ignores its stop still gets its own,
    /// logged verdict first.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When <paramref name="shutdown"/> is cancelled, start the countdown; if the process is still up after
    /// <paramref name="deadline"/>, <paramref name="terminate"/> ends it (default: log, then kill this process).
    /// A dedicated background thread, so neither a starved thread pool can delay it nor it delay a normal exit.
    /// </summary>
    public static void Arm(CancellationToken shutdown, TimeSpan deadline, Action<string>? terminate = null)
    {
        terminate ??= EndProcess;
        shutdown.Register(() => new Thread(() =>
        {
            Thread.Sleep(deadline);
            terminate($"shutdown did not finish within {deadline.TotalSeconds:0}s of starting");
        })
        { IsBackground = true, Name = "ps-bash-host shutdown backstop" }.Start());
    }

    private static void EndProcess(string reason)
    {
        // Kill, not Environment.Exit: the exit path is one of the things that can hang. Kill skips the process-exit
        // handler that writes the exit line, so write it here.
        HostLog.SetExitReason(reason);
        HostLog.Write("forced exit: " + reason);
        using var self = Process.GetCurrentProcess();
        self.Kill();
    }
}
