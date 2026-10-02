using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU wc --total=WHEN and --files0-from=F (coreutils 9.4, wsl). Files: a="a b\nc\n" (2 3 6), b="xyz" (0 1 3).
/// Output columns are compared after collapsing whitespace (the width policy is not under test here).
/// </summary>
public class WcTotalFiles0Tests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public WcTotalFiles0Tests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), "psb-wct-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
        File.WriteAllText(Path.Combine(_tmp, "a"), "a b\nc\n");
        File.WriteAllText(Path.Combine(_tmp, "b"), "xyz");
        File.WriteAllBytes(Path.Combine(_tmp, "L"), new byte[] { (byte)'a', 0, (byte)'b', 0 });
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private CmdResult Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        return CmdResult.Run(pwsh, $"Push-Location '{_tmp.Replace("'", "''")}'; try {{ {script} }} finally {{ Pop-Location }}");
    }

    private static string Norm(CmdResult r) =>
        string.Join("|", r.Lines.Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " ")));

    [Theory]
    [InlineData("auto", "2 3 6 a")]
    [InlineData("always", "2 3 6 a|2 3 6 total")]
    [InlineData("only", "2 3 6")]
    [InlineData("never", "2 3 6 a")]
    [InlineData("al", "2 3 6 a|2 3 6 total")]
    public void Total_OneFile(string when, string expected) =>
        Assert.Equal(expected, Norm(Run($"Invoke-BashWc '--total={when}' a").AssertSuccess()));

    [Theory]
    [InlineData("auto", "2 3 6 a|0 1 3 b|2 4 9 total")]
    [InlineData("always", "2 3 6 a|0 1 3 b|2 4 9 total")]
    [InlineData("only", "2 4 9")]
    [InlineData("never", "2 3 6 a|0 1 3 b")]
    public void Total_TwoFiles(string when, string expected) =>
        Assert.Equal(expected, Norm(Run($"Invoke-BashWc '--total={when}' a b").AssertSuccess()));

    [Fact]
    public void Total_Only_WithColumnSelector() =>
        Assert.Equal("2", Norm(Run("Invoke-BashWc '-l' '--total=only' a b").AssertSuccess()));

    [Fact]
    public void Total_OnStdin_Only_And_Always()
    {
        Assert.Equal("1 1 2", Norm(Run("'q' | Invoke-BashWc '--total=only'").AssertSuccess()));
        Assert.Equal("1 1 2|1 1 2 total", Norm(Run("'q' | Invoke-BashWc '--total=always'").AssertSuccess()));
    }

    [Fact]
    public void Total_BadWord_IsGnuUsageErrorExit1() =>
        Run("Invoke-BashWc '--total=bogus' a").AssertFailed(1, "invalid argument 'bogus' for '--total'", "Valid arguments are:");

    [Fact]
    public void Total_AmbiguousPrefix_IsRejected() =>
        Run("Invoke-BashWc '--total=a' a").AssertFailed(1, "ambiguous argument 'a' for '--total'");

    [Fact]
    public void Files0From_File_CountsEachNameThenTotal() =>
        Assert.Equal("2 3 6 a|0 1 3 b|2 4 9 total", Norm(Run("Invoke-BashWc '--files0-from=L'").AssertSuccess()));

    [Fact]
    public void Files0From_Stdin() =>
        Assert.Equal("2 3 6 a|0 1 3 b|2 4 9 total",
            Norm(Run("Invoke-BashPrintf 'a\\0b' | Invoke-BashWc '--files0-from=-'").AssertSuccess()));

    [Fact]
    public void Files0From_WithOperand_IsUsageError() =>
        Run("Invoke-BashWc '--files0-from=L' a").AssertFailed(1, "extra operand 'a'", "file operands cannot be combined with --files0-from");

    [Fact]
    public void Files0From_MissingList_Exit1() =>
        Run("Invoke-BashWc '--files0-from=nosuch'").AssertFailed(1, "cannot open 'nosuch' for reading");

    [Fact]
    public void Files0From_ZeroLengthName_ReportedAndRestCounted() =>
        Run("Invoke-BashPrintf 'a\\0\\0b\\0' | Invoke-BashWc '--files0-from=-'")
            .AssertFailed(1, "wc: -:2: invalid zero-length file name");

    [Fact]
    public void Files0From_MissingEntry_Exit1_StillTotals()
    {
        var r = Run("Invoke-BashPrintf 'a\\0zz\\0' | Invoke-BashWc '--files0-from=-'");
        Assert.Equal("2 3 6 a|2 3 6 total", Norm(r));
        r.AssertFailed(1, "zz: No such file or directory");
    }

    [Fact]
    public void Files0From_NeverTotal() =>
        Assert.Equal("2 3 6 a|0 1 3 b", Norm(Run("Invoke-BashWc '--total=never' '--files0-from=L'").AssertSuccess()));

    [Fact]
    public void Plan_ParsesBoth()
    {
        var p = InvokeBashWcCommand.Plan(new[] { "--total=only", "--files0-from=-" });
        Assert.Equal(InvokeBashWcCommand.TotalMode.Only, p.Total);
        Assert.Equal("-", p.Files0From);
    }
}
