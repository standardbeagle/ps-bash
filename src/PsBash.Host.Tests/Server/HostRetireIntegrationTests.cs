using System.Collections;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using PsBash.Testing;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Retiring a real host (HostRetire, and the launcher's replacement of an older-build host) never interrupts a
/// command another client has in flight. REGRESSION: an install, and then the first new-build launcher to reach each
/// older-build slot host, shut it down and killed it mid-command; agents' long commands ended with exit 125.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)] // PSBASH_IPC_ENDPOINT
[Trait("Category", "Lifecycle")]
public sealed class HostRetireIntegrationTests
{
    private const string LongCommand = "Start-Sleep -Milliseconds 2500; 'long-done'";

    private sealed class LiveHost : IAsyncDisposable
    {
        public readonly string Scheme = "unix";
        public readonly string Endpoint;
        public readonly HostServer Server;
        private readonly IIpcTransport _transport;
        private readonly WorkerPool<SdkWorker> _pool;
        private readonly CancellationTokenSource _cts = new();
        private Task? _run;

        public LiveHost()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ps-bash");
            Directory.CreateDirectory(dir);
            Endpoint = Path.Combine(dir, $"host-retire-{Guid.NewGuid().ToString("N")[..8]}.sock");
            _transport = new UnixSocketTransport(Endpoint);
            _pool = new WorkerPool<SdkWorker>(1, 2, SdkWorker.Create);
            Server = new HostServer(_transport, _pool);
        }

        public string Spec => $"{Scheme}:{Endpoint}";

        public async Task StartAsync()
        {
            _run = Server.RunAsync(_cts.Token);
            var first = await Task.WhenAny(Server.WhenListening, _run, Task.Delay(TimeSpan.FromMinutes(5)));
            if (first != Server.WhenListening) await first;
        }

        public async Task WaitUntilWarmAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTime.UtcNow < deadline)
            {
                await using var stream = await new UnixSocketTransport(Endpoint).ConnectAsync();
                await HostProtocol.WriteRequestAsync(stream, new Mode.Health());
                var lines = new List<string>();
                await HostProtocol.ReadResponseAsync(stream, l => lines.Add(l));
                if (lines.Count == 1 && lines[0] == HostProtocol.HealthPayload) return;
                await Task.Delay(50);
            }
            Assert.Fail("the host never finished warming");
        }

        /// <summary>The host answers a shutdown before it starts acting on it (Connection), so wait for the flag.</summary>
        public async Task ShutdownRequestedAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!Server.ShutdownRequested && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(Server.ShutdownRequested, "the host was never told to shut down");
        }

        /// <summary>Another agent's command: returns once the host has STARTED it, with the task of its whole response.</summary>
        public async Task<Task<(int Exit, List<string> Lines)>> StartLongCommandAsync()
        {
            var stream = await new UnixSocketTransport(Endpoint).ConnectAsync();
            await HostProtocol.WriteRequestAsync(stream, new Mode.Command(LongCommand, SessionMode.Framed, Environment()));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lines = new List<string>();
            var response = Task.Run(async () =>
            {
                await using (stream)
                {
                    int exit = await HostProtocol.ReadResponseAsync(stream, (l, _) => lines.Add(l), () => started.TrySetResult());
                    return (exit, lines);
                }
            });
            await Task.WhenAny(started.Task, response).WaitAsync(TimeSpan.FromMinutes(2));
            Assert.True(started.Task.IsCompleted, "the long command never started");
            return response;
        }

        public void WriteOlderBuildSidecar()
            => new HostMetadata(
                Pid: System.Environment.ProcessId,
                ExecutablePath: System.Environment.ProcessPath ?? "<unknown>",
                ProtocolVersion: HostProtocol.ProtocolVersion,
                BuildIdentity: "older-build-" + Guid.NewGuid().ToString("N")[..6],
                TransportScheme: Scheme,
                Endpoint: Endpoint,
                StartedAt: DateTimeOffset.UtcNow,
                Owner: System.Environment.UserName).Write(Scheme, Endpoint);

        private static IReadOnlyList<KeyValuePair<string, string>> Environment()
        {
            var block = new List<KeyValuePair<string, string>>();
            foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
                if (e.Key is string k && e.Value is string v) block.Add(new(k, v));
            return block;
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            try { if (_run is not null) await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await Server.DisposeAsync();
            await _pool.DisposeAsync();
            await _transport.DisposeAsync();
            try { HostMetadata.Remove(Scheme, Endpoint); } catch { }
            try { IpcTransportFactory.RetireEndpoint(Scheme, Endpoint); } catch { }
        }
    }

    [Fact]
    public async Task HostRunningACommand_IsLeftToFinishIt_ThenRetiredOnceIdle()
    {
        await using var host = new LiveHost();
        await host.StartAsync();
        var other = await host.StartLongCommandAsync();

        var whileBusy = await HostRetire.TryRetireIdleAsync(() => new UnixSocketTransport(host.Endpoint), 5000, CancellationToken.None);

        Assert.Equal(HostRetire.Outcome.Busy, whileBusy);
        Assert.False(host.Server.ShutdownRequested);
        var (exit, lines) = await other.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(0, exit);
        Assert.Contains("long-done", string.Concat(lines));

        var onceIdle = await HostRetire.TryRetireIdleAsync(() => new UnixSocketTransport(host.Endpoint), 5000, CancellationToken.None);
        Assert.Equal(HostRetire.Outcome.Retired, onceIdle);
        await host.ShutdownRequestedAsync();
    }

    // The launcher side: a new-build launcher reaching an older-build host that is running someone's command.
    [Fact]
    public async Task Launcher_OlderBuildHostRunningACommand_LeavesItRunning()
    {
        await using var host = new LiveHost();
        await host.StartAsync();
        using var env = ProcessEnv.Set(IpcTransportFactory.EndpointEnvVar, host.Spec);
        var other = await host.StartLongCommandAsync();
        // After the command starts: the host reaps sidecars it cannot match to a ps-bash-host (this one names testhost).
        host.WriteOlderBuildSidecar();
        Assert.NotNull(HostMetadata.TryRead(host.Scheme, host.Endpoint));

        var bogusBinary = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var launcher = await IpcWorker.StartAsync(bogusBinary, startupTimeout: TimeSpan.FromSeconds(10),
                         lifetime: Lifetime.Daemon, ct: startCts.Token))
        {
            Assert.False(host.Server.ShutdownRequested);
            Assert.Equal(host.Endpoint, launcher.Endpoint);
        }

        var (exit, lines) = await other.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(0, exit);
        Assert.Contains("long-done", string.Concat(lines));
        Assert.False(host.Server.ShutdownRequested);
    }

    // An idle older-build host is still replaced (here the replacement binary does not exist, so the start fails
    // after the old host was retired).
    [Fact]
    public async Task Launcher_IdleOlderBuildHost_IsRetired()
    {
        await using var host = new LiveHost();
        await host.StartAsync();
        await host.WaitUntilWarmAsync();   // a host still warming is waited out, not classified
        host.WriteOlderBuildSidecar();
        using var env = ProcessEnv.Set(IpcTransportFactory.EndpointEnvVar, host.Spec);

        var bogusBinary = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<HostUnavailableException>(async () =>
        {
            await using var launcher = await IpcWorker.StartAsync(bogusBinary, startupTimeout: TimeSpan.FromSeconds(10),
                lifetime: Lifetime.Daemon, ct: startCts.Token);
        });
        await host.ShutdownRequestedAsync();
    }
}
