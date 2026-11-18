using PsBash.Core.Runtime.Processes;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Contract of <see cref="JobProcess"/> (docs/specs/background-jobs.md §6): the same assertions run on
/// Windows (Job Object) and POSIX (process group). Each test starts a real child.
/// </summary>
public class JobProcessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>A leader that spawns one long-lived grandchild, then lingers.</summary>
    private static JobProcessStart TreeWithGrandchild(string pidFile)
        => OperatingSystem.IsWindows()
            ? new("cmd.exe", ["/c", "ping -n 120 127.0.0.1 >nul"])
            : new("/bin/sh", ["-c", $"sleep 120 & echo $! > '{pidFile}'; wait"]);

    private static JobProcessStart ExitWith(int code)
        => OperatingSystem.IsWindows()
            ? new("cmd.exe", ["/c", $"exit {code}"])
            : new("/bin/sh", ["-c", $"exit {code}"]);

    private static bool Alive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<int> GrandchildPid(JobProcess job, string pidFile)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (OperatingSystem.IsWindows())
            {
                var other = job.TreePids().Where(p => p != job.Pid).ToList();
                if (other.Count > 0) return other[0];
            }
            else if (PsBash.Testing.PublishedFile.TryRead(pidFile) is { } text && int.TryParse(text.Trim(), out var pid))
                return pid;
            await Task.Delay(50);
        }
        throw new TimeoutException("grandchild never appeared");
    }

    private static async Task WaitUntilGone(int pid)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (Alive(pid) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(Alive(pid), $"pid {pid} still running");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ExitStatus_IsTheLeadersCode(int code)
    {
        using var job = JobProcess.Start(ExitWith(code));
        Assert.Equal(code, await job.Exited.WaitAsync(Wait));
        Assert.True(job.HasExited);
    }

    // REGRESSION (macOS CI since d4ad485f: `ps` listed no process, its /bin/ps lookup never started): a setuid child
    // runs as root, so reading its start time right after the spawn is refused (EPERM), and the spawn threw it,
    // leaving the child unreaped. The identity comes from ProcessIdentity.Of, which never throws.
    [SkippableFact]
    public async Task Start_ASetuidProgram_RunsAndReportsItsExit()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "macOS /bin/ps is setuid root; Linux and Windows let the parent read its child's start time");
        using var job = JobProcess.Start(new("/bin/ps", ["-p", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        Assert.Equal(0, await job.Exited.WaitAsync(Wait));
    }

    [Fact]
    public void Start_UnknownProgram_ThrowsHostUnavailable()
        => Assert.Throws<PsBash.Core.Runtime.HostUnavailableException>(
            () => JobProcess.Start(new("definitely-not-a-real-program-" + Guid.NewGuid().ToString("N"), [])));

    /// <summary>
    /// PID reuse: a recorded identity must not match a DIFFERENT process that now owns the same pid. The pid here is
    /// live (this test process), the recorded start time is not its own, so a pid-only check would wrongly say "running".
    /// </summary>
    [Fact]
    public void Identity_SamePidDifferentStartTime_IsNotTheSameProcess()
    {
        long realStart = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        var stale = new PsBash.Core.Runtime.Ipc.ProcessIdentity(Environment.ProcessId, realStart - TimeSpan.FromDays(1).Ticks);
        Assert.False(stale.IsRunning());
    }

    [Fact]
    public async Task Identity_MatchesTheLiveProcess_AndStopsMatchingAfterExit()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "jp-" + Guid.NewGuid().ToString("N"));
        using var job = JobProcess.Start(TreeWithGrandchild(pidFile));
        Assert.Equal(job.Pid, job.Identity.Pid);
        Assert.True(job.Identity.IsRunning());
        job.KillTree();
        await job.Exited.WaitAsync(Wait);
        Assert.False(job.Identity.IsRunning());
        File.Delete(pidFile);
    }

    [Fact]
    public async Task KillTree_KillsGrandchildrenToo()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "jp-" + Guid.NewGuid().ToString("N"));
        using var job = JobProcess.Start(TreeWithGrandchild(pidFile));
        var grandchild = await GrandchildPid(job, pidFile);
        Assert.True(Alive(grandchild));

        Assert.True(job.KillTree());

        await job.Exited.WaitAsync(Wait);
        await WaitUntilGone(grandchild);
        File.Delete(pidFile);
    }

    [Fact]
    public async Task Terminate_ReportsSignalStatus()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "jp-" + Guid.NewGuid().ToString("N"));
        using var job = JobProcess.Start(TreeWithGrandchild(pidFile));
        await GrandchildPid(job, pidFile);

        Assert.True(job.Signal(JobSignal.Terminate));

        // POSIX: 128+SIGTERM. Windows: terminate is an immediate tree kill with status 143.
        Assert.Equal(143, await job.Exited.WaitAsync(Wait));
        File.Delete(pidFile);
    }

    [Fact]
    public async Task Dispose_KillsTheTree_UnlessReleased()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "jp-" + Guid.NewGuid().ToString("N"));
        int grandchild;
        var job = JobProcess.Start(TreeWithGrandchild(pidFile));
        grandchild = await GrandchildPid(job, pidFile);
        job.Dispose();
        await WaitUntilGone(grandchild);

        // Released: Dispose only frees handles; the job keeps running until we kill it by hand.
        var pidFile2 = pidFile + "-2";
        var kept = JobProcess.Start(TreeWithGrandchild(pidFile2));
        var keptGrandchild = await GrandchildPid(kept, pidFile2);
        var keptPid = kept.Pid;
        kept.Release();
        kept.Dispose();
        await Task.Delay(300);
        Assert.True(Alive(keptPid));
        Assert.True(Alive(keptGrandchild));

        // cleanup of what we deliberately leaked
        if (OperatingSystem.IsWindows())
            foreach (var pid in new[] { keptGrandchild, keptPid }) try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
        else
            System.Diagnostics.Process.Start("kill", ["-9", "--", $"-{keptPid}"])!.WaitForExit();
        await WaitUntilGone(keptPid);
        File.Delete(pidFile);
        File.Delete(pidFile2);
    }

    [Fact]
    public async Task SuspendResume_FreezesThenThawsTheJob()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "jp-" + Guid.NewGuid().ToString("N"));
        using var job = JobProcess.Start(TreeWithGrandchild(pidFile));
        await GrandchildPid(job, pidFile);

        Assert.True(job.Suspend());
        await Task.Delay(200);
        Assert.False(job.HasExited);
        Assert.True(job.Resume());

        job.KillTree();
        await job.Exited.WaitAsync(Wait);
        File.Delete(pidFile);
    }

    [Fact]
    public async Task Environment_OverridesAndRemovals_ReachTheChild()
    {
        if (OperatingSystem.IsWindows()) // cmd: exit code = length of the variable's value, 99 when unset
        {
            using var set = JobProcess.Start(new("cmd.exe", ["/c", "if defined JP_TEST (exit 42) else (exit 99)"],
                new Dictionary<string, string?> { ["JP_TEST"] = "x" }));
            Assert.Equal(42, await set.Exited.WaitAsync(Wait));
        }
        else
        {
            using var set = JobProcess.Start(new("/bin/sh", ["-c", "[ \"$JP_TEST\" = x ] && exit 42; exit 99"],
                new Dictionary<string, string?> { ["JP_TEST"] = "x" }));
            Assert.Equal(42, await set.Exited.WaitAsync(Wait));
        }
    }
}
