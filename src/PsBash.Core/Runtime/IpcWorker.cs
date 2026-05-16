using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime;

/// <summary>
/// Host process lifetime model for an <see cref="IpcWorker"/>. REFACTOR-7.
/// </summary>
public enum Lifetime
{
    /// <summary>
    /// One private <c>ps-bash-host</c> per launcher invocation. The worker spawns
    /// a fresh host on a process-local endpoint (see
    /// <see cref="IpcTransportFactory.ResolvePerInvocationEndpoint"/>), owns the
    /// <see cref="System.Diagnostics.Process"/> handle, and kills the host tree
    /// when it is disposed. The host therefore never outlives its single client,
    /// which contains the pipe-inheritance hazard within the launcher's lifetime
    /// and gives every invocation a clean PowerShell session by construction.
    /// Default for the non-interactive modes (<c>-c</c>, stdin pipe, script file).
    /// </summary>
    PerInvocation,

    /// <summary>
    /// Long-lived shared daemon on the canonical per-user endpoint
    /// (<see cref="IpcTransportFactory.ResolveEndpoint(string)"/>). The worker connects
    /// to an existing healthy host if one answers, otherwise spawns one and
    /// leaves it running for subsequent launchers. The worker does NOT kill the
    /// host on dispose. This is the shared-socket daemon-reuse path; it pays for
    /// the cross-launcher state-isolation guardrails and the dup2-detach hang fix.
    /// <para>PTY-10 — who actually uses this: the <b>only</b> caller is
    /// <c>ps-bash host restart</c> (<c>HostCommands.cs</c>), the explicit
    /// daemon-management subcommand. It is <b>not</b> used by the interactive
    /// REPL. The interactive launcher path (<c>Program.RunHostUnderPtyAsync</c>
    /// and the legacy inherited-stdio fallback in <c>Program.cs</c>) spawns its
    /// host <i>directly</i> — <c>PtySpawner</c> / <c>Process.Start</c> with
    /// <c>--interactive --launcher-pid</c> — and never reaches
    /// <see cref="IpcWorker"/> at all. An interactive host is PTY-bound and must
    /// not be shared across launchers (keystroke cross-talk); a fresh host per
    /// session is guaranteed because the interactive path bypasses this
    /// discovery branch entirely. See <c>docs/specs/host-lifecycle-contract.md</c>
    /// and <c>docs/specs/pty.md</c> §10.5.</para>
    /// </summary>
    Daemon,
}

/// <summary>
/// AOT-safe launcher-side <see cref="IWorker"/> that proxies command execution
/// to an out-of-process <c>ps-bash-host</c> over a duplex IPC transport
/// (Unix socket on POSIX, named pipe on Windows).
/// </summary>
/// <remarks>
/// <para>Two host lifetimes are supported (REFACTOR-7), selected via the
/// <see cref="Lifetime"/> argument to <see cref="StartAsync"/>:</para>
/// <para><see cref="Lifetime.PerInvocation"/> (default): the worker spawns a
/// private host on a process-local endpoint, owns the process handle, and kills
/// the host tree on <see cref="DisposeAsync"/>. There is no daemon discovery,
/// no ownership classification, and no obsolete-host handshake — the endpoint
/// is private to this launcher.</para>
/// <para><see cref="Lifetime.Daemon"/>: discovery is socket-direct against the
/// canonical per-user endpoint. If the connect fails, the host binary is spawned
/// and connect is retried until the socket accepts or the timeout elapses. The
/// host is left running for the next launcher. There is no lock file, no session
/// id, and no fallback to an SDK host worker — failure to reach the host throws
/// <see cref="HostUnavailableException"/>.</para>
/// </remarks>
public sealed class IpcWorker : IWorker
{
    private readonly string _scheme;
    private readonly string _endpoint;
    private readonly string _hostBinaryPath;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _startupPollInterval;
    private readonly Lifetime _lifetime;
    // PerInvocation only: the private host process this worker owns and kills on
    // dispose. Null for Daemon lifetime (the shared host outlives this worker).
    private Process? _ownedHost;
    private int _disposed;

