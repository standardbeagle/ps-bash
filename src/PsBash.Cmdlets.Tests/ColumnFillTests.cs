using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>column</c> without <c>-t</c> (util-linux 2.39 fill layout, `wsl bash` oracle, `| cat -A` byte checks):
/// cells are the widest entry + 1 rounded up to a multiple of 8, <c>width / cell</c> columns are filled down
/// (or across with -x), columns are separated by tabs up to the cell boundary.
/// </summary>
public class ColumnFillTests
{
    private static string[] Seq(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => i.ToString()).ToArray();

    private static string Fill(string[] lines, long width, bool rows = false, bool keepEmpty = false) =>
        string.Join("|", InvokeBashColumnCommand.RenderFill(lines, width, rows, keepEmpty)).Replace("\t", "<T>");

    [Fact]
    public void TenNumbers_ColumnMajor_AndRowMajor()
    {
        Assert.Equal("1<T>3<T>5<T>7<T>9|2<T>4<T>6<T>8<T>10", Fill(Seq(1, 10), 40));
        Assert.Equal("1<T>2<T>3<T>4<T>5|6<T>7<T>8<T>9<T>10", Fill(Seq(1, 10), 40, rows: true));
    }

    [Fact]
    public void Defaults_FortyNumbers_TenColumnsOfEight()
    {
        Assert.Equal(
            "1<T>5<T>9<T>13<T>17<T>21<T>25<T>29<T>33<T>37|2<T>6<T>10<T>14<T>18<T>22<T>26<T>30<T>34<T>38|" +
            "3<T>7<T>11<T>15<T>19<T>23<T>27<T>31<T>35<T>39|4<T>8<T>12<T>16<T>20<T>24<T>28<T>32<T>36<T>40",
            Fill(Seq(1, 40), 80));
    }

    [Fact]
    public void Width60_SevenColumns_ShortLastColumns()
    {
        Assert.Equal(
            "1<T>7<T>13<T>19<T>25<T>31<T>37|2<T>8<T>14<T>20<T>26<T>32<T>38|3<T>9<T>15<T>21<T>27<T>33<T>39|" +
            "4<T>10<T>16<T>22<T>28<T>34<T>40|5<T>11<T>17<T>23<T>29<T>35|6<T>12<T>18<T>24<T>30<T>36",
            Fill(Seq(1, 40), 60));
    }

    [Fact]
    public void HundredNumbers_RowMajor()
    {
        var rows = InvokeBashColumnCommand.RenderFill(Seq(1, 100), 80, fillRows: true, keepEmpty: false);
        Assert.Equal(10, rows.Count);
        Assert.Equal("1\t2\t3\t4\t5\t6\t7\t8\t9\t10", rows[0]);
        Assert.Equal("91\t92\t93\t94\t95\t96\t97\t98\t99\t100", rows[9]);
    }

    [Theory]
    [InlineData(30, "apple|banana|cherry|date|elderberry|fig|grape|honeydew|kiwi|lemon")]   // cell 16, one column
    public void WideEntries_OnePerLine(int width, string expected) =>
        Assert.Equal(expected, Fill(new[] { "apple", "banana", "cherry", "date", "elderberry", "fig", "grape", "honeydew", "kiwi", "lemon" }, width));

    [Fact]
    public void CellWidth_IsWidestPlusOneRoundedToEight()
    {
        string[] Rep(string s, int n) => Enumerable.Repeat(s, n).ToArray();
        // 7 chars: cell 8 -> 5 columns; 8 chars: cell 16 -> 2 columns; 15: cell 16; 16: cell 24 -> 1 column (width 40)
        Assert.Equal("abcdefg<T>abcdefg<T>abcdefg<T>abcdefg", Fill(Rep("abcdefg", 4), 40));
        Assert.Equal("abcdefgh<T>abcdefgh|abcdefgh<T>abcdefgh", Fill(Rep("abcdefgh", 4), 40));
        Assert.Equal("abcdefghijklmno<T>abcdefghijklmno|abcdefghijklmno", Fill(Rep("abcdefghijklmno", 3), 40));
        Assert.Equal("abcdefghijklmnop|abcdefghijklmnop|abcdefghijklmnop", Fill(Rep("abcdefghijklmnop", 3), 40));
    }

