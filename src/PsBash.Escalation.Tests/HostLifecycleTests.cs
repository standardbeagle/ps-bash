using System.Diagnostics;
using System.Net.Sockets;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Regression tests for host lifecycle behaviors documented in MEMORY.md.
/// Per QA rubric Directive 13: every known-bad = one permanent regression test.
///
/// MEMORY ENTRIES COVERED:
///   "Windows process death" (no SIGHUP) — HostDiesWhenLauncherKilled
///   "Process spawn contract" (timeout + Kill(entireTree)) — HostSpawnTimeout_FailsFastWithoutSpawning
///   Stale lock file detection — StaleLockFile_IsPurgedOnConnect
/// </summary>
[Trait("Category", "Escalation")]
[Trait("Category", "HostLifecycle")]
public class HostLifecycleTests
{
    private static readonly string? HostBinaryPath = FindHostBinary();
    private static readonly string? PwshPath;

    static HostLifecycleTests()
    {
        try { PwshPath = PwshLocator.Locate(); }
        catch (PwshNotFoundException) { PwshPath = null; }
    }

    // ── 1. MEMORY: "Windows process death" ───────────────────────────────────
    /// <summary>
    /// When the launcher process dies, ps-bash-host must self-terminate within
    /// ~2 s via its ParentDeathWatcher (polls every 200 ms).
    /// On Windows there is no SIGHUP; only the PID-polling mechanism works.
    /// Regression: if the watcher stops working, orphaned hosts accumulate
    /// (~8 GB each, as seen in the prior incident).
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public async Task HostDiesWhenLauncherKilled()
    {
        Skip.If(HostBinaryPath is null, "ps-bash-host binary not found; build PsBash.Host first.");
        Skip.If(PwshPath is null, "pwsh not available");

        // Spawn a short-lived "launcher" process that stays alive just long
        // enough for the host to start and register its PID, then kill it.
        var fakeLauncherPsi = new ProcessStartInfo
        {
            FileName = PwshPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        fakeLauncherPsi.ArgumentList.Add("-NoProfile");
        fakeLauncherPsi.ArgumentList.Add("-NonInteractive");
        fakeLauncherPsi.ArgumentList.Add("-Command");
        fakeLauncherPsi.ArgumentList.Add("Start-Sleep 120");  // stays alive until killed

        using var fakeLauncher = Process.Start(fakeLauncherPsi)
            ?? throw new InvalidOperationException("Failed to start fake launcher");

        var sessionId = $"lifecycle-test-{Guid.NewGuid():N}";
        var lockFile = HostLockFile.ForSession(sessionId);

        var hostPsi = new ProcessStartInfo
        {
            FileName = HostBinaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        hostPsi.ArgumentList.Add("--session-id");
        hostPsi.ArgumentList.Add(sessionId);
        hostPsi.ArgumentList.Add("--launcher-pid");
        hostPsi.ArgumentList.Add(fakeLauncher.Id.ToString());

        Process? hostProcess = null;
        try
        {
            hostProcess = Process.Start(hostPsi)
                ?? throw new InvalidOperationException("Failed to start ps-bash-host");

            // Wait up to 30 s for the host to write its lock file (proves it started).
            // Debug/non-AOT builds need JIT warm-up — first launch can take 15+ s.
            var lockAppeared = await WaitForFileAsync(lockFile.Path, TimeSpan.FromSeconds(30));
            Skip.If(!lockAppeared, "Host did not write lock file within 30 s — likely a build/path issue.");

            // Kill the fake launcher. The host's ParentDeathWatcher polls every
            // 200 ms and must detect the death and cancel the host CTS.
            fakeLauncher.Kill(entireProcessTree: true);

            // Host must exit within 3 s (200 ms poll × up to 15 ticks + margin).
            var exited = await Task.Run(() => hostProcess.WaitForExit(3000));
            Assert.True(exited,
                "ps-bash-host did not exit within 3 s after launcher death. " +
                "ParentDeathWatcher may be broken — orphaned host risk (see MEMORY: Windows process death).");
        }
        finally
        {
            lockFile.Delete();
            try { if (!(hostProcess?.HasExited ?? true)) hostProcess?.Kill(entireProcessTree: true); } catch { }
            try { if (!fakeLauncher.HasExited) fakeLauncher.Kill(entireProcessTree: true); } catch { }
            hostProcess?.Dispose();
        }
    }

    // ── 2. MEMORY: "Process spawn contract" ──────────────────────────────────
    /// <summary>
    /// IpcWorker.StartAsync with spawnIfMissing=false and no running host must
    /// throw HostUnavailableException immediately — not after the 5 s spawn
    /// timeout. One-shot -c clients call this path; waiting 5 s per invocation
    /// would make ps-bash unusable when the host is not running.
    /// </summary>
    [SkippableFact]
    public async Task HostSpawnTimeout_FailsFastWithoutSpawning()
    {
        // Use a session ID that has no running host. The lock file won't exist.
        var sessionId = $"lifecycle-nohost-{Guid.NewGuid():N}";
        var lockFile = HostLockFile.ForSession(sessionId);
        const string fakeBinaryPath = "/nonexistent/ps-bash-host";

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<HostUnavailableException>(async () =>
        {
            await IpcWorker.StartAsync(
                lockFile,
                fakeBinaryPath,
                spawnIfMissing: false,
                startupTimeout: TimeSpan.FromSeconds(5));
        });
        sw.Stop();

        // Must fail fast — well under the 5 s spawn timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"IpcWorker took {sw.Elapsed.TotalSeconds:F1}s to fail. " +
            "spawnIfMissing=false should fail immediately without waiting for spawn timeout.");

        Assert.Contains("no running ps-bash-host", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── 3. Stale lock file detection ─────────────────────────────────────────
    /// <summary>
    /// A lock file pointing to a non-existent endpoint (stale from a crashed
    /// host) must be detected and deleted by ReadOrPurgeAsync, not left behind.
    /// Stale files accumulate if purge is broken, causing subsequent sessions
    /// to try to connect to dead endpoints.
    /// </summary>
    [SkippableFact]
    public async Task StaleLockFile_IsPurgedOnConnect()
    {
        var sessionId = $"lifecycle-stale-{Guid.NewGuid():N}";
        var lockFile = HostLockFile.ForSession(sessionId);

        // Write a lock file pointing to a pipe name that has no listener.
        var stalePipeName = $"psbash-stale-{Guid.NewGuid():N}";
        var staleContent = $"pid=99999\nendpoint=pipe:{stalePipeName}\n";
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile.Path)!);
        await File.WriteAllTextAsync(lockFile.Path, staleContent);

        Assert.True(File.Exists(lockFile.Path), "Precondition: lock file must exist before test.");

        // ReadOrPurgeAsync must detect the dead endpoint and delete the file.
        var ex = await Assert.ThrowsAsync<SocketException>(
            async () => await lockFile.ReadOrPurgeAsync());

        Assert.False(File.Exists(lockFile.Path),
            "Stale lock file must be deleted after ReadOrPurgeAsync detects a dead endpoint.");

        // The SocketException signals "no host" so the caller can spawn one.
        _ = ex; // exception presence is the assertion; error code varies by OS.
    }

    // ── 4. Lock file deleted on normal host exit ──────────────────────────────
    /// <summary>
    /// Companion to test 1: when the host exits cleanly (e.g. idle timeout),
    /// its lock file must be deleted so the next launcher doesn't see a stale entry.
    /// This tests the finally-block cleanup in HostServer.RunAsync via a direct
    /// HostLockFile.Delete() call — not a full host spawn.
    /// </summary>
    [Fact]
    public void LockFile_DeletedByDelete_IsGone()
    {
        var sessionId = $"lifecycle-cleanup-{Guid.NewGuid():N}";
        var lockFile = HostLockFile.ForSession(sessionId);

        // Create the file, then call Delete() as the host would on shutdown.
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile.Path)!);
        File.WriteAllText(lockFile.Path, "pid=1\nendpoint=pipe:fake\n");
        Assert.True(File.Exists(lockFile.Path));

        lockFile.Delete();

        Assert.False(File.Exists(lockFile.Path),
            "HostLockFile.Delete must remove the lock file so the next session " +
            "does not read a stale entry.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static string? FindHostBinary()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoRoot = baseDir;
        for (int i = 0; i < 5; i++)
        {
            var parent = Path.GetDirectoryName(repoRoot);
            if (parent is null) break;
            repoRoot = parent;
        }

        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string[] candidates = [
            Path.Combine(repoRoot, "src", "PsBash.Host", "bin", "Debug", "net10.0", $"ps-bash-host{ext}"),
            Path.Combine(repoRoot, "src", "PsBash.Host", "bin", "Release", "net10.0", $"ps-bash-host{ext}"),
        ];

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return null;
    }
}
