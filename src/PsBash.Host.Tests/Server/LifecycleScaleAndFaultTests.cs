using System.Diagnostics;
using System.IO.Pipes;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Lifecycle scale + fault-injection coverage for launcher-side host startup
/// (IpcWorker.StartAsync). Proves the launcher's tmux-style single-host-per-user
/// behavior under concurrent races, reuse, replacement, stale endpoints,
/// wedged hosts, and idle shutdown.
///
/// Oracle note (Directive 1): no bash oracle — ps-bash IPC lifecycle is
/// outside the bash compatibility surface and is exercised against the
/// .NET-side wire contract directly.
///
/// Reliability contract (testing.md / qa-rubric Directive 6):
/// - Each test isolates its endpoint via PSBASH_IPC_ENDPOINT so it never races
///   against the user's canonical daemon.
/// - All spawned ps-bash-host processes are killed with entireProcessTree=true
///   in finally, even on test failure.
/// - All in-process HostServer instances are disposed.
/// - Endpoint sidecars and unix socket files are cleaned up.
/// - CI-safe timeouts: every wait has a hard deadline; nothing hangs.
/// </summary>
[Collection("SdkHost")]
[Trait("Category", "Lifecycle")]
public sealed class LifecycleScaleAndFaultTests
{
    // ─── Endpoint isolation helpers ─────────────────────────────────────────

