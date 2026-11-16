using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Stopping a child (Ctrl+C / pipeline stop / <c>timeout</c> / a budget) ends its WHOLE process tree, and the wait
/// never depends on an orphan closing a pipe (<c>docs/specs/stdio-messaging.md</c> §4.6). Each test runs a parent
/// that starts a grandchild holding the parent's stdout/stderr pipes, stops the parent, and asserts on the
/// grandchild's PID: gone after a stop, alive after a normal exit. Elapsed time is only a secondary bound.
/// <para>
/// Hand-written asserts: process containment is ps-bash's own mechanism (Job Object / process group). What bash does
/// in each shape was read off the oracle (Ubuntu 24.04, coreutils timeout): <c>timeout</c> kills the foreground
/// process group including <c>&amp;</c> children and orphans, a <c>setsid</c> child survives, and a background child
/// survives its parent's normal exit.
/// </para>
/// </summary>
public class ProcessTreeStopTests : IClassFixture<SharedPwshFixture>
{
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReturnLimit = TimeSpan.FromSeconds(10);

    private readonly SharedPwshFixture _fixture;

    static ProcessTreeStopTests()
    {
        // A test run from a developer's terminal is that terminal's foreground job, where children deliberately
        // stay in the host's process group. These tests are about the contained case.
        ChildStdio.TerminalForegroundOverride = static () => false;
    }

    public ProcessTreeStopTests(SharedPwshFixture fixture) => _fixture = fixture;

    /// <summary>How the grandchild hangs off the parent we stop.</summary>
    public enum Shape
    {
        /// <summary>parent -> grandchild; the parent waits. Parent-pid links are intact.</summary>
        LiveParent,

        /// <summary>parent -> middle (exits at once) -> grandchild. The grandchild's parent is gone: a parent-pid walk cannot find it.</summary>
        Orphan,
    }

    private sealed class Scenario : IDisposable
    {
        public string Dir { get; }
        public string PidFile { get; }
        public ProcessStartInfo StartInfo { get; }

        /// <summary>A parent that starts a 30 s grandchild sharing its stdout/stderr, writes the grandchild's pid to <see cref="PidFile"/>, and waits 30 s.</summary>
        public Scenario(Shape shape, bool parentExitsAtOnce = false, bool grandchildDetaches = false)
        {
            Dir = Path.Combine(Path.GetTempPath(), "ps-bash", "tree-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            PidFile = Path.Combine(Dir, "grandchild.pid");
            StartInfo = new ProcessStartInfo { CreateNoWindow = true };
            if (OperatingSystem.IsWindows())
            {
                // powershell.exe (always present) can report the pid of what it starts; -NoNewWindow shares the std handles.
                string start = parentExitsAtOnce
                    ? "$p = Start-Process ping -ArgumentList '-n','30','127.0.0.1' -WindowStyle Hidden -PassThru"
                    : "$p = Start-Process ping -ArgumentList '-n','30','127.0.0.1' -NoNewWindow -PassThru";
                string record = "[IO.File]::WriteAllText('" + PidFile + ".tmp', [string]$p.Id); Move-Item -LiteralPath '" + PidFile + ".tmp' -Destination '" + PidFile + "'";
                string inner = Path.Combine(Dir, "inner.ps1");
                File.WriteAllText(inner, start + "\n" + record + "\n");
                string outer = Path.Combine(Dir, "outer.ps1");
                string wait = parentExitsAtOnce ? "" : "Start-Sleep -Seconds 30\n";
                File.WriteAllText(outer, shape == Shape.Orphan
                    ? "& powershell.exe -NoProfile -ExecutionPolicy Bypass -File '" + inner + "'\n" + wait
                    : start + "\n" + record + "\n" + wait);
                StartInfo.FileName = "powershell.exe";
                foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", outer }) StartInfo.ArgumentList.Add(a);
            }
            else
            {
                // `echo` is redirected; the sleep keeps the parent's stdout and stderr (the pipes we read).
                string record = "echo $! > \"$PIDFILE.tmp\"; mv \"$PIDFILE.tmp\" \"$PIDFILE\"";
                string sleeper = grandchildDetaches
                    ? "setsid sh -c 'echo $$ > \"$PIDFILE.tmp\"; mv \"$PIDFILE.tmp\" \"$PIDFILE\"; exec sleep 30' >/dev/null 2>&1 &"
                    : parentExitsAtOnce ? "sleep 30 >/dev/null 2>&1 & " + record : "sleep 30 & " + record;
                string script = parentExitsAtOnce ? sleeper
                    : grandchildDetaches ? sleeper + " sleep 30"
                    : shape == Shape.Orphan ? "(" + sleeper + "); sleep 30"
                    : sleeper + "; wait";
                StartInfo.FileName = "/bin/sh";
                StartInfo.ArgumentList.Add("-c");
                StartInfo.ArgumentList.Add(script);
                StartInfo.Environment["PIDFILE"] = PidFile;
            }
        }

        /// <summary>Block until the grandchild's pid is on disk (it is running and holds the pipes), and return it.</summary>
        public int WaitForGrandchild()
        {
            string? text = PsBash.Testing.PublishedFile.ReadWhenReady(PidFile, StartLimit);
            Assert.True(text is not null,
                $"the test parent never recorded its grandchild within {StartLimit.TotalSeconds:0}s");
            return int.Parse(text!.Trim());
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The process must be gone within a few seconds (the kill is asynchronous on every OS).</summary>
    private static void AssertGone(int pid, string what)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            Assert.True(p.WaitForExit(5_000), $"{what} (pid {pid}) is still running after the stop");
        }
        catch (ArgumentException) { /* not running */ }
        catch (InvalidOperationException) { /* exited between the lookup and the wait */ }
    }

