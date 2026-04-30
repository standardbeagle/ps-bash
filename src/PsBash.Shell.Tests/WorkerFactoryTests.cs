using System.Net.Sockets;
using System.Runtime.InteropServices;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// T07 acceptance tests for <see cref="WorkerFactory"/>. Verifies the worker
/// selection contract: PSBASH_HOST override, PSBASH_DISABLE_HOST opt-out,
/// missing-binary fallback, and HostUnavailableException soft-fallback.
/// </summary>
/// <remarks>
/// Tests use an in-test <see cref="HostServer"/> fixture against the real
/// IPC transports (named pipe on Windows, Unix socket on POSIX) so the
/// IpcWorker selection branch can be exercised without a real ps-bash-host
/// binary. Tests that exercise PwshWorker spawn use the real PwshLocator
/// resolution and are tagged with <see cref="SkippableFactAttribute"/> so
/// they no-op when pwsh is not on the test PATH.
/// </remarks>
[Collection("WorkerFactoryEnvSerial")]
public class WorkerFactoryTests : IDisposable
{
    private readonly string? _origDisable;
    private readonly string? _origHost;
    private readonly string? _origSession;

    public WorkerFactoryTests()
    {
        _origDisable = Environment.GetEnvironmentVariable("PSBASH_DISABLE_HOST");
        _origHost = Environment.GetEnvironmentVariable("PSBASH_HOST");
        _origSession = Environment.GetEnvironmentVariable("PSBASH_SESSION_ID");
        Environment.SetEnvironmentVariable("PSBASH_DISABLE_HOST", null);
        Environment.SetEnvironmentVariable("PSBASH_HOST", null);
        WorkerFactory.ResetWarningStateForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PSBASH_DISABLE_HOST", _origDisable);
        Environment.SetEnvironmentVariable("PSBASH_HOST", _origHost);
        Environment.SetEnvironmentVariable("PSBASH_SESSION_ID", _origSession);
        WorkerFactory.ResetWarningStateForTests();
    }

    private static string PwshPathOrSkip()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException ex) { throw new SkipException("pwsh not available: " + ex.Message); }
    }

    private static IIpcTransport CreatePlatformTransport()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new NamedPipeTransport("psbash-wf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash");
        Directory.CreateDirectory(dir);
        return new UnixSocketTransport(Path.Combine(dir, "wf-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock"));
    }

    [SkippableFact]
    public async Task CreateAsync_ForcePwsh_ReturnsPwshWorker()
    {
        var pwsh = PwshPathOrSkip();
        await using var w = await WorkerFactory.CreateAsync(pwsh, forcePwsh: true);
        Assert.IsType<PwshWorker>(w);
    }

    [SkippableFact]
    public async Task CreateAsync_DisableHostEnvVar_ReturnsPwshWorker()
    {
        var pwsh = PwshPathOrSkip();
        Environment.SetEnvironmentVariable("PSBASH_DISABLE_HOST", "1");
        // Even if PSBASH_HOST points somewhere, the disable flag wins.
        Environment.SetEnvironmentVariable("PSBASH_HOST", "/nonexistent/should-be-ignored");

        await using var w = await WorkerFactory.CreateAsync(pwsh);
        Assert.IsType<PwshWorker>(w);
    }

    [SkippableFact]
    public async Task CreateAsync_PsbashHostEnvVarPointsAtNonexistent_FallsBackToPwshWithWarning()
    {
        var pwsh = PwshPathOrSkip();
        var bogus = Path.Combine(Path.GetTempPath(), "ps-bash", "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".exe");
        Environment.SetEnvironmentVariable("PSBASH_HOST", bogus);

        var stderr = new StringWriter();
        var origErr = Console.Error;
        Console.SetError(stderr);
        try
        {
            await using var w = await WorkerFactory.CreateAsync(pwsh);
            Assert.IsType<PwshWorker>(w);
        }
        finally { Console.SetError(origErr); }

        var captured = stderr.ToString();
        Assert.Contains("host unavailable", captured, StringComparison.Ordinal);
        Assert.Contains(bogus, captured, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task CreateAsync_PsbashHostNonexistent_WarnsOnlyOnce()
    {
        var pwsh = PwshPathOrSkip();
        var bogus = Path.Combine(Path.GetTempPath(), "ps-bash", "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".exe");
        Environment.SetEnvironmentVariable("PSBASH_HOST", bogus);

        var stderr = new StringWriter();
        var origErr = Console.Error;
        Console.SetError(stderr);
        try
        {
            await using (await WorkerFactory.CreateAsync(pwsh)) { }
            await using (await WorkerFactory.CreateAsync(pwsh)) { }
            await using (await WorkerFactory.CreateAsync(pwsh)) { }
        }
        finally { Console.SetError(origErr); }

        var captured = stderr.ToString();
        var occurrences = 0;
        var idx = 0;
        while ((idx = captured.IndexOf("host unavailable", idx, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            idx += 1;
        }
        Assert.Equal(1, occurrences);
    }

    [SkippableFact]
    public async Task CreateAsync_HostBinaryReachable_ReturnsIpcWorker()
    {
        // When PSBASH_HOST points to an existing file AND the lock-file
        // discovery picks up a live host, factory must return IpcWorker.
        // We stand up a HostServer fixture and pre-write the lock file so the
        // factory's IpcWorker.StartAsync hits a cache hit and never tries to
        // spawn the binary.
        var pwsh = PwshPathOrSkip();
        var sessionId = "wf-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Environment.SetEnvironmentVariable("PSBASH_SESSION_ID", sessionId);

        // Use *this* test assembly as a stand-in "host binary" — its only job
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
            await using var w = await WorkerFactory.CreateAsync(pwsh);
            Assert.IsType<IpcWorker>(w);

            // Sanity-check: the worker actually round-trips through the host fixture.
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

    [SkippableFact]
    public async Task CreateAsync_DisposeAsync_CompletesWithinTimeout()
    {
        // No-host path: PwshWorker disposal must not hang. We bound the
        // dispose at 5 s — far above the runtime worst case but tight enough
        // to catch a regression where DisposeAsync waits on a dead process.
        var pwsh = PwshPathOrSkip();
        Environment.SetEnvironmentVariable("PSBASH_DISABLE_HOST", "1");

        var w = await WorkerFactory.CreateAsync(pwsh);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var disposeTask = w.DisposeAsync().AsTask();
        var winner = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        sw.Stop();

        Assert.True(ReferenceEquals(winner, disposeTask),
            $"DisposeAsync did not complete within 5s (elapsed {sw.ElapsedMilliseconds} ms).");
        await disposeTask; // surface any exception
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
        // Either null (no host shipped) or an existing path next to the test assembly.
        if (resolved is not null)
        {
            Assert.True(File.Exists(resolved), $"side-by-side resolution returned non-existent path: {resolved}");
            Assert.Equal(IpcWorker.GetHostBinaryName(), Path.GetFileName(resolved));
        }
    }

    /// <summary>
    /// Minimal in-test host: listens on the supplied transport, services one
    /// request per connection, and emits the framed EXIT sentinel. Same shape
    /// as the fixture inside Core.Tests' IpcWorkerTests, kept private here so
    /// Shell.Tests stays decoupled from internals of the other assembly.
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
