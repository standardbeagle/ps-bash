using System.Net.Sockets;
using System.Runtime.InteropServices;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// T06 acceptance tests for <see cref="IpcWorker"/>. Uses an in-test
/// <see cref="HostServer"/> fixture (built on the real
/// <see cref="IIpcTransport"/> + <see cref="HostProtocol"/> stack) to simulate
/// the host side, so round-trips run without a real ps-bash-host binary.
/// </summary>
public class IpcWorkerTests
{
    private static string TempLockPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "ps-bash",
            "test-ipc-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".lock");

    private static IIpcTransport CreatePlatformTransport()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new NamedPipeTransport("psbash-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash");
        Directory.CreateDirectory(dir);
        return new UnixSocketTransport(Path.Combine(dir, "test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock"));
    }

    [Fact]
    public async Task ExecuteAsync_RoundTripsEchoCommand_AgainstHostServerFixture()
    {
        // GREEN: end-to-end round-trip using HostProtocol on both sides.
        var lockPath = TempLockPath();
        var lockFile = new HostLockFile(lockPath);
        var transport = CreatePlatformTransport();

        await using var server = new HostServer(transport, (mode, write) =>
        {
            // The fixture echoes any Mode.Command body back as a single line.
            var cmd = Assert.IsType<Mode.Command>(mode);
            write(cmd.Body.TrimEnd('\n'));
            return 0;
        });
        await server.StartAsync();
        lockFile.Write(transport, pid: Environment.ProcessId);

        try
        {
            await using var worker = await IpcWorker.StartAsync(
                lockFile,
                hostBinaryPath: "/nonexistent/ps-bash-host", // not consulted on cache hit
                startupTimeout: TimeSpan.FromSeconds(2));

            var lines = new List<string>();
            worker.OutputCallback = line => lines.Add(line);
            var exitCode = await worker.ExecuteAsync("hello");

            Assert.Equal(0, exitCode);
            Assert.Single(lines);
            Assert.Equal("hello", lines[0]);
        }
        finally
        {
            lockFile.Delete();
        }
    }

    [Fact]
    public async Task QueryAsync_RestoresOutputCallback_AcrossCall()
    {
        var lockPath = TempLockPath();
        var lockFile = new HostLockFile(lockPath);
        var transport = CreatePlatformTransport();

        await using var server = new HostServer(transport, (mode, write) =>
        {
            var cmd = Assert.IsType<Mode.Command>(mode);
            write("Q:" + cmd.Body.TrimEnd('\n'));
            return 0;
        });
        await server.StartAsync();
        lockFile.Write(transport, pid: Environment.ProcessId);

        try
        {
            await using var worker = await IpcWorker.StartAsync(
                lockFile, "/nonexistent/ps-bash-host", TimeSpan.FromSeconds(2));

            var outerLines = new List<string>();
            worker.OutputCallback = line => outerLines.Add(line);

            var captured = await worker.QueryAsync("ping");
            Assert.Equal("Q:ping", captured);
            // Outer callback must be restored AND must not have been touched
            // by the QueryAsync call (which uses its own internal callback).
            Assert.Empty(outerLines);
            Assert.NotNull(worker.OutputCallback);
        }
        finally
        {
            lockFile.Delete();
        }
    }

    [Fact]
    public async Task StartAsync_WithStaleLock_AndMissingBinary_ThrowsHostUnavailable()
    {
        // RED: stale lock file pointing at a non-listening endpoint, plus
        // missing binary, must surface as HostUnavailableException — not
        // SocketException, not FileNotFoundException.
        var lockPath = TempLockPath();
        var lockFile = new HostLockFile(lockPath);
        try
        {
            // Write a lock file pointing at a transport with no listener.
            IIpcTransport stale = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new NamedPipeTransport("psbash-stale-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                : new UnixSocketTransport(Path.Combine(Path.GetTempPath(), "ps-bash", "stale-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock"));
            lockFile.Write(stale, pid: 999_999);
            Assert.True(File.Exists(lockFile.Path));

            await Assert.ThrowsAsync<HostUnavailableException>(async () =>
            {
                await IpcWorker.StartAsync(
                    lockFile,
                    hostBinaryPath: Path.Combine(Path.GetTempPath(), "ps-bash", "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".exe"),
                    startupTimeout: TimeSpan.FromMilliseconds(200));
            });

            // Stale lock file was purged on the way through.
            Assert.False(File.Exists(lockFile.Path));
        }
        finally
        {
            lockFile.Delete();
        }
    }

    [Fact]
    public async Task StartAsync_NoLockFile_AndMissingBinary_ThrowsHostUnavailable()
    {
        // RED: no lock file + no binary → HostUnavailableException, not
        // FileNotFoundException leaking from File.Exists check or generic
        // exception from spawn path.
        var lockPath = TempLockPath();
        var lockFile = new HostLockFile(lockPath);
        // Don't write the lock file — simulate a fresh user with no host.
        Assert.False(File.Exists(lockFile.Path));

        await Assert.ThrowsAsync<HostUnavailableException>(async () =>
        {
            await IpcWorker.StartAsync(
                lockFile,
                hostBinaryPath: Path.Combine(Path.GetTempPath(), "ps-bash", "missing-binary-" + Guid.NewGuid().ToString("N") + ".exe"),
                startupTimeout: TimeSpan.FromMilliseconds(200));
        });
    }

    [Fact]
    public async Task ExecuteAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var lockPath = TempLockPath();
        var lockFile = new HostLockFile(lockPath);
        var transport = CreatePlatformTransport();

        await using var server = new HostServer(transport, (mode, write) => 0);
        await server.StartAsync();
        lockFile.Write(transport, pid: Environment.ProcessId);

        IpcWorker worker;
        try
        {
            worker = await IpcWorker.StartAsync(lockFile, "/nonexistent", TimeSpan.FromSeconds(2));
        }
        finally
        {
            lockFile.Delete();
        }

        await worker.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.ExecuteAsync("anything"));
        Assert.True(worker.HasExited);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesNonZeroExitCode()
    {
        var lockPath = TempLockPath();
        var lockFile = new HostLockFile(lockPath);
        var transport = CreatePlatformTransport();

        await using var server = new HostServer(transport, (mode, write) =>
        {
            write("error: nope");
            return 42;
        });
        await server.StartAsync();
        lockFile.Write(transport, pid: Environment.ProcessId);

        try
        {
            await using var worker = await IpcWorker.StartAsync(
                lockFile, "/nonexistent", TimeSpan.FromSeconds(2));

            var lines = new List<string>();
            worker.OutputCallback = line => lines.Add(line);
            var exit = await worker.ExecuteAsync("doomed");

            Assert.Equal(42, exit);
            Assert.Single(lines);
            Assert.Equal("error: nope", lines[0]);
        }
        finally
        {
            lockFile.Delete();
        }
    }

    /// <summary>
    /// Minimal in-test host that listens on the supplied transport, reads one
    /// <see cref="HostProtocol"/> request per accepted connection, invokes a
    /// caller-supplied handler, and emits the response with the framed EXIT
    /// sentinel. Lives in-process so tests don't depend on a real
    /// ps-bash-host binary.
    /// </summary>
    private sealed class HostServer : IAsyncDisposable
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
                            try
                            {
                                exit = _handler(mode, line => lines.Add(line));
                            }
                            catch
                            {
                                exit = 1;
                            }
                            foreach (var l in lines)
                                await HostProtocol.WriteResponseLineAsync(client, l, ct);
                            await HostProtocol.WriteExitAsync(client, exit, ct);
                        }
                        catch (IOException) { /* client hung up */ }
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
