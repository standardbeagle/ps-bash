using System.Diagnostics;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Testing;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// A daemon waiting for its next command uses no CPU: every wait it has (accept, idle timer, session-anchor
/// watcher) blocks. Measured on a desktop running six agents, the hosts' CPU between commands was put down to an
/// idle spin; it was the per-command runspace refill, which ends a second or two after each command. This pins the
/// idle state itself, the anchor watcher's once-a-second poll included.
/// Oracle note (Directive 1): no bash oracle — ps-bash host lifecycle surface.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class HostIdleCpuTests
{
    /// <summary>CPU an idle host may use over <see cref="Window"/>: 2% of one core.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    [SkippableFact]
    public async Task Daemon_WaitingForACommand_CpuTimeStaysFlat()
    {
        var hostBinary = HostStartupStressTests.TryLocateHostBinary();
        Skip.If(hostBinary is null, "ps-bash-host binary not found — build src/PsBash.Host first");

        // A live stand-in anchor (its pid+start time key an endpoint no other test uses): the watcher polls it every
        // second for the whole window.
        using var anchorProc = SessionAnchorWatcherTests.StartLongLivedProcess();
        var anchor = new ProcessIdentity(anchorProc.Id, ProcessIdentity.StartTicksOf(anchorProc));
        using var env = ProcessEnv.Set(
            (IpcTransportFactory.EndpointEnvVar, null),
            (IpcTransportFactory.SessionEnvVar, null),
            ("PSBASH_HOST_IDLE_SECS", "600"));
        IpcTransportFactory.SessionAnchorOverride = () => anchor;
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        int? hostPid = null;
        try
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using (var worker = await IpcWorker.StartAsync(
                hostBinary!, startupTimeout: TimeSpan.FromSeconds(40), lifetime: Lifetime.Daemon, ct: startCts.Token))
            {
                hostPid = int.Parse((await worker.QueryAsync("$PID", startCts.Token)).Trim());
                await worker.ExecuteAsync("Write-Output warm", startCts.Token);
            }

            using var host = Process.GetProcessById(hostPid.Value);
            // The pool refills the runspace the command used; wait for that burst to end (a quiet second).
            var settle = Stopwatch.StartNew();
            while (CpuOver(host, TimeSpan.FromSeconds(1)) > TimeSpan.FromMilliseconds(20))
                Assert.True(settle.Elapsed < TimeSpan.FromSeconds(60), "the host never went quiet after its command");

            var used = CpuOver(host, Window);
            Assert.True(used <= Budget,
                $"an idle host used {used.TotalMilliseconds:0} ms of CPU in {Window.TotalSeconds:0} s (budget {Budget.TotalMilliseconds:0} ms)");
        }
        finally
        {
            if (!anchorProc.HasExited) anchorProc.Kill(entireProcessTree: true);
            if (hostPid is { } pid)
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            IpcTransportFactory.SessionAnchorOverride = null;
            try { HostMetadata.Remove(scheme, endpoint); } catch { }
            try { IpcTransportFactory.RetireEndpoint(scheme, endpoint); } catch { }
        }
    }

    private static TimeSpan CpuOver(Process host, TimeSpan window)
    {
        host.Refresh();
        var before = host.TotalProcessorTime;
        Thread.Sleep(window);
        host.Refresh();
        return host.TotalProcessorTime - before;
    }
}
