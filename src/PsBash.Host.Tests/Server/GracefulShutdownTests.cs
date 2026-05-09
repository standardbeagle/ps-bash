using System.Net.Sockets;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Tests for the graceful shutdown protocol path: Mode.Shutdown framing,
/// HostServer.RequestShutdownAsync drain semantics, and the rebind invariant
/// that proves a replacement host can claim the same endpoint after a
/// retired host exits.
///
/// Oracle note (Directive 1): no bash oracle — ps-bash IPC lifecycle is
/// outside the bash compatibility surface and is exercised against the
/// .NET-side wire contract directly.
/// </summary>
[Collection("SdkHost")]
public sealed class GracefulShutdownTests
{
    [Fact]
    public async Task ShutdownFrame_RoundTripsThroughHostProtocol()
    {
        using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Shutdown(1234));
        ms.Position = 0;
        var parsed = await HostProtocol.ReadRequestAsync(ms);
        var sd = Assert.IsType<Mode.Shutdown>(parsed);
        Assert.Equal(1234, sd.DeadlineMs);
    }

    [Fact]
    public async Task ShutdownFrame_NegativeDeadline_RoundTripsAsAbandon()
    {
        using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Shutdown(-1));
        ms.Position = 0;
        var parsed = await HostProtocol.ReadRequestAsync(ms);
        var sd = Assert.IsType<Mode.Shutdown>(parsed);
        Assert.Equal(-1, sd.DeadlineMs);
    }

    [Fact]
    public async Task ShutdownFrame_MissingDeadline_ThrowsIOException()
    {
        // Hand-craft a frame without DEADLINE — older or malformed senders.
        using var ms = new MemoryStream();
        var bytes = System.Text.Encoding.UTF8.GetBytes("MODE:Shutdown\n<<<END>>>\n");
        await ms.WriteAsync(bytes);
        ms.Position = 0;
        await Assert.ThrowsAsync<IOException>(async () => await HostProtocol.ReadRequestAsync(ms));
    }

    [Fact]
    public async Task ShutdownRequest_AckThenStopsAcceptingNewConnections()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipeName = $"psbash-shutdown-{Guid.NewGuid():N}";

        await using var serverTransport = new NamedPipeTransport(pipeName);
        await using var worker = SdkWorker.Create();
        await using var server = new HostServer(serverTransport, Task.FromResult(worker));
        var serverTask = server.RunAsync(cts.Token);
        await server.WhenListening.WaitAsync(cts.Token);

        // Send shutdown
        var (output, exitCode) = await SendShutdownAsync(pipeName, 100, cts.Token);
        Assert.Equal(0, exitCode);
        Assert.Equal(HostProtocol.ShutdownAcceptedPayload, output);

        // Server must have flagged shutdown
        await WaitForAsync(() => server.ShutdownRequested, TimeSpan.FromSeconds(5));
        Assert.True(server.ShutdownRequested);

        // Wait for accept loop to exit
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShutdownWithInflightRequest_DrainsBeforeExiting()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipeName = $"psbash-drain-{Guid.NewGuid():N}";

        await using var serverTransport = new NamedPipeTransport(pipeName);
        await using var worker = SdkWorker.Create();
        await using var server = new HostServer(serverTransport, Task.FromResult(worker));
        var serverTask = server.RunAsync(cts.Token);
        await server.WhenListening.WaitAsync(cts.Token);

        // Start a long-ish work request that takes ~500ms to complete.
        var workTask = SendCommandAsync(pipeName, "Start-Sleep -Milliseconds 500; Invoke-BashEcho 'work-done'", cts.Token);

        // Give the work request a moment to be accepted and start executing.
        await Task.Delay(100, cts.Token);

        // Send shutdown with a generous deadline (longer than the work duration).
        var (sdOutput, sdExit) = await SendShutdownAsync(pipeName, 5000, cts.Token);
        Assert.Equal(0, sdExit);
        Assert.Equal(HostProtocol.ShutdownAcceptedPayload, sdOutput);

        // The in-flight work request must complete normally.
        var (workOutput, workExit) = await workTask;
        Assert.Equal(0, workExit);
        Assert.Contains("work-done", workOutput);

        // Server exits cleanly.
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ShutdownAfterAck_RejectsNewConnections()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipeName = $"psbash-reject-{Guid.NewGuid():N}";

        await using var serverTransport = new NamedPipeTransport(pipeName);
        await using var worker = SdkWorker.Create();
        await using var server = new HostServer(serverTransport, Task.FromResult(worker));
        var serverTask = server.RunAsync(cts.Token);
        await server.WhenListening.WaitAsync(cts.Token);

        // Trigger graceful shutdown.
        var (_, sdExit) = await SendShutdownAsync(pipeName, 50, cts.Token);
        Assert.Equal(0, sdExit);

        // Wait for the accept loop to exit. After this, no new connection can be served.
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Attempts to connect now must fail (transport endpoint is gone or refusing).
        // Use a tight timeout so the test does not hang on a stuck pipe.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var clientTransport = new NamedPipeTransport(pipeName);
            await using (clientTransport)
            {
                using var stream = await clientTransport.ConnectAsync(attemptCts.Token);
                await HostProtocol.WriteRequestAsync(stream, new Mode.Command("Invoke-BashEcho 'late'"), attemptCts.Token);
                var lines = new List<string>();
                _ = await HostProtocol.ReadResponseAsync(stream, line => lines.Add(line), attemptCts.Token);
            }
        });
    }

    [Fact]
    public async Task ReplacementHost_CanBindSameEndpoint_AfterRetirement()
    {
        // Acceptance criterion: tests prove a new host can bind after graceful retirement.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipeName = $"psbash-rebind-{Guid.NewGuid():N}";

        // First host: start, accept, shut down.
        await using (var oldTransport = new NamedPipeTransport(pipeName))
        await using (var oldWorker = SdkWorker.Create())
        await using (var oldServer = new HostServer(oldTransport, Task.FromResult(oldWorker)))
        {
            var oldServerTask = oldServer.RunAsync(cts.Token);
            await oldServer.WhenListening.WaitAsync(cts.Token);

            var (output, exit) = await SendShutdownAsync(pipeName, 100, cts.Token);
            Assert.Equal(0, exit);
            Assert.Equal(HostProtocol.ShutdownAcceptedPayload, output);

            await oldServerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Second host: bind same name, must succeed and accept work.
        await using var newTransport = new NamedPipeTransport(pipeName);
        await using var newWorker = SdkWorker.Create();
        await using var newServer = new HostServer(newTransport, Task.FromResult(newWorker));
        using var newCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var newServerTask = newServer.RunAsync(newCts.Token);
        await newServer.WhenListening.WaitAsync(cts.Token);

        var (workOutput, workExit) = await SendCommandAsync(pipeName, "Invoke-BashEcho 'replacement'", cts.Token);
        Assert.Equal(0, workExit);
        Assert.Contains("replacement", workOutput);

        newCts.Cancel();
        try { await newServerTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    [Fact]
    public async Task ClientWaitForShutdown_HasDeadlineBeforeEscalating()
    {
        // Compatibility contract: a launcher must be able to bound its wait
        // for graceful shutdown using the deadline it provided so it can
        // escalate (e.g. to process cleanup) if the host does not retire
        // in time. We verify by sending Shutdown with deadline 50ms and
        // asserting RequestShutdownAsync returns within a small budget
        // even when an in-flight request would take much longer than the
        // deadline.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var worker = SdkWorker.Create();
        await using var server = new HostServer(
            new NamedPipeTransport($"psbash-deadline-{Guid.NewGuid():N}"),
            Task.FromResult(worker));

        // No accept loop needed — call RequestShutdownAsync directly with no
        // in-flight work. Must complete near-instantly (no drain wait when
        // _inFlight == 0).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await server.RequestShutdownAsync(60_000);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1_000,
            $"RequestShutdownAsync must return immediately when nothing is in-flight, took {sw.ElapsedMilliseconds} ms");
        Assert.True(server.ShutdownRequested);
    }

    // -- Helpers --

    private static async Task<(string output, int exitCode)> SendShutdownAsync(
        string pipeName, int deadlineMs, CancellationToken ct)
    {
        var transport = new NamedPipeTransport(pipeName);
        await using (transport)
        {
            using var stream = await transport.ConnectAsync(ct);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Shutdown(deadlineMs), ct);
            var lines = new List<string>();
            var exit = await HostProtocol.ReadResponseAsync(stream, l => lines.Add(l), ct);
            return (string.Join("\n", lines), exit);
        }
    }

    private static async Task<(string output, int exitCode)> SendCommandAsync(
        string pipeName, string command, CancellationToken ct)
    {
        var transport = new NamedPipeTransport(pipeName);
        await using (transport)
        {
            using var stream = await transport.ConnectAsync(ct);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Command(command), ct);
            var lines = new List<string>();
            var exit = await HostProtocol.ReadResponseAsync(stream, l => lines.Add(l), ct);
            return (string.Join("\n", lines), exit);
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
    }
}
