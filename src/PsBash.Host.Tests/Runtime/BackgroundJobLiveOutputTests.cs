using System.Text;
using PsBash.Core.Transpiler;
using PsBash.Host.Runtime;
using PsBash.Testing;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Live background-job output (docs/specs/background-jobs.md section 12): a terminal-bound job's stdout and stderr reach
/// the session's streams while it runs, in production order with the foreground's output, through the same batcher and
/// stderr path (so the launcher only ever sees whole frames from the connection's one output queue), exactly once
/// (<c>wait</c> does not replay it), and with bounded memory when nothing drains it.
///
/// <para>Oracle note (qa-rubric Directive 1): the bash-visible ordering is oracle-checked in
/// <c>BackgroundJobDifferentialTests</c>; these probe the in-host properties the oracle cannot see (stream routing,
/// frames, queue bound, teardown of a blocked job).</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)] // PSBASH_BG_MODE; runs alone, so off the SdkHost lane too
public class BackgroundJobLiveOutputTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();
    private ProcessEnv? _env;

    public async Task InitializeAsync()
    {
        // The default runspace-pool job (no extra process); `-Isolated` jobs would otherwise need ps-bash-host.exe.
        _env = ProcessEnv.Set("PSBASH_BG_MODE", "inproc");
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        _env?.Dispose();
        await _fixture.DisposeAsync();
    }

    private sealed record Run(string Stdout, string Stderr, List<string> Order, int ExitCode);

    private static async Task<Run> RunBashAsync(SdkWorker worker, string bash)
    {
        var ps = BashTranspiler.Transpile(bash)!;
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        var order = new List<string>();
        var gate = new object();
        int exit = await worker.ExecuteWithOutputAsync(
            ps,
            s => { lock (gate) { outSb.Append(s); order.Add("O:" + s); } },
            s => { lock (gate) { errSb.Append(s); order.Add("E:" + s); } });
        lock (gate) return new Run(outSb.ToString(), errSb.ToString(), order, exit);
    }

    [Fact]
    public async Task LiveJob_StreamsInProductionOrderWithTheForeground_AndWaitDoesNotReplayIt()
    {
        var run = await RunBashAsync(_fixture.CreateWorker(),
            "{ echo bg1; sleep 2; echo bg2; } & sleep 1; echo fg; wait");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("bg1\nfg\nbg2\n", run.Stdout);
    }

    [Fact]
    public async Task LiveJob_StderrGoesToTheStderrPath_StdoutToStdout()
    {
        var run = await RunBashAsync(_fixture.CreateWorker(), "{ echo out; echo err >&2; } & wait");
        Assert.Equal("out\n", run.Stdout);
        Assert.Equal("err\n", run.Stderr);
    }

    /// <summary>A frame is a whole callback string: a job's line never lands inside a foreground record.</summary>
    [Fact]
    public async Task LiveJobAndForeground_NeverShareOrSplitARecord()
    {
        var run = await RunBashAsync(_fixture.CreateWorker(),
            "for i in 1 2 3; do { for j in 1 2 3 4 5 6 7 8 9 10; do echo job$i-$j; done; } & done; for k in 1 2 3 4 5 6 7 8 9 10; do echo fg-$k; done; wait");
        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(40, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^(job[123]-\d+|fg-\d+)$", l));
        Assert.Equal(40, lines.Distinct().Count());
    }

    [Fact]
    public async Task JobWithItsOwnRedirect_WritesTheFileAndNothingToTheSession()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-live-");
        try
        {
            var file = Path.Combine(dir.FullName, "out.txt").Replace('\\', '/');
            var run = await RunBashAsync(_fixture.CreateWorker(), $"echo tofile > '{file}' & wait; echo done");
            Assert.Equal("done\n", run.Stdout);
            Assert.Equal("tofile\n", File.ReadAllText(file).Replace("\r\n", "\n"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>A job started inside a function is not live (the function may run under a redirect); it keeps the
    /// collected-at-wait path and its output still appears exactly once.</summary>
    [Fact]
    public async Task NonLiveJob_StillPrintsOnceAtWait()
    {
        var run = await RunBashAsync(_fixture.CreateWorker(), "f() { echo inf & }; f; wait; echo after");
        Assert.Equal("inf\nafter\n", run.Stdout);
    }

    [Fact]
    public async Task LiveJob_WaitReportsItsExitStatus()
    {
        var run = await RunBashAsync(_fixture.CreateWorker(), "{ echo x; exit 3; } & wait $!; echo \"rc=$?\"");
        Assert.Equal("x\nrc=3\n", run.Stdout);
    }

    /// <summary>Output a job writes after its command ended is queued, then delivered once, first, by the next command
    /// of the same session.</summary>
    [Fact]
    public async Task OutputQueuedBetweenCommands_IsDeliveredOnceByTheNextCommand()
    {
        var worker = _fixture.CreateWorker();
        var first = await RunBashAsync(worker, "{ sleep 1; echo late; } &");
        Assert.Equal("", first.Stdout);

        var second = await RunBashAsync(worker, "wait; echo next");
        Assert.Equal("late\nnext\n", second.Stdout);
    }

    /// <summary>
    /// A job that writes forever while no command is running (nothing drains it) holds at most its window of queued
    /// text: it blocks, as a writer to a full pipe does. Before live output its collection grew without bound. Tearing
    /// the session down still stops the blocked job promptly (the abandon poll; Stop() must not deadlock against it).
    /// </summary>
    [Fact]
    public async Task DetachedFloodingJob_StaysWithinItsWindow_AndTeardownStopsIt()
    {
        var worker = _fixture.CreateWorker();
        var mux = worker.JobOutput;

        var run = await RunBashAsync(worker, "while true; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; done &");
        Assert.Equal(0, run.ExitCode);

        Assert.True(SpinWait.SpinUntil(() => mux.PendingChars >= mux.WindowChars - 100, TimeSpan.FromSeconds(30)),
            $"job never filled its window (pending {mux.PendingChars})");
        // Measured from here: the job pool's runspace (a psm1 import) exists by now and is not job output.
        long before = GC.GetTotalMemory(forceFullCollection: true);
        // Must NOT happen: the queue growing past the window while the job keeps looping.
        Assert.False(SpinWait.SpinUntil(() => mux.PendingChars > mux.WindowChars, TimeSpan.FromSeconds(2)),
            $"pending {mux.PendingChars} > window {mux.WindowChars}");
        long growth = GC.GetTotalMemory(forceFullCollection: true) - before;
        Assert.True(growth < 8L * 1024 * 1024, $"managed heap grew {growth / 1024} KiB while the job was blocked");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await worker.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"teardown of a blocked job took {sw.Elapsed}");
    }

    /// <summary>
    /// <c>echo word_a &amp; echo word_b</c> prints word_b first, as bash does (spec/oils/shell-grammar.spec.sh "a &amp; b"):
    /// once the job is started, the launcher's path to its next command must not include a script-to-.NET method call
    /// (background-jobs.md section 12.4). On Windows each such call is reported to AMSI under PowerShell's process-wide
    /// lock, slow when Defender is loaded; the launcher's job-table <c>.Add</c> came after the start, the job's path to
    /// its write has no such call, and under load the job printed first. Induced here: the AMSI lock is held from the
    /// moment the job is started (<c>OutputMux.ProducerStarted</c>) until the foreground's word_b arrives, or 10 s.
    /// Before the fix the foreground stalled the whole 10 s and the job printed word_a first.
    /// </summary>
    [SkippableFact]
    public async Task LiveJobStart_ForegroundReachesItsNextCommand_WhileTheAmsiLockIsHeldFromTheJobsStart()
    {
        var amsiLock = typeof(System.Management.Automation.PSObject).Assembly
            .GetType("System.Management.Automation.AmsiUtils")
            ?.GetField("s_amsiLockObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null);
        Skip.If(!OperatingSystem.IsWindows() || amsiLock is null, "AMSI and its process-wide lock exist on Windows only");

        var worker = _fixture.CreateWorker();
        var foregroundArrived = new ManualResetEventSlim();
        var arrivedWhileHeld = false;
        Thread? holder = null;
        void HoldAcrossJobStart(PsBash.Core.Stdio.OutputChannel _)
        {
            var acquired = new ManualResetEventSlim();
            holder = new Thread(() =>
            {
                lock (amsiLock!)
                {
                    acquired.Set();
                    arrivedWhileHeld = foregroundArrived.Wait(TimeSpan.FromSeconds(10));
                }
            }) { IsBackground = true };
            holder.Start();
            acquired.Wait();
        }

        var stdout = new StringBuilder();
        var gate = new object();
        worker.JobOutput.ProducerStarted += HoldAcrossJobStart;
        int exit;
        try
        {
            exit = await worker.ExecuteWithOutputAsync(
                BashTranspiler.Transpile("echo word_a & echo word_b; wait")!,
                s =>
                {
                    lock (gate) stdout.Append(s);
                    if (s.Contains("word_b", StringComparison.Ordinal)) foregroundArrived.Set();
                },
                _ => { });
        }
        finally
        {
            worker.JobOutput.ProducerStarted -= HoldAcrossJobStart;
            foregroundArrived.Set();
            holder?.Join();
        }

        Assert.True(arrivedWhileHeld,
            "the foreground's next command did not run while the AMSI lock was held from the job's start: the launcher "
            + "made a script->.NET call after starting the job (Start-BashBgJob must start the job as its last act)");
        Assert.Equal(0, exit);
        // Both lines, once each. Their order is the natural race here (the lock stalls neither side now), so the order
        // bash shows is the spec case's to check, not this test's.
        lock (gate)
            Assert.Equal(["word_a", "word_b"], stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Order());
    }

    /// <summary>
    /// A job's way from its start to its first statement compiles nothing (background-jobs.md section 12.5, cause 7):
    /// its function and its body were compiled before the launch (<c>JobScript</c>). Each compile is an AMSI scan under
    /// PowerShell's process-wide lock, slow when Defender is loaded: the job used to run <c>AddScript</c> text, compiled
    /// afresh by every job after its start, so its body began 24-130 ms after its <c>&amp;</c> on a loaded host and the
    /// oils cases "wait -n" and "wait for N parallel jobs" (sleeps of 10-90 ms) saw jobs end in launch order. Induced
    /// here: the AMSI lock is held from the moment the job is started until the job's line arrives, or 10 s. Before the
    /// fix the job's compile waited the whole 10 s. The body text is new to this process, so the launcher compiles it.
    /// </summary>
    [SkippableFact]
    public async Task LiveJobBody_RunsWhileTheAmsiLockIsHeldFromTheJobsStart()
    {
        var amsiLock = typeof(System.Management.Automation.PSObject).Assembly
            .GetType("System.Management.Automation.AmsiUtils")
            ?.GetField("s_amsiLockObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null);
        Skip.If(!OperatingSystem.IsWindows() || amsiLock is null, "AMSI and its process-wide lock exist on Windows only");

        var worker = _fixture.CreateWorker();
        // Open the job runspaces first (their psm1 import holds the lock for long stretches): this test is about the job.
        await RunBashAsync(worker, "echo warm & wait");
        string word = "job_" + Guid.NewGuid().ToString("N");
        var jobArrived = new ManualResetEventSlim();
        var arrivedWhileHeld = false;
        Thread? holder = null;
        void HoldAcrossJobStart(PsBash.Core.Stdio.OutputChannel _)
        {
            var acquired = new ManualResetEventSlim();
            holder = new Thread(() =>
            {
                lock (amsiLock!)
                {
                    acquired.Set();
                    arrivedWhileHeld = jobArrived.Wait(TimeSpan.FromSeconds(10));
                }
            }) { IsBackground = true };
            holder.Start();
            acquired.Wait();
        }

        var stdout = new StringBuilder();
        var gate = new object();
        worker.JobOutput.ProducerStarted += HoldAcrossJobStart;
        int exit;
        try
        {
            exit = await worker.ExecuteWithOutputAsync(
                BashTranspiler.Transpile($"{{ echo {word}; }} & wait")!,
                s =>
                {
                    lock (gate) stdout.Append(s);
                    if (s.Contains(word, StringComparison.Ordinal)) jobArrived.Set();
                },
                _ => { });
        }
        finally
        {
            worker.JobOutput.ProducerStarted -= HoldAcrossJobStart;
            jobArrived.Set();
            holder?.Join();
        }

        Assert.True(arrivedWhileHeld,
            "the job's body did not run while the AMSI lock was held from its start: something on its way to its first "
            + "statement was compiled after the launch (JobScript: the job function and body are compiled before it)");
        Assert.Equal(0, exit);
        lock (gate) Assert.Equal(word + "\n", stdout.ToString());
    }
}
