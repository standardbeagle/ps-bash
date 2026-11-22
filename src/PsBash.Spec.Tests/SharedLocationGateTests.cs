using PsBash.Spec;

namespace PsBash.Spec.Tests;

/// <summary>
/// The second half of case isolation (.claude/rules/testing.md "SPEC CASE ISOLATION"): the temp and home directories
/// cannot be given per case, so a case that names one runs with no other case beside it. builtin-cd has five cases on
/// <c>/tmp/oil-spec-test</c> and builtin-dirs four on <c>/tmp/oil_test</c>; at the default parallelism they ran together.
/// Oracle note (qa-rubric Directive 1): the runner is ps-bash's own.
/// </summary>
public class SharedLocationGateTests
{
    [Theory]
    [InlineData("mkdir -p /tmp/oil-spec-test")]
    [InlineData("cd /tmp")]
    [InlineData("echo x > /tmp/$$.txt")]
    [InlineData("cd ~/src")]
    [InlineData("echo ~")]
    [InlineData("echo $HOME/x")]
    [InlineData("HOME=/x; cd")]
    [InlineData("f=${TMPDIR:-/x}/a")]
    [InlineData("echo $TMP/a")]
    [InlineData("HISTFILE=myhistory")]
    [InlineData("$SH --rcfile /dev/null -i")]
    [InlineData("echo exit | bash -i\n")]
    public void UsesSharedLocation_ScriptNamingTempHomeOrAnInteractiveShell_IsTrue(string script)
        => Assert.True(CaseIsolation.UsesSharedLocation(script));

    [Theory]
    [InlineData("echo hi > out.txt; cat out.txt")]
    [InlineData("mkdir _tmp; cd _tmp; echo x > f")]
    [InlineData("[[ $x =~ ^a ]] && echo y")]
    [InlineData("echo a/tmp b/tmp/c x~y")]
    [InlineData("sed -i.bak s/a/b/ f; grep -in x f")]
    [InlineData("echo $TMPL ${HOMER} mytmp /tmpfile")]
    public void UsesSharedLocation_ScriptThatStaysInItsDirectory_IsFalse(string script)
        => Assert.False(CaseIsolation.UsesSharedLocation(script));

    /// <summary>
    /// A real-launcher case's limit grows with the nested shells it starts: the limit ends a hang, it does not time
    /// process starts (array-assign "More fragments like a[" has seven and ran 8 s against a fixed 10 s).
    /// </summary>
    [Theory]
    [InlineData("echo hi", true, 10)]
    [InlineData("$SH -c 'a['; ${SH} -c 'b'; echo $SHELL $SHX", true, 18)]
    [InlineData("case $SH in bash) exit;; esac; echo \"$SH\"", true, 18)]
    [InlineData("$SH -c x; $SH -c y", false, 10)]
    public void TimeoutFor_RealLauncherCase_AddsAnAllowancePerNestedShell(string script, bool realLauncher, int seconds)
    {
        var c = new SpecCase("t", script, new Expectation(null, null, null), [], null, TimeSpan.FromSeconds(10), realLauncher);
        Assert.Equal(TimeSpan.FromSeconds(seconds), SpecRunner.TimeoutFor(c));
    }

    [Fact]
    public void TimeoutFor_KnownGap_KeepsItsShortCap()
    {
        var c = new SpecCase("t", "$SH -c x; $SH -c y", new Expectation(null, null, null), [], new XfailMark("gap", "r"), TimeSpan.FromSeconds(30), true);
        Assert.Equal(SpecRunner.RealLauncherGapTimeout, SpecRunner.TimeoutFor(c));
    }

    // holdFirst: the first own-directory case waits (bounded) until a second case is running, so "cases still run
    // together" is decided by the gate, not by whether 20 yields happen to interleave. They did not on a loaded fleet
    // host (s24q: "no two cases ever ran together" at parallelism 2). A gate that serialised everything never starts the
    // second case, so the wait times out and MaxTogether stays 1: the check still fails for the bug it is there for.
    private sealed class OverlapRecorder(bool holdFirst) : IScriptExecutor
    {
        private int _active;
        private int _held;
        private readonly TaskCompletionSource _secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxTogether;
        public readonly List<string> AloneViolations = new();

        public async Task<ExecResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancel)
        {
            int now = Interlocked.Increment(ref _active);
            if (now >= 2) _secondStarted.TrySetResult();
            bool alone = CaseIsolation.UsesSharedLocation(script);
            try
            {
                if (holdFirst && !alone && Interlocked.Exchange(ref _held, 1) == 0)
                {
                    try { await _secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel); }
                    catch (TimeoutException) { }
                }
                for (int i = 0; i < 20; i++)
                {
                    lock (AloneViolations)
                    {
                        MaxTogether = Math.Max(MaxTogether, Volatile.Read(ref _active));
                        if (alone && Volatile.Read(ref _active) != 1) AloneViolations.Add($"{script} ran with {_active - 1} other case(s)");
                    }
                    await Task.Yield();
                }
            }
            finally { Interlocked.Decrement(ref _active); }
            return new ExecResult(0, "", "", now);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    [InlineData(1)]
    public async Task RunAsync_CaseNamingASharedLocation_NeverOverlapsAnotherCase(int parallelism)
    {
        var none = new Expectation(null, null, null);
        var cases = new List<SpecCase>();
        for (int i = 0; i < 40; i++)
            cases.Add(new SpecCase("c" + i, i % 5 == 4 ? $"echo {i} > /tmp/shared" : $"echo {i} > own.txt", none, [], null, null, RealLauncher: i % 7 == 0));
        var executor = new OverlapRecorder(holdFirst: parallelism > 1);
        var results = await SpecRunner.RunAsync(new SpecFile("t.spec.sh", "", cases), executor, parallelism, launcher: executor);
        Assert.Equal(40, results.Count);
        Assert.All(results, r => Assert.Equal(Verdict.Pass, r.Verdict));
        Assert.Empty(executor.AloneViolations);
        // The gate is not a global lock: cases that stay in their own directory still run together.
        if (parallelism > 1) Assert.True(executor.MaxTogether > 1, "no two cases ever ran together");
    }
}