    /// <summary>
    /// Generate a fresh isolated endpoint spec ("pipe:psbash-test-...") and the
    /// scheme/endpoint pair the test will probe. Every caller MUST clean up
    /// any sidecar / socket file written under the resolved path in a finally
    /// block (see CleanupEndpoint).
    /// </summary>
    private static (string Spec, string Scheme, string Endpoint) NewIsolatedEndpoint(string label)
    {
        var unique = $"{label}-{Guid.NewGuid():N}";
        if (OperatingSystem.IsWindows())
        {
            var name = $"psbash-{unique}";
            return ($"pipe:{name}", "pipe", name);
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "ps-bash");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"host-{unique}.sock");
            return ($"unix:{path}", "unix", path);
        }
    }

    private static void CleanupEndpoint(string scheme, string endpoint)
    {
        try { HostMetadata.Remove(scheme, endpoint); } catch { }
        try { IpcTransportFactory.RetireEndpoint(scheme, endpoint); } catch { }
    }

    /// <summary>
    /// Find the published ps-bash-host binary built by the host project. Tests
    /// that spawn a real host depend on this — when missing, the test is
    /// skipped with a clear reason rather than failing or hanging.
    /// </summary>
    private static string? TryLocateHostBinary()
    {
        var name = OperatingSystem.IsWindows() ? "ps-bash-host.exe" : "ps-bash-host";
        var asmDir = Path.GetDirectoryName(typeof(LifecycleScaleAndFaultTests).Assembly.Location)!;
        // Walk up to find src/, then descend into src/PsBash.Host/bin/<config>/<tfm>/.
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && dir.Name != "src" && dir.Parent is not null)
            dir = dir.Parent;
        if (dir is null) return null;
        var hostBinDir = Path.Combine(dir.FullName, "PsBash.Host", "bin");
        if (!Directory.Exists(hostBinDir)) return null;
        var matches = Directory.EnumerateFiles(hostBinDir, name, SearchOption.AllDirectories)
            .Where(p => !p.Contains("win-x64") || OperatingSystem.IsWindows())
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        return matches.FirstOrDefault();
    }

    private IIpcTransport NewTransport(string scheme, string endpoint) => scheme switch
    {
        "unix" => new UnixSocketTransport(endpoint),
        "pipe" => new NamedPipeTransport(endpoint),
        _ => throw new InvalidOperationException($"unknown scheme {scheme}"),
    };

    // ─── 2. Reuse healthy current-build host ────────────────────────────────

    /// <summary>
    /// Acceptance: existing healthy current-build host is reused. An in-process
    /// HostServer running on the isolated endpoint must satisfy IpcWorker's
    /// initial Health probe so StartAsync returns without spawning a replacement.
    /// We point hostBinaryPath at a non-existent path; if the launcher tried to
    /// spawn it would throw "binary not found".
    /// </summary>
    [Fact]
    public async Task ReuseHealthyCurrentBuildHost_StartAsync_DoesNotSpawn()
    {
        var (spec, scheme, endpoint) = NewIsolatedEndpoint("reuse");
        var prior = Environment.GetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar);
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, spec);

        await using var transport = NewTransport(scheme, endpoint);
        await using var worker = SdkWorker.Create();
        await using var server = new HostServer(transport, Task.FromResult(worker));
        using var serverCts = new CancellationTokenSource();
        Task? serverTask = null;
        try
        {
            serverTask = server.RunAsync(serverCts.Token);
            await server.WhenListening.WaitAsync(TimeSpan.FromSeconds(5));

            var bogusBinary = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var ipc = await IpcWorker.StartAsync(
                bogusBinary,
                startupTimeout: TimeSpan.FromSeconds(5),
                lifetime: Lifetime.Daemon,
                ct: startCts.Token);

            // If we got here, healthy host was reused (no spawn attempt).
            // Verify by issuing a real command through the same endpoint.
            var output = await ipc.QueryAsync("Invoke-BashEcho 'reused'", startCts.Token);
            Assert.Contains("reused", output);
        }
        finally
        {
            try { serverCts.Cancel(); } catch { }
            try { if (serverTask is not null) await serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, prior);
            CleanupEndpoint(scheme, endpoint);
        }
    }

    // ─── 4. Stale endpoint with no live host is removed ─────────────────────

    /// <summary>
    /// Acceptance: stale Unix socket / Windows-pipe metadata path with no live
    /// host is removed. Pre-write a sidecar pointing at a definitely-dead PID,
    /// then verify Classify on that sidecar returns SafeArtifactCleanup. This
    /// is the unit-level proof that IpcWorker.EnsureHostReachableAsync would
    /// retire the endpoint and spawn a replacement, without paying the cost
    /// of spawning a binary in this test.
    /// </summary>
    [Fact]
    public void StaleEndpointWithDeadPid_ClassifiesAsSafeArtifactCleanup()
    {
        var (_, scheme, endpoint) = NewIsolatedEndpoint("stale");
        try
        {
            // PID 0 / 1 is reserved on Unix; on Windows PID 0 is "Idle" / 4 is
            // "System" — both ProbeProcess will reject. We use an
            // intentionally-invalid PID below to make alive=false unconditional.
            var deadPid = -1;

            // Write a sidecar for a PID we KNOW is dead (Process.GetProcessById
            // with negative id throws ArgumentException → ProbeProcess returns
            // (false, null)).
            var meta = new HostMetadata(
                Pid: deadPid,
                ExecutablePath: Path.Combine(Path.GetTempPath(), "ghost.exe"),
                ProtocolVersion: HostProtocol.ProtocolVersion,
                BuildIdentity: HostProtocol.BuildIdentity,
                TransportScheme: scheme,
                Endpoint: endpoint,
                StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                Owner: Environment.UserName);
            meta.Write(scheme, endpoint);

            // Also create a stale unix socket file if applicable so the cleanup
            // path is visible.
            if (scheme == "unix")
                File.WriteAllText(endpoint, "");

            var read = HostMetadata.TryRead(scheme, endpoint);
            Assert.NotNull(read);

            var decision = HostOwnership.Classify(read, Environment.UserName, out var reason);
            Assert.Equal(HostOwnership.CleanupDecision.SafeArtifactCleanup, decision);
            Assert.Equal("", reason);

            // Verify the cleanup primitives the launcher uses:
            HostMetadata.Remove(scheme, endpoint);
            IpcTransportFactory.RetireEndpoint(scheme, endpoint);

            Assert.Null(HostMetadata.TryRead(scheme, endpoint));
            if (scheme == "unix")
                Assert.False(File.Exists(endpoint), "unix socket file must be unlinked");
        }
        finally
        {
            CleanupEndpoint(scheme, endpoint);
        }
    }

    /// <summary>
    /// Acceptance variant for Windows named pipes: the metadata sidecar lives
    /// under %TEMP%/ps-bash/ rather than next to a socket file, and there is
    /// no kernel-namespace endpoint to unlink. RetireEndpoint must be a no-op
    /// for pipe scheme; only the sidecar must come away.
    /// </summary>
    [SkippableFact]
    public void StaleNamedPipeMetadata_RemovedWithoutTouchingPipeNamespace()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Named-pipe path is Windows-only");

        var pipeName = $"psbash-stale-pipe-{Guid.NewGuid():N}";
        try
        {
            var meta = new HostMetadata(
                Pid: -1,
                ExecutablePath: "C:\\nonexistent\\ps-bash-host.exe",
                ProtocolVersion: HostProtocol.ProtocolVersion,
                BuildIdentity: HostProtocol.BuildIdentity,
                TransportScheme: "pipe",
                Endpoint: pipeName,
                StartedAt: DateTimeOffset.UtcNow.AddHours(-1),
                Owner: Environment.UserName);
            meta.Write("pipe", pipeName);

            var sidecarPath = HostMetadata.PathFor("pipe", pipeName);
            Assert.True(File.Exists(sidecarPath));

            // RetireEndpoint must NOT throw and must NOT touch the pipe
            // namespace (no way to verify directly, but the call returning
            // without exception is the contract).
            IpcTransportFactory.RetireEndpoint("pipe", pipeName);
            HostMetadata.Remove("pipe", pipeName);

            Assert.False(File.Exists(sidecarPath), "sidecar must be removed");
        }
        finally
        {
            try { HostMetadata.Remove("pipe", pipeName); } catch { }
        }
    }

    // ─── 5. Wedged host bounded timeout ─────────────────────────────────────

    /// <summary>
    /// Acceptance: a wedged host that accepts connections but never completes
    /// the health handshake must NOT block startup forever. IpcWorker.StartAsync
    /// must surface HostUnavailableException within its startup timeout.
    ///
    /// We model a wedged host with a server that accepts pipe connections and
    /// then sleeps. CheckHealthAsync uses a 750ms inner probe timeout, so the
    /// initial probe surfaces Unhealthy. With a non-existent binary, the
    /// SpawnAndWaitAsync step then surfaces "binary not found" — proving the
    /// startup path is bounded.
    /// </summary>
    [Fact]
    public async Task WedgedHost_NeverRepliesHealth_StartAsyncFailsBounded()
    {
        var (spec, scheme, endpoint) = NewIsolatedEndpoint("wedged");
        var prior = Environment.GetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar);
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, spec);

        // Wedged listener: accept, then sit forever on the stream until disposal.
        using var wedgedCts = new CancellationTokenSource();
        Task? wedgedTask = null;
        var heldStreams = new List<Stream>();
        await using var transport = NewTransport(scheme, endpoint);
        try
        {
            await transport.ListenAsync(wedgedCts.Token);
            wedgedTask = Task.Run(async () =>
            {
                while (!wedgedCts.IsCancellationRequested)
                {
                    try
                    {
                        var s = await transport.AcceptAsync(wedgedCts.Token);
                        // Hold the connection open without responding so the
                        // launcher's CheckHealthAsync probe sees a wedged peer.
                        lock (heldStreams) heldStreams.Add(s);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* swallow accept errors; loop continues */ }
                }
            }, wedgedCts.Token);

            var bogusBinary = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
            var sw = Stopwatch.StartNew();
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ex = await Assert.ThrowsAsync<HostUnavailableException>(async () =>
            {
                await using var ipc = await IpcWorker.StartAsync(
                    bogusBinary,
                    startupTimeout: TimeSpan.FromSeconds(2),
                    startupPollInterval: TimeSpan.FromMilliseconds(50),
                    lifetime: Lifetime.Daemon,
                    ct: startCts.Token);
            });
            sw.Stop();

            // Must complete within a small multiple of startup timeout — proves
            // bounded behavior. 10s is the CI-safe ceiling (well above the 2s
            // timeout + a few probe iterations + overhead).
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                $"StartAsync must not hang on a wedged host; took {sw.ElapsedMilliseconds}ms. ex={ex.Message}");
        }
        finally
        {
            try { wedgedCts.Cancel(); } catch { }
            try { if (wedgedTask is not null) await wedgedTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            lock (heldStreams)
            {
                foreach (var hs in heldStreams)
                    try { hs.Dispose(); } catch { }
                heldStreams.Clear();
            }
            Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, prior);
            CleanupEndpoint(scheme, endpoint);
        }
    }

    // ─── 5b. Per-call timeout surfaces TimeoutException, not raw OCE ─────────

    /// <summary>
    /// Acceptance: when a command exceeds the per-call timeout, SendRequestAsync
    /// must surface a clean <see cref="TimeoutException"/> within a bounded time,
    /// NOT leak a raw OperationCanceledException. (The launcher's Main maps the
    /// TimeoutException to a one-line "ps-bash:" diagnostic + exit 124; before the
    /// fix the raw OCE propagated out of Main and the runtime dumped a managed
    /// stack trace with exit 82 — the failure an embedding parent like the Claude
    /// Code Bash tool saw when the host wedged.)
    ///
    /// We reuse a real healthy in-process host, set a 1s call budget via
    /// PSBASH_TIMEOUT, and issue a command that runs well past it. The host is
    /// merely busy, not wedged; the recovery probe must not crash and must leave
    /// the TimeoutException intact. (The self-PID guard in TryKillRecordedHost
    /// guarantees the in-process host — whose recorded PID is this test process —
    /// can never be killed by the recovery path.)
    ///
    /// Oracle note (Directive 1): IPC lifecycle is outside the bash surface;
    /// asserted against the .NET wire contract directly.
    /// </summary>
    [Fact]
    public async Task PerCallTimeout_BusyHost_ThrowsTimeoutExceptionBounded()
    {
        var (spec, scheme, endpoint) = NewIsolatedEndpoint("calltimeout");
        var priorEndpoint = Environment.GetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar);
        var priorTimeout = Environment.GetEnvironmentVariable("PSBASH_TIMEOUT");
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, spec);
        Environment.SetEnvironmentVariable("PSBASH_TIMEOUT", "1"); // 1s per-call budget

        await using var transport = NewTransport(scheme, endpoint);
        await using var worker = SdkWorker.Create();
        await using var server = new HostServer(transport, Task.FromResult(worker));
        using var serverCts = new CancellationTokenSource();
        Task? serverTask = null;
        try
        {
            serverTask = server.RunAsync(serverCts.Token);
            await server.WhenListening.WaitAsync(TimeSpan.FromSeconds(5));

            var bogusBinary = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var ipc = await IpcWorker.StartAsync(
                bogusBinary,
                startupTimeout: TimeSpan.FromSeconds(5),
                lifetime: Lifetime.Daemon,
                ct: startCts.Token);

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<TimeoutException>(
                async () => await ipc.QueryAsync("Start-Sleep -Seconds 30"));
            sw.Stop();

            Assert.Contains("did not respond", ex.Message);
            // Bounded: ~1s call budget + ~750ms recovery probe + overhead. 8s is a
            // CI-safe ceiling that still proves we did not wait the full 30s.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8),
                $"per-call timeout must be bounded; took {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            try { serverCts.Cancel(); } catch { }
            try { if (serverTask is not null) await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, priorEndpoint);
            Environment.SetEnvironmentVariable("PSBASH_TIMEOUT", priorTimeout);
            CleanupEndpoint(scheme, endpoint);
        }
    }

    // ─── 3. Replace incompatible build/protocol host ────────────────────────

    /// <summary>
    /// Acceptance: existing incompatible build/protocol host with foreign
    /// ownership must NOT be silently retired by the launcher. The launcher
    /// classifies the sidecar as UnsafeToTouch and surfaces a
    /// HostUnavailableException naming the ownership conflict, rather than
    /// killing a process it does not own.
    ///
    /// We pre-write a sidecar with a bogus owner string and start a fake
    /// server that returns a non-matching build payload (Obsolete state at
    /// the wire level). The launcher reads the sidecar, sees foreign owner,
    /// and refuses cleanup — exactly the safety behavior the lifecycle
    /// contract requires. Real "obsolete + owned + replaced" coverage lands
    /// in cross-process integration; this test pins the launcher's safety
    /// gate so a future refactor cannot regress to silent kill.
    /// </summary>
    [Fact]
    public async Task ObsoleteBuildHost_ForeignOwner_LauncherRefusesCleanup()
    {
        var (spec, scheme, endpoint) = NewIsolatedEndpoint("obsolete");
        var prior = Environment.GetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar);
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, spec);

        using var fakeCts = new CancellationTokenSource();
        Task? fakeTask = null;
        await using var transport = NewTransport(scheme, endpoint);
        try
        {
            await transport.ListenAsync(fakeCts.Token);

            // Sidecar must NOT match current owner so Classify yields
            // UnsafeToTouch — that's the strict obsolete-without-ownership
            // branch we want to surface (HostUnavailableException with
            // "Cannot replace ... — sidecar owner ... does not match").
            var meta = new HostMetadata(
                Pid: Environment.ProcessId, // alive
                ExecutablePath: Environment.ProcessPath ?? "<unknown>",
                ProtocolVersion: HostProtocol.ProtocolVersion,
                BuildIdentity: HostProtocol.BuildIdentity,
                TransportScheme: scheme,
                Endpoint: endpoint,
                StartedAt: DateTimeOffset.UtcNow,
                Owner: "different-owner-9d3f1c"); // intentionally bogus
            meta.Write(scheme, endpoint);

            // Fake server: respond to Health with a non-matching payload so
            // launcher classifies as Obsolete. We need to actually accept
            // connections so the wire-level probe gets a response.
            fakeTask = Task.Run(async () =>
            {
                while (!fakeCts.IsCancellationRequested)
                {
                    Stream? s = null;
                    try
                    {
                        s = await transport.AcceptAsync(fakeCts.Token);
                        _ = await HostProtocol.ReadRequestAsync(s, fakeCts.Token);
                        // Send a "ps-bash-host ..." line that does NOT match
                        // HealthPayload — Obsolete.
                        await HostProtocol.WriteResponseLineAsync(s, "ps-bash-host protocol=999 build=obsolete-xyz", fakeCts.Token);
                        await HostProtocol.WriteExitAsync(s, 0, fakeCts.Token);
                    }
                    catch { /* connection closed / cancellation */ }
                    finally { try { s?.Dispose(); } catch { } }
                }
            }, fakeCts.Token);

            var bogusBinary = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ex = await Assert.ThrowsAsync<HostUnavailableException>(async () =>
            {
                await using var ipc = await IpcWorker.StartAsync(
                    bogusBinary,
                    startupTimeout: TimeSpan.FromSeconds(2),
                    lifetime: Lifetime.Daemon,
                    ct: startCts.Token);
            });
            // The exception message must surface the ownership-mismatch reason
            // from HostOwnership.Classify (proves we hit the obsolete-handling
            // path that consults the sidecar).
            Assert.Contains("different-owner-9d3f1c", ex.Message);
        }
        finally
        {
            try { fakeCts.Cancel(); } catch { }
            try { if (fakeTask is not null) await fakeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, prior);
            CleanupEndpoint(scheme, endpoint);
        }
    }

    // ─── 1. Concurrent clients race — exactly one healthy host wins ─────────

    /// <summary>
    /// Acceptance: many concurrent clients race to start the host; exactly one
    /// healthy server wins. We start a single in-process HostServer on an
    /// isolated endpoint, then race N IpcWorker.StartAsync calls against it.
    /// All must succeed (Healthy initial probe → reuse). All must produce
    /// correct command output, proving they share the same backend.
    ///
    /// This exercises the "Reusable" classification under concurrent load —
    /// the contract that a healthy host isn't accidentally retired by a
    /// concurrent launcher's classification path.
    /// </summary>
    [Fact]
    public async Task ConcurrentClients_RaceForHealthyHost_ExactlyOneServes()
    {
        var (spec, scheme, endpoint) = NewIsolatedEndpoint("race");
        var prior = Environment.GetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar);
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, spec);

        await using var transport = NewTransport(scheme, endpoint);
        await using var worker = SdkWorker.Create();
        await using var server = new HostServer(transport, Task.FromResult(worker));
        using var serverCts = new CancellationTokenSource();
        Task? serverTask = null;
        var clients = new List<IpcWorker>();
        try
        {
            serverTask = server.RunAsync(serverCts.Token);
            await server.WhenListening.WaitAsync(TimeSpan.FromSeconds(5));

            const int N = 8;
            var bogusBinary = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Launch N parallel StartAsync calls. All should succeed because
            // the in-process HostServer is healthy on the canonical endpoint.
            var startTasks = Enumerable.Range(0, N)
                .Select(i => IpcWorker.StartAsync(
                    bogusBinary,
                    startupTimeout: TimeSpan.FromSeconds(10),
                    lifetime: Lifetime.Daemon,
                    ct: startCts.Token))
                .ToArray();
            var workers = await Task.WhenAll(startTasks);
            clients.AddRange(workers);
            Assert.Equal(N, workers.Length);

            // All clients must be able to send a command through and get
            // their unique marker back, proving exactly one backend served
            // every connection (no fork into separate hosts).
            var queryTasks = workers
                .Select((w, i) => w.QueryAsync($"Invoke-BashEcho 'race-{i}'", startCts.Token))
                .ToArray();
            var outputs = await Task.WhenAll(queryTasks);
            for (int i = 0; i < N; i++)
                Assert.Contains($"race-{i}", outputs[i]);
        }
        finally
        {
            foreach (var c in clients)
                try { await c.DisposeAsync(); } catch { }
            try { serverCts.Cancel(); } catch { }
            try { if (serverTask is not null) await serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, prior);
            CleanupEndpoint(scheme, endpoint);
        }
    }

    // ─── 6. Idle shutdown still exits ───────────────────────────────────────

    /// <summary>
    /// Acceptance: idle shutdown still exits after PSBASH_HOST_IDLE_SECS when no
    /// connections remain. We spawn the real ps-bash-host binary with
    /// PSBASH_HOST_IDLE_SECS=1 on an isolated endpoint, wait for it to come up,
    /// make zero connections, and verify the process exits within a CI-safe
    /// budget (15s — covers cold runspace init + 1s idle + Process exit).
    ///
    /// Skipped if the host binary cannot be located (e.g. fresh checkout
    /// without a build).
    /// </summary>
    [SkippableFact]
    public async Task IdleShutdown_NoConnections_HostExitsAfterIdleSecs()
    {
        var hostBinary = TryLocateHostBinary();
        Skip.If(hostBinary is null, "ps-bash-host binary not found — build src/PsBash.Host first");

        var (spec, scheme, endpoint) = NewIsolatedEndpoint("idle");
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = hostBinary!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--ipc-endpoint");
            psi.ArgumentList.Add(spec);
            psi.Environment["PSBASH_HOST_IDLE_SECS"] = "1";
            // Disable parent-death watcher: testhost is the parent, and if testhost
            // exits during the wait the process should exit anyway. We just need
            // the idle path to be the dominant exit cause.
            psi.Environment["PSBASH_HOST_PARENT_PID"] = Environment.ProcessId.ToString();

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");

            // Wait for the host to exit on idle. Cold runspace init takes a
            // few seconds; with 1s idle timer firing right after listen-ready,
            // total time should be well under 15s.
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await proc.WaitForExitAsync(waitCts.Token);
            }
            catch (OperationCanceledException)
            {
                // proc still running → idle shutdown failed.
                Assert.Fail($"Host PID {proc.Id} did not exit within idle window. " +
                            $"endpoint={spec}");
            }

            // Exit code must be 0 (clean idle exit).
            Assert.Equal(0, proc.ExitCode);
        }
        finally
        {
            if (proc is not null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                try { proc.Dispose(); } catch { }
            }
            CleanupEndpoint(scheme, endpoint);
        }
    }

    // ─── REFACTOR-7: PerInvocation host lifetime ────────────────────────────

    /// <summary>
    /// Returns true if a process with <paramref name="pid"/> is currently alive.
    /// Mirrors ParentDeathWatcher.IsAlive — GetProcessById throws ArgumentException
    /// for a dead/unknown pid.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// REFACTOR-7 acceptance: a <see cref="Lifetime.PerInvocation"/> worker
    /// spawns a private <c>ps-bash-host</c> and kills it on dispose — the host
    /// never outlives its single client. We start the worker, ask the host for
    /// its own PID (<c>$PID</c> on the host runspace), confirm the process is
    /// alive while the worker is open, dispose the worker, then confirm the
    /// process is gone within a CI-safe budget.
    ///
    /// Oracle note (Directive 1): no bash oracle — ps-bash IPC host lifetime is
    /// outside the bash compatibility surface and is exercised against the
    /// .NET-side wire contract directly.
    /// </summary>
    [SkippableFact]
    public async Task PerInvocation_DisposeWorker_KillsPrivateHost()
    {
        var hostBinary = TryLocateHostBinary();
        Skip.If(hostBinary is null, "ps-bash-host binary not found — build src/PsBash.Host first");

        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int hostPid;

        var worker = await IpcWorker.StartAsync(
            hostBinary!,
            startupTimeout: TimeSpan.FromSeconds(20),
            lifetime: Lifetime.PerInvocation,
            ct: startCts.Token);
        try
        {
            // $PID on the host runspace is the ps-bash-host process id.
            var pidText = await worker.QueryAsync("$PID", startCts.Token);
            Assert.True(int.TryParse(pidText.Trim(), out hostPid),
                $"expected a numeric host PID from $PID, got: '{pidText}'");
            Assert.True(IsProcessAlive(hostPid),
                $"private host PID {hostPid} must be alive while the worker is open");
        }
        finally
        {
            await worker.DisposeAsync();
        }

        // DisposeAsync kills the host tree. Poll briefly for the process to go
        // away — Kill is synchronous but OS process teardown is not instant.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && IsProcessAlive(hostPid))
            await Task.Delay(100);

        Assert.False(IsProcessAlive(hostPid),
            $"PerInvocation host PID {hostPid} must be killed when the worker is disposed");
    }

    /// <summary>
    /// REFACTOR-7 acceptance: two concurrent <see cref="Lifetime.PerInvocation"/>
    /// workers each get their OWN private host on a process-local endpoint — they
    /// do NOT share the per-user daemon socket. Proven by asking each host for
    /// its <c>$PID</c>: distinct PIDs ⇒ distinct host processes ⇒ no shared
    /// daemon. Each host also gets a clean session by construction, which is the
    /// state-isolation property the refactor buys.
    /// </summary>
    [SkippableFact]
    public async Task PerInvocation_TwoWorkers_GetDistinctPrivateHosts()
    {
        var hostBinary = TryLocateHostBinary();
        Skip.If(hostBinary is null, "ps-bash-host binary not found — build src/PsBash.Host first");

        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var workerA = await IpcWorker.StartAsync(
            hostBinary!, startupTimeout: TimeSpan.FromSeconds(20),
            lifetime: Lifetime.PerInvocation, ct: startCts.Token);
        var workerB = await IpcWorker.StartAsync(
            hostBinary!, startupTimeout: TimeSpan.FromSeconds(20),
            lifetime: Lifetime.PerInvocation, ct: startCts.Token);
        try
        {
            var pidA = (await workerA.QueryAsync("$PID", startCts.Token)).Trim();
            var pidB = (await workerB.QueryAsync("$PID", startCts.Token)).Trim();

            Assert.True(int.TryParse(pidA, out _), $"worker A host PID not numeric: '{pidA}'");
            Assert.True(int.TryParse(pidB, out _), $"worker B host PID not numeric: '{pidB}'");
            Assert.NotEqual(pidA, pidB);
        }
        finally
        {
            await workerA.DisposeAsync();
            await workerB.DisposeAsync();
        }
    }

    /// <summary>
    /// REFACTOR-7 acceptance: <see cref="IpcTransportFactory.ResolvePerInvocationEndpoint"/>
    /// returns a process-local endpoint that is NOT the shared per-user daemon
    /// endpoint (<see cref="IpcTransportFactory.ResolveEndpoint()"/>), and two
    /// calls never collide. This is the unit-level proof that a PerInvocation
    /// host binds a private socket — no real spawn needed.
    /// </summary>
    [Fact]
    public void ResolvePerInvocationEndpoint_IsPrivate_AndUnique()
    {
        var (sharedScheme, sharedEndpoint) = IpcTransportFactory.ResolveEndpoint();

        var (s1, e1) = IpcTransportFactory.ResolvePerInvocationEndpoint();
        var (s2, e2) = IpcTransportFactory.ResolvePerInvocationEndpoint();

        // Same scheme as the shared endpoint (unix on POSIX, pipe on legacy Win)
        // but a DIFFERENT endpoint name — never the canonical daemon socket.
        Assert.Equal(sharedScheme, s1);
        Assert.Equal(sharedScheme, s2);
        Assert.NotEqual(sharedEndpoint, e1);
        Assert.NotEqual(sharedEndpoint, e2);

        // Two per-invocation endpoints never collide.
        Assert.NotEqual(e1, e2);

        // The per-invocation name carries the launcher PID so concurrent
        // launchers are disambiguated even before the random suffix.
        Assert.Contains(Environment.ProcessId.ToString(), e1);
        Assert.Contains(Environment.ProcessId.ToString(), e2);
    }
}
