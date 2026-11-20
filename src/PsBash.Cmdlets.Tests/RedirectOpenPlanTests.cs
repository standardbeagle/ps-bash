using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// bash applies a command's redirects left to right: an open that fails is reported WHERE FD 2 POINTS THEN, and the
/// redirects after it are never opened. Oracle (bash 5.2, WSL): <c>echo hi 2&gt;/dev/null &gt; nodir/y</c> prints
/// nothing (status 1); <c>2&gt;err &gt; nodir/y</c> writes the report into err; <c>&gt; nodir/y 2&gt;err2</c> never
/// creates err2; <c>2&gt;&amp;1 &gt; nodir/q | sed …</c> sends it down the pipe. The emitter writes the order and the
/// report targets as <c>-OpenPlan</c> (<see cref="RedirectOpenPlan"/>); these drive the stage with the plans it writes
/// for those commands (emitted text: Core.Tests <c>RedirectOpenErrorEmitTests</c>; end to end: Differential
/// <c>RedirectOpenErrorDifferentialTests</c>). Spec: docs/specs/error-handling.md "Redirect open errors".
/// </summary>
public class RedirectOpenPlanTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public RedirectOpenPlanTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), $"psb-rop-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string P(string name) => Path.Combine(_tmpDir, name);

    private string Bad => Path.Combine(_tmpDir, "nodir", "y");

    // ── the plan text ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EveryRoleOnce_ReportsToEarlierRoles()
    {
        var plan = RedirectOpenPlan.Parse("t0 e:t0", truncate: 1, touch: 0, path: false, errorPath: true);
        Assert.NotNull(plan);
        Assert.Equal(new RedirectOpenPlan.Step[] { new("t0", null), new("e", "t0") }, plan);
    }

    [Theory]
    [InlineData("p:null", 0, 0, true, false)]
    [InlineData("p:out", 0, 0, true, false)]
    [InlineData("a0 e p:e", 0, 1, true, true)]
    public void Parse_ValidPlans(string text, int truncate, int touch, bool path, bool errorPath)
        => Assert.NotNull(RedirectOpenPlan.Parse(text, truncate, touch, path, errorPath));

    // A plan that does not describe the stage's targets is ignored (the stage opens in its own order), never trusted.
    [Theory]
    [InlineData("", 0, 0, true, false)]              // no plan
    [InlineData("p p", 0, 0, true, false)]           // a role twice
    [InlineData("e", 0, 0, true, true)]              // a role missing
    [InlineData("p e", 0, 0, true, false)]           // a role the stage does not have
    [InlineData("p:e e", 0, 0, true, true)]          // report into a target not opened yet
    [InlineData("p:p", 0, 0, true, false)]           // report into itself
    [InlineData("t1", 1, 0, false, false)]           // an index out of range
    [InlineData("p:stderr", 0, 0, true, false)]      // an unknown report target
    public void Parse_PlansThatDoNotMatch_AreNull(string text, int truncate, int touch, bool path, bool errorPath)
        => Assert.Null(RedirectOpenPlan.Parse(text, truncate, touch, path, errorPath));

    // ── the stage ────────────────────────────────────────────────────────────

    [Fact]
    public void Stage_StderrDiscardedBeforeTheFailingOpen_ReportsNothing()
    {
        // echo hi 2>/dev/null > nodir/y
        Run($"Invoke-BashEcho hi 2>$null | Invoke-BashRedirect -Path {Q(Bad)} -OpenPlan 'p:null'").AssertFailed(1);
    }

    [Fact]
    public void Stage_StderrRedirectedBeforeTheFailingOpen_ReportsIntoThatFile()
    {
        // echo hi 2>err > nodir/y: the report is in err, not on stderr.
        var err = P("err");
        var r = Run($"Invoke-BashEcho hi 2>&1 | Invoke-BashRedirect -Path {Q(Bad)} -ErrorPath {Q(err)} -OpenPlan 'e p:e'");
        Assert.Equal(1, r.ExitCode);
        Assert.DoesNotContain("No such file or directory", r.Stderr);
        Assert.Contains("y: No such file or directory", File.ReadAllText(err));
    }

    [Fact]
    public void Stage_FailingOpenBeforeTheStderrRedirect_NeverOpensIt()
    {
        // echo hi > nodir/y 2>err2: reported on stderr; err2 is never created.
        var err2 = P("err2");
        Run($"Invoke-BashEcho hi 2>&1 | Invoke-BashRedirect -Path {Q(Bad)} -ErrorPath {Q(err2)}")
            .AssertFailed(1, "No such file or directory");
        Assert.False(File.Exists(err2));
    }

    [Fact]
    public void Stage_StderrDupedToStdoutBeforeTheFailingOpen_ReportsOnStdout()
    {
        // ls 2>&1 > nodir/q | sed …: the report goes down the pipe.
        var r = Run($"Invoke-BashEcho hi 2>&1 | Invoke-BashRedirect -Path {Q(Bad)} -PassErrors -OpenPlan 'p:out'");
        Assert.Equal(1, r.ExitCode);
        Assert.DoesNotContain("No such file or directory", r.Stderr);
        Assert.Contains(r.Lines, l => l.Contains("y: No such file or directory"));
    }

    [Fact]
    public void Stage_FailingOpenBeforeALaterTarget_NeverCreatesIt()
    {
        // echo hi > nodir/y > b
        var b = P("b");
        Run($"Invoke-BashEcho hi | Invoke-BashRedirect -Path {Q(b)} -Truncate @({Q(Bad)})").AssertFailed(1, "No such file or directory");
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void Stage_PlanThatDoesNotMatch_IsIgnored()
    {
        var o = P("o");
        Run($"Invoke-BashEcho hi | Invoke-BashRedirect -Path {Q(o)} -OpenPlan 'zz'").AssertSuccess();
        Assert.Equal("hi\n", File.ReadAllText(o));
    }

    [Fact]
    public void Stage_PlannedOpens_WriteTheirTargets()
    {
        // cmd >>a 2>e >o: every target opened in bash's order, the final ones written.
        var a = P("a"); var e = P("e"); var o = P("o");
        Run($"Invoke-BashEcho hi 2>&1 | Invoke-BashRedirect -Path {Q(o)} -ErrorPath {Q(e)} -Touch @({Q(a)}) -OpenPlan 'a0 e p:e'")
            .AssertSuccess();
        Assert.Equal("hi\n", File.ReadAllText(o));
        Assert.True(File.Exists(a));
        Assert.Equal("", File.ReadAllText(e));
    }

    [Fact]
    public void Stage_OpenError_CarriesTheShellPrefix()
    {
        // bash: `s.sh: line 7: nodir/y: No such file or directory` (ShellDiagnostic). It was `bash: nodir/y: …`.
        Run("$global:BASH_SOURCE = @('s.sh'); [PsBash.Cmdlets.VarFrameStack]::SetLine(7); "
            + $"Invoke-BashEcho hi | Invoke-BashRedirect -Path {Q(Bad)}")
            .AssertFailed(1, "s.sh: line 7: ");
    }
}
