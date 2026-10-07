using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Once a launcher has connected, the host must answer — an error frame and a nonzero EXIT — never
/// just close the stream. Under load, HostServerTests / lifecycle tests failed with
/// "Response stream closed before EXIT sentinel": a failure escaping Connection was caught only AFTER
/// the stream had been disposed, and the request-read-timeout / malformed-request paths returned
/// silently. Each is the same user-visible "host connection reset". Same collection as
/// HostServerTests: <see cref="Connection.FaultForTest"/> and the env knob are process-wide.
/// </summary>
[Collection("SdkHost")]
public sealed class HostServerAnswersEveryConnectionTests : IAsyncLifetime
{
    private WorkerPool<SdkWorker> _pool = null!;
    private HostServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serverTask = null!;
    private string _pipeName = null!;
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "ps-bash", "answer-" + Guid.NewGuid().ToString("N")[..8]);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_logDir);
        HostLog.PathOverride = Path.Combine(_logDir, "host.log");
        _pool = new WorkerPool<SdkWorker>(warmTarget: 0, max: 2, SdkWorker.Create);
        _pipeName = $"psbash-test-{Guid.NewGuid():N}";
        _server = new HostServer(new NamedPipeTransport(_pipeName), _pool);
        _cts = new CancellationTokenSource();
        _serverTask = _server.RunAsync(_cts.Token);
        return _server.WhenListening;
    }

    public async Task DisposeAsync()
    {
        Connection.FaultForTest = null;
        Environment.SetEnvironmentVariable("PSBASH_REQUEST_READ_TIMEOUT_MS", null);
        HostLog.PathOverride = null;
        _cts.Cancel();
        try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        await _server.DisposeAsync();
        await _pool.DisposeAsync();
        _cts.Dispose();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private string LogText => File.Exists(HostLog.PathOverride!) ? File.ReadAllText(HostLog.PathOverride!) : "";

    [SkippableFact]
    public async Task FailureEscapingTheConnection_IsAnsweredWithErrorAndExit_AndLogged()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "named-pipe fixture");
        Connection.FaultForTest = stage =>
        {
            if (stage == "before-exit") throw new InvalidOperationException("injected fault");
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var client = new NamedPipeTransport(_pipeName);
        using var stream = await client.ConnectAsync(cts.Token);
        await HostProtocol.WriteRequestAsync(stream, new Mode.Command("Invoke-BashEcho 'ran'"), cts.Token);
        var lines = new List<string>();
        int exit = await HostProtocol.ReadResponseAsync(stream, l => lines.Add(l), cts.Token);

        Assert.Equal(HostServer.HostFailureExitCode, exit);   // before: IOException, stream closed before EXIT
        Assert.Contains("connection error: InvalidOperationException: injected fault", LogText);
    }

    [SkippableFact]
    public async Task RequestNeverArrives_IsAnsweredNotSilentlyClosed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "named-pipe fixture");
        Environment.SetEnvironmentVariable("PSBASH_REQUEST_READ_TIMEOUT_MS", "500");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var client = new NamedPipeTransport(_pipeName);
        using var stream = await client.ConnectAsync(cts.Token);
        // Connected, request withheld: the host's read deadline elapses.
        var lines = new List<string>();
        int exit = await HostProtocol.ReadResponseAsync(stream, l => lines.Add(l), cts.Token);

        Assert.Equal(2, exit);
        Assert.Contains("request not received within 500 ms", LogText);
    }
}
