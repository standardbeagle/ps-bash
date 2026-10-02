using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// diff option resolution, header formatting, direct-call decoys and the diagnostics the oracle harness does not compare. Expected
/// values were read from GNU diffutils 3.10 (<c>wsl bash</c>); byte-for-byte output parity is <c>DiffDifferentialTests</c>.
/// </summary>
public class DiffOptionsTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public DiffOptionsTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-dfo-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
        Write("a", "a\nb\nc\n");
        Write("b", "a\nB\nc\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static string[] Lines(CmdResult r) => r.Lines.Select(l => l.TrimEnd('\r', '\n')).ToArray();

    // ───────────── plan (pure) ─────────────

    private static DiffPlan Plan(params string[] argv)
    {
        var parsed = InvokeBashDiffCommand.ScanArgs(argv);
        Assert.False(parsed.HasError, parsed.Error?.Message("diff"));
        Assert.True(DiffPlan.TryBuild(argv, parsed, out var plan, out var error), error);
        return plan;
    }

    private static string PlanError(params string[] argv)
    {
        var parsed = InvokeBashDiffCommand.ScanArgs(argv);
        if (parsed.Error is { } e) return e.Message("diff");
        Assert.False(DiffPlan.TryBuild(argv, parsed, out _, out var error));
        return error!;
    }

    [Theory]
    [InlineData("Normal", 3, "a", "b")]
    [InlineData("Unified", 3, "-u", "a", "b")]
    [InlineData("Context", 3, "-c", "a", "b")]
    [InlineData("Unified", 1, "-U1", "a", "b")]
    [InlineData("Unified", 1, "-U", "1", "a", "b")]
    [InlineData("Unified", 0, "-U0", "a", "b")]
    [InlineData("Unified", 2, "-U2", "-U1", "a", "b")]            // the largest number wins
    [InlineData("Unified", 3, "-u", "-U1", "a", "b")]             // -u means at least 3
    [InlineData("Context", 3, "-c", "-C0", "a", "b")]
    [InlineData("Context", 2, "-C2", "a", "b")]
    [InlineData("Context", 3, "--context", "a", "b")]
    [InlineData("Context", 1, "--context=1", "a", "b")]
    [InlineData("Unified", 5, "--unified=5", "a", "b")]
    [InlineData("Unified", 3, "--unified", "a", "b")]
    [InlineData("Context", 3, "-c", "-C2", "a", "b")]
    public void Plan_StyleAndContextLength(string style, int context, params string[] argv)
    {
        var plan = Plan(argv);
        Assert.Equal(style, plan.Style.ToString());
        Assert.Equal(context, plan.Context);
    }

    [Theory]
    [InlineData("diff: conflicting output style options\ndiff: Try 'diff --help' for more information.", "-u", "-c", "a", "b")]
    [InlineData("diff: conflicting output style options\ndiff: Try 'diff --help' for more information.", "-c", "-U1", "a", "b")]
    [InlineData("diff: invalid context length 'x'\ndiff: Try 'diff --help' for more information.", "-U", "x", "a", "b")]
    [InlineData("diff: invalid context length '-1'\ndiff: Try 'diff --help' for more information.", "-C", "-1", "a", "b")]
    [InlineData("diff: missing operand after 'diff'\ndiff: Try 'diff --help' for more information.")]
    [InlineData("diff: missing operand after 'a'\ndiff: Try 'diff --help' for more information.", "a")]
    [InlineData("diff: extra operand 'c'\ndiff: Try 'diff --help' for more information.", "a", "b", "c")]
    [InlineData("diff: too many file label options", "--label", "x", "--label", "y", "--label", "z", "a", "b")]
    public void Plan_Errors_AreGnusWording(string expected, params string[] argv)
    {
        Assert.Equal(expected, PlanError(argv));
    }

    [Fact]
    public void Plan_Flags()
    {
        var plan = Plan("-qsrNiwbBa", "--strip-trailing-cr", "a", "b");
        Assert.True(plan.Brief); Assert.True(plan.ReportIdentical); Assert.True(plan.Recursive); Assert.True(plan.NewFile);
        Assert.True(plan.IgnoreCase); Assert.True(plan.IgnoreAllSpace); Assert.True(plan.IgnoreSpaceChange);
        Assert.True(plan.IgnoreBlankLines); Assert.True(plan.Text); Assert.True(plan.StripTrailingCr);
    }

    [Fact]
    public void Plan_LabelsInOrder_ShortAndLong()
    {
        var plan = Plan("-L", "X", "--label=Y", "a", "b");
        Assert.Equal(new[] { "X", "Y" }, plan.Labels);
    }

    [Theory]
    [InlineData(" -ru", "-ru", "d1", "d2")]
    [InlineData(" -u -r", "-u", "-r", "d1", "d2")]
    [InlineData(" --recursive --unified", "--recursive", "--unified", "d1", "d2")]
    [InlineData(" -r -U 5 --label A", "-r", "-U", "5", "--label", "A", "d1", "d2")]
    [InlineData(" -r -c", "d1", "d2", "-r", "-c")]
    [InlineData("", "d1", "d2")]
    public void Plan_SwitchString_IsTheOptionsAsTyped(string expected, params string[] argv)
    {
        Assert.Equal(expected, Plan(argv).SwitchString);
    }

    // ───────────── the scan ─────────────

    [Theory]
    [InlineData("option '-y' is recognized but not supported by ps-bash", "-y")]
    [InlineData("option '--side-by-side' is recognized but not supported by ps-bash", "--side-by-side")]
    [InlineData("option '-x' is recognized but not supported by ps-bash", "-x", "pat")]
    [InlineData("option '-D' is recognized but not supported by ps-bash", "-D", "NAME")]
    [InlineData("option '--exclude-from' is recognized but not supported by ps-bash", "--exclude-from=f")]
    [InlineData("invalid option -- 'z'", "-z")]
    [InlineData("unrecognized option '--bogus'", "--bogus")]
    public void Scan_RefusesWhatItDoesNotImplement(string expectedFragment, params string[] argv)
    {
        var parsed = InvokeBashDiffCommand.ScanArgs(argv.Concat(new[] { "a", "b" }).ToArray());
        Assert.Contains(expectedFragment, parsed.Error!.Value.Message("diff"));
        Assert.Equal(2, parsed.ErrorExitCode);
    }

    [Fact]
    public void Scan_AbbreviatedLongOptions()
    {
        Assert.Equal("Unified", Plan("--unif", "a", "b").Style.ToString());
        Assert.True(Plan("--recurs", "a", "b").Recursive);
        Assert.True(Plan("--ignore-c", "a", "b").IgnoreCase);
    }

    // ───────────── header timestamp ─────────────

    [Fact]
    public void FormatTimestamp_IsGnusNineDigitFraction_AndNumericOffset()
    {
        var utc = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);
        Assert.Equal("2020-01-02 03:04:05.123456700 +0000", DiffFormatter.FormatTimestamp(utc));
        var west = new DateTimeOffset(2020, 1, 1, 21, 4, 5, TimeSpan.FromHours(-6)).AddTicks(1234567);
        Assert.Equal("2020-01-01 21:04:05.123456700 -0600", DiffFormatter.FormatTimestamp(west));
        var east = new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.FromMinutes(330));
        Assert.Equal("2020-06-01 12:00:00.000000000 +0530", DiffFormatter.FormatTimestamp(east));
    }

    [Fact]
    public void Unified_HeaderCarriesName_Tab_Timestamp()
    {
        var a = Path.Combine(_tmp, "a");
        var b = Path.Combine(_tmp, "b");
        File.SetLastWriteTimeUtc(a, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(b, new DateTime(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var r = InTmp("Invoke-BashDiff '-u' a b");
        Assert.Equal(1, r.ExitCode);
        var lines = Lines(r);
        Assert.Matches(@"^--- a\t2020-01-0[123] \d\d:\d\d:05\.000000000 [+-]\d{4}$", lines[0]);
        Assert.Matches(@"^\+\+\+ b\t2021-01-0[123] \d\d:\d\d:05\.000000000 [+-]\d{4}$", lines[1]);
        Assert.Equal("@@ -1,3 +1,3 @@", lines[2]);
    }

    [Fact]
    public void Unified_LabelReplacesNameAndTimestamp()
    {
        var lines = Lines(InTmp("Invoke-BashDiff '-u' '--label' A '--label=B' a b"));
        Assert.Equal("--- A", lines[0]);
        Assert.Equal("+++ B", lines[1]);
    }

    // ───────────── cmdlet diagnostics and exits ─────────────

    [Fact]
    public void Exit_Zero_One_Two()
    {
        Assert.Equal(0, InTmp("Invoke-BashDiff a a").ExitCode);
        Assert.Equal(1, InTmp("Invoke-BashDiff a b").ExitCode);
        Assert.Equal(2, InTmp("Invoke-BashDiff a nosuch").ExitCode);
    }

    [Fact]
    public void MissingFile_IsGnusMessage_Exit2()
    {
        InTmp("Invoke-BashDiff a nosuch").AssertFailed(2, "diff: nosuch: No such file or directory");
        var both = InTmp("Invoke-BashDiff nosuch1 nosuch2");
        both.AssertFailed(2, "diff: nosuch1: No such file or directory", "diff: nosuch2: No such file or directory");
    }

    [Fact]
    public void MissingOperand_IsGnusMessage_Exit2()
    {
        InTmp("Invoke-BashDiff a").AssertFailed(2, "diff: missing operand after 'a'", "diff: Try 'diff --help' for more information.");
        InTmp("Invoke-BashDiff").AssertFailed(2, "diff: missing operand after 'diff'");
    }

    [Fact]
    public void UnsupportedOption_IsRefused_Exit2_AndPrintsNothing()
    {
        var r = InTmp("Invoke-BashDiff '-y' a b");
        r.AssertFailed(2, "recognized but not supported");
        Assert.Equal("", r.Stdout.Trim());
    }

    [Fact]
    public void Binary_IsBinaryFilesDiffer_AndDashAIsText()
    {
        File.WriteAllText(Path.Combine(_tmp, "x"), "a\0b\n");
        File.WriteAllText(Path.Combine(_tmp, "y"), "a\0c\n");
        Assert.Equal(new[] { "Binary files x and y differ" }, Lines(InTmp("Invoke-BashDiff x y")));
        Assert.Equal(new[] { "Files x and y differ" }, Lines(InTmp("Invoke-BashDiff '-q' x y")));
        Assert.Equal("1c1", Lines(InTmp("Invoke-BashDiff '-a' x y"))[0]);
    }

    [Fact]
    public void Directory_PerFileHeaderNamesTheSwitchesAsTyped()
    {
        Write("d1/f", "1\n"); Write("d2/f", "2\n"); Write("d1/only", "x\n");
        var lines = Lines(InTmp("Invoke-BashDiff '-ru' d1 d2"));
        Assert.Equal("diff -ru d1/f d2/f", lines[0]);
        Assert.StartsWith("--- d1/f\t", lines[1]);
        Assert.Equal("Only in d1: only", lines[^1]);
    }

    [Fact]
    public void Stdin_DashOperand_ReadsThePipeline()
    {
        var r = InTmp("'a','B','c' | Invoke-BashDiff a '-'");
        Assert.Equal(1, r.ExitCode);
        Assert.Equal(new[] { "2c2", "< b", "---", "> B" }, Lines(r));
    }

    // ───────────── direct-call decoys ─────────────

    [Fact]
    public void DirectCall_BareDashIAndDashW_BindDecoys()
    {
        Write("i1", "AbC\n"); Write("i2", "aBc\n");
        Assert.Equal(0, InTmp("Invoke-BashDiff -i i1 i2").ExitCode);
        Write("w1", "a b\n"); Write("w2", "ab\n");
        Assert.Equal(0, InTmp("Invoke-BashDiff -w w1 w2").ExitCode);
    }

    [Fact]
    public void DirectCall_BareDashC_IsContextFormat()
    {
        var lines = Lines(InTmp("Invoke-BashDiff -c a b"));
        Assert.StartsWith("*** a\t", lines[0]);
        Assert.Equal("***************", lines[2]);
    }
}
