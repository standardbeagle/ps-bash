using System.Diagnostics;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// The host side of <see cref="ShellSignals.Route"/>: deliver a signal to the shell another ps-bash host runs, as a
/// <see cref="Mode.Signal"/> request on that host's endpoint (docs/specs/background-jobs.md §13).
///
/// <para>Identifying the target: a pid is a ps-bash shell when a host sidecar in this user's runtime directory names it
/// and was written after that process started (a recycled pid's stale sidecar is not it), exactly the rule the nested
/// launcher uses to find its enclosing host (<see cref="IpcTransportFactory.EnclosingHostOwnEndpoint"/>). The runtime
/// directory is owner-only, so only this user's hosts are found, and only a host answers the request.</para>
/// </summary>
public static class ShellSignalSender
{
    /// <summary>How long a signal may take to reach a host and be answered; a wedged host is reported unreachable.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Make this process's <c>kill</c> route signals for other ps-bash shells here (the host calls it at startup).</summary>
    public static void Register() => ShellSignals.Route = Send;

    /// <summary>
    /// Signal the shell whose <c>$$</c> is <paramref name="pid"/>. <see cref="ShellSignals.Outcome.NotAShell"/> when no
    /// ps-bash host of this user owns the pid, or it could not be reached / does not know the request (an older host):
    /// the caller then treats the pid as an ordinary process, and its own guard refuses to kill a host.
    /// </summary>
    public static ShellSignals.Outcome Send(int pid, int signal)
    {
        if (pid <= 0 || signal is < 1 or > 64) return ShellSignals.Outcome.NotAShell;
        if (pid == Environment.ProcessId) return ShellSignals.Deliver(signal);
        if (EndpointOf(pid) is not { } endpoint) return ShellSignals.Outcome.NotAShell;
        // Off the caller's thread (a PowerShell pipeline thread), bounded: a wedged target cannot hang `kill`.
        return SendToEndpoint(endpoint, signal);
    }

    /// <summary>The endpoint of the live host with <paramref name="pid"/>, or null when no ps-bash host of this user owns it.</summary>
    internal static string? EndpointOf(int pid)
    {
        long startTicks;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return null;
            startTicks = ProcessIdentity.StartTicksOf(proc);
        }
        catch (Exception) { return null; }
        return IpcTransportFactory.EnclosingHostOwnEndpoint(new ProcessIdentity(pid, startTicks),
            IpcTransportFactory.ReachableHostSidecars());
    }

    /// <summary>Send <paramref name="signal"/> to the host serving <paramref name="endpoint"/> (bounded; NotAShell on any failure).</summary>
    public static ShellSignals.Outcome SendToEndpoint(string endpoint, int signal)
    {
        try { return Task.Run(() => SendAsync(endpoint, signal)).GetAwaiter().GetResult(); }
        catch (Exception) { return ShellSignals.Outcome.NotAShell; }
    }

    /// <summary>
    /// Unix: catch a REAL SIGUSR1 / SIGUSR2 aimed at this process and hand it to <paramref name="deliver"/> as signal 10 /
    /// 12 (bash's Linux numbering, which the trap table uses on every platform). Their default action ends the process: a
    /// host ended every script of its session, a launcher its own script without running the trap. The host delivers to
    /// the command it runs (<see cref="ShellSignals.Deliver"/>), the launcher forwards to its host. Null on Windows, which
    /// has no POSIX signals (there, <c>kill</c> reaches a shell only through <see cref="ShellSignals.SendToShell"/>).
    /// </summary>
    public static IDisposable? CatchPosixSignals(Func<int, ShellSignals.Outcome> deliver)
    {
        if (OperatingSystem.IsWindows()) return null;
        // Raw numbers (PosixSignal names only the signals .NET itself handles): Linux 10/12, the BSDs and macOS 30/31.
        bool linux = OperatingSystem.IsLinux();
        var registrations = new List<System.Runtime.InteropServices.PosixSignalRegistration>(2);
        foreach (var (raw, bashNumber) in new[] { (linux ? 10 : 30, 10), (linux ? 12 : 31, 12) })
        {
            try
            {
                registrations.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(
                    (System.Runtime.InteropServices.PosixSignal)raw,
                    ctx =>
                    {
                        ctx.Cancel = true;   // never the default action (terminate)
                        _ = Task.Run(() => { try { deliver(bashNumber); } catch (Exception) { } });
                    }));
            }
            catch (Exception) { /* unsupported here: the signal keeps its default */ }
        }
        return new Registrations(registrations);
    }

    private sealed class Registrations(List<System.Runtime.InteropServices.PosixSignalRegistration> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var r in items) r.Dispose();
        }
    }

    internal static async Task<ShellSignals.Outcome> SendAsync(string endpoint, int signal)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var transport = new UnixSocketTransport(endpoint);
        using var stream = await transport.ConnectAsync(cts.Token).ConfigureAwait(false);
        await HostProtocol.WriteRequestAsync(stream, new Mode.Signal(signal), cts.Token).ConfigureAwait(false);
        string? answer = null;
        int exit = await HostProtocol.ReadResponseAsync(stream, line => answer ??= line, cts.Token).ConfigureAwait(false);
        return exit == 0 ? ParseOutcome(answer) : ShellSignals.Outcome.NotAShell;
    }

    /// <summary>The outcome named by a host's answer line; <see cref="ShellSignals.Outcome.NotAShell"/> for anything else.</summary>
    internal static ShellSignals.Outcome ParseOutcome(string? line)
        => line is not null && line.StartsWith(HostProtocol.SignalOutcomePrefix, StringComparison.Ordinal)
           && Enum.TryParse<ShellSignals.Outcome>(line.AsSpan(HostProtocol.SignalOutcomePrefix.Length).Trim(), out var o)
           && o != ShellSignals.Outcome.NotAShell
            ? o
            : ShellSignals.Outcome.NotAShell;

    /// <summary>The answer line a host writes for <paramref name="outcome"/>.</summary>
    public static string FormatOutcome(ShellSignals.Outcome outcome) => HostProtocol.SignalOutcomePrefix + outcome;
}
