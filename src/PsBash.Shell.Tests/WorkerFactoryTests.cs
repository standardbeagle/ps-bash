using System.Net.Sockets;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Acceptance tests for <see cref="WorkerFactory"/>. Verifies the worker
/// selection contract: PSBASH_HOST override, missing-binary error, and
/// IpcWorker connection against a live in-test server fixture.
/// </summary>
[Collection("WorkerFactoryEnvSerial")]
public class WorkerFactoryTests : IDisposable
{
    private readonly string? _origHost;
    private readonly string? _origSession;

    public WorkerFactoryTests()
    {
        _origHost = Environment.GetEnvironmentVariable("PSBASH_HOST");
        _origSession = Environment.GetEnvironmentVariable("PSBASH_SESSION_ID");
        Environment.SetEnvironmentVariable("PSBASH_HOST", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PSBASH_HOST", _origHost);
        Environment.SetEnvironmentVariable("PSBASH_SESSION_ID", _origSession);
    }

    private static IIpcTransport CreatePlatformTransport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash");
        Directory.CreateDirectory(dir);
        return new UnixSocketTransport(Path.Combine(dir, "wf-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock"));
    }

    [Fact]
    public async Task CreateAsync_PsbashHostEnvVarPointsAtNonexistent_Throws()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "ps-bash", "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".exe");
        Environment.SetEnvironmentVariable("PSBASH_HOST", bogus);

        await Assert.ThrowsAsync<HostUnavailableException>(
            async () => await WorkerFactory.CreateAsync());
    }

    [Fact]
    public async Task CreateAsync_HostBinaryReachable_ReturnsIpcWorker()
    {
        var sessionId = "wf-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Environment.SetEnvironmentVariable("PSBASH_SESSION_ID", sessionId);

        // Use this test assembly as a stand-in "host binary" — its only job
        // is to satisfy File.Exists(); the lock file shortcut means the factory
        // never executes it.
        var fakeHost = typeof(WorkerFactoryTests).Assembly.Location;
        Environment.SetEnvironmentVariable("PSBASH_HOST", fakeHost);

        var lockFile = HostLockFile.ForSession(sessionId);
        var transport = CreatePlatformTransport();

        await using var server = new HostServer(transport, (mode, write) =>
        {
            var cmd = Assert.IsType<Mode.Command>(mode);
            write(cmd.Body.TrimEnd('\n'));
            return 0;
        });
        await server.StartAsync();
        lockFile.Write(transport, pid: Environment.ProcessId);

        try
        {
            await using var w = await WorkerFactory.CreateAsync();
            Assert.IsType<IpcWorker>(w);

            var lines = new List<string>();
            w.OutputCallback = line => lines.Add(line);
            var rc = await w.ExecuteAsync("hello");
            Assert.Equal(0, rc);
            Assert.Single(lines);
            Assert.Equal("hello", lines[0]);
        }
        finally
        {
            lockFile.Delete();
        }
    }

    [Fact]
    public void ResolveHostBinary_PsbashHostOverride_ReturnsOverridePathEvenWhenMissing()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "ps-bash", "override-" + Guid.NewGuid().ToString("N") + ".exe");
        Environment.SetEnvironmentVariable("PSBASH_HOST", bogus);
        var resolved = WorkerFactory.ResolveHostBinary();
        Assert.Equal(bogus, resolved);
    }

    [Fact]
    public void ResolveHostBinary_NoOverride_ReturnsSideBySidePathOrNull()
    {
        Environment.SetEnvironmentVariable("PSBASH_HOST", null);
        var resolved = WorkerFactory.ResolveHostBinary();
        if (resolved is not null)
        {
            Assert.True(File.Exists(resolved), $"side-by-side resolution returned non-existent path: {resolved}");
            Assert.Equal(IpcWorker.GetHostBinaryName(), Path.GetFileName(resolved));
        }
    }

    /// <summary>
    /// Minimal in-test host that services one request per connection.
    /// </summary>
    internal sealed class HostServer : IAsyncDisposable
    {
        private readonly IIpcTransport _transport;
        private readonly Func<Mode, Action<string>, int> _handler;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private int _disposed;

        public HostServer(IIpcTransport transport, Func<Mode, Action<string>, int> handler)
        {
            _transport = transport;
            _handler = handler;
        }

        public async Task StartAsync()
        {
            await _transport.ListenAsync();
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Stream? client;
                try
                {
                    client = await _transport.AcceptAsync(ct);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                catch (IOException) { return; }

                _ = Task.Run(async () =>
                {
                    await using (client)
                    {
                        try
                        {
                            var mode = await HostProtocol.ReadRequestAsync(client, ct);
                            var lines = new List<string>();
                            int exit;
                            try { exit = _handler(mode, line => lines.Add(line)); }
                            catch { exit = 1; }
                            foreach (var l in lines)
                                await HostProtocol.WriteResponseLineAsync(client, l, ct);
                            await HostProtocol.WriteExitAsync(client, exit, ct);
                        }
                        catch (IOException) { }
                        catch (OperationCanceledException) { }
                    }
                }, ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts?.Cancel(); } catch { }
            if (_acceptLoop is not null)
            {
                try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            await _transport.DisposeAsync();
            _cts?.Dispose();
        }
    }
}