    [Fact]
    public void EntryWiderThanTheWidth_StillOnePerLine() =>
        Assert.Equal(new string('a', 99) + "|b|c", Fill(new[] { new string('a', 99), "b", "c" }, 40));

    [Fact]
    public void ExactFitBoundaries()
    {
        var e = new[] { "aaaa", "bbbb", "cccc", "dddd" };
        Assert.Equal("aaaa<T>cccc|bbbb<T>dddd", Fill(e, 19));
        Assert.Equal("aaaa<T>cccc|bbbb<T>dddd", Fill(e, 18));
        Assert.Equal("aaaa<T>bbbb<T>cccc<T>dddd", Fill(e, 32));
        Assert.Equal("aaaa|bbbb|cccc|dddd", Fill(e, 7));   // cell 8 > width/1: still one column
    }

    [Fact]
    public void BlankLines_AreDropped_UnlessKeepEmpty_AndAnEmptyEntryStillTakesACell()
    {
        var lines = new[] { "a", "", "b", "", "c" };
        Assert.Equal("a<T>b<T>c", Fill(lines, 40));
        Assert.Equal("a<T><T>b<T><T>c", Fill(lines, 40, keepEmpty: true));
    }

    [Fact]
    public void MixedWidths_PadWithSeveralTabs()
    {
        var e = new[] { "a", "bbbbbbbbbbbbbb", "cc", "d", "eeeeeeeeee", "ff", "g", "hhhh" };
        Assert.Equal("a<T><T>d<T><T>g|bbbbbbbbbbbbbb<T>eeeeeeeeee<T>hhhh|cc<T><T>ff", Fill(e, 60));
        Assert.Equal("a<T><T>bbbbbbbbbbbbbb<T>cc|d<T><T>eeeeeeeeee<T>ff|g<T><T>hhhh", Fill(e, 60, rows: true));
    }

    [Fact]
    public void Width_IsDisplayWidth_NotLength()
    {
        // CJK = 2 cells, a combining mark = 0, an emoji = 2, CR = 0 (oracle: `column -c 30` / `-c 40`).
        Assert.Equal("日本語<T>한국어<T>def|中文<T>abc<T>ghi", Fill(new[] { "日本語", "中文", "한국어", "abc", "def", "ghi" }, 30));
        Assert.Equal("é<T>abc<T>abc<T>abc", Fill(new[] { "é", "abc", "abc", "abc" }, 40));
        Assert.Equal("\U0001F600<T>abc<T>abc<T>abc", Fill(new[] { "\U0001F600", "abc", "abc", "abc" }, 40));
        Assert.Equal("aaaaaaa\r<T>bbbbbbb\r<T>ccccccc\r", Fill(new[] { "aaaaaaa\r", "bbbbbbb\r", "ccccccc\r" }, 40));
    }

    [Fact]
    public void LeadingAndTrailingBlanks_AreKept() =>
        Assert.Equal("  a  <T> b<T>c", Fill(new[] { "  a  ", " b", "c" }, 40));

    [Fact]
    public void ZeroOrTinyWidth_IsOneColumn()
    {
        Assert.Equal("1|2|3", Fill(Seq(1, 3), 0));
        Assert.Equal("1|2|3", Fill(Seq(1, 3), 1));
        Assert.Equal("1<T>2<T>3", Fill(Seq(1, 3), 4294967295));
    }

    [Fact]
    public void Empty_And_Single()
    {
        Assert.Equal(string.Empty, Fill(Array.Empty<string>(), 80));
        Assert.Equal("hello", Fill(new[] { "hello" }, 80));
    }

