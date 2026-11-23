using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// <see cref="HostRetire"/> against a scripted host that speaks the real wire protocol: a host running someone's
/// command is never told to shut down; an idle one is told only after it has STARTED the claim.
/// </summary>
public sealed class HostRetireTests
{
    private sealed class ScriptedHost : IAsyncDisposable
    {
        private readonly UnixSocketTransport _transport;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _leaseHeld;
        public readonly ConcurrentQueue<string> Events = new();
        public readonly TaskCompletionSource ClaimClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Endpoint { get; }

        public ScriptedHost(bool busy)
        {
            Endpoint = Path.Combine(Path.GetTempPath(), "ps-bash", $"retire-{Guid.NewGuid():N}"[..20] + ".sock");
            Directory.CreateDirectory(Path.GetDirectoryName(Endpoint)!);
            _transport = new UnixSocketTransport(Endpoint);
            _leaseHeld = busy ? 1 : 0;
        }

        /// <summary>The command that held the host finishes.</summary>
        public void Release() => Interlocked.Exchange(ref _leaseHeld, 0);

        public async Task StartAsync()
        {
            await _transport.ListenAsync(_cts.Token);
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    Stream s;
                    try { s = await _transport.AcceptAsync(_cts.Token); } catch { return; }
                    _ = Handle(s);
                }
            });
        }

        private async Task Handle(Stream stream)
        {
            try
            {
                var mode = await HostProtocol.ReadRequestAsync(stream, _cts.Token);
                switch (mode)
                {
                    case Mode.Command c:
                        if (Interlocked.CompareExchange(ref _leaseHeld, 1, 0) != 0)
                        {
                            Events.Enqueue("refused-busy");
                            await HostProtocol.WriteExitAsync(stream, HostProtocol.ExecBusyExitCode, _cts.Token);
                            return;
                        }
                        Events.Enqueue("started:" + c.Body);
                        await HostProtocol.WriteStartedAsync(stream, _cts.Token);
                        // Runs until the launcher disconnects, as the host's disconnect watchdog would end it.
                        var buf = new byte[1];
                        try { while (await stream.ReadAsync(buf, _cts.Token) > 0) { } } catch { }
                        Events.Enqueue("claim-closed");
                        ClaimClosed.TrySetResult();
                        return;
                    case Mode.Shutdown sd:
                        Events.Enqueue("shutdown");
                        await HostProtocol.WriteResponseLineAsync(stream, HostProtocol.ShutdownAcceptedPayload, _cts.Token);
                        await HostProtocol.WriteExitAsync(stream, 0, _cts.Token);
                        return;
                }
            }
            catch { }
            finally { try { await stream.DisposeAsync(); } catch { } }
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            if (_loop is not null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
            await _transport.DisposeAsync();
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task BusyHost_IsNotToldToShutDown()
    {
        await using var host = new ScriptedHost(busy: true);
        await host.StartAsync();

        var outcome = await HostRetire.TryRetireIdleAsync(() => new UnixSocketTransport(host.Endpoint), 5000, CancellationToken.None);

        Assert.Equal(HostRetire.Outcome.Busy, outcome);
        Assert.Equal(["refused-busy"], host.Events);
    }

    [Fact]
    public async Task IdleHost_IsClaimedFirst_ThenToldToShutDown_ThenTheClaimEnds()
    {
        await using var host = new ScriptedHost(busy: false);
        await host.StartAsync();

        var outcome = await HostRetire.TryRetireIdleAsync(() => new UnixSocketTransport(host.Endpoint), 5000, CancellationToken.None);
        await host.ClaimClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HostRetire.Outcome.Retired, outcome);
        Assert.Equal(["started:" + HostRetire.HoldBody, "shutdown", "claim-closed"], host.Events);
    }

    [Fact]
    public async Task NoHost_IsUnreachable()
    {
        var endpoint = Path.Combine(Path.GetTempPath(), "ps-bash", $"none-{Guid.NewGuid():N}"[..17] + ".sock");
        var outcome = await HostRetire.TryRetireIdleAsync(() => new UnixSocketTransport(endpoint), 5000, CancellationToken.None);
        Assert.Equal(HostRetire.Outcome.Unreachable, outcome);
    }
}