    /// <summary>End a grandchild a test left running ON PURPOSE (we started it; nothing else is ever touched).</summary>
    private static void EndOurGrandchild(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Cancel <paramref name="stop"/> from a dedicated thread once the grandchild exists; returns when that happened.</summary>
    private static (Thread Thread, Stopwatch SinceStop, int[] Pid) StopWhenGrandchildRuns(Scenario s, CancellationTokenSource stop)
    {
        var sinceStop = new Stopwatch();
        var pid = new int[1];
        var t = new Thread(() =>
        {
            try { pid[0] = s.WaitForGrandchild(); }
            finally { sinceStop.Start(); stop.Cancel(); }
        }) { IsBackground = true, Name = "tree-test stop" };
        t.Start();
        return (t, sinceStop, pid);
    }

    [Theory]
    [InlineData(Shape.LiveParent, false)]
    [InlineData(Shape.Orphan, false)]
    [InlineData(Shape.LiveParent, true)]
    [InlineData(Shape.Orphan, true)]
    public void RunChildProcess_Stop_EndsTheWholeTreeAndReturns(Shape shape, bool merged)
    {
        using var s = new Scenario(shape);
        using var stop = new CancellationTokenSource();
        var (thread, sinceStop, pid) = StopWhenGrandchildRuns(s, stop);

        var result = BashRuntime.RunChildProcess(s.StartInfo, BashRuntime.NoTimeLimit, null, stop.Token,
            merged ? ChildStdioMode.Merged : ChildStdioMode.Separate);
        var returnedAfter = sinceStop.Elapsed;
        thread.Join();

        Assert.NotEqual(0, pid[0]);
        Assert.Equal(130, result.ExitCode);
        Assert.True(result.TimedOut);
        AssertGone(pid[0], "the grandchild");
        Assert.True(returnedAfter < ReturnLimit, $"the stop must end the wait; it took {returnedAfter.TotalMilliseconds:0}ms");
    }

    [Fact]
    public void RunChildProcess_Budget_EndsAnOrphanedGrandchildToo()
    {
        using var s = new Scenario(Shape.Orphan);
        var budget = TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 12 : 4); // two powershell.exe starts on Windows

        var sw = Stopwatch.StartNew();
        var result = BashRuntime.RunChildProcess(s.StartInfo, budget);
        sw.Stop();

        string? recorded = PsBash.Testing.PublishedFile.ReadWhenReady(s.PidFile, ReturnLimit);
        Assert.True(recorded is not null, "the grandchild did not start inside the budget; nothing was tested");
        Assert.Equal(124, result.ExitCode);
        AssertGone(int.Parse(recorded!.Trim()), "the orphaned grandchild");
        Assert.True(sw.Elapsed < budget + ReturnLimit, $"the budget must end the wait; it took {sw.ElapsedMilliseconds}ms");
    }

    [Theory]
    [InlineData(Shape.LiveParent, false)]
    [InlineData(Shape.Orphan, false)]
    [InlineData(Shape.Orphan, true)]
    public void StreamingChild_Kill_EndsTheWholeTree(Shape shape, bool merged)
    {
        using var s = new Scenario(shape);
        var child = BashRuntime.StartChildProcessStreaming(s.StartInfo, keepStdinOpen: false,
            new ChildStdioMode(MergeStderr: merged));
        int pid;
        try
        {
            pid = s.WaitForGrandchild();
            var sw = Stopwatch.StartNew();
            child.Kill(); // what StopProcessing does
            child.Dispose();
            Assert.True(sw.Elapsed < ReturnLimit, $"kill + dispose must not wait for a pipe; took {sw.ElapsedMilliseconds}ms");
        }
        finally { child.Dispose(); }
        AssertGone(pid, "the grandchild");
    }