    public Action<string>? OutputCallback { get; set; }

    public bool HasExited => _disposed != 0;

    private IpcWorker(string scheme, string endpoint, string hostBinaryPath, TimeSpan timeout, TimeSpan poll, Lifetime lifetime)
    {
        _scheme = scheme;
        _endpoint = endpoint;
        _hostBinaryPath = hostBinaryPath;
        _startupTimeout = timeout;
        _startupPollInterval = poll;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Start a worker against <c>ps-bash-host</c>. With
    /// <see cref="Lifetime.PerInvocation"/> (default) a fresh private host is
    /// spawned on a process-local endpoint and owned by the returned worker;
    /// with <see cref="Lifetime.Daemon"/> the worker connects to the canonical
    /// per-user daemon, spawning one only if no healthy host answers. Throws
    /// <see cref="HostUnavailableException"/> if the binary is missing or fails
    /// to come up within the startup timeout.
    /// </summary>
    public static async Task<IpcWorker> StartAsync(
        string hostBinaryPath,
        TimeSpan? startupTimeout = null,
        TimeSpan? startupPollInterval = null,
        Lifetime lifetime = Lifetime.PerInvocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hostBinaryPath);

        var (scheme, endpoint) = lifetime == Lifetime.PerInvocation
            ? IpcTransportFactory.ResolvePerInvocationEndpoint()
            : IpcTransportFactory.ResolveEndpoint();
        var timeout = startupTimeout ?? GetStartupTimeout();
        var poll = startupPollInterval ?? TimeSpan.FromMilliseconds(50);
        var worker = new IpcWorker(scheme, endpoint, hostBinaryPath, timeout, poll, lifetime);
        if (lifetime == Lifetime.PerInvocation)
            await worker.SpawnPrivateHostAsync(ct).ConfigureAwait(false);
        else
            await worker.EnsureHostReachableAsync(ct).ConfigureAwait(false);
        return worker;
    }

