using System.Diagnostics;
using PsBash.Testing;
using Xunit;

namespace PsBash.Canary.Tests;

/// <summary>
/// Canary regression for DART-E84SFIVCE6zL — "Fix stale ps-bash-host processes
/// piling up on Windows". Verifies the layered defenses against orphaned hosts:
///
///   1. <c>ParentDeathWatcher</c> (host-side, cross-platform): when the
///      launcher dies, the host's 200ms poll observes the dead parent PID and
///      self-terminates.
///   2. <c>ps-bash host gc</c> admin subcommand: belt-and-suspenders cleanup
///      that enumerates ps-bash-host processes and kills those whose parent
///      launcher PID is dead.
///
/// Per qa-rubric Directive 8 (canary suite — one test per failure surface):
/// the host-orphan failure surface is the launcher-crash → host-leak path.
/// Per Directive 13 (known-bad memory): windows_process_death has a permanent
/// regression test here AND in PsBash.Shell.Tests.ProcessLifecycleTests
/// (Windows-only, more thorough).
///
/// Cross-platform: this canary exercises the gc admin subcommand, which works
/// identically on Linux and Windows (POSIX uses /proc/{pid}/stat, Windows uses
/// NtQueryInformationProcess). The Windows-specific Job Object KillOnJobClose
/// path is covered by PsBash.Shell.Tests.ProcessLifecycleTests on the Windows
/// CI runner.
/// </summary>
[Trait("Category", "Canary")]
public sealed class CanaryHostGcTests
{
    private static string? PsBashPath => PsBashLocator.Resolve();

    /// <summary>
    /// Spawn a ps-bash-host with a bogus <c>--launcher-pid</c> pointing at a
    /// PID that has already exited (and was not recycled in the &lt;200 ms
    /// window before the watcher's first poll — overwhelmingly likely on any
    /// modern OS with 32-bit PIDs). Assert the host self-terminates within
    /// 10 seconds via its ParentDeathWatcher.
    ///
    /// macOS skip: <see cref="JobObjectWatchdog.GetParentProcessIdByPid"/> is
    /// not implemented on macOS, so the gc-side assertion would be unreliable.
    /// The host-side ParentDeathWatcher itself uses portable
    /// <c>Process.GetProcessById</c> and is exercised on macOS via other tests
    /// (the cross-platform PerInvocation IpcWorker DisposeAsync path).
    /// </summary>
    [SkippableFact]
    public async Task OrphanHost_ParentDeathWatcher_SelfTerminatesWithin10s()
    {
        Skip.If(PsBashPath is null, "ps-bash launcher not built");
        Skip.If(OperatingSystem.IsMacOS(), "macOS: GetParentProcessIdByPid is not implemented (use PerInvocation IpcWorker test instead)");

        // Find the host binary that sits alongside ps-bash.
        var hostBin = ResolveHostBinary(PsBashPath!);
        Skip.If(hostBin is null, "ps-bash-host binary not found next to launcher");

        // Spawn a short-lived dummy "parent" we'll let exit immediately.
        int deadPid;
        using (var dummy = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/true",
            Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            await dummy.WaitForExitAsync();
            deadPid = dummy.Id;
        }

        // Race-window note: between deadPid being reaped and the host's first
        // 200 ms poll, the OS could in principle recycle this PID. On Linux
        // PIDs are 32-bit and start fresh at low values; on Windows PIDs are
        // multiples of 4 and slow-rolling. In practice the recycle window is
        // far longer than 200 ms. If this canary ever flakes here, add a
        // sentinel watcher.

        var endpoint = Guid.NewGuid().ToString("N")[..8];
        var endpointArg = OperatingSystem.IsWindows()
            ? $"--ipc-endpoint=pipe:psbash-canary-orphan-{endpoint}"
            : $"--ipc-endpoint=uds:{Path.Combine(Path.GetTempPath(), "ps-bash", $"canary-orphan-{endpoint}.sock")}";

        var hostPsi = new ProcessStartInfo
        {
            FileName = hostBin,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        hostPsi.ArgumentList.Add(endpointArg);
        hostPsi.ArgumentList.Add($"--launcher-pid={deadPid}");

        using var host = Process.Start(hostPsi)!;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.WaitForExitAsync(cts.Token);
            Assert.True(host.HasExited,
                $"ps-bash-host with dead --launcher-pid={deadPid} did not self-terminate within 10s");
        }
        catch (OperationCanceledException)
        {
            // Capture diagnostics for the qa-rubric Directive 9 artifact bundle.
            var stderr = host.HasExited ? await host.StandardError.ReadToEndAsync() : "(still running)";
            Assert.Fail($"ParentDeathWatcher did not fire within 10s. host.HasExited={host.HasExited} stderr={stderr}");
        }
        finally
        {
            if (!host.HasExited)
            {
                try { host.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    /// <summary>
    /// Verify <c>ps-bash host gc --dry-run</c> runs cleanly with exit 0 and
    /// produces a parseable summary line. The dry-run path exercises process
    /// enumeration + parent-PID lookup without side effects, so it is safe to
    /// run in CI alongside other tests that spawn ps-bash-host instances.
    /// </summary>
    [SkippableFact]
    public async Task HostGc_DryRun_ExitsZeroWithSummary()
    {
        Skip.If(PsBashPath is null, "ps-bash launcher not built");

        var psi = new ProcessStartInfo
        {
            FileName = PsBashPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("host");
        psi.ArgumentList.Add("gc");
        psi.ArgumentList.Add("--dry-run");

        using var proc = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(cts.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(proc.HasExited, "ps-bash host gc --dry-run hung");
        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("gc:", stdout);
        Assert.Contains("would-kill=", stdout);
        // Sanity: should NOT have killed anything on the dry-run path.
        Assert.DoesNotContain("failed to kill", stderr);
    }

    private static string? ResolveHostBinary(string psBashPath)
    {
        var dir = Path.GetDirectoryName(psBashPath);
        if (dir is null) return null;
        var binName = OperatingSystem.IsWindows() ? "ps-bash-host.exe" : "ps-bash-host";
        var sxs = Path.Combine(dir, binName);
        if (File.Exists(sxs)) return sxs;
        // Fall back to PsBash.Host/bin/<config>/<tfm>/
        var hostBin = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PsBash.Host", "bin", "Debug", "net10.0", binName));
        return File.Exists(hostBin) ? hostBin : null;
    }
}
