using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime;

/// <summary>
/// AOT-safe launcher-side <see cref="IWorker"/> that proxies command execution
/// to an out-of-process <c>ps-bash-host</c> over a duplex IPC transport
/// (Unix socket on POSIX, named pipe on Windows). Each call opens a fresh
/// connection, writes one <see cref="Mode"/> request via
/// <see cref="HostProtocol.WriteRequestAsync"/>, then drains the response
/// stream via <see cref="HostProtocol.ReadResponseAsync"/> until the host
/// emits <c>&lt;&lt;&lt;EXIT:N&gt;&gt;&gt;</c>.
/// </summary>
/// <remarks>
/// <para>Phase-1 protocol: one request per connection. Cancellation is
/// signalled by closing the connection (no in-band cancel message).
/// ExecuteScriptAsync is intentionally not implemented in T06
/// (deferred to T09).</para>
///
/// <para>Startup contract: <see cref="StartAsync"/> reads the session lock
/// file via <see cref="HostLockFile.ReadOrPurgeAsync"/>. If the lock file is
/// absent or stale, it spawns <c>ps-bash-host</c> and polls for the lock file
/// every 50&#160;ms for up to 5 seconds. Spawn uses
/// <see cref="ProcessStartInfo.UseShellExecute"/>=<c>false</c>,
/// <see cref="ProcessStartInfo.CreateNoWindow"/>=<c>true</c>, no stdio
/// inheritance — the host binary detaches and writes its lock file from
/// inside its own startup handshake.</para>
///
/// <para>Process spawn contract (per project memory): every spawn enters a
/// <c>finally</c> block that calls <see cref="Process.Kill(bool)"/> with
/// <c>entireProcessTree:true</c> if the startup poll fails, so a misbehaving
/// host cannot leak. We deliberately do not store or own the spawned
/// <see cref="Process"/> after a successful startup — the host outlives the
/// launcher and is reaped by the OS.</para>
///
/// <para>AOT cleanliness: this type uses no reflection, no dynamic
/// serialization, and no <c>System.Reflection.Emit</c>. All payloads are
/// hand-marshalled by <see cref="HostProtocol"/>.</para>
/// </remarks>
public sealed class IpcWorker : IWorker
{
    private readonly HostLockFile _lockFile;
    private readonly string _hostBinaryPath;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _startupPollInterval;
    private HostLockEntry _entry;
    private int _disposed;

    /// <inheritdoc />
    public Action<string>? OutputCallback { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// IpcWorker has no long-lived process to track; <c>HasExited</c> reflects
    /// dispose state only. Per-call connection failures surface as exceptions
    /// from <see cref="ExecuteAsync"/> / <see cref="QueryAsync"/>.
    /// </remarks>
    public bool HasExited => _disposed != 0;

    private IpcWorker(HostLockFile lockFile, string hostBinaryPath, TimeSpan startupTimeout, TimeSpan startupPollInterval)
    {
        _lockFile = lockFile;
        _hostBinaryPath = hostBinaryPath;
        _startupTimeout = startupTimeout;
        _startupPollInterval = startupPollInterval;
        _entry = default;
    }

    /// <summary>
    /// Resolve the host endpoint and ensure a host is reachable. Reads the
    /// lock file (purging it if stale) and spawns <c>ps-bash-host</c> on
    /// cache miss. Throws <see cref="HostUnavailableException"/> if the host
    /// binary is missing or the spawn fails to advertise a lock file within
    /// the startup timeout.
    /// </summary>
    /// <param name="lockFile">Discovery lock file (typically built from
    /// <see cref="HostLockFile.ForSession(string, string?)"/>).</param>
    /// <param name="hostBinaryPath">Absolute path to <c>ps-bash-host</c> (or
    /// <c>ps-bash-host.exe</c>) used when a spawn is needed.</param>
    /// <param name="startupTimeout">How long to poll for the lock file after
    /// spawning. Default 5 s. Honors <c>PSBASH_TIMEOUT</c> env var when
    /// caller passes <see langword="null"/>.</param>
    /// <param name="startupPollInterval">Interval between lock-file existence
    /// checks. Default 50 ms.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<IpcWorker> StartAsync(
        HostLockFile lockFile,
        string hostBinaryPath,
        TimeSpan? startupTimeout = null,
        TimeSpan? startupPollInterval = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lockFile);
        ArgumentNullException.ThrowIfNull(hostBinaryPath);

