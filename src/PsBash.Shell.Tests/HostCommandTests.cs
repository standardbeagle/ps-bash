using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Shell.Tests;

public sealed class HostCommandTests
{
    // host gc: a shared daemon's parent is always gone (it outlives its launcher); it used to be killed for that.
    [Theory]
    [InlineData(false, true, false, 600, false)]   // registered daemon, dead parent: in use, kept
    [InlineData(false, false, true, 600, false)]   // interactive host: no sidecar, live launcher
    [InlineData(false, false, false, 5, false)]    // just started, sidecar not written yet
    [InlineData(false, false, false, 600, true)]   // no sidecar, no parent, old: orphan
    [InlineData(true, true, true, 5, true)]        // --force stops everything
    public void ShouldGc_OnlyUnregisteredParentlessOldHosts(bool force, bool registered, bool parentAlive, int ageSeconds, bool expected)
        => Assert.Equal(expected, HostCommands.ShouldGc(force, registered, parentAlive, TimeSpan.FromSeconds(ageSeconds)));

    [Theory]
    [InlineData(42, "42s")]
    [InlineData(185, "3m05s")]
    [InlineData(7380, "2h03m")]
    public void FormatAge_Compact(int seconds, string expected)
        => Assert.Equal(expected, HostCommands.FormatAge(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ReadAll_ReturnsEverySidecarInTheDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-inv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (pid, name) in new[] { (11, "a.sock"), (22, "a.sock-x1") })
                new HostMetadata(pid, "h.exe", 1, "b", "unix", Path.Combine(dir, name), DateTimeOffset.UtcNow, "u")
                    .Write("unix", Path.Combine(dir, name));
            File.WriteAllText(Path.Combine(dir, "broken.sock.host.json"), "{not json");

            var pids = HostMetadata.ReadAll(dir).Select(static m => m.Pid).Order().ToArray();

            Assert.Equal([11, 22], pids);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BuildStatusLines_WhenCompactOutputEnabled_CollapsesMetadata()
    {
        var meta = new HostMetadata(
            Pid: 1234,
            ExecutablePath: @"C:\tools\ps-bash-host.exe",
            ProtocolVersion: 1,
            BuildIdentity: "test-build",
            TransportScheme: "unix",
            Endpoint: "/tmp/psbash-test.sock",
            StartedAt: DateTimeOffset.Parse("2026-05-28T20:00:00Z"),
            Owner: "andy");
        var health = new[] { "health: ok", "runspace: ready" };

        var verbose = HostCommands.BuildStatusLines("unix", "/tmp/psbash-test.sock", meta, 0, health, compactOutput: false);
        var compact = HostCommands.BuildStatusLines("unix", "/tmp/psbash-test.sock", meta, 0, health, compactOutput: true);

        Assert.True(compact.Count < verbose.Count);
        Assert.True(string.Join('\n', compact).Length < string.Join('\n', verbose).Length);
        Assert.Single(compact);
        Assert.Contains("status: running", compact[0]);
        Assert.Contains("endpoint=unix:/tmp/psbash-test.sock", compact[0]);
        Assert.Contains("pid=1234", compact[0]);
        Assert.Contains("protocol=1", compact[0]);
        Assert.Contains("health=\"health: ok | runspace: ready\"", compact[0]);
    }

    [Fact]
    public void BuildStatusLines_WhenStoppedAndCompactOutputEnabled_PreservesState()
    {
        var compact = HostCommands.BuildStatusLines(
            "unix",
            "/tmp/psbash-test.sock",
            meta: null,
            exitCode: null,
            healthLines: [],
            compactOutput: true);

        Assert.Equal(
            "ps-bash-host status: stopped endpoint=unix:/tmp/psbash-test.sock metadata=absent",
            compact.Single());
    }

    [Fact]
    public async Task ControlRequest_WhenHostClosesDuringResponse_ReturnsUnavailableWithoutThrowing()
    {
        // macOS limits an AF_UNIX path to 104 chars and $TMPDIR already takes ~49, so the
        // name carries only 8 hex digits of a guid. Collision risk in a single run is negligible.
        var sockDir = Path.Combine(Path.GetTempPath(), "ps-bash");
        Directory.CreateDirectory(sockDir);
        var endpoint = "unix:" + Path.Combine(sockDir, $"abrupt-{Guid.NewGuid().ToString("N")[..8]}.sock");
        await using var transport = IpcTransportFactory.CreateDefault(endpoint);
        await transport.ListenAsync();

        // Pass a cancellation token to AcceptAsync / ReadRequestAsync so the
        // server task can be aborted cleanly. Under Windows CI load the
        // client's 750ms connect timeout (HostCommands.SendControlRequestAsync)
        // can fire before the server task has even called WaitForConnectionAsync,
        // leaving the server stuck waiting for a client that already gave up.
        // Cancelling at test end unwinds that state deterministically so the
        // final WaitAsync never times out.
        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await using var stream = await transport.AcceptAsync(serverCts.Token);
                _ = await HostProtocol.ReadRequestAsync(stream, serverCts.Token);
            }
            catch (OperationCanceledException) { /* expected when client aborts before/after connect */ }
            catch (IOException) { /* expected: client closed without sending a full request */ }
        });

        var (exitCode, lines) = await HostCommands.SendControlRequestAsync(
            new Mode.Health(),
            CancellationToken.None,
            endpoint);

        Assert.Null(exitCode);
        Assert.Empty(lines);
        serverCts.Cancel();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
