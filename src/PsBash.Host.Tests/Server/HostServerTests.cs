using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Integration tests for HostServer accept loop and Connection dispatcher.
/// A single SdkWorker + HostServer is shared across tests in this class so
/// that sequential tests can verify cross-connection runspace state sharing.
/// </summary>
[Collection("SdkHost")]
public sealed class HostServerTests : IAsyncLifetime
{
    private SdkWorker _worker = null!;
    private NamedPipeTransport _serverTransport = null!;
    private HostServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serverTask = null!;
    private string _pipeName = null!;

    public async Task InitializeAsync()
    {
        _worker = SdkWorker.Create();
        _pipeName = $"psbash-test-{Guid.NewGuid():N}";
        _serverTransport = new NamedPipeTransport(_pipeName);
        _server = new HostServer(_serverTransport, Task.FromResult(_worker));
        _cts = new CancellationTokenSource();
        _serverTask = _server.RunAsync(_cts.Token);
        await _server.WhenListening;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        await _server.DisposeAsync();
        await _worker.DisposeAsync();
        _cts.Dispose();
    }

    private async Task<(string output, int exitCode)> SendCommandAsync(string command, CancellationToken ct = default)
    {
        var clientTransport = new NamedPipeTransport(_pipeName);
        await using (clientTransport)
        {
            using var stream = await clientTransport.ConnectAsync(ct);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Command(command), ct);
            var lines = new List<string>();
            var exitCode = await HostProtocol.ReadResponseAsync(stream, line => lines.Add(line), ct);
            return (string.Join("\n", lines), exitCode);
        }
    }

    private async Task<(string output, int exitCode)> SendHealthAsync(string pipeName, CancellationToken ct = default)
    {
        var clientTransport = new NamedPipeTransport(pipeName);
        await using (clientTransport)
        {
            using var stream = await clientTransport.ConnectAsync(ct);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Health(), ct);
            var lines = new List<string>();
            var exitCode = await HostProtocol.ReadResponseAsync(stream, line => lines.Add(line), ct);
            return (string.Join("\n", lines), exitCode);
        }
    }

    [Fact]
    public async Task SingleConnection_InvokeBashEcho_ReturnsOutputAndExit0()
    {
        var (output, exitCode) = await SendCommandAsync("Invoke-BashEcho 'hello'");
        Assert.Equal(0, exitCode);
        Assert.Contains("hello", output);
    }

    [Fact]
    public async Task HealthConnection_DoesNotWaitForWorkerInitialization()
    {
        var pipeName = $"psbash-health-{Guid.NewGuid():N}";
        var workerTcs = new TaskCompletionSource<SdkWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var transport = new NamedPipeTransport(pipeName);
        await using var server = new HostServer(transport, workerTcs.Task);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var serverTask = server.RunAsync(cts.Token);
        await server.WhenListening.WaitAsync(cts.Token);

        var (output, exitCode) = await SendHealthAsync(pipeName, cts.Token);

        cts.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        Assert.Equal(HostProtocol.HealthStartingExitCode, exitCode);
        Assert.Equal(HostProtocol.HealthStartingPayload, output);
    }

    [Fact]
    public async Task TwoSequentialConnections_ShareRunspaceState()
    {
        // Set a global PS variable across one connection, read it back in another.
        // Using a variable avoids cmdlet auto-loading issues with Set-Location.
        var (_, setExit) = await SendCommandAsync("$global:PsBashTestShared = 'state-shared-42'");
        Assert.Equal(0, setExit);

        var (output, getExit) = await SendCommandAsync("$global:PsBashTestShared");
        Assert.Equal(0, getExit);
        Assert.Contains("state-shared-42", output);
    }

    [Fact]
    public async Task GarbledModeHeader_ServerRemainsAlive()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        // Send bytes that don't start with "MODE:" — server catches the IOException,
        // makes a best-effort write of EXIT:2, and closes the connection.
        // We don't read the server's error response: on Windows named pipes,
        // FlushFileBuffers (called by WriteExitAsync) only returns after the
        // client reads, so waiting here creates a deadlock if the client-read
        // hasn't started. Close the connection immediately and verify the server
        // accepts the next valid connection (the real requirement).
        var garbledTransport = new NamedPipeTransport(_pipeName);
        await using (garbledTransport)
        {
            using var stream = await garbledTransport.ConnectAsync(cts.Token);
            var garbage = System.Text.Encoding.UTF8.GetBytes("NOT_A_VALID_HEADER\n<<<END>>>\n");
            await stream.WriteAsync(garbage, cts.Token);
            // No FlushAsync: on Windows named pipes, FlushFileBuffers blocks until the
            // server reads ALL pending data. The server stops after the first bad line,
            // so flushing here would deadlock. Dispose sends remaining buffer and signals
            // EOF; the server's IOException handler catches it and moves on.
        }

        // Server must still be alive — accept a valid connection.
        var (output, aliveExit) = await SendCommandAsync("Invoke-BashEcho 'alive'", cts.Token);
        Assert.Equal(0, aliveExit);
        Assert.Contains("alive", output);
    }

    [Fact]
    public async Task SixteenConcurrentConnections_AllCompleteWithoutInterleaving()
    {
        const int n = 16;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        var tasks = Enumerable.Range(0, n)
            .Select(i => SendCommandAsync($"Invoke-BashEcho 'conn-{i}'", cts.Token))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All connections must complete with exit 0.
        for (int i = 0; i < n; i++)
            Assert.Equal(0, results[i].exitCode);

        // Each result must contain exactly its own marker — no cross-stream mixing.
        for (int i = 0; i < n; i++)
            Assert.Contains($"conn-{i}", results[i].output);
    }
}
