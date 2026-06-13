using System.Diagnostics;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// xUnit collection for host-startup STRESS tests — the CPU-heavy ones that
/// spawn real <c>ps-bash-host</c> processes and/or race many launchers from
/// cold. Isolated for two reasons:
///   1. <c>DisableParallelization</c> serializes them so they do not herd the
///      box against each other (the stress suite must not reproduce the very
///      CPU saturation it exists to test against).
///   2. Every test here carries <c>[Trait("Category","Stress")]</c> so
///      <c>scripts/test.sh</c> excludes them from the default fast run and only
///      executes them on an explicit opt-in (<c>--stress</c> / PSBASH_STRESS=1).
/// </summary>
[CollectionDefinition("HostStartupStress", DisableParallelization = true)]
public sealed class HostStartupStressCollection { }

/// <summary>
/// Concurrent host-startup stress coverage for <see cref="IpcWorker"/>'s
/// shared-daemon path. Proves single-flight spawn: N launchers racing to start
/// the canonical (per-session) daemon FROM COLD spawn exactly one host, not N (which would
/// orphan N-1 runspaces via UnixSocketTransport's unlink-before-bind or the
/// Windows 16-instance pipe).
///
/// Oracle note (qa-rubric Directive 1): IPC host lifecycle is outside the bash
/// compatibility surface; asserted against the .NET wire contract directly.
///
/// Reliability contract (testing.md / qa-rubric Directive 6): each test isolates
/// its endpoint via PSBASH_IPC_ENDPOINT so it never races the user's canonical
/// daemon; every spawned host is killed with entireProcessTree=true in finally;
/// every wait has a hard CI-safe deadline.
/// </summary>
[Collection("HostStartupStress")]
[Trait("Category", "Stress")]
public sealed class HostStartupStressTests
{
    /// <summary>
    /// Acceptance: N concurrent <see cref="Lifetime.Daemon"/> launchers racing to
    /// start the shared host from cold (no host running) spawn EXACTLY ONE host.
    /// Asserted two ways that together hold on both transports:
    ///   • <see cref="IpcWorker.SpawnCounts"/> for this (unique) endpoint == 1 —
    ///     the deterministic, endpoint-scoped single-flight proof (catches the
    ///     unix unlink-before-bind orphan race even when all connects land on the
    ///     last binder).
    ///   • Every launcher's <c>$PID</c> is identical — one backend serves all
    ///     (catches the Windows multi-instance-pipe split-state race).
    /// Without single-flight, SpawnCounts would be N and (on Windows) the PIDs
    /// would diverge.
    /// </summary>
    [SkippableFact]
    public async Task ColdStartHerd_ConcurrentDaemonLaunchers_SpawnExactlyOneHost()
    {
        var hostBinary = TryLocateHostBinary();
        Skip.If(hostBinary is null, "ps-bash-host binary not found — build src/PsBash.Host first");

        var (spec, scheme, endpoint) = NewIsolatedEndpoint("herd");
        var prior = Environment.GetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar);
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, spec);
        var spawnKey = $"{scheme}:{endpoint}";
        IpcWorker.SpawnCounts.TryRemove(spawnKey, out _);

        const int N = 6;
        var workers = new List<IpcWorker>();
        string? hostPid = null;
        try
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

            // Launch N concurrent cold starts against the SAME shared endpoint.
            var startTasks = Enumerable.Range(0, N)
                .Select(_ => IpcWorker.StartAsync(
                    hostBinary!,
                    startupTimeout: TimeSpan.FromSeconds(20),
                    lifetime: Lifetime.Daemon,
                    ct: startCts.Token))
                .ToArray();
            var started = await Task.WhenAll(startTasks);
            workers.AddRange(started);
            Assert.Equal(N, started.Length);

            // Every launcher must reach a host and report the SAME pid — one shared
            // backend served them all (no fork into N hosts).
            var pids = new List<string>(N);
            foreach (var w in started)
                pids.Add((await w.QueryAsync("$PID", startCts.Token)).Trim());
            hostPid = pids[0];
            Assert.All(pids, p =>
            {
                Assert.True(int.TryParse(p, out _), $"expected numeric host PID, got '{p}'");
                Assert.Equal(hostPid, p);
            });

            // Deterministic single-flight proof: exactly one launcher ran
            // Process.Start for this endpoint.
            IpcWorker.SpawnCounts.TryGetValue(spawnKey, out var spawnCount);
            Assert.True(spawnCount == 1,
                $"single-flight must spawn exactly one host from a cold concurrent race; " +
                $"SpawnCounts['{spawnKey}']={spawnCount} (N={N}).");
        }
        finally
        {
            foreach (var w in workers)
                try { await w.DisposeAsync(); } catch { }

            // Daemon hosts are NOT killed on worker dispose — terminate the shared
            // host we spawned so it does not linger past the test.
            if (hostPid is not null && int.TryParse(hostPid, out var pid))
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            }
            IpcWorker.SpawnCounts.TryRemove(spawnKey, out _);
            Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, prior);
            CleanupEndpoint(scheme, endpoint);
        }
    }

    // ─── Helpers (kept local so the stress suite is self-contained) ─────────────

    private static (string Spec, string Scheme, string Endpoint) NewIsolatedEndpoint(string label)
    {
        var unique = $"{label}-{Guid.NewGuid():N}";
        if (OperatingSystem.IsWindows())
        {
            var name = $"psbash-{unique}";
            return ($"pipe:{name}", "pipe", name);
        }
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"host-{unique}.sock");
        return ($"unix:{path}", "unix", path);
    }

    private static void CleanupEndpoint(string scheme, string endpoint)
    {
        try { HostMetadata.Remove(scheme, endpoint); } catch { }
        try { IpcTransportFactory.RetireEndpoint(scheme, endpoint); } catch { }
    }

    private static string? TryLocateHostBinary()
    {
        var name = OperatingSystem.IsWindows() ? "ps-bash-host.exe" : "ps-bash-host";
        var asmDir = Path.GetDirectoryName(typeof(HostStartupStressTests).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && dir.Name != "src" && dir.Parent is not null)
            dir = dir.Parent;
        if (dir is null) return null;
        var hostBinDir = Path.Combine(dir.FullName, "PsBash.Host", "bin");
        if (!Directory.Exists(hostBinDir)) return null;
        return Directory.EnumerateFiles(hostBinDir, name, SearchOption.AllDirectories)
            .Where(p => !p.Contains("win-x64") || OperatingSystem.IsWindows())
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