    [Fact]
    public void SpawnedChild_KillTree_LeavesNoMemberOfTheTreeRunning()
    {
        using var s = new Scenario(Shape.Orphan);
        using var child = ChildStdio.Start(s.StartInfo, ChildStdioMode.Separate);
        int grandchild = s.WaitForGrandchild();
        var before = child.TreePids();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Assert.Contains(grandchild, before); // the orphan is a member of the tree, by construction

        child.KillTree();

        Assert.True(child.WaitForExit(5_000), "the leader did not exit after KillTree");
        AssertGone(grandchild, "the orphaned grandchild");
        foreach (var pid in before) AssertGone(pid, "a tree member");
    }

    [Fact]
    public void SpawnedChild_DisposedWhileRunning_EndsTheTree()
    {
        using var s = new Scenario(Shape.LiveParent);
        var child = ChildStdio.Start(s.StartInfo, ChildStdioMode.Merged);
        int grandchild = s.WaitForGrandchild();

        child.Dispose();

        AssertGone(grandchild, "the grandchild of an abandoned child");
    }

    // bash: `sh -c 'server &'` leaves the server running after sh exits. A normal exit is not a stop.
    [Fact]
    public void RunChildProcess_NormalExit_LeavesABackgroundedGrandchildRunning()
    {
        using var s = new Scenario(Shape.LiveParent, parentExitsAtOnce: true);

        var result = BashRuntime.RunChildProcess(s.StartInfo, BashRuntime.NoTimeLimit, null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        int grandchild = s.WaitForGrandchild();
        try { Assert.True(IsRunning(grandchild), "a background child must survive its parent's normal exit (bash parity)"); }
        finally { EndOurGrandchild(grandchild); }
    }

    // The child exits; what it left behind still holds the pipes. The call returns after a bounded grace, not when
    // the survivor ends, and disposing the child does not block on the survivor's pipe either.
    [Fact]
    public void RunChildProcess_ExitedParent_SurvivorHoldingThePipes_DoesNotHoldTheCall()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "start /b ping -n 25 127.0.0.1" }, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 25 &" } };

        var sw = Stopwatch.StartNew();
        var result = BashRuntime.RunChildProcess(psi, BashRuntime.NoTimeLimit, null, CancellationToken.None);
        sw.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"the survivor's pipe must not hold the call; took {sw.ElapsedMilliseconds}ms");
    }

    // bash: a setsid child left the process group, so neither Ctrl+C nor `timeout` reaches it.
    [SkippableFact]
    public void RunChildProcess_Stop_SparesAChildThatDetachedWithSetsid()
    {
        Skip.If(OperatingSystem.IsWindows(), "setsid is POSIX; on Windows the counterpart is CREATE_BREAKAWAY_FROM_JOB (nohup's DetachedProcess)");
        Skip.IfNot(File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid"), "no setsid(1) on this host");
        using var s = new Scenario(Shape.LiveParent, grandchildDetaches: true);
        using var stop = new CancellationTokenSource();
        var (thread, _, pid) = StopWhenGrandchildRuns(s, stop);

        var result = BashRuntime.RunChildProcess(s.StartInfo, BashRuntime.NoTimeLimit, null, stop.Token);
        thread.Join();

        Assert.Equal(130, result.ExitCode);
        try { Assert.True(IsRunning(pid[0]), "a setsid child left the group and must survive the stop (bash parity)"); }
        finally { EndOurGrandchild(pid[0]); }
    }

    // `timeout 2>&1`: the program runs through Invoke-BashNative, i.e. the primitive; the budget ends the tree.
    [Fact]
    public void Timeout_BudgetEnds_TheOrphanedGrandchildIsGone()
    {
        using var s = new Scenario(Shape.Orphan);
        string Quote(string a) => "'" + a.Replace("'", "''") + "'";
        var words = new List<string> { Quote(s.StartInfo.FileName) };
        foreach (var a in s.StartInfo.ArgumentList) words.Add(Quote(a));
        int budget = OperatingSystem.IsWindows() ? 12 : 4;
        var pwsh = _fixture.AcquireFresh();
        var previous = Environment.GetEnvironmentVariable("PIDFILE");
        Environment.SetEnvironmentVariable("PIDFILE", s.PidFile);
        CmdResult result;
        var sw = Stopwatch.StartNew();
        try { result = CmdResult.Run(pwsh, $"Invoke-BashTimeout {budget} {string.Join(" ", words)} 2>&1"); }
        finally { Environment.SetEnvironmentVariable("PIDFILE", previous is null ? null : previous); }
        sw.Stop();

        string? recorded = PsBash.Testing.PublishedFile.ReadWhenReady(s.PidFile, ReturnLimit);
        Assert.True(recorded is not null, "the grandchild did not start inside the budget; nothing was tested");
        result.AssertFailed(124, "");
        AssertGone(int.Parse(recorded!.Trim()), "the orphaned grandchild of a timed-out command");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(budget) + ReturnLimit, $"timeout must return at its budget; took {sw.ElapsedMilliseconds}ms");
    }
}