    [Theory]
    [InlineData("12", true, 12)]
    [InlineData(" 12", true, 12)]
    [InlineData("+12", true, 12)]
    [InlineData("0", true, 0)]
    [InlineData("4294967295", true, 4294967295)]
    [InlineData("-0", true, 0)]
    [InlineData("4294967296", false, 0)]
    [InlineData("-5", false, 0)]
    [InlineData("99999999999999999999", false, 0)]
    [InlineData("x", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("12abc", false, 0)]
    [InlineData("0x10", false, 0)]
    public void WidthParsing_FollowsStrtou32(string text, bool ok, long expected)
    {
        Assert.Equal(ok, InvokeBashColumnCommand.TryParseWidth(text, out long w, out _));
        if (ok) Assert.Equal(expected, w);
    }

    // ── end to end ──────────────────────────────────────────────────────────────────────────────
}

/// <summary>The cmdlet: width from -c, COLUMNS or the 80 default; -x; mutually exclusive with -t.</summary>
public class ColumnFillCmdletTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public ColumnFillCmdletTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    [Fact]
    public void WidthOption_AndFillRows()
    {
        Assert.Equal(new[] { "1\t3\t5\t7\t9", "2\t4\t6\t8\t10" },
            Run("1..10 | Invoke-BashColumn '-c' 40").AssertSuccess().Lines);
        Assert.Equal(new[] { "1\t2\t3\t4\t5", "6\t7\t8\t9\t10" },
            Run("1..10 | Invoke-BashColumn '-xc' 40").AssertSuccess().Lines);
        Assert.Equal(new[] { "1\t2\t3\t4\t5", "6\t7\t8\t9\t10" },
            Run("1..10 | Invoke-BashColumn '--fillrows' '--output-width=40'").AssertSuccess().Lines);
    }

    [Fact]
    public void ColumnsEnvironment_IsTheDefaultWidth_InvalidFallsBackTo80()
    {
        var sixty = Run("$env:COLUMNS='60'; try { 1..40 | Invoke-BashColumn } finally { $env:COLUMNS=$null }").AssertSuccess().Lines;
        Assert.Equal(6, sixty.Count);
        Assert.Equal("1\t7\t13\t19\t25\t31\t37", sixty[0]);
        Assert.Equal(new[] { "1\t2\t3\t4\t5\t6" },
            Run("$env:COLUMNS='abc'; try { 1..6 | Invoke-BashColumn } finally { $env:COLUMNS=$null }").AssertSuccess().Lines);
        Assert.Equal(new[] { "1\t2\t3\t4\t5\t6" },
            Run("$env:COLUMNS='0'; try { 1..6 | Invoke-BashColumn } finally { $env:COLUMNS=$null }").AssertSuccess().Lines);
        // -c beats COLUMNS
        Assert.Equal(14, Run("$env:COLUMNS='60'; try { 1..40 | Invoke-BashColumn '-c' 30 } finally { $env:COLUMNS=$null }").AssertSuccess().Lines.Count);
    }

    [Fact]
    public void TableAndFillRows_AreMutuallyExclusive() =>
        Run("'a b' | Invoke-BashColumn '-t' '-x'").AssertFailed(1, "mutually exclusive arguments: --table --fillrows");

    [Fact]
    public void BadWidth_IsExit1() =>
        Run("'a' | Invoke-BashColumn '-c' 'x'").AssertFailed(1, "invalid columns argument: 'x'");

    [Fact]
    public void Direct_BareC_Binds_TheWidth() =>
        Assert.Equal(new[] { "1", "2", "3" }, Run("1..3 | Invoke-BashColumn -c 1").AssertSuccess().Lines);

    [Fact]
    public void TableMode_IgnoresWidth() =>
        Assert.Equal(new[] { "a  b", "c  d" }, Run("'a b','c d' | Invoke-BashColumn '-c' 40 '-t'").AssertSuccess().Lines);
}
