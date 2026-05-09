using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime;

/// <summary>
/// AOT-safe launcher-side <see cref="IWorker"/> that proxies command execution
/// to an out-of-process <c>ps-bash-host</c> over a duplex IPC transport
/// (Unix socket on POSIX, named pipe on Windows).
/// </summary>
/// <remarks>
/// <para>Discovery is socket-direct: <see cref="StartAsync"/> attempts to
/// connect to the canonical endpoint. If the connect fails, the host binary
/// is spawned and connect is retried until the socket accepts or the timeout
/// elapses. There is no lock file, no session id, and no fallback to an
/// SDK host worker — failure to reach the host throws
/// <see cref="HostUnavailableException"/>.</para>
/// </remarks>
public sealed class IpcWorker : IWorker
{
    private readonly string _scheme;
    private readonly string _endpoint;
    private readonly string _hostBinaryPath;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _startupPollInterval;
    private int _disposed;

    public Action<string>? OutputCallback { get; set; }

    public bool HasExited => _disposed != 0;

    private IpcWorker(string scheme, string endpoint, string hostBinaryPath, TimeSpan timeout, TimeSpan poll)
    {
        _scheme = scheme;
        _endpoint = endpoint;
        _hostBinaryPath = hostBinaryPath;
        _startupTimeout = timeout;
        _startupPollInterval = poll;
    }

    /// <summary>
    /// Connect to the running host, spawning <c>ps-bash-host</c> if no listener
    /// answers. Throws <see cref="HostUnavailableException"/> if the binary is
    /// missing or fails to come up within the startup timeout.
    /// </summary>
    public static async Task<IpcWorker> StartAsync(
        string hostBinaryPath,
        TimeSpan? startupTimeout = null,
        TimeSpan? startupPollInterval = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hostBinaryPath);

        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        var timeout = startupTimeout ?? GetStartupTimeout();
        var poll = startupPollInterval ?? TimeSpan.FromMilliseconds(50);
        var worker = new IpcWorker(scheme, endpoint, hostBinaryPath, timeout, poll);
        await worker.EnsureHostReachableAsync(ct).ConfigureAwait(false);
        return worker;
    }

    private async Task EnsureHostReachableAsync(CancellationToken ct)
    {
        // 1) Probe the canonical socket. If a compatible host answers the health
        // handshake, reuse it.
        if (await WaitForHealthyAsync(ct).ConfigureAwait(false))
            return;

        // 2) A socket may exist but belong to a stale, incompatible, or wedged
        // host. Unlink it before starting a replacement so the new host can bind.
        IpcTransportFactory.RetireEndpoint(_scheme, _endpoint);

        // 3) No healthy host running — spawn one. Verify the binary first so the
        //    exception names the actual problem.
        if (!File.Exists(_hostBinaryPath))
            throw new HostUnavailableException(
                $"ps-bash-host binary not found at '{_hostBinaryPath}'. Cannot start host.");

        await SpawnAndWaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> WaitForHealthyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _startupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await CheckHealthAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            if (state == HostHealthState.Healthy) return true;
            if (state == HostHealthState.Unhealthy) return false;

            try { await Task.Delay(_startupPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        return false;
    }

    private async Task<HostHealthState> CheckHealthAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(timeout);
            await using var probe = NewTransport();
            using var stream = await probe.ConnectAsync(probeCts.Token).ConfigureAwait(false);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Health(), probeCts.Token).ConfigureAwait(false);

            var lines = new List<string>(capacity: 1);
            var exitCode = await HostProtocol.ReadResponseAsync(
                stream,
                line => lines.Add(line),
                probeCts.Token).ConfigureAwait(false);

            if (exitCode == 0 && lines.Count == 1 && lines[0] == HostProtocol.HealthPayload)
                return HostHealthState.Healthy;
            if (exitCode == HostProtocol.HealthStartingExitCode
                && lines.Count == 1
                && lines[0] == HostProtocol.HealthStartingPayload)
                return HostHealthState.Starting;
            return HostHealthState.Unhealthy;
        }
        catch (SocketException) { return HostHealthState.Unhealthy; }
        catch (IOException) { return HostHealthState.Unhealthy; }
        catch (TimeoutException) { return HostHealthState.Unhealthy; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return HostHealthState.Unhealthy; }
    }

    private async Task SpawnAndWaitAsync(CancellationToken ct)
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
        psi.Environment["PSBASH_HOST_PARENT_PID"] = Environment.ProcessId.ToString();

        Process? proc = null;
        bool spawnSucceeded = false;
        try
        {
            proc = Process.Start(psi)
                ?? throw new HostUnavailableException(
                    $"Process.Start returned null for '{_hostBinaryPath}'.");

            var deadline = DateTime.UtcNow + _startupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (proc.HasExited)
                    throw new HostUnavailableException(
                        $"ps-bash-host exited prematurely with code {proc.ExitCode}.");

                if (await CheckHealthAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false)
                    == HostHealthState.Healthy)
                {
                    spawnSucceeded = true;
                    return;
                }

                try { await Task.Delay(_startupPollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }

            throw new HostUnavailableException(
                $"ps-bash-host did not accept connections within {_startupTimeout.TotalSeconds:0.##}s.");
        }
        finally
        {
            if (!spawnSucceeded && proc is not null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }
            proc?.Dispose();
        }
    }

    public async Task<int> ExecuteAsync(string command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(command);
        return await SendRequestAsync(new Mode.Command(command), ct).ConfigureAwait(false);
    }

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

        await using var transport = NewTransport();
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

    private IIpcTransport NewTransport() => _scheme switch
    {
        "unix" => new UnixSocketTransport(_endpoint),
        "pipe" => new NamedPipeTransport(_endpoint),
        _ => throw new InvalidOperationException($"Unknown transport scheme '{_scheme}'."),
    };

    private static TimeSpan GetStartupTimeout()
    {
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

    public static string GetHostBinaryName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ps-bash-host.exe"
            : "ps-bash-host";

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        return ValueTask.CompletedTask;
    }

    private enum HostHealthState
    {
        Unhealthy,
        Starting,
        Healthy,
    }
}
