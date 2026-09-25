using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// R05 acceptance tests: the launcher may retry a pre-output transport reset
/// ONLY when the host never acknowledged execution-start. Before R05 the
/// launcher inferred "not executed yet" from "no output frame received" — false
/// for any silent command (`echo x &gt;&gt; f`, `rm`, `mkdir`), so a genuine
/// reset after such a command finished re-ran it. These tests drive
/// <see cref="IpcWorker"/> against a scripted in-process host that speaks the
/// real wire protocol over a real transport and counts executions (the side
/// effect).
/// </summary>
public sealed class IpcWorkerStartedAckTests
{
    private const string StartedFrame = "<<<STARTED>>>";

    private enum ResetPoint
    {
        /// <summary>Host runs the command (side effect), acknowledges STARTED, then the transport resets.</summary>
        AfterStarted,

        /// <summary>Host resets before executing anything and before acknowledging.</summary>
        BeforeStarted,
    }

    [Fact]
    public async Task ResetAfterStarted_DoesNotReRun_AssertingASingleSideEffect()
    {
        await using var host = await ScriptedHost.StartAsync(ResetPoint.AfterStarted);
        await using var worker = IpcWorker.ConnectForTest(host.Scheme, host.Endpoint, host.FakeHostBinary);

        await Assert.ThrowsAsync<IOException>(() => worker.QueryAsync("echo x >> f"));

        // The command already ran on the host; a retry would double its side
        // effect. Exactly one execution proves the reset was NOT retried.
        Assert.Equal(1, Volatile.Read(ref host.Executions));
        Assert.Equal(1, host.CommandConnections);
    }

    [Fact]
    public async Task ResetBeforeStarted_IsRetried_StillAssertingASingleSideEffect()
    {
        await using var host = await ScriptedHost.StartAsync(ResetPoint.BeforeStarted);
        await using var worker = IpcWorker.ConnectForTest(host.Scheme, host.Endpoint, host.FakeHostBinary);

        var output = await worker.QueryAsync("echo x >> f");

        // The first attempt reset before executing, so the safe retry is
        // allowed; it executes once and succeeds.
        Assert.Equal(1, Volatile.Read(ref host.Executions));
        Assert.Contains("ok", output);
    }

    /// <summary>
    /// Minimal scripted host over a real <see cref="IIpcTransport"/>: it answers
    /// framed Health probes, and on the first Command connection either resets
    /// before executing (<see cref="ResetPoint.BeforeStarted"/>) or executes,
    /// emits the STARTED acknowledgement, then resets
    /// (<see cref="ResetPoint.AfterStarted"/>). A later Command connection
    /// (which only a buggy launcher would make) executes and completes
    /// normally, so a regression fails on the execution count instead of
    /// hanging.
    /// </summary>
    private sealed class ScriptedHost : IAsyncDisposable
    {
        private readonly IIpcTransport _transport;
        private readonly ResetPoint _resetPoint;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _commandConnections;

        private ScriptedHost(string scheme, string endpoint, ResetPoint resetPoint)
        {
            Scheme = scheme;
            Endpoint = endpoint;
            _resetPoint = resetPoint;
            _transport = scheme == "unix"
                ? new UnixSocketTransport(endpoint)
                : new NamedPipeTransport(endpoint);
        }

        public string Scheme { get; }
        public string Endpoint { get; }
        public string FakeHostBinary { get; } = Path.Combine(Path.GetTempPath(), "ps-bash-host-fake");
        public int Executions;
        public int CommandConnections => Volatile.Read(ref _commandConnections);

        public static async Task<ScriptedHost> StartAsync(ResetPoint resetPoint)
        {
            var unique = Guid.NewGuid().ToString("N");
            var (scheme, endpoint) = OperatingSystem.IsWindows()
                ? ("pipe", $"psbash-r05-test-{unique}")
                : ("unix", Path.Combine(Path.GetTempPath(), "ps-bash", $"r05-{unique}.sock"));
            if (scheme == "unix")
                Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);

            var host = new ScriptedHost(scheme, endpoint, resetPoint);
            await host._transport.ListenAsync(host._cts.Token);
            host._loop = Task.Run(host.AcceptLoopAsync);
            return host;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Stream stream;
                try
                {
                    stream = await _transport.AcceptAsync(_cts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch { return; }
                _ = HandleConnectionAsync(stream);
            }
        }

        private async Task HandleConnectionAsync(Stream stream)
        {
            try
            {
                var mode = await HostProtocol.ReadRequestAsync(stream, _cts.Token);
                switch (mode)
                {
                    case Mode.Health:
                        await HostProtocol.WriteResponseLineAsync(stream, HostProtocol.HealthPayload, _cts.Token);
                        await HostProtocol.WriteExitAsync(stream, 0, _cts.Token);
                        break;

                    case Mode.Command:
                        var first = Interlocked.Increment(ref _commandConnections) == 1;
                        if (first && _resetPoint == ResetPoint.BeforeStarted)
                            break; // reset before executing or acknowledging

                        Interlocked.Increment(ref Executions);
                        if (first && _resetPoint == ResetPoint.AfterStarted)
                        {
                            // The silent command has already run; acknowledge, then
                            // drop the transport with no output and no EXIT.
                            await HostProtocol.WriteStartedAsync(stream, _cts.Token);
                            break;
                        }

                        await HostProtocol.WriteStartedAsync(stream, _cts.Token);
                        await HostProtocol.WriteResponseLineAsync(stream, "ok", _cts.Token);
                        await HostProtocol.WriteExitAsync(stream, 0, _cts.Token);
                        break;

                    default:
                        await HostProtocol.WriteExitAsync(stream, 0, _cts.Token);
                        break;
                }
            }
            catch { /* scripted host: any failure just ends the connection */ }
            finally
            {
                try { await stream.DisposeAsync(); } catch { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            }
            try { await _transport.DisposeAsync(); } catch { }
            _cts.Dispose();
        }
    }
}
