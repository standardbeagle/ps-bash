using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The pure <c>ls -C</c> / <c>-x</c> / <c>-m</c> layouts against GNU coreutils 9.4 (the expected text of every row was
/// read from <c>wsl ls -C -w N</c> on six four-letter names), plus the cmdlet end to end.
/// </summary>
public class LsColumnsTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public LsColumnsTests(SharedPwshFixture fixture) => _fixture = fixture;

    private static List<LsCell> Cells(params string[] names) => names.Select(n => new LsCell(n, n.Length)).ToList();
    private static readonly string[] Six = { "aaaa", "bbbb", "cccc", "dddd", "eeee", "ffff" };

    [Theory]
    [InlineData(1, "aaaa|bbbb|cccc|dddd|eeee|ffff")]
    [InlineData(7, "aaaa|bbbb|cccc|dddd|eeee|ffff")]
    [InlineData(11, "aaaa  dddd|bbbb  eeee|cccc  ffff")]
    [InlineData(14, "aaaa  dddd|bbbb  eeee|cccc  ffff")]
    [InlineData(18, "aaaa  cccc  eeee|bbbb  dddd  ffff")]
    [InlineData(30, "aaaa  cccc  eeee|bbbb  dddd  ffff")]
    public void Vertical_FitsTheWidth(int width, string expected)
        => Assert.Equal(expected, string.Join("|", LsColumns.Render(Cells(Six), width, across: false, tabSize: 8)));

    [Fact]
    public void Vertical_WidthZero_IsOneLine()
        => Assert.Equal("aaaa  bbbb  cccc  dddd  eeee  ffff",
            Assert.Single(LsColumns.Render(Cells(Six), 0, across: false, tabSize: 8)));

    [Fact]
    public void Across_FillsRowsFirst()
        => Assert.Equal("aaaa  bbbb  cccc|dddd  eeee  ffff",
            string.Join("|", LsColumns.Render(Cells(Six), 20, across: true, tabSize: 8)));

    [Fact]
    public void Commas_WrapBeforeANameThatWouldNotFit()
    {
        Assert.Equal("aaaa, bbbb, cccc,|dddd, eeee, ffff", string.Join("|", LsColumns.RenderCommas(Cells(Six), 20)));
        Assert.Equal("aaaa, bbbb, cccc, dddd, eeee, ffff", Assert.Single(LsColumns.RenderCommas(Cells(Six), 0)));
    }

    [Fact]
    public void Tabs_AreUsedWhereATabStopIsCrossed_AndTabSizeZeroMeansSpaces()
    {
        // oracle: ls -x -w 30 on aaaa..ffff prints "aaaa  bbbb  cccc  dddd<TAB>eeee" (24 -> tab stop 24).
        var line = LsColumns.Render(Cells(Six), 30, across: true, tabSize: 8)[0];
        Assert.Equal("aaaa  bbbb  cccc  dddd\teeee", line);
        Assert.Equal("aaaa  bbbb  cccc  dddd  eeee", LsColumns.Render(Cells(Six), 30, across: true, tabSize: 0)[0]);
        // oracle: -C -T 3 -w 40 puts a tab between every pair (every cell is 6 wide, tab stops every 3).
        Assert.Equal("aaaa\tbbbb\tcccc\tdddd\teeee\tffff", Assert.Single(LsColumns.Render(Cells(Six), 40, across: false, tabSize: 3)));
    }

    [Fact]
    public void WideCharacters_CountTheirDisplayWidth()
    {
        var cells = new List<LsCell> { new("日本語", TextWidth.Of("日本語")), new("ab", 2) };
        Assert.Equal(6, cells[0].Width);
        Assert.Equal("日本語  ab", Assert.Single(LsColumns.Render(cells, 80, across: false, tabSize: 0)));
    }

    // ---- end to end through the cmdlet ----

    private string[] Run(string dirScript)
    {
        var pwsh = _fixture.AcquireFresh();
        var res = pwsh.AddScript(dirScript).Invoke();
        pwsh.Commands.Clear();
        return res.Select(o => o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
    }

    private static string MakeDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "psb-lsc-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        foreach (var n in Six) File.WriteAllText(Path.Combine(d, n), "");
        return d;
    }

    [Fact]
    public void Cmdlet_C_X_M_WithWidth()
    {
        var d = MakeDir();
        try
        {
            Assert.Equal(new[] { "aaaa  cccc  eeee", "bbbb  dddd  ffff" }, Run($"Invoke-BashLs '-C' '-w' '20' '{d}'"));
            Assert.Equal(new[] { "aaaa  bbbb  cccc", "dddd  eeee  ffff" }, Run($"Invoke-BashLs '-x' '-w' '20' '{d}'"));
            Assert.Equal(new[] { "aaaa, bbbb, cccc,", "dddd, eeee, ffff" }, Run($"Invoke-BashLs '-m' '-w' '20' '{d}'"));
            Assert.Equal(new[] { "aaaa  bbbb  cccc  dddd  eeee  ffff" }, Run($"Invoke-BashLs '-C' '-w' '0' '{d}'"));
            Assert.Equal(new[] { "aaaa  cccc  eeee", "bbbb  dddd  ffff" }, Run($"Invoke-BashLs '--width=20' '--format=vertical' '{d}'"));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void DirectCall_BareW_BindsTheDecoy_NotWarningAction()
    {
        var d = MakeDir();
        try { Assert.Equal(new[] { "aaaa  bbbb  cccc", "dddd  eeee  ffff" }, Run($"Invoke-BashLs -w 20 '-x' '{d}'")); }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Cmdlet_ColumnsEnvironment_IsTheDefaultWidth_AndWBeatsIt()
    {
        var d = MakeDir();
        var old = Environment.GetEnvironmentVariable("COLUMNS");
        try
        {
            Environment.SetEnvironmentVariable("COLUMNS", "20");
            Assert.Equal(new[] { "aaaa  cccc  eeee", "bbbb  dddd  ffff" }, Run($"Invoke-BashLs '-C' '{d}'"));
            Assert.Single(Run($"Invoke-BashLs '-C' '-w' '100' '{d}'"));
        }
        finally { Environment.SetEnvironmentVariable("COLUMNS", old); Directory.Delete(d, true); }
    }

    [Fact]
    public void Cmdlet_FormatIsLastWins_ButDashOneNeverCancelsLong()
    {
        var d = MakeDir();
        try
        {
            Assert.Equal(6, Run($"Invoke-BashLs '-x' '-1' '{d}'").Length);                        // -1 after -x = one per line
            Assert.Equal(2, Run($"Invoke-BashLs '-1' '-x' '-w' '20' '{d}'").Length);              // -x after -1 = across
            Assert.All(Run($"Invoke-BashLs '-C' '-l' '{d}'").Skip(1), l => Assert.Matches(@"^-[rwx-]{9} ", l));
            Assert.Equal(2, Run($"Invoke-BashLs '-l' '-C' '-w' '20' '{d}'").Length);              // oracle: -lC is vertical
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Cmdlet_IndicatorsAndInodeBlocksCountTowardsTheWidth()
    {
        var d = MakeDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(d, "gggg"));
            // "gggg/" is 5 wide: 7 cells of at most 7 columns each -> 2 columns of the 22-wide line need 14+.
            var lines = Run($"Invoke-BashLs '-F' '-C' '-w' '14' '{d}'");
            Assert.Contains("gggg/", string.Join("|", lines));
            Assert.Equal(new[] { "aaaa  eeee", "bbbb  ffff", "cccc  gggg/", "dddd" }, lines);
        }
        finally { Directory.Delete(d, true); }
    }
}
