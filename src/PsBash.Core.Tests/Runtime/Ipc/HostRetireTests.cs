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

    // The install's drain: each host is retired once it is idle; one still busy at the deadline is reported, never
    // told to shut down; a host whose process exited, or whose endpoint another host now owns, is left alone.
    [Fact]
    public async Task Drain_RetiresEachHostOnceIdle_ReportsTheStillBusy_LeavesGoneAndReplacedAlone()
    {
        await using var finishes = new ScriptedHost(busy: true);
        await using var neverFinishes = new ScriptedHost(busy: true);
        await using var replaced = new ScriptedHost(busy: false);
        await finishes.StartAsync();
        await neverFinishes.StartAsync();
        await replaced.StartAsync();
        var targets = new[]
        {
            new HostDrain.Target(101, "unix", finishes.Endpoint),
            new HostDrain.Target(102, "unix", neverFinishes.Endpoint),
            new HostDrain.Target(103, "unix", replaced.Endpoint),
            new HostDrain.Target(104, "unix", Path.Combine(Path.GetTempPath(), "ps-bash", "gone.sock")),
        };
        int polls = 0;
        bool IsAlive(int pid)
        {
            if (pid == 101 && Interlocked.Increment(ref polls) == 3) finishes.Release();  // its command ends mid-drain
            return pid != 104;
        }

        var results = await HostDrain.DrainAsync(targets, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100),
            IsAlive, t => t.Pid == 103 ? 999 : t.Pid, report: null, CancellationToken.None);

        var byPid = results.ToDictionary(r => r.Target.Pid, r => r.State);
        Assert.Equal(HostDrain.State.Retired, byPid[101]);
        Assert.Equal(HostDrain.State.Busy, byPid[102]);
        Assert.Equal(HostDrain.State.Replaced, byPid[103]);
        Assert.Equal(HostDrain.State.Gone, byPid[104]);
        Assert.Contains("shutdown", finishes.Events);
        Assert.DoesNotContain("shutdown", neverFinishes.Events);
        Assert.Empty(replaced.Events);
    }

    private static bool Shut(ScriptedHost h) => h.Events.Contains("shutdown");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition never held");
            await Task.Delay(20);
        }
    }

    // REGRESSION: the install's drain retired an idle slot host while another slot host, anchored on it, ran an agent's
    // command; 10 s later that slot hung the command up (exit 125). A host another one is anchored on is claimed first,
    // so no launcher's command starts on it and no launcher retires it, and it is shut down only once its dependents
    // are gone.
    [Fact]
    public async Task Drain_HoldsAHostWhileOneAnchoredOnItRuns_ThenRetiresIt()
    {
        await using var anchor = new ScriptedHost(busy: false);
        await using var dependent = new ScriptedHost(busy: true);
        await anchor.StartAsync();
        await dependent.StartAsync();
        var targets = new[]
        {
            new HostDrain.Target(201, "unix", anchor.Endpoint),
            new HostDrain.Target(202, "unix", dependent.Endpoint, AnchorPid: 201),
        };

        var drain = HostDrain.DrainAsync(targets, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50),
            pid => pid == 201 ? !Shut(anchor) : !Shut(dependent), t => t.Pid, report: null, CancellationToken.None);

        await WaitForAsync(() => anchor.Events.Contains("started:" + HostRetire.HoldBody));
        Assert.Equal(HostRetire.Outcome.Busy,
            await HostRetire.TryRetireIdleAsync(() => new UnixSocketTransport(anchor.Endpoint), 5000, CancellationToken.None));
        await Task.Delay(300);
        Assert.False(Shut(anchor), "the anchor was shut down while a host anchored on it ran a command");

        dependent.Release();
        var byPid = (await drain.WaitAsync(TimeSpan.FromSeconds(30))).ToDictionary(r => r.Target.Pid, r => r.State);

        Assert.Equal(HostDrain.State.Retired, byPid[201]);
        Assert.Equal(HostDrain.State.Retired, byPid[202]);
        Assert.True(Shut(anchor));
    }

    // At the deadline the held anchor is released untouched and reported busy, like its dependent: the caller decides.
    [Fact]
    public async Task Drain_DependentStillBusyAtTheDeadline_ReleasesItsAnchorUnretired()
    {
        await using var anchor = new ScriptedHost(busy: false);
        await using var dependent = new ScriptedHost(busy: true);
        await anchor.StartAsync();
        await dependent.StartAsync();
        var targets = new[]
        {
            new HostDrain.Target(301, "unix", anchor.Endpoint),
            new HostDrain.Target(302, "unix", dependent.Endpoint, AnchorPid: 301),
        };

        var results = await HostDrain.DrainAsync(targets, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50),
            _ => true, t => t.Pid, report: null, CancellationToken.None);
        await anchor.ClaimClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var byPid = results.ToDictionary(r => r.Target.Pid, r => r.State);
        Assert.Equal(HostDrain.State.Busy, byPid[301]);
        Assert.Equal(HostDrain.State.Busy, byPid[302]);
        Assert.False(Shut(anchor));
        Assert.False(Shut(dependent));
    }

    [Fact]
    public async Task NoHost_IsUnreachable()
    {
        var endpoint = Path.Combine(Path.GetTempPath(), "ps-bash", $"none-{Guid.NewGuid():N}"[..17] + ".sock");
        var outcome = await HostRetire.TryRetireIdleAsync(() => new UnixSocketTransport(endpoint), 5000, CancellationToken.None);
        Assert.Equal(HostRetire.Outcome.Unreachable, outcome);
    }
}
