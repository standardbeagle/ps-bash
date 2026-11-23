using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Testing;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Executor-slot spill (docs/specs/executor-slots.md): a host that is running another command refuses a new
/// one with <see cref="HostProtocol.ExecBusyExitCode"/> before STARTED, and the launcher re-sends the command to
/// the next slot endpoint. Scripted hosts speak the real wire protocol; nothing here spawns a process.
/// Runs alone: the spill delay and session knobs are process environment.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class IpcWorkerExecutorSlotTests
{
    private sealed class SlotHost : IAsyncDisposable
    {
        private readonly UnixSocketTransport _transport;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private readonly bool _busy;
        public int Commands;
        /// <summary>Completes when this host has received its second command (a retry), so tests wait on the event, not the clock.</summary>
        public readonly TaskCompletionSource SecondCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Endpoint { get; }

        public SlotHost(string endpoint, bool busy)
        {
            Endpoint = endpoint;
            _busy = busy;
            Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);
            _transport = new UnixSocketTransport(endpoint);
        }

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
                if (mode is Mode.Command)
                {
                    if (Interlocked.Increment(ref Commands) >= 2) SecondCommand.TrySetResult();
                    if (_busy)
                    {
                        await HostProtocol.WriteExitAsync(stream, HostProtocol.ExecBusyExitCode, _cts.Token);
                        return;
                    }
                    await HostProtocol.WriteStartedAsync(stream, _cts.Token);
                    await HostProtocol.WriteResponseLineAsync(stream, "ran-on-" + Path.GetFileName(Endpoint), _cts.Token);
                    await HostProtocol.WriteExitAsync(stream, 0, _cts.Token);
                }
                else
                {
                    await HostProtocol.WriteResponseLineAsync(stream, HostProtocol.HealthPayload, _cts.Token);
                    await HostProtocol.WriteExitAsync(stream, 0, _cts.Token);
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

    private static string NewBase()
        => Path.Combine(Path.GetTempPath(), "ps-bash", $"slot-{Guid.NewGuid():N}.sock");

    private static async Task<T> WithSpillDelay<T>(string ms, Func<Task<T>> body)
    {
        using var env = ProcessEnv.Set("PSBASH_EXEC_SPILL_MS", ms);
        return await body();
    }

    [Fact]
    public async Task BusyHost_CommandIsResentToTheNextSlot_AndRunsExactlyOnce()
    {
        var baseEndpoint = NewBase();
        await using var busy = new SlotHost(baseEndpoint, busy: true);
        await using var free = new SlotHost(IpcTransportFactory.ResolveSlotEndpoint("unix", baseEndpoint, 1).Endpoint, busy: false);
        await busy.StartAsync();
        await free.StartAsync();
        await using var worker = IpcWorker.ConnectForTest("unix", baseEndpoint, Path.Combine(Path.GetTempPath(), "ps-bash-host-fake"));
        worker.AssumeSlotHostsReachableForTest = true;

        var output = await WithSpillDelay("0", () => worker.QueryAsync("echo x"));

        Assert.Contains("ran-on-" + Path.GetFileName(free.Endpoint), output);
        Assert.Equal(1, busy.Commands);  // refused once, never retried against the busy host
        Assert.Equal(1, free.Commands);  // the command ran exactly once
        Assert.Equal(1, worker.SlotForTest);
    }

    [Fact]
    public async Task BusyHost_BelowTheSpillDelay_KeepsRetryingTheSameSlot()
    {
        var baseEndpoint = NewBase();
        await using var busy = new SlotHost(baseEndpoint, busy: true);
        await busy.StartAsync();
        await using var worker = IpcWorker.ConnectForTest("unix", baseEndpoint, Path.Combine(Path.GetTempPath(), "ps-bash-host-fake"));
        worker.AssumeSlotHostsReachableForTest = true;
        // Safety net only: the test cancels itself as soon as the busy host has seen the retry.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // A very long spill delay: the launcher must back off on slot 0, never move, never run the command.
        await WithSpillDelay("600000", async () =>
        {
            var query = worker.QueryAsync("echo x", cts.Token);
            await busy.SecondCommand.Task.WaitAsync(TimeSpan.FromSeconds(30));   // the retry arrived: no clock race
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
            return 0;
        });

        Assert.Equal(0, worker.SlotForTest);
        Assert.True(busy.Commands >= 2, $"expected repeated attempts on slot 0, saw {busy.Commands}");
    }

    // A launcher started by a slot host's command is not a process descendant of slot 0 (slot hosts are spawned
    // detached), yet slot 0 is waiting on it. Before the fix it backed off for the whole spill delay (~300 ms per
    // nested `bash -c`); with the delay at 10 minutes this test would hang until its safety net fired.
    [Fact]
    public async Task BusyHost_LauncherInsideTheEnclosingSession_SpillsAtOnceDespiteTheSpillDelay()
    {
        var baseEndpoint = NewBase();
        await using var busy = new SlotHost(baseEndpoint, busy: true);
        await using var free = new SlotHost(IpcTransportFactory.ResolveSlotEndpoint("unix", baseEndpoint, 1).Endpoint, busy: false);
        await busy.StartAsync();
        await free.StartAsync();
        await using var worker = IpcWorker.ConnectForTest("unix", baseEndpoint, Path.Combine(Path.GetTempPath(), "ps-bash-host-fake"));
        worker.AssumeSlotHostsReachableForTest = true;
        worker.InsideEnclosingHostForTest = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var output = await WithSpillDelay("600000", () => worker.QueryAsync("echo x", cts.Token));

        Assert.Contains("ran-on-" + Path.GetFileName(free.Endpoint), output);
        Assert.Equal(1, busy.Commands);  // one refusal, no back-off retries on the slot that waits on us
        Assert.Equal(1, free.Commands);
        Assert.Equal(1, worker.SlotForTest);
    }

    private static async Task<T> WithEnv<T>((string Name, string Value)[] vars, Func<Task<T>> body)
    {
        using var env = ProcessEnv.Set(vars.Select(v => (v.Name, (string?)v.Value)).ToArray());
        return await body();
    }

    /// <summary>
    /// Every slot of a fresh session busy, free scripted hosts on its two overflow slots, and a free scripted host
    /// standing in for a private host.
    /// </summary>
    private sealed class AllBusySession : IAsyncDisposable
    {
        public readonly string Base = NewBase();
        public readonly SlotHost[] Slots = new SlotHost[IpcTransportFactory.MaxExecSlots + 1];
        public readonly SlotHost[] Overflow = new SlotHost[2];
        public SlotHost Private = null!;

        public static async Task<AllBusySession> StartAsync()
        {
            var s = new AllBusySession();
            for (int k = 0; k < s.Slots.Length; k++)
            {
                s.Slots[k] = new SlotHost(IpcTransportFactory.ResolveSlotEndpoint("unix", s.Base, k).Endpoint, busy: true);
                await s.Slots[k].StartAsync();
            }
            for (int i = 0; i < s.Overflow.Length; i++)
            {
                s.Overflow[i] = new SlotHost(
                    IpcTransportFactory.ResolveSlotEndpoint("unix", s.Base, IpcTransportFactory.OverflowSlot(i)).Endpoint, busy: false);
                await s.Overflow[i].StartAsync();
            }
            s.Private = new SlotHost(Path.Combine(Path.GetDirectoryName(s.Base)!, $"priv-{Guid.NewGuid():N}"[..13] + ".sock"), busy: false);
            await s.Private.StartAsync();
            return s;
        }

        public IpcWorker Worker()
        {
            var worker = IpcWorker.ConnectForTest("unix", Base, Path.Combine(Path.GetTempPath(), "ps-bash-host-fake"));
            worker.AssumeSlotHostsReachableForTest = true;
            worker.PrivateHostEndpointForTest = Private.Endpoint;
            return worker;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var host in Slots) await host.DisposeAsync();
            foreach (var host in Overflow) await host.DisposeAsync();
            await Private.DisposeAsync();
        }
    }

    // REGRESSION: an unrelated launcher that found every slot busy went round them again until one freed — with six
    // long builds in flight, every further `bash -c` from an agent waited minutes. Before the fix this test spun until
    // its safety net cancelled it.
    // REGRESSION (private-host churn): the overflow then went to a private host started for the one command and killed
    // after it, ~3 CPU-seconds of cold start per command. A desktop whose one Claude session ran ~7 agents kept all
    // seven slots busy with their long commands and started 60-310 such hosts an hour (hooks included). An overflow
    // ticket now names a warm slot of its own past the last executor slot, which the next overflow command reuses.
    [Fact]
    public async Task EverySlotBusy_PastTheGrace_RunsOnTheWarmOverflowSlot_TheNextLauncherReusesIt()
    {
        await using var session = await AllBusySession.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var outputs = await WithEnv([("PSBASH_EXEC_SPILL_MS", "0"), ("PSBASH_EXEC_OVERFLOW_MS", "0"), ("PSBASH_EXEC_OVERFLOW_MAX", "2")],
            async () =>
            {
                var ran = new System.Collections.Generic.List<string>();
                for (int i = 0; i < 2; i++)
                {
                    await using var worker = session.Worker();  // one launcher per command, as the Bash tool runs them
                    ran.Add(await worker.QueryAsync("echo x", cts.Token));
                }
                return ran;
            });

        string overflowHost = "ran-on-" + Path.GetFileName(session.Overflow[0].Endpoint);
        Assert.All(outputs, output => Assert.Contains(overflowHost, output));
        Assert.Equal(2, session.Overflow[0].Commands);
        Assert.Equal(0, session.Private.Commands);                             // no host started for one command
        foreach (var host in session.Slots) Assert.Equal(2, host.Commands);    // one refusal each per command, no second round
    }

    // Two overflow commands at once: each ticket has its own slot, so they never queue on one host.
    [Fact]
    public async Task EverySlotBusy_FirstOverflowTicketHeld_RunsOnTheSecondOverflowSlot()
    {
        await using var session = await AllBusySession.StartAsync();
        using var held = ExecOverflowTicket.TryAcquire("unix", session.Base, 2);
        Assert.NotNull(held);
        await using var worker = session.Worker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var output = await WithEnv([("PSBASH_EXEC_SPILL_MS", "0"), ("PSBASH_EXEC_OVERFLOW_MS", "0"), ("PSBASH_EXEC_OVERFLOW_MAX", "2")],
            () => worker.QueryAsync("echo x", cts.Token));

        Assert.Contains("ran-on-" + Path.GetFileName(session.Overflow[1].Endpoint), output);
        Assert.Equal(0, session.Overflow[0].Commands);
        Assert.Equal(0, session.Private.Commands);
    }

    [Fact]
    public void OverflowSlots_FollowTheLastExecutorSlot_AndParseBack()
    {
        Assert.Equal(IpcTransportFactory.MaxExecSlots + 1, IpcTransportFactory.OverflowSlot(0));
        var endpoint = IpcTransportFactory.ResolveSlotEndpoint("unix", "/r/host.sock", IpcTransportFactory.OverflowSlot(1)).Endpoint;
        Assert.Equal("/r/host.sock-x" + (IpcTransportFactory.MaxExecSlots + 2), endpoint);
        Assert.Equal("/r/host.sock", IpcTransportFactory.BaseOfSlotEndpoint(endpoint));
        Assert.Equal(IpcTransportFactory.OverflowSlot(1), IpcTransportFactory.SlotIndexOf(endpoint));
    }

    // Memory: a burst of short commands frees a slot inside the grace, so it must start no host.
    [Fact]
    public async Task EverySlotBusy_InsideTheGrace_GoesRoundAgain_StartsNoHost()
        => await AssertWaitsForASlot([("PSBASH_EXEC_OVERFLOW_MS", "600000"), ("PSBASH_EXEC_OVERFLOW_MAX", "2")], holdTickets: 0);

    // Memory: overflow hosts are capped per session; with every ticket held the launcher waits for a slot instead.
    [Fact]
    public async Task EverySlotBusy_EveryOverflowTicketHeld_GoesRoundAgain_StartsNoHost()
        => await AssertWaitsForASlot([("PSBASH_EXEC_OVERFLOW_MS", "0"), ("PSBASH_EXEC_OVERFLOW_MAX", "1")], holdTickets: 1);

    private static async Task AssertWaitsForASlot((string, string)[] env, int holdTickets)
    {
        await using var session = await AllBusySession.StartAsync();
        var held = Enumerable.Range(0, holdTickets).Select(_ => ExecOverflowTicket.TryAcquire("unix", session.Base, holdTickets)).ToList();
        try
        {
            Assert.All(held, Assert.NotNull);
            await using var worker = session.Worker();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));  // safety net only

            await WithEnv([("PSBASH_EXEC_SPILL_MS", "0"), .. env], async () =>
            {
                var query = worker.QueryAsync("echo x", cts.Token);
                await session.Slots[0].SecondCommand.Task.WaitAsync(TimeSpan.FromSeconds(30));  // went round: no clock race
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
                return 0;
            });

            Assert.Equal(0, session.Private.Commands);
            foreach (var host in session.Overflow) Assert.Equal(0, host.Commands);
        }
        finally { foreach (var t in held) t?.Dispose(); }
    }

    [Fact]
    public void OverflowTicket_AtMostMaxPerSession_FreedOnDispose()
    {
        string session = NewBase();
        using var first = ExecOverflowTicket.TryAcquire("unix", session, 2);
        var second = ExecOverflowTicket.TryAcquire("unix", session, 2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(ExecOverflowTicket.TryAcquire("unix", session, 2));
        using (var other = ExecOverflowTicket.TryAcquire("unix", NewBase(), 2)) Assert.NotNull(other);  // per session
        second!.Dispose();
        using var again = ExecOverflowTicket.TryAcquire("unix", session, 2);
        Assert.NotNull(again);
        Assert.Null(ExecOverflowTicket.TryAcquire("unix", session, 0));  // 0 = never overflow
    }
    [Theory]
    [InlineData("/r/host.sock", 1)]                                // inside the base host: slot 1 first
    [InlineData("/r/host.sock-x1", 2)]
    [InlineData("/r/host.sock-x5", 6)]
    [InlineData("/r/host.sock-x6", 7)]                               // past MaxExecSlots: a private host
    [InlineData("/r/psbash-slot-0123456789ABCDEF-x2", 1)]            // hashed slot is its own base: its first slot
    public void FirstSlotPastEnclosingHost_SkipsTheHostRunningOurParent(string enclosingHostEndpoint, int expected)
        => Assert.Equal(expected, IpcWorker.FirstSlotPastEnclosingHost(enclosingHostEndpoint));
}