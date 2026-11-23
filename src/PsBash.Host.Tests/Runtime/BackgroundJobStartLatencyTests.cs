using System.Globalization;
using PsBash.Testing;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// An in-process job (<c>cmd &amp;</c>) starts in an already-open runspace (<c>PsBash.Cmdlets.BashJobRunspaces</c>,
/// docs/specs/background-jobs.md section 12.5), so jobs launched back to back start in launch order, each within a few
/// milliseconds of its <c>&amp;</c>. Before, each command opened a RunspacePool at its first <c>&amp;</c> and the pool
/// opened a runspace (psm1 import) for every job that found the others busy: jobs started 45-370 ms late and out of
/// order, and <c>for i in 3 2 1; do { sleep 0.0$i; echo $i; } &amp; done</c> printed 3 2 1.
/// <para>
/// Runs alone (a time bound beside parallel classes measures their AMSI-lock holds, testing.md). The bound is 150 ms:
/// far below the 300-900 ms a runspace open takes, far above the 10-50 ms a ready one takes under load. The structural
/// check (no runspace opened on the launch path) does not depend on load, and neither does the order
/// (<c>Enter-BashJobTurn</c>).
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): job start latency is ps-bash plumbing; the bash-visible order is pinned
/// by the oils spec cases "wait for N parallel jobs and check failure" and "wait -n".</para>
/// </summary>
[Collection(nameof(BackgroundJobStartLatencyTests))]
[CollectionDefinition(nameof(BackgroundJobStartLatencyTests), DisableParallelization = true)]
public class BackgroundJobStartLatencyTests : IAsyncLifetime
{
    private static readonly TimeSpan StartBound = TimeSpan.FromMilliseconds(150);

    private readonly HostWorkerFixture _fixture = new();
    private ProcessEnv? _env;

    public async Task InitializeAsync()
    {
        _env = ProcessEnv.Set("PSBASH_BG_MODE", "inproc");
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        _env?.Dispose();
        await _fixture.DisposeAsync();
    }

    // The warm-up first, as the daemon starts it before a command that uses `&`; the first job, launched while the
    // warm-up is opening, takes that runspace instead of opening one beside it (no cold open). Then wait (bounded)
    // until the spares are idle. Each job's first statement records the time; each job then holds its runspace, so the
    // next job needs another. Times are DateTime ticks read as properties: a .NET method call would wait on the AMSI
    // lock (section 12.4).
    private const string Script = """
        $R = [PsBash.Cmdlets.BashJobRunspaces]
        $first = $R::ColdOpens
        $R::WarmAhead((Get-Variable PsBashModulePsm1Path -ValueOnly))
        Invoke-BashBackground { 'warm' }; Invoke-BashWait | Out-Null
        "firstcold $($R::ColdOpens - $first)"
        $ready = [Threading.SpinWait]::SpinUntil([Func[bool]]{ $R::IdleCount -ge $R::Spares }, 120000)
        "ready $ready"
        $cold = $R::ColdOpens
        $n = $R::Spares
        $launch = @{}
        for ($i = 1; $i -le $n; $i++) {
            $launch[$i] = [datetime]::UtcNow.Ticks
            Invoke-BashBackground ([scriptblock]::Create("`"start $i `$([datetime]::UtcNow.Ticks)`"; Start-Sleep -Milliseconds 400"))
        }
        foreach ($k in $launch.Keys) { "launch $k $($launch[$k])" }
        Invoke-BashWait
        "cold $($R::ColdOpens - $cold)"
        """;

    [Fact]
    public async Task JobsLaunchedBackToBack_StartInLaunchOrder_EachWithinTheBoundOfItsAmpersand()
    {
        var worker = _fixture.CreateWorker();
        var run = await HostWorkerFixture.CaptureAsync(worker, Script);
        var text = string.Join("\n", run.Lines);
        Assert.True(run.ExitCode == 0, text);
        Assert.Contains("ready True", text);

        var launch = new SortedDictionary<int, long>();
        var start = new SortedDictionary<int, long>();
        string? cold = null, firstCold = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split(' ');
            if (f is ["launch", var i, var t]) launch[int.Parse(i, CultureInfo.InvariantCulture)] = long.Parse(t, CultureInfo.InvariantCulture);
            else if (f is ["start", var j, var u]) start[int.Parse(j, CultureInfo.InvariantCulture)] = long.Parse(u, CultureInfo.InvariantCulture);
            else if (f is ["cold", var c]) cold = c;
            else if (f is ["firstcold", var fc]) firstCold = fc;
        }
        Assert.True(launch.Count >= 2 && start.Count == launch.Count, text);

        // Structural: the first job waited for the warm-up's runspace, and every later job found an open one; none was
        // opened on the launch path.
        Assert.True(firstCold == "0", $"the first job opened a runspace beside the warm-up\n{text}");
        Assert.Equal("0", cold);

        long? previous = null;
        foreach (var (i, l) in launch)
        {
            var s = start[i];
            var latency = TimeSpan.FromTicks(s - l);
            Assert.True(s >= l && latency < StartBound, $"job {i} started {latency.TotalMilliseconds:F1} ms after its launch\n{text}");
            Assert.True(previous is null || s >= previous, $"job {i} started before job {i - 1}\n{text}");
            previous = s;
        }
    }
}