    /// <summary>
    /// REFACTOR-7 PerInvocation path. The endpoint is process-local and was
    /// just minted by <see cref="IpcTransportFactory.ResolvePerInvocationEndpoint"/>,
    /// so no listener can already be answering it — there is nothing to discover,
    /// no ownership to classify, and no obsolete host to retire. Verify the
    /// binary, spawn it bound to the private endpoint, and wait for it to accept.
    /// The worker keeps the <see cref="Process"/> handle and kills the tree on
    /// <see cref="DisposeAsync"/> so the host never outlives this launcher.
    /// </summary>
    private async Task SpawnPrivateHostAsync(CancellationToken ct)
    {
        if (!File.Exists(_hostBinaryPath))
            throw new HostUnavailableException(
                $"ps-bash-host binary not found at '{_hostBinaryPath}'. Cannot start host.");

        await SpawnAndWaitAsync(ct).ConfigureAwait(false);
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
            // UseShellExecute=false uniformly so we can set per-process env
            // vars (PSBASH_HOST_DETACH=1 on POSIX) and so we never go through
            // ShellExecuteEx, which requires Shell COM / STA and fails with
            // Win32 error 126 when ps-bash.exe is spawned by vstest (whose
            // testhost runs tests on MTA threads).
            //
            // RedirectStandardInput=true: the host's stdin is given a fresh
            // pipe from the launcher rather than being left inherited from
            // the launcher's own stdin. We close the parent side immediately
            // after spawn (see below), so the host sees an EOF-closed stdin.
            // Without this, a launcher that was itself spawned by a test
            // runner with redirected stdio (vstest's testhost) would pass
            // vstest's stdin handle straight through to the host, and the
            // host would block trying to read it — surfacing as
            // HostUnavailableException("did not accept connections within
            // startup timeout"). The Windows equivalent of POSIX's
            // PSBASH_HOST_DETACH dup2(/dev/null) replacement.
            //
            // We deliberately do NOT redirect stdout/stderr: the host is
            // largely silent, and unredirected stdout/stderr inherit the
            // launcher's handles (which on Windows under CreateProcess with
            // bInheritHandles=false means the host starts with detached/NUL
            // handles, not the launcher's pipe).
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add($"--ipc-endpoint={_scheme}:{_endpoint}");
        // REFACTOR-7: a PerInvocation host is private to this launcher — pass
        // our PID so the host's ParentDeathWatcher self-terminates if the
        // launcher is force-killed before DisposeAsync can run. The Daemon path
        // intentionally does NOT pass a launcher PID: a shared daemon must
        // outlive any single launcher.
        if (_lifetime == Lifetime.PerInvocation)
            psi.ArgumentList.Add($"--launcher-pid={Environment.ProcessId}");
        // POSIX-only: signal the host to dup2 /dev/null over the inherited
        // stdio fds at startup so it can never write into the launcher's pipes.
        // Windows handles the same hazard via RedirectStandardInput=true above
        // plus proc.StandardInput.Close() right after Process.Start: the host
        // sees a closed stdin instead of the launcher's inherited handle, and
        // CreateProcess with bInheritHandles=false keeps stdout/stderr detached.
        //
        // REFACTOR-7 note on cc8bf88: this dup2-detach is the daemon-era hang
        // fix. PerInvocation already contains the pipe-inheritance hazard within
        // the launcher's lifetime (single client, single connection, host killed
        // on dispose), so the detach is not strictly load-bearing for that path
        // — but it is harmless there and still REQUIRED for the Daemon path,
        // where the host outlives the launcher. Kept for both lifetimes per the
        // task's "only remove it where genuinely dead" guidance; deleting it
        // would regress Daemon.
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

            // Close our end of the redirected stdin pipe. The host now sees
            // an EOF-closed stdin instead of whatever stdin the launcher
            // inherited (e.g. vstest's redirected pipe). Without this the
            // host would block on a read of vstest's stream that never
            // produces data — the failure mode that broke the prior
            // UseShellExecute=false attempt.
            try { proc.StandardInput.Close(); }
            catch { /* harmless — already closed if host exited fast */ }

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
                    // PerInvocation owns the host process for the lifetime of
                    // the worker — keep the handle so DisposeAsync can kill the
                    // tree. Daemon leaves the host running for the next launcher
                    // and must NOT retain (or later kill) the handle.
                    if (_lifetime == Lifetime.PerInvocation)
                        _ownedHost = proc;
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
                proc.Dispose();
            }
            else if (_lifetime != Lifetime.PerInvocation)
            {
                // Daemon: we do not retain the handle; dispose it now. The host
                // process keeps running — disposing a Process handle does not
                // terminate the process.
                proc?.Dispose();
            }
            // PerInvocation + spawnSucceeded: _ownedHost now holds the live
            // handle; DisposeAsync disposes it. Do NOT dispose here.
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

        // REFACTOR-7: PerInvocation owns its private host — kill the tree so the
        // host never outlives this launcher, then unlink the process-local
        // socket artifact. Daemon retains nothing here: the shared host stays up
        // for the next launcher and its endpoint is the canonical per-user one.
        if (_lifetime == Lifetime.PerInvocation && _ownedHost is { } host)
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); }
            catch { /* already exited / race — best effort */ }
            try { host.Dispose(); }
            catch { /* best effort */ }
            _ownedHost = null;

            // The host's metadata sidecar and (on POSIX) the socket file are
            // process-local artifacts of a host we just killed — remove them so
            // {TEMP}/ps-bash does not accumulate dead per-invocation sockets.
            try { IpcTransportFactory.RetireEndpoint(_scheme, _endpoint); } catch { }
            try { HostMetadata.Remove(_scheme, _endpoint); } catch { }
        }

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