        var timeout = startupTimeout ?? GetStartupTimeout();
        var poll = startupPollInterval ?? TimeSpan.FromMilliseconds(50);
        var worker = new IpcWorker(lockFile, hostBinaryPath, timeout, poll);
        worker._entry = await worker.ResolveOrSpawnAsync(ct).ConfigureAwait(false);
        return worker;
    }

    private async Task<HostLockEntry> ResolveOrSpawnAsync(CancellationToken ct)
    {
        // 1) Try the existing lock file. ReadOrPurgeAsync probes the endpoint
        //    and deletes the file if no listener answers within 500 ms. A
        //    successful read means a live host is listening.
        try
        {
            return await _lockFile.ReadOrPurgeAsync(ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { /* no host advertised — spawn one */ }
        catch (SocketException) { /* stale lock purged — spawn one */ }
        catch (IOException) { /* lock file vanished mid-read — spawn one */ }

        // 2) Spawn the host binary. Verify the binary exists first so the
        //    HostUnavailableException carries the right cause.
        if (!File.Exists(_hostBinaryPath))
        {
            throw new HostUnavailableException(
                $"ps-bash-host binary not found at '{_hostBinaryPath}'. Cannot spawn host.");
        }

        return await SpawnAndPollAsync(ct).ConfigureAwait(false);
    }

    private async Task<HostLockEntry> SpawnAndPollAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _hostBinaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        // Pass the parent pid so the host can install its own death-watcher.
        psi.Environment["PSBASH_HOST_PARENT_PID"] = Environment.ProcessId.ToString();

        Process? proc = null;
        bool spawnSucceeded = false;
        try
        {
            proc = Process.Start(psi)
                ?? throw new HostUnavailableException(
                    $"Process.Start returned null for '{_hostBinaryPath}'.");

            // Poll for the lock file every _startupPollInterval until either
            // the process exits early (failure) or the file appears (success).
            var deadline = DateTime.UtcNow + _startupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (proc.HasExited)
                {
                    throw new HostUnavailableException(
                        $"ps-bash-host exited prematurely with code {proc.ExitCode} before advertising a lock file.");
                }

                if (File.Exists(_lockFile.Path))
                {
                    try
                    {
                        var entry = await _lockFile.ReadOrPurgeAsync(ct).ConfigureAwait(false);
                        spawnSucceeded = true;
                        return entry;
                    }
                    catch (SocketException)
                    {
                        // Host wrote the file but isn't listening yet — keep polling.
                    }
                    catch (FormatException)
                    {
                        // Partial write — keep polling until the file stabilizes.
                    }
                }

                try
                {
                    await Task.Delay(_startupPollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            throw new HostUnavailableException(
                $"ps-bash-host did not advertise a lock file within {_startupTimeout.TotalSeconds:0.##}s.");
        }
        finally
        {
            // Process-spawn contract: kill the entire tree if startup failed.
            // The host outlives a successful spawn, so we only kill on failure
            // paths. We always Dispose() the handle either way.
            if (!spawnSucceeded && proc is not null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { /* best effort */ }
            }
            proc?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(command);
        return await SendRequestAsync(new Mode.Command(command), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> QueryAsync(string expression, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(expression);

        var lines = new List<string>();
        var prevCallback = OutputCallback;
        OutputCallback = line => lines.Add(line);
        try
        {
            await SendRequestAsync(new Mode.Command(expression), ct).ConfigureAwait(false);
            return string.Join('\n', lines);
        }
        finally
        {
            OutputCallback = prevCallback;
        }
    }

    private async Task<int> SendRequestAsync(Mode mode, CancellationToken ct)
    {
        var timeout = GetCallTimeout();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        await using var transport = OpenTransport(_entry);
        Stream stream;
        try
        {
            stream = await transport.ConnectAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ps-bash: connection to host timed out after {timeout.TotalSeconds:0.##}s");
        }

        await using (stream)
        {
            await HostProtocol.WriteRequestAsync(stream, mode, linked.Token).ConfigureAwait(false);
            return await HostProtocol.ReadResponseAsync(
                stream,
                line =>
                {
                    if (OutputCallback is { } cb) cb(line);
                    else Console.WriteLine(line);
                },
                linked.Token).ConfigureAwait(false);
        }
    }

    private static IIpcTransport OpenTransport(HostLockEntry entry) => entry.Scheme switch
    {
        "unix" => new UnixSocketTransport(entry.Endpoint),
        "pipe" => new NamedPipeTransport(entry.Endpoint),
        _ => throw new InvalidOperationException(
            $"Unknown lock-file scheme '{entry.Scheme}'."),
    };

    private static TimeSpan GetStartupTimeout()
    {
        // Mirror PwshWorker's PSBASH_TIMEOUT semantics for parity, but cap the
        // host-startup wait separately at 5s by default — startup should be
        // fast and a long wait masks a hung host binary.
        var envValue = Environment.GetEnvironmentVariable("PSBASH_TIMEOUT");
        if (envValue is not null && int.TryParse(envValue, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(5);
    }

    private static TimeSpan GetCallTimeout()
    {
        var envValue = Environment.GetEnvironmentVariable("PSBASH_TIMEOUT");
        if (envValue is not null && int.TryParse(envValue, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(120);
    }

    /// <summary>
    /// Resolve the conventional ps-bash-host binary name for the current
    /// platform. Callers typically combine this with the launcher's
    /// <see cref="AppContext.BaseDirectory"/> to locate the binary alongside
    /// the launcher.
    /// </summary>
    public static string GetHostBinaryName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ps-bash-host.exe"
            : "ps-bash-host";

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        // No long-lived state to release — connections are per-call.
        return ValueTask.CompletedTask;
    }
}
