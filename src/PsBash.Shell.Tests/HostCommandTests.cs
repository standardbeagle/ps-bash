using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Shell.Tests;

public sealed class HostCommandTests
{
    [Fact]
    public void BuildStatusLines_WhenCompactOutputEnabled_CollapsesMetadata()
    {
        var meta = new HostMetadata(
            Pid: 1234,
            ExecutablePath: @"C:\tools\ps-bash-host.exe",
            ProtocolVersion: 1,
            BuildIdentity: "test-build",
            TransportScheme: "pipe",
            Endpoint: "psbash-test",
            StartedAt: DateTimeOffset.Parse("2026-05-28T20:00:00Z"),
            Owner: "andy");
        var health = new[] { "health: ok", "runspace: ready" };

        var verbose = HostCommands.BuildStatusLines("pipe", "psbash-test", meta, 0, health, compactOutput: false);
        var compact = HostCommands.BuildStatusLines("pipe", "psbash-test", meta, 0, health, compactOutput: true);

        Assert.True(compact.Count < verbose.Count);
        Assert.True(string.Join('\n', compact).Length < string.Join('\n', verbose).Length);
        Assert.Single(compact);
        Assert.Contains("status: running", compact[0]);
        Assert.Contains("endpoint=pipe:psbash-test", compact[0]);
        Assert.Contains("pid=1234", compact[0]);
        Assert.Contains("protocol=1", compact[0]);
        Assert.Contains("health=\"health: ok | runspace: ready\"", compact[0]);
    }

    [Fact]
    public void BuildStatusLines_WhenStoppedAndCompactOutputEnabled_PreservesState()
    {
        var compact = HostCommands.BuildStatusLines(
            "pipe",
            "psbash-test",
            meta: null,
            exitCode: null,
            healthLines: [],
            compactOutput: true);

        Assert.Equal(
            "ps-bash-host status: stopped endpoint=pipe:psbash-test metadata=absent",
            compact.Single());
    }

    [Fact]
    public async Task ControlRequest_WhenHostClosesDuringResponse_ReturnsUnavailableWithoutThrowing()
    {
        // macOS limits AF_UNIX path to 104 chars; the default temp prefix
        // (/var/folders/tb/<14-char-id>/T/CoreFxPipe_) eats ~50 chars before
        // the pipe name even starts. Use the first 8 hex digits of a guid
        // instead of the full 32 to stay under the cap. Collision risk on a
        // single-run test is negligible.
        var endpoint = $"pipe:psbash-abrupt-{Guid.NewGuid():N}".Substring(0, "pipe:psbash-abrupt-".Length + 8);
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
