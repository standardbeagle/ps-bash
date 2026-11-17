using PsBash.Testing;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A background job runs in its own host process (docs/specs/background-jobs.md), so it has bash's fork
/// semantics: it sees a copy of the parent's state and nothing it does leaks back. Expected values are bash's
/// (checked against Ubuntu bash): a hand-written assert, not <c>AssertOracle</c>, because the oracle fixture
/// cannot set the host-config switch <c>PSBASH_BG_MODE</c> and pids differ anyway.
/// </summary>
public class BackgroundJobProcessTests
{
    private static async Task<SpawnResult> RunAsync(string script)
    {
        var runner = PsBashRunner.Create().WithTimeout(60);
        Skip.If(runner.TryResolveBinary() is null, "ps-bash binary not found -- build PsBash.Shell first");
        return await runner.RunScriptAsync("export PSBASH_BG_MODE=process\n" + script);
    }

    [SkippableFact]
    public async Task Job_AssignmentDoesNotReachTheParent()
    {
        var r = await RunAsync("x=parent; { x=child; echo in-job; } & wait $!; echo \"x=$x\"");
        Assert.Equal("in-job\nx=parent\n", r.Stdout.Replace("\r\n", "\n"));
        Assert.Equal(0, r.ExitCode);
    }

    [SkippableFact]
    public async Task Job_ParentAssignmentAfterStartDoesNotReachTheJob()
    {
        var r = await RunAsync("x=before; { sleep 1; echo \"job:$x\"; } & x=after; wait; echo \"parent:$x\"");
        Assert.Equal("job:before\nparent:after\n", r.Stdout.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public async Task Wait_ReturnsTheJobsExitStatus_AndErrexitSeesIt()
    {
        var r = await RunAsync("false & wait $!; echo \"rc=$?\"; (exit 3) & wait $!; echo \"rc=$?\"");
        Assert.Equal("rc=1\nrc=3\n", r.Stdout.Replace("\r\n", "\n"));

        var e = await RunAsync("set -e; false & wait $!; echo SHOULD-NOT-RUN");
        Assert.DoesNotContain("SHOULD-NOT-RUN", e.Stdout);
        Assert.Equal(1, e.ExitCode);
    }

    [SkippableFact]
    public async Task Job_SeesFunctionsAndArraysOfTheParent()
    {
        var r = await RunAsync("f() { echo \"f:$1\"; }; arr=(a b c); f hi & wait; { echo \"arr=${arr[1]} n=${#arr[@]}\"; } & wait");
        Assert.Equal("f:hi\narr=b n=3\n", r.Stdout.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public async Task Job_PidIsARealProcess_AndKillStopsItAndReportsTheSignal()
    {
        var r = await RunAsync(
            "sleep 60 & p=$!; kill -0 $p && echo alive; kill $p; wait $p; echo \"rc=$?\"; " +
            "kill -0 $p 2>/dev/null && echo still-alive || echo gone");
        Assert.Equal("alive\nrc=143\ngone\n", r.Stdout.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public async Task Job_StdoutAndStderrReachTheirStreams_InOrderWithTheLineTerminator()
    {
        var r = await RunAsync("{ echo out1; echo err1 >&2; echo out2; } & wait; echo next");
        Assert.Equal("out1\nout2\nnext\n", r.Stdout.Replace("\r\n", "\n"));
        Assert.Equal("err1", r.Stderr.Trim());
    }

    [SkippableFact]
    public async Task Job_BeyondJobsMax_IsRefusedAndTheRunningJobIsUnaffected()
    {
        var r = await RunAsync("export PSBASH_JOBS_MAX=1; { sleep 2; echo first; } & { echo second; } & wait; echo done");
        Assert.Contains("too many background jobs", r.Stderr);
        Assert.Contains("first", r.Stdout);
        Assert.DoesNotContain("second", r.Stdout);
    }
    /// <summary>
    /// The launcher dies without cleanup (no tree kill, as a crash or a closed console would): the per-invocation
    /// host sees its launcher gone and exits, and the Job Object around each job host takes the job with it.
    /// Windows only: POSIX tree containment on a hard kill is not implemented (docs/specs/background-jobs.md Q3).
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public async Task Job_LauncherKilledWithoutCleanup_LeavesNoJobProcessBehind()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "hard-kill containment is Windows-only for now");
        var binary = PsBashRunner.Create().TryResolveBinary();
        Skip.If(binary is null, "ps-bash binary not found -- build PsBash.Shell first");

        var psi = new System.Diagnostics.ProcessStartInfo(binary!)
        {
            RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 120 & echo $!; sleep 120");
        psi.Environment["PSBASH_BG_MODE"] = "process";
        psi.Environment["PSBASH_PER_INVOCATION"] = "1";

        using var launcher = System.Diagnostics.Process.Start(psi)!;
        int jobPid = 0;
        try
        {
            var first = await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Assert.True(int.TryParse(first?.Trim(), out jobPid), $"expected the job pid, got '{first}'");
            Assert.True(IsAlive(jobPid), "the job host should be running before the launcher is killed");

            launcher.Kill(entireProcessTree: false);   // no cleanup: the hub must notice by itself

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (IsAlive(jobPid) && DateTime.UtcNow < deadline) await Task.Yield();
            Assert.False(IsAlive(jobPid), "the job host outlived its killed launcher");
        }
        finally
        {
            if (jobPid > 0 && IsAlive(jobPid))
            {
                try { using var p = System.Diagnostics.Process.GetProcessById(jobPid); p.Kill(entireProcessTree: true); } catch { }
            }
            try { if (!launcher.HasExited) launcher.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [SkippableFact]
    public async Task Job_ManyShortJobs_AllReapedAndNoneLeftBehind()
    {
        var r = await RunAsync("for i in 1 2 3 4 5 6 7 8 9 10; do true & echo $!; done; wait; echo \"rc=$?\"");
        var lines = r.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("rc=0", lines[^1]);
        var pids = lines[..^1].Select(int.Parse).ToArray();
        Assert.Equal(10, pids.Distinct().Count());

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (pids.Any(IsAlive) && DateTime.UtcNow < deadline) await Task.Yield();
        Assert.DoesNotContain(pids, IsAlive);
    }

    /// <summary>
    /// Soak: 100 jobs started in waves (PSBASH_JOBS_MAX bounds the concurrent ones), all reaped, none left behind.
    /// Minutes long (every job is a host process), so opt-in: set PSBASH_SOAK=1.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Soak")]
    public async Task Job_HundredJobsInWaves_AllReapedAndNoneLeftBehind()
    {
        Skip.If(Environment.GetEnvironmentVariable("PSBASH_SOAK") != "1", "opt-in soak: set PSBASH_SOAK=1");
        var runner = PsBashRunner.Create().WithTimeout(900);
        Skip.If(runner.TryResolveBinary() is null, "ps-bash binary not found -- build PsBash.Shell first");
        var r = await runner.RunScriptAsync(
            "export PSBASH_BG_MODE=process\n" +
            "for w in 1 2 3 4 5 6 7 8 9 10; do for i in 1 2 3 4 5 6 7 8 9 10; do true & echo $!; done; wait; done; echo \"rc=$?\"");
        var lines = r.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("rc=0", lines[^1]);
        var pids = lines[..^1].Select(int.Parse).ToArray();
        Assert.Equal(100, pids.Length);
        Assert.Equal(100, pids.Distinct().Count());

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (pids.Any(IsAlive) && DateTime.UtcNow < deadline) await Task.Yield();
        Assert.DoesNotContain(pids, IsAlive);
    }

    [SkippableFact]
    public async Task Job_StillRunningAtCommandEnd_LeavesNoProcessBehind()
    {
        var r = await RunAsync("sleep 120 & echo $!; sleep 120 & echo $!; sleep 120 & echo $!");
        var pids = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();
        Assert.Equal(3, pids.Length);

        // Condition-poll (no fixed sleep): the host reaps its jobs as the command ends.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int[] alive;
        do
        {
            alive = pids.Where(p => { try { using var pr = System.Diagnostics.Process.GetProcessById(p); return !pr.HasExited; } catch (ArgumentException) { return false; } }).ToArray();
            if (alive.Length == 0) break;
            await Task.Yield();
        } while (DateTime.UtcNow < deadline);
        Assert.Empty(alive);
    }}
