using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Executor-slot spill (docs/specs/executor-slots.md): a host that is running another command refuses a new
/// one with <see cref="HostProtocol.ExecBusyExitCode"/> before STARTED, and the launcher re-sends the command to
/// the next slot endpoint. Scripted hosts speak the real wire protocol; nothing here spawns a process.
/// </summary>
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
        var prior = Environment.GetEnvironmentVariable("PSBASH_EXEC_SPILL_MS");
        Environment.SetEnvironmentVariable("PSBASH_EXEC_SPILL_MS", ms);
        try { return await body(); }
        finally { Environment.SetEnvironmentVariable("PSBASH_EXEC_SPILL_MS", prior); }
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
}