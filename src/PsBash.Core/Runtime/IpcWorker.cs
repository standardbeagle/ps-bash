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
        // handshake, reuse it. If the host answers but is obsolete (protocol
        // or build mismatch), ask it to retire gracefully before we replace.
        var initial = await CheckHealthAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
        if (initial == HostHealthState.Healthy)
        {
            var healthyMetadata = HostMetadata.TryRead(_scheme, _endpoint);
            if (HostOwnership.MetadataMatchesLauncher(
                    healthyMetadata,
                    _hostBinaryPath,
                    HostProtocol.BuildIdentity))
                return;

            initial = HostHealthState.Obsolete;
        }
        if (initial == HostHealthState.Starting)
        {
            if (await WaitForHealthyAsync(ct).ConfigureAwait(false)) return;
        }

        // 2) Classify what cleanup we are allowed to do BEFORE touching anything.
        // The metadata sidecar is the ownership proof; without it, only the
        // endpoint artifact may be unlinked, never a process. Per
        // docs/specs/host-lifecycle-contract.md.
        var metadata = HostMetadata.TryRead(_scheme, _endpoint);
        var decision = HostOwnership.Classify(metadata, Environment.UserName, out var unsafeReason);
        if (decision == HostOwnership.CleanupDecision.UnsafeToTouch)
            throw new HostUnavailableException(
                $"Cannot replace ps-bash-host at {_scheme}:{_endpoint} — {unsafeReason}.");

        if (initial == HostHealthState.Obsolete)
        {
            // The current host responded — give it a chance to drain and exit
            // cleanly before we touch the endpoint or spawn a replacement. Bound
            // the wait so an unresponsive host cannot block startup forever.
            await TryRequestGracefulShutdownAsync(GetShutdownDeadline(), ct).ConfigureAwait(false);
        }

        // 3) Re-probe after graceful attempt. If a process is still answering
        // and we have ownership, escalate to kill. If a process is still
        // answering and we do NOT have ownership, refuse cleanup so we never
        // leave a recycled-PID footgun for the next launcher run.
        var post = await CheckHealthAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        if (post != HostHealthState.Unhealthy)
        {
            if (decision == HostOwnership.CleanupDecision.SafeProcessShutdown && metadata is not null)
            {
                TryKillRecordedHost(metadata);
            }
            else
            {
                // initial wasn't Healthy (we'd have returned); host is alive but
                // not gracefully retiring and we cannot prove ownership. Surface
                // rather than risk a wrong-process kill.
                throw new HostUnavailableException(
                    $"ps-bash-host at {_scheme}:{_endpoint} is alive but did not shut down gracefully " +
                    $"and has no ownership proof to authorize a process kill.");
            }
        }

        // 4) Endpoint and sidecar artifacts may now be cleaned. Unix unlinks the
        // socket file; Windows named pipes have no kernel-namespace file to
        // remove (the pipe disappears when the previous server's handle closed).
        // The sidecar is removed in either case so the next launcher does not
        // see ghost ownership info.
        IpcTransportFactory.RetireEndpoint(_scheme, _endpoint);
        HostMetadata.Remove(_scheme, _endpoint);

        // 5) No healthy host running — spawn one. Verify the binary first so the
        //    exception names the actual problem.
        if (!File.Exists(_hostBinaryPath))
            throw new HostUnavailableException(
                $"ps-bash-host binary not found at '{_hostBinaryPath}'. Cannot start host.");

        await SpawnAndWaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort termination of a host process whose ownership has already
    /// been classified as <see cref="HostOwnership.CleanupDecision.SafeProcessShutdown"/>.
    /// Re-verifies pid liveness and executable match immediately before kill so
    /// a PID reused between classification and termination cannot be hit.
    /// </summary>
    private static void TryKillRecordedHost(HostMetadata metadata)
    {
        var (alive, exe) = HostOwnership.ProbeProcess(metadata.Pid);
        if (!alive) return;
        if (!string.IsNullOrEmpty(exe) &&
            !string.Equals(
                Path.GetFullPath(exe),
                Path.GetFullPath(metadata.ExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            // PID was reused between Classify and now — abandon kill silently;
            // the next iteration's Classify will surface UnsafeToTouch.
            return;
        }

        try
        {
            using var proc = Process.GetProcessById(metadata.Pid);
            proc.Kill(entireProcessTree: true);
            // Bounded wait so a wedged kernel handle doesn't stall startup; the
            // outer loop times out via _startupTimeout regardless.
            try { proc.WaitForExit(2_000); } catch { }
        }
        catch (ArgumentException) { /* exited between probe and kill */ }
        catch (InvalidOperationException) { /* same */ }
        catch (System.ComponentModel.Win32Exception) { /* permission denied; surfaces as still-listening on next loop */ }
    }

    /// <summary>
    /// Send a <see cref="Mode.Shutdown"/> request to the current host and wait
    /// up to <paramref name="deadline"/> for it to acknowledge and stop
    /// listening. Best-effort — any failure (timeout, connection refused,
    /// protocol error from an older host that does not understand the
    /// frame) is swallowed: the caller already plans to retire the endpoint
    /// and spawn a replacement.
    /// </summary>
    private async Task TryRequestGracefulShutdownAsync(TimeSpan deadline, CancellationToken ct)
    {
        try
        {
            using var sdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sdCts.CancelAfter(deadline);
            await using var transport = NewTransport();
            using var stream = await transport.ConnectAsync(sdCts.Token).ConfigureAwait(false);
            var deadlineMs = (int)Math.Min(deadline.TotalMilliseconds, int.MaxValue);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Shutdown(deadlineMs), sdCts.Token).ConfigureAwait(false);
            await HostProtocol.ReadResponseAsync(stream, _ => { }, sdCts.Token).ConfigureAwait(false);
        }
        catch (SocketException) { /* host already gone */ }
        catch (IOException) { /* old host or wedged — fall through to endpoint cleanup */ }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* deadline */ }

        // Wait briefly for the listener to actually disappear so the spawn
        // step has a chance to bind. Bounded by the same deadline so a wedged
        // host cannot stall the launcher indefinitely.
        var waitDeadline = DateTime.UtcNow + deadline;
        while (DateTime.UtcNow < waitDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await CheckHealthAsync(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false);
            if (state == HostHealthState.Unhealthy) return;
            try { await Task.Delay(_startupPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
    }

    private static TimeSpan GetShutdownDeadline()
        => TimeSpan.FromMilliseconds(HostProtocol.DefaultShutdownDeadlineMs);

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
            // The host answered the health frame but the payload doesn't match
            // this build's expected payload — protocol or build identity has
            // drifted. Treat as obsolete so the caller can request graceful
            // shutdown rather than silently retiring the endpoint.
            if (lines.Count == 1 && lines[0].StartsWith("ps-bash-host", StringComparison.Ordinal))
                return HostHealthState.Obsolete;
            return HostHealthState.Unhealthy;
        }
        catch (SocketException) { return HostHealthState.Unhealthy; }
        catch (IOException) { return HostHealthState.Unhealthy; }
        catch (TimeoutException) { return HostHealthState.Unhealthy; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return HostHealthState.Unhealthy; }
    }

    private async Task SpawnAndWaitAsync(CancellationToken ct)
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);

        var psi = new ProcessStartInfo
        {
            FileName = _hostBinaryPath,
            // On POSIX (Linux/macOS) flip UseShellExecute off so we can set
            // PSBASH_HOST_DETACH=1 via psi.Environment. On Windows keep
            // UseShellExecute=true — the legacy daemon-detach behaviour
            // relies on the shell-execute spawn path to break the pipe
            // inheritance from the launcher.
            UseShellExecute = isWindows,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add($"--ipc-endpoint={_scheme}:{_endpoint}");
        // POSIX-only: signal the host to dup2 /dev/null over the inherited
        // stdio fds at startup so the daemon can never write into the
        // launcher's pipes. Windows uses shell-execute spawn which already
        // detaches the daemon's stdio from the launcher.
        if (!isWindows)
        {
            psi.Environment["PSBASH_HOST_DETACH"] = "1";
        }
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
            // REFACTOR-4: route each response frame by its stream tag. STDOUT
            // frames go to OutputCallback (or Console.Out when no callback is
            // set); STDERR frames always go to Console.Error — they are never
            // folded into OutputCallback so QueryAsync's collected result and
            // the launcher's stdout stay free of diagnostic text.
            return await HostProtocol.ReadResponseAsync(
                stream,
                (line, tag) =>
                {
                    if (tag == StreamTag.Stderr)
                    {
                        Console.Error.Write(line.EndsWith('\n') ? line : line + "\n");
                        return;
                    }
                    if (OutputCallback is { } cb) cb(line);
                    else Console.Write(line);
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
        return TimeSpan.FromSeconds(20);
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
        /// <summary>Host answered but with an incompatible protocol/build payload.</summary>
        Obsolete,
    }
}
