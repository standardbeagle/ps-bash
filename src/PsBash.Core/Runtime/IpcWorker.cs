using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PsBash.Core.Runtime.Compaction;
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
    /// and gives every invocation a hard process boundary.
    /// <para>Opt-in via <c>PSBASH_PER_INVOCATION=1</c>. No longer the default for
    /// <c>-c</c> — see <see cref="Daemon"/> — but retained for callers that need a
    /// fresh host process per command.</para>
    /// </summary>
    PerInvocation,

    /// <summary>
    /// Long-lived shared daemon on the canonical per-session endpoint
    /// (<see cref="IpcTransportFactory.ResolveEndpoint(string)"/>; one daemon per
    /// <c>(user, session)</c>). The worker connects
    /// to an existing healthy host if one answers, otherwise spawns one (single-
    /// flighted via <c>HostSpawnLock</c>) and leaves it running for subsequent
    /// launchers. The worker does NOT kill the host on dispose. This is the
    /// shared-socket daemon-reuse path; it pays for the cross-launcher
    /// ownership/health guardrails and the dup2-detach hang fix.
    /// <para><b>Default for the non-interactive modes</b> (<c>-c</c>, stdin pipe,
    /// script file) as of the pooled-host change: a <c>-c</c> invocation pays the
    /// host runspace cold-start only on the first call, then reuses the warm
    /// daemon. Per-command isolation is preserved because the daemon hands each
    /// connection its OWN runspace from a discard-on-release pool
    /// (<c>WorkerPool</c>), so reuse is fast WITHOUT leaking session state across
    /// commands, and concurrent launchers run in parallel. Other callers:
    /// <c>ps-bash host restart</c> (<c>HostCommands.cs</c>).</para>
    /// <para>It is <b>not</b> used by the interactive REPL. The interactive
    /// launcher path (<c>Program.RunHostUnderPtyAsync</c> and the legacy
    /// inherited-stdio fallback in <c>Program.cs</c>) spawns its host
    /// <i>directly</i> — <c>PtySpawner</c> / <c>Process.Start</c> with
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
/// canonical per-session endpoint. If the connect fails, the host binary is spawned
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

    /// <summary>
    /// Test seam: count of actual host <see cref="Process.Start(ProcessStartInfo)"/> calls, keyed by
    /// <c>scheme:endpoint</c>. The concurrent-cold-start test asserts single-flight
    /// spawned exactly one host for its (unique) endpoint. Keyed by endpoint so it
    /// is immune to spawns on any other endpoint running concurrently — never read
    /// in production.
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> SpawnCounts = new();

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
    /// per-session daemon, spawning one only if no healthy host answers. Throws
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
        // handshake, reuse it. A host that answers but is Starting is waited out;
        // an obsolete (protocol/build mismatch) host is handled under the spawn
        // lock below (graceful shutdown → replace).
        var initial = await CheckHealthAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
        if (initial == HostHealthState.Healthy && CurrentHostMatchesLauncher())
            return;
        if (initial == HostHealthState.Starting && await WaitForHealthyAsync(ct).ConfigureAwait(false))
            return;

        // 2) No reusable host answered. Single-flight the spawn: when N launchers
        // race from cold, exactly ONE runs the classify→graceful→retire→spawn path
        // while the rest wait for its host. Without this, every launcher spawns a
        // host and — because UnixSocketTransport unlinks-before-bind and the
        // Windows pipe allows 16 instances — N-1 become orphan runspaces (the
        // concurrent-cold-start thundering herd). The lock is endpoint-scoped, so
        // an isolated test endpoint never serializes against the real daemon.
        var deadline = DateTime.UtcNow + _startupTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using (var spawnLock = HostSpawnLock.TryAcquire(_scheme, _endpoint))
            {
                if (spawnLock is not null)
                {
                    // We hold the spawn right — run the full replace-or-spawn path.
                    await SpawnOrReplaceHostAsync(ct).ConfigureAwait(false);
                    return;
                }
            }

            // A concurrent launcher holds the spawn lock. Wait for its host to come
            // up instead of spawning our own; reuse the moment it answers healthy.
            var state = await CheckHealthAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            if (state == HostHealthState.Healthy && CurrentHostMatchesLauncher())
                return;

            if (DateTime.UtcNow >= deadline)
                throw new HostUnavailableException(
                    $"ps-bash-host did not become reachable within {_startupTimeout.TotalSeconds:0.##}s " +
                    "(a concurrent launcher held the spawn lock but no healthy host appeared).");

            try { await Task.Delay(_startupPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
    }

    /// <summary>
    /// True when a host currently answering the canonical endpoint is the same
    /// build/binary this launcher would spawn (or there is no sidecar to compare,
    /// which <see cref="HostOwnership.MetadataMatchesLauncher"/> treats as a
    /// match). Used both for initial reuse and for the wait-for-winner path.
    /// </summary>
    private bool CurrentHostMatchesLauncher()
        => HostOwnership.MetadataMatchesLauncher(
               HostMetadata.TryRead(_scheme, _endpoint),
               _hostBinaryPath,
               // Stamp the expected identity with the host binary we would spawn,
               // so a daemon running an OLDER build of the same <Version> is
               // classified Obsolete and replaced (the host records the matching
               // stamp from its own Environment.ProcessPath). Without the stamp a
               // recompiled-but-not-version-bumped host is silently reused.
               HostProtocol.BuildIdentityFor(_hostBinaryPath));

    /// <summary>
    /// The spawn/replace path, run ONLY by the launcher holding the
    /// <see cref="HostSpawnLock"/>. Re-probes under the lock (a prior winner may
    /// have brought a host up while we waited to acquire), then classifies
    /// ownership, gracefully retires an obsolete host, kills a wedged owned host,
    /// cleans endpoint+sidecar artifacts, and spawns a fresh host. All the
    /// ownership-safety gates from docs/specs/host-lifecycle-contract.md are
    /// preserved — single-flight only ensures one launcher executes them at a time.
    /// </summary>
    private async Task SpawnOrReplaceHostAsync(CancellationToken ct)
    {
        // Re-derive health under the lock. A compatible host that appeared while we
        // waited for the lock means there is nothing to spawn.
        var state = await CheckHealthAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
        if (state == HostHealthState.Healthy)
        {
            if (CurrentHostMatchesLauncher()) return;
            state = HostHealthState.Obsolete; // answered but wrong build/protocol
        }

        // Classify what cleanup we are allowed to do BEFORE touching anything. The
        // metadata sidecar is the ownership proof; without it, only the endpoint
        // artifact may be unlinked, never a process.
        var metadata = HostMetadata.TryRead(_scheme, _endpoint);
        var decision = HostOwnership.Classify(metadata, Environment.UserName, out var unsafeReason);
        if (decision == HostOwnership.CleanupDecision.UnsafeToTouch)
            throw new HostUnavailableException(
                $"Cannot replace ps-bash-host at {_scheme}:{_endpoint} — {unsafeReason}.");

        if (state == HostHealthState.Obsolete)
        {
            // The current host responded — give it a chance to drain and exit
            // cleanly before we touch the endpoint or spawn a replacement. Bound
            // the wait so an unresponsive host cannot block startup forever.
            await TryRequestGracefulShutdownAsync(GetShutdownDeadline(), ct).ConfigureAwait(false);
        }

        // Re-probe after graceful attempt. If a process is still answering and we
        // have ownership, escalate to kill. If it is still answering and we do NOT
        // have ownership, refuse cleanup so we never leave a recycled-PID footgun.
        var post = await CheckHealthAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        if (post != HostHealthState.Unhealthy)
        {
            if (decision == HostOwnership.CleanupDecision.SafeProcessShutdown && metadata is not null)
            {
                TryKillRecordedHost(metadata);
            }
            else
            {
                // Host is alive but not gracefully retiring and we cannot prove
                // ownership. Surface rather than risk a wrong-process kill.
                throw new HostUnavailableException(
                    $"ps-bash-host at {_scheme}:{_endpoint} is alive but did not shut down gracefully " +
                    $"and has no ownership proof to authorize a process kill.");
            }
        }

        // Endpoint and sidecar artifacts may now be cleaned. Unix unlinks the
        // socket file; Windows named pipes have no kernel-namespace file to remove.
        // The sidecar is removed so the next launcher does not see ghost ownership.
        IpcTransportFactory.RetireEndpoint(_scheme, _endpoint);
        HostMetadata.Remove(_scheme, _endpoint);

        // No healthy host running — spawn one. Verify the binary first so the
        // exception names the actual problem.
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
        // Never target the launcher's own process. An in-process host (tests) or
        // a recycled PID that happens to equal ours must not make the launcher
        // kill itself / its own process tree.
        if (metadata.Pid == Environment.ProcessId) return;
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
            // We MUST redirect stdout/stderr (not leave them inherited). Because
            // RedirectStandardInput=true forces CreateProcess bInheritHandles=true,
            // a host with un-redirected stdout/stderr INHERITS the launcher's own
            // stdout/stderr handles. For a Daemon host — which outlives the
            // launcher — that is fatal: the persisted host keeps the launcher's
            // stdout pipe open forever, so the launcher's PARENT (e.g. the Claude
            // Code Bash tool) never sees stdout EOF and hangs after every command,
            // even though the launcher process itself has exited. Redirecting binds
            // the host's stdout/stderr to launcher-owned pipes instead; we drain
            // them to null below so the host never blocks on a full pipe and the
            // launcher's real parent stdout/stderr are never shared with the host.
            // (PerInvocation killed its host on dispose, masking this; Daemon does
            // not — and Daemon never ran a -c command before it became the -c
            // default, so this latent hazard had never fired.)
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
        // On Windows the equivalent is structural: stdin is redirected + closed
        // (above), and stdout/stderr are redirected to launcher-owned pipes that
        // are drained to null (below) — so the host never shares the launcher's
        // real parent stdout/stderr, the hazard that hangs a persisted Daemon.
        //
        // REFACTOR-7 note: this detach is the daemon-era hang fix. PerInvocation
        // contains the pipe-inheritance hazard within the launcher's lifetime
        // (host killed on dispose), but it is still load-bearing for Daemon, where
        // the host outlives the launcher. Kept for both lifetimes.
        if (!isWindows)
        {
            psi.Environment["PSBASH_HOST_DETACH"] = "1";
        }
        // Windows: clear HANDLE_FLAG_INHERIT on our OWN std handles before spawn.
        // RedirectStandardInput forces CreateProcess bInheritHandles=true, which
        // otherwise leaks a copy of the launcher's real stdout/stderr handles into
        // the child as stray inherited handles (separate from the redirect pipes
        // it uses as StdOut/StdErr). A persisted Daemon holding those stray copies
        // keeps the launcher's PARENT pipe open after the launcher exits → the
        // parent (Bash tool) hangs with no EOF. Clearing inherit means the child
        // inherits ONLY the redirect pipes. (POSIX handles the equivalent via
        // PSBASH_HOST_DETACH dup2 + pipe-fd close in InheritedFdDetach.)
        if (isWindows)
            ClearStdHandleInheritanceWindows();

        Process? proc = null;
        bool spawnSucceeded = false;
        try
        {
            proc = Process.Start(psi)
                ?? throw new HostUnavailableException(
                    $"Process.Start returned null for '{_hostBinaryPath}'.");

            // Test seam: record that this launcher actually spawned a host on this
            // endpoint. Single-flight (EnsureHostReachableAsync) must keep this at
            // exactly one per shared endpoint under a concurrent cold-start race.
            SpawnCounts.AddOrUpdate($"{_scheme}:{_endpoint}", 1, static (_, n) => n + 1);

            // Close our end of the redirected stdin pipe. The host now sees
            // an EOF-closed stdin instead of whatever stdin the launcher
            // inherited (e.g. vstest's redirected pipe). Without this the
            // host would block on a read of vstest's stream that never
            // produces data — the failure mode that broke the prior
            // UseShellExecute=false attempt.
            try { proc.StandardInput.Close(); }
            catch { /* harmless — already closed if host exited fast */ }

            // Drain the host's redirected stdout/stderr to null on background
            // tasks. This keeps the host from ever blocking on a full pipe (it is
            // near-silent in framed mode, but a stray banner/error must not wedge
            // it) and, crucially, means the host's stdout/stderr are launcher-owned
            // pipes — NOT the launcher's real parent handles. When the launcher
            // exits, these pipes close; a persisted daemon writing afterward just
            // gets a broken pipe (swallowed). Fire-and-forget: process exit does
            // not wait on these.
            DrainToNull(proc.StandardOutput);
            DrainToNull(proc.StandardError);

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

    /// <summary>
    /// True for the exceptions that mean "the IPC connection broke" — the host
    /// was killed, the endpoint went stale, or the peer reset the socket
    /// (<c>SocketException</c> / connection refused) — as opposed to a timeout
    /// (<c>OperationCanceledException</c> → <c>TimeoutException</c>) or a clean
    /// non-zero exit. A <c>NetworkStream</c> read/write surfaces a reset as an
    /// <c>IOException</c> (often wrapping a <c>SocketException</c>); a connect to
    /// a missing/listener-less endpoint surfaces a bare <c>SocketException</c>.
    /// Both are recoverable by retiring the host and reconnecting.
    /// </summary>
    internal static bool IsTransportReset(Exception ex)
        => ex is System.IO.IOException or System.Net.Sockets.SocketException;

    private async Task<int> SendRequestAsync(Mode mode, CancellationToken ct)
    {
        var idleTimeout = GetCallTimeout();
        bool unbounded = idleTimeout <= TimeSpan.Zero;
        var compactOutput = EnvFlags.IsTruthy("PSBASH_COMPACT_OUTPUT");
        var compactFrames = compactOutput ? new List<OutputFrame>() : null;
        var command = CommandLabel(mode);

        // Self-healing retry for a mid-command transport RESET (host killed,
        // endpoint gone stale, connection refused). The reset is recoverable —
        // but ONLY when no output frame escaped on the failed attempt, so a
        // side-effecting command can never double-execute (the user-chosen
        // "safe pre-output retry" policy). Any reset, retried or not, also
        // retires/respawns the host so the NEXT invocation starts clean rather
        // than re-hanging on the corpse. Timeouts are NOT retried here: they
        // throw TimeoutException from ExchangeOnceAsync (see below).
        const int maxAttempts = 2;
        for (int attempt = 1; ; attempt++)
        {
            // framesDelivered is flipped by the line handler on the FIRST frame
            // (any mode). Read in the catch filter to gate the retry: no frame
            // delivered ⇒ nothing observed downstream ⇒ safe to re-run.
            bool framesDelivered = false;
            try
            {
                return await ExchangeOnceAsync(
                    mode, command, idleTimeout, unbounded, compactFrames,
                    () => framesDelivered = true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportReset(ex))
            {
                if (attempt < maxAttempts && !framesDelivered)
                {
                    // Pre-output reset: retire the broken host, bring up a
                    // healthy one (reuse if it still answers, else spawn fresh),
                    // and retry. A genuine inability to get a host (re-spawn
                    // fails) surfaces as HostUnavailableException — let it out.
                    await RetireAndRespawnAsync(ct).ConfigureAwait(false);
                    continue;
                }
                // Output already streamed (unsafe to retry) or retries exhausted:
                // retire so the next invocation self-heals, then surface the
                // failure (Program.cs maps it to a one-line diagnostic + exit 125).
                await RetireIfUnresponsiveAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// One connect → write-request → read-response exchange against the current
    /// host. Owns its own per-attempt timeout <see cref="CancellationTokenSource"/>
    /// (so it is safe to call again on retry). Converts a connect / idle timeout
    /// into a clean <see cref="TimeoutException"/> (after a best-effort host
    /// retire); lets a transport RESET (<see cref="IsTransportReset"/>) propagate
    /// so the caller's retry loop can recover. <paramref name="onFirstFrame"/> is
    /// invoked the first time any output frame is delivered.
    /// </summary>
    private async Task<int> ExchangeOnceAsync(
        Mode mode, string command, TimeSpan idleTimeout, bool unbounded,
        List<OutputFrame>? compactFrames, Action onFirstFrame, CancellationToken ct)
    {
        // INACTIVITY (idle) timeout, not a total wall-clock cap: the deadline is
        // re-armed every time the host produces an output frame, so a command
        // that keeps streaming output (a long build, `tail -f`, `gh run watch`)
        // runs as long as it stays active. Only genuine silence — no output for
        // the idle window — trips the timeout. An idle value of zero/negative
        // (PSBASH_TIMEOUT=0|none|infinite, or --timeout none) disables it
        // entirely, leaving the caller's CancellationToken as the only stop.
        using var timeoutCts = new CancellationTokenSource();
        // Tripped by the host-liveness watchdog (below) when the host PROCESS
        // dies mid-exchange. On Windows a killed AF_UNIX peer does not reliably
        // surface a socket reset on the client's pending read — without this the
        // launcher would hang to the idle timeout (exit 124), or FOREVER when the
        // idle timeout is unbounded (the default for -c). Watching the PID detects
        // host death directly and converts it into the recoverable reset path,
        // while leaving a genuinely slow-but-alive host untouched (no false retry).
        using var hostDeadCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, timeoutCts.Token, hostDeadCts.Token);

        void ArmIdle()
        {
            if (unbounded) return;
            try { timeoutCts.CancelAfter(idleTimeout); }
            catch (ObjectDisposedException) { /* call finished; nothing to re-arm */ }
        }

        // Bound the CONNECT attempt even when the post-connect idle is unbounded
        // (the default). A host that never accepts a connection is wedged and must
        // be retired so the next invocation spawns fresh, rather than re-hanging on
        // the corpse — the orphaned-host poison guard below. The post-connect idle
        // (what `--timeout` governs) is a separate concern handled by ArmIdle once
        // we are connected. Use the caller's idle timeout for the connect window
        // when they set one, else the startup window.
        var connectBound = unbounded ? _startupTimeout : idleTimeout;
        try { timeoutCts.CancelAfter(connectBound); }
        catch (ObjectDisposedException) { }

        await using var transport = NewTransport();
        Stream stream;
        try
        {
            stream = await transport.ConnectAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The host is not accepting connections within the connect window. For a
            // shared (Daemon) host this usually means a wedged process still
            // holding the endpoint; retire it so the NEXT invocation spawns a
            // fresh host instead of re-hanging on the same corpse — the
            // orphaned-host failure mode where a stale host poisons every
            // subsequent call until it is manually killed.
            await RetireIfUnresponsiveAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"ps-bash: no response from host within {connectBound.TotalSeconds:0.##}s. " +
                "Raise the idle timeout with --timeout <seconds> / PSBASH_TIMEOUT, or --timeout none to disable.");
        }

        // Connected: the connect window no longer applies. Disarm it so a long,
        // quiet command isn't killed when the idle timeout is unbounded; ArmIdle()
        // re-arms below for the bounded case.
        if (unbounded)
        {
            try { timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        // Start the host-liveness watchdog now that we are connected. The PID
        // comes from the owned handle (PerInvocation) or the sidecar (Daemon);
        // if neither is available we simply skip the watchdog and fall back to
        // the timeout backstops. Stopped in the finally below.
        int hostPid = _lifetime == Lifetime.PerInvocation
            ? (_ownedHost?.Id ?? 0)
            : (HostMetadata.TryRead(_scheme, _endpoint)?.Pid ?? 0);
        using var watchdogStop = new CancellationTokenSource();
        Task? watchdog = hostPid > 0
            ? StartHostLivenessWatchdog(hostPid, hostDeadCts, watchdogStop.Token)
            : null;

        try
        {
            await using (stream)
            {
                await HostProtocol.WriteRequestAsync(stream, mode, linked.Token).ConfigureAwait(false);
                ArmIdle(); // reset: now waiting for the first response frame

                // REFACTOR-4: route each response frame by its stream tag. STDOUT
                // frames go to OutputCallback (or Console.Out when no callback is
                // set); STDERR frames always go to Console.Error — they are never
                // folded into OutputCallback so QueryAsync's collected result and
                // the launcher's stdout stay free of diagnostic text.
                var exitCode = await HostProtocol.ReadResponseAsync(
                    stream,
                    (line, tag) =>
                    {
                        onFirstFrame(); // mark output observed (gates the retry decision)
                        ArmIdle(); // each frame is activity — push the idle deadline out
                        if (compactFrames is not null)
                        {
                            // Compact mode trades streaming for a bounded summary: every
                            // frame (BOTH stdout and stderr) is buffered in memory until the
                            // command finishes, then collapsed by OutputCompactor. Two
                            // intentional departures from the REFACTOR-4 routing below:
                            //   1. No streaming — output is held until exit, so memory grows
                            //      with the line count (capped on emit, not on intake). This
                            //      mode is opt-in for agent contexts that want a small digest.
                            //   2. Stderr is folded into the same buffer as stdout; the
                            //      stream distinction is preserved textually via the
                            //      [out]/[err] prefixes OutputCompactor writes.
                            compactFrames.Add(new OutputFrame(tag, line));
                            return;
                        }

                        if (tag == StreamTag.Stderr)
                        {
                            Console.Error.Write(line.EndsWith('\n') ? line : line + "\n");
                            return;
                        }
                        if (OutputCallback is { } cb) cb(line);
                        else Console.Write(line);
                    },
                    linked.Token).ConfigureAwait(false);

                if (compactFrames is not null)
                {
                    EmitCompactedOutput(command, exitCode, timedOut: false, compactFrames);
                }

                return exitCode;
            }
        }
        catch (OperationCanceledException) when (hostDeadCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The host PROCESS died mid-exchange (watchdog). Surface it as a
            // transport reset so SendRequestAsync's retry loop recovers it (pre-
            // output) or fails cleanly (post-output) — never a silent hang. This
            // is the case a killed Windows AF_UNIX peer does NOT report as a
            // socket reset on the pending read.
            throw new IOException("ps-bash-host process exited mid-command.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The host accepted the connection but produced no output for a full
            // idle window, AND the watchdog confirms the host is still alive
            // (else the hostDeadCts branch above would have fired) — so this is a
            // genuinely slow/quiet command, not a dead host. Convert the raw
            // cancellation into a clean, actionable TimeoutException (the
            // launcher's Main maps this to exit 124 instead of a stack trace),
            // and retire the host only if a re-probe confirms it is wedged.
            await RetireIfUnresponsiveAsync().ConfigureAwait(false);
            if (compactFrames is not null)
                EmitCompactedOutput(command, 124, timedOut: true, compactFrames);
            throw new TimeoutException(
                $"ps-bash: no output from host for {idleTimeout.TotalSeconds:0.##}s (idle timeout). " +
                "Raise it with --timeout <seconds> / PSBASH_TIMEOUT, or --timeout none to disable.");
        }
        finally
        {
            // Stop the watchdog. Fire-and-forget cancellation: the task observes
            // watchdogStop and exits; we never block command teardown on it.
            try { watchdogStop.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// Background loop that trips <paramref name="hostDeadCts"/> when the host
    /// process <paramref name="pid"/> exits, so a dead host aborts the pending
    /// read instead of hanging it. Cheap (a liveness check every ~150 ms) and
    /// bounded by <paramref name="stop"/>, which the exchange cancels on
    /// completion. Best-effort: any failure leaves the timeout backstops intact.
    /// </summary>
    private static Task StartHostLivenessWatchdog(int pid, CancellationTokenSource hostDeadCts, CancellationToken stop)
        => Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!IsProcessAlive(pid))
                    {
                        try { hostDeadCts.Cancel(); } catch { }
                        return;
                    }
                    await Task.Delay(150, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* exchange finished — normal stop */ }
            catch { /* never let the watchdog throw into the void */ }
        }, CancellationToken.None);

    /// <summary>
    /// True while process <paramref name="pid"/> is running. A gone PID (or a
    /// recycled one we can no longer open) reads as not-alive; when we cannot
    /// tell, we err toward alive so the watchdog never forces a false retry on a
    /// healthy host.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }        // no such process
        catch (InvalidOperationException) { return false; } // exited / no handle
        catch { return true; }                               // unsure ⇒ don't force a retry
    }

    /// <summary>
    /// Recovery for a per-call timeout. A short health re-probe distinguishes a
    /// genuinely wedged host (retire it so the next invocation self-heals) from
    /// one that is merely slow or busy serving another connection (leave it
    /// untouched). PerInvocation hosts are private and torn down by
    /// <see cref="DisposeAsync"/>, so only the shared Daemon host needs endpoint
    /// retirement here. Best-effort throughout: any failure is swallowed so it
    /// never masks the <see cref="TimeoutException"/> the caller is about to throw.
    /// </summary>
    private async Task RetireIfUnresponsiveAsync()
    {
        try
        {
            // A fresh, short probe on a NEW connection. A host that is single-
            // flighted and wedged on our timed-out call cannot answer this either
            // (→ Unhealthy → retire); a concurrent host that is merely busy still
            // answers (→ Healthy/Starting → leave alone).
            var state = await CheckHealthAsync(TimeSpan.FromMilliseconds(750), CancellationToken.None)
                .ConfigureAwait(false);
            if (state is HostHealthState.Healthy or HostHealthState.Starting)
                return;

            // PerInvocation's owned host is killed by DisposeAsync; nothing to
            // retire at the endpoint level.
            if (_lifetime == Lifetime.PerInvocation)
                return;

            // Shared Daemon host is wedged and would poison every future
            // invocation. Kill the recorded owner if (and only if) we can prove
            // ownership, then unlink the endpoint + sidecar so the next launcher
            // spawns a fresh host. Reuses the same ownership-gated cleanup
            // primitives EnsureHostReachableAsync relies on.
            var metadata = HostMetadata.TryRead(_scheme, _endpoint);
            if (metadata is not null)
            {
                var decision = HostOwnership.Classify(metadata, Environment.UserName, out _);
                if (decision == HostOwnership.CleanupDecision.SafeProcessShutdown)
                    TryKillRecordedHost(metadata);
            }
            IpcTransportFactory.RetireEndpoint(_scheme, _endpoint);
            HostMetadata.Remove(_scheme, _endpoint);
        }
        catch
        {
            // Best-effort recovery — never mask the TimeoutException.
        }
    }

    /// <summary>
    /// Recovery for a pre-output transport RESET: bring up a host we can retry
    /// against. For a shared <see cref="Lifetime.Daemon"/>, reuse a host that
    /// still answers the health handshake, else retire the corpse and spawn a
    /// fresh one (single-flighted) — exactly the <see cref="EnsureHostReachableAsync"/>
    /// path. For a private <see cref="Lifetime.PerInvocation"/> host, the reset
    /// means OUR host died mid-command: kill the stale handle, clean the
    /// process-local endpoint, and spawn a fresh private host on it. Throws
    /// <see cref="HostUnavailableException"/> if no host can be brought up.
    /// </summary>
    private async Task RetireAndRespawnAsync(CancellationToken ct)
    {
        if (_lifetime == Lifetime.PerInvocation)
        {
            var old = _ownedHost;
            _ownedHost = null;
            if (old is not null)
            {
                try { if (!old.HasExited) old.Kill(entireProcessTree: true); }
                catch { /* already gone / race — best effort */ }
                try { old.Dispose(); } catch { }
            }
            IpcTransportFactory.RetireEndpoint(_scheme, _endpoint);
            await SpawnAndWaitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await EnsureHostReachableAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// The per-call <b>inactivity</b> timeout (see <see cref="SendRequestAsync"/>),
    /// read from <c>PSBASH_TIMEOUT</c> (which the <c>--timeout</c> CLI flag sets, so
    /// the two share one parser). See <see cref="ParseCallTimeout"/> for the rules;
    /// the default (unset) is unbounded.
    /// </summary>
    private static TimeSpan GetCallTimeout()
        => ParseCallTimeout(Environment.GetEnvironmentVariable("PSBASH_TIMEOUT"));

    /// <summary>
    /// Pure parser for the per-call inactivity timeout (see
    /// <see cref="GetCallTimeout"/> / <see cref="SendRequestAsync"/>). A bare
    /// positive integer is seconds; <c>0</c>, <c>none</c>, <c>off</c>,
    /// <c>infinite</c>, <c>never</c> (and any non-positive integer) mean
    /// <b>unbounded</b> (<see cref="TimeSpan.Zero"/>). Unset/blank/unparseable
    /// also mean unbounded — the default matches core bash, which applies no
    /// idle timeout to <c>-c</c>/non-interactive runs (the only modes that reach
    /// <see cref="IpcWorker"/>; the interactive REPL is PTY-bound and bypasses it).
    /// Bound a run with <c>--timeout N</c> / <c>PSBASH_TIMEOUT=N</c>.
    /// </summary>
    internal static TimeSpan ParseCallTimeout(string? raw)
    {
        var envValue = raw?.Trim();
        if (string.IsNullOrEmpty(envValue))
            return TimeSpan.Zero; // default: unbounded, matching core bash
        switch (envValue.ToLowerInvariant())
        {
            case "0":
            case "none":
            case "off":
            case "infinite":
            case "never":
                return TimeSpan.Zero; // unbounded
        }
        if (int.TryParse(envValue, out var seconds))
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        return TimeSpan.Zero; // unparseable falls back to the (unbounded) default
    }

    private static string CommandLabel(Mode mode)
    {
        var original = Environment.GetEnvironmentVariable("PSBASH_COMPACT_COMMAND");
        if (!string.IsNullOrWhiteSpace(original)) return original;

        return mode switch
        {
            Mode.Command c => c.Body,
            Mode.Stdin s => s.Body,
            Mode.Script s => $"{s.Path} {string.Join(' ', s.Argv)}",
            _ => mode.GetType().Name,
        };
    }

    private void EmitCompactedOutput(string command, int exitCode, bool timedOut, IReadOnlyList<OutputFrame> frames)
    {
        // FilterEngine routes to a command-aware filter when one matches; otherwise an
        // unrecognized FAILING command is trimmed to its error/summary lines (ErrorExtract),
        // and everything else falls back to the generic OutputCompactor digest.
        var compacted = FilterEngine.Apply(
            command, exitCode, timedOut, frames, ResolveFilters(),
            fallback: GenericFallback.ErrorExtract);
        if (OutputCallback is { } cb) cb(compacted);
        else Console.Write(compacted);
    }

    // Resolve the active filter set for compact mode: embedded built-ins plus user
    // (~/.config/ps-bash/filters) and project (.ps-bash/filters) overrides, minus any
    // excluded commands. PSBASH_NO_FILTER disables command-aware filters (generic digest
    // only). Loading is best-effort: a malformed filter file must never crash a command's
    // output, so any failure degrades to the faithful generic digest (filters = null).
    private static IReadOnlyList<FilterSpec>? ResolveFilters()
    {
        if (EnvFlags.IsTruthy("PSBASH_NO_FILTER")) return null;

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userDir = string.IsNullOrEmpty(home)
                ? null
                : Path.Combine(home, ".config", "ps-bash", "filters");
            var projectDir = Path.Combine(Directory.GetCurrentDirectory(), ".ps-bash", "filters");
            var exclude = ParseExcludeCommands(Environment.GetEnvironmentVariable("PSBASH_FILTER_EXCLUDE"));

            var filters = FilterLibrary.Load(userDir, projectDir, exclude);
            return filters.Count == 0 ? null : filters;
        }
        catch
        {
            return null; // degrade to the generic digest; never break command output
        }
    }

    private static IReadOnlySet<string>? ParseExcludeCommands(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part);
        return set.Count == 0 ? null : set;
    }

    public static string GetHostBinaryName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ps-bash-host.exe"
            : "ps-bash-host";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    private const uint HANDLE_FLAG_INHERIT = 0x1;

    // Clear the inherit flag on this process's stdin/stdout/stderr so a spawned
    // child (with CreateProcess bInheritHandles=true, forced by stream redirection)
    // does NOT inherit stray copies of them. See call site in SpawnAndWaitAsync.
    private static void ClearStdHandleInheritanceWindows()
    {
        foreach (var id in stackalloc[] { -10, -11, -12 }) // STD_INPUT/OUTPUT/ERROR
        {
            try
            {
                var h = GetStdHandle(id);
                // Skip null/invalid (-1) handles — a launcher with no console.
                if (h != IntPtr.Zero && h != new IntPtr(-1))
                    SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0);
            }
            catch { /* best effort — never block spawn on this */ }
        }
    }

    // Read a child stream to completion and discard it, on a background task that
    // never blocks process exit. Used to absorb a spawned host's redirected
    // stdout/stderr (see SpawnAndWaitAsync) so the host neither blocks on a full
    // pipe nor holds the launcher's real parent stdout/stderr open.
    private static void DrainToNull(StreamReader reader)
    {
        _ = Task.Run(async () =>
        {
            try { await reader.BaseStream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
            catch { /* pipe closed on launcher exit / host death — expected */ }
        });
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;

        // REFACTOR-7: PerInvocation owns its private host — kill the tree so the
        // host never outlives this launcher, then unlink the process-local
        // socket artifact. Daemon retains nothing here: the shared host stays up
        // for the next launcher and its endpoint is the canonical per-session one.
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
