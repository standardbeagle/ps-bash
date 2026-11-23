using System.Diagnostics;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Server;
using PsBash.Testing;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// GH #7: a shared daemon exits soon after the session anchor that keyed its endpoint is gone,
/// instead of idling for the full <c>PSBASH_HOST_IDLE_SECS</c>. Before the fix an agent whose
/// per-call shell became the anchor left one idle ~65 MB daemon per call for 600 s.
/// Oracle note (Directive 1): no bash oracle — ps-bash host lifecycle surface.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)] // Daemon_* sets the endpoint/idle vars; runs alone, so off the SdkHost lane too
public sealed class SessionAnchorWatcherTests
{
    [Fact]
    public void TryCreate_NoAnchor_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromHours(1), _ => { });
        Assert.Null(SessionAnchorWatcher.TryCreate(null, idle, cts));
    }

    [Fact]
    public async Task AnchorGone_ShortensIdleSoHostExits()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromHours(1), _ => { });
        await using var watcher = SessionAnchorWatcher.TryCreate(
            new ProcessIdentity(1, 2), idle, cts,
            pollInterval: TimeSpan.FromMilliseconds(20), grace: TimeSpan.FromMilliseconds(50),
            isAnchorRunning: () => false, recordExitReason: _ => { });

        Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)),
            "a daemon whose session anchor is gone must exit within the grace, not the idle hour");
    }

    [Fact]
    public async Task AnchorAlive_HostKeepsServing()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromHours(1), _ => { });
        await using var watcher = SessionAnchorWatcher.TryCreate(
            new ProcessIdentity(1, 2), idle, cts,
            pollInterval: TimeSpan.FromMilliseconds(20), grace: TimeSpan.FromMilliseconds(50),
            isAnchorRunning: () => true, recordExitReason: _ => { });

        Assert.False(cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(300)),
            "warm reuse inside a live session must be unchanged");
    }

    /// <summary>
    /// The orphaned-daemon leak: a command still in flight when the session ended kept the daemon alive for
    /// good (idle shortening waits for the last command); after a Claude Code restart seven orphaned hosts on
    /// one desktop ran for 25+ minutes at about half a core each. Nobody in a gone session waits for that command, so after a grace it
    /// is hung up, as bash hangs up a session's jobs when the session leader dies.
    /// </summary>
    [Fact]
    public async Task AnchorGone_CommandStillRunning_IsHungUpAfterTheGrace()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromHours(1), _ => { });
        idle.ConnectionStarted(); // a command whose launcher never disconnects
        string? reason = null;
        await using var watcher = SessionAnchorWatcher.TryCreate(
            new ProcessIdentity(1, 2), idle, cts,
            pollInterval: TimeSpan.FromMilliseconds(20), grace: TimeSpan.FromMilliseconds(50),
            hangupGrace: TimeSpan.FromMilliseconds(100),
            isAnchorRunning: () => false, recordExitReason: r => reason = r);

        Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)),
            "a command still running after its session is gone must not keep the daemon alive");
        Assert.Contains("hung up", reason);
    }

    [Fact]
    public async Task AnchorAlive_CommandStillRunning_IsLeftAlone()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromHours(1), _ => { });
        idle.ConnectionStarted();
        await using var watcher = SessionAnchorWatcher.TryCreate(
            new ProcessIdentity(1, 2), idle, cts,
            pollInterval: TimeSpan.FromMilliseconds(20), grace: TimeSpan.FromMilliseconds(50),
            hangupGrace: TimeSpan.FromMilliseconds(50),
            isAnchorRunning: () => true, recordExitReason: _ => { });

        Assert.False(cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(400)),
            "a long command inside a live session runs as long as it needs");
    }

    [Theory]
    [InlineData("--session-anchor=4242:99")]
    [InlineData("--session-anchor 4242:99")]
    public void GetSessionAnchor_ParsesBothArgumentForms(string argLine)
        => Assert.Equal(new ProcessIdentity(4242, 99), Program.GetSessionAnchor(argLine.Split(' ')));

    [Fact]
    public void GetSessionAnchor_AbsentOrMalformed_IsNull()
    {
        Assert.Null(Program.GetSessionAnchor(Array.Empty<string>()));
        Assert.Null(Program.GetSessionAnchor(["--session-anchor=garbage"]));
    }

    /// <summary>
    /// End to end: a real <see cref="Lifetime.Daemon"/> launcher keyed by a stand-in anchor
    /// process spawns a daemon that outlives the launcher, then exits once the anchor dies —
    /// with the idle timeout pinned at 600 s so only the anchor can explain the exit.
    /// </summary>
    [SkippableFact]
    public async Task Daemon_ExitsAfterItsSessionAnchorDies()
    {
        var hostBinary = HostStartupStressTests.TryLocateHostBinary();
        Skip.If(hostBinary is null, "ps-bash-host binary not found — build src/PsBash.Host first");

        using var anchorProc = StartLongLivedProcess();
        var anchor = new ProcessIdentity(anchorProc.Id, ProcessIdentity.StartTicksOf(anchorProc));

        using var env = ProcessEnv.Set(
            (IpcTransportFactory.EndpointEnvVar, null),
            (IpcTransportFactory.SessionEnvVar, null),
            ("PSBASH_HOST_IDLE_SECS", "600"));
        // The endpoint is keyed by the stand-in's pid+start time, so it is unique to this test.
        IpcTransportFactory.SessionAnchorOverride = () => anchor;
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        int? hostPid = null;
        try
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using (var worker = await IpcWorker.StartAsync(
                hostBinary!, startupTimeout: TimeSpan.FromSeconds(40),
                lifetime: Lifetime.Daemon, ct: startCts.Token))
            {
                hostPid = int.Parse((await worker.QueryAsync("$PID", startCts.Token)).Trim());
            }

            Assert.True(IsAlive(hostPid.Value), "a daemon must outlive the launcher while its session is alive");

            anchorProc.Kill(entireProcessTree: true);
            anchorProc.WaitForExit();

            // 1 s poll + 5 s grace; generous slack for a loaded box.
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var host = Process.GetProcessById(hostPid.Value);
            await host.WaitForExitAsync(exitCts.Token);
        }
        finally
        {
            if (!anchorProc.HasExited) anchorProc.Kill(entireProcessTree: true);
            if (hostPid is { } pid && IsAlive(pid))
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            IpcTransportFactory.SessionAnchorOverride = null;
            try { HostMetadata.Remove(scheme, endpoint); } catch { }
            try { IpcTransportFactory.RetireEndpoint(scheme, endpoint); } catch { }
        }
    }

    /// <summary>
    /// End to end, the leak itself: the daemon is running a command for a launcher that stays connected (this
    /// test), and the session anchor dies. Before the fix the daemon logged "session anchor gone; exiting 5s after
    /// the last command" and then ran forever, because that last command never ended.
    /// </summary>
    [SkippableFact]
    public async Task Daemon_WithACommandStillRunning_ExitsAfterItsSessionAnchorDies()
    {
        var hostBinary = HostStartupStressTests.TryLocateHostBinary();
        Skip.If(hostBinary is null, "ps-bash-host binary not found — build src/PsBash.Host first");

        using var anchorProc = StartLongLivedProcess();
        var anchor = new ProcessIdentity(anchorProc.Id, ProcessIdentity.StartTicksOf(anchorProc));

        using var env = ProcessEnv.Set(
            (IpcTransportFactory.EndpointEnvVar, null),
            (IpcTransportFactory.SessionEnvVar, null),
            ("PSBASH_HOST_IDLE_SECS", "600"));
        IpcTransportFactory.SessionAnchorOverride = () => anchor;
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        int? hostPid = null;
        IpcWorker? worker = null;
        Task<int>? running = null;
        try
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            worker = await IpcWorker.StartAsync(
                hostBinary!, startupTimeout: TimeSpan.FromSeconds(40),
                lifetime: Lifetime.Daemon, ct: startCts.Token);
            hostPid = int.Parse((await worker.QueryAsync("$PID", startCts.Token)).Trim());

            // The connected launcher's command: still running when the session ends.
            running = worker.ExecuteAsync("Start-Sleep -Seconds 600");
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(running.IsCompleted, "the command must be in flight when the anchor dies");

            anchorProc.Kill(entireProcessTree: true);
            anchorProc.WaitForExit();

            // 1 s poll + hang-up grace + the command's stop; generous slack for a loaded box.
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var host = Process.GetProcessById(hostPid.Value);
            await host.WaitForExitAsync(exitCts.Token);
        }
        finally
        {
            if (!anchorProc.HasExited) anchorProc.Kill(entireProcessTree: true);
            if (hostPid is { } pid && IsAlive(pid))
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            if (running is not null) try { await running; } catch { /* the host hung it up */ }
            if (worker is not null) try { await worker.DisposeAsync(); } catch { }
            IpcTransportFactory.SessionAnchorOverride = null;
            try { HostMetadata.Remove(scheme, endpoint); } catch { }
            try { IpcTransportFactory.RetireEndpoint(scheme, endpoint); } catch { }
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>A stand-in session anchor: lives two minutes, killable at will.</summary>
    internal static Process StartLongLivedProcess()
    {
        var si = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 }
            : new ProcessStartInfo("sleep", "120") { UseShellExecute = false };
        return Process.Start(si)!;
    }
}
