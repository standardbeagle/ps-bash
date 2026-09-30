using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution + table-layout tables for column (util-linux 2.39, `wsl bash`): usage errors
/// exit 1; `-s` is a SET of delimiter characters; `-l N` caps the field count; blank lines are ignored
/// unless `-L`; a short row is padded with empty cells; ambiguity lists follow util-linux's option table
/// (`--t`, `--o`, `--table-h`). Unsupported (exit 2): the fill-columns, naming, ordering, truncation,
/// JSON and tree options.
/// </summary>
public class ColumnArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashColumnCommand.Plan(argv);
        if (p.Parsed.Error is { } e) return "ERR " + e.Message("column");
        if (p.Error is { } m) return "ERR " + m;
        string limit = p.Limit == int.MaxValue ? "inf" : p.Limit.ToString();
        return $"t={p.Table} L={p.KeepEmpty} s={p.Separators ?? "<ws>"} o=[{p.OutputSeparator}] l={limit} ops=[{string.Join(",", p.Operands)}]";
    }

    [Theory]
    [InlineData("t=False L=False s=<ws> o=[  ] l=inf ops=[]")]
    [InlineData("t=True L=False s=<ws> o=[  ] l=inf ops=[f]", "-t", "f")]
    [InlineData("t=True L=False s=<ws> o=[  ] l=inf ops=[f]", "--table", "f")]
    [InlineData("t=True L=False s=: o=[  ] l=inf ops=[]", "-t", "-s", ":")]
    [InlineData("t=True L=False s=: o=[  ] l=inf ops=[]", "-ts:")]  // FIX (bundle with attached value)
    [InlineData("t=True L=False s=,; o=[  ] l=inf ops=[]", "-t", "-s", ",;")]  // a SET, was one literal string
    [InlineData("t=True L=False s=: o=[  ] l=inf ops=[]", "-t", "--separator=:")]
    [InlineData("t=True L=False s=: o=[  ] l=inf ops=[]", "-t", "--sep", ":")]
    [InlineData("t=True L=False s=<ws> o=[ | ] l=inf ops=[]", "-t", "-o", " | ")]  // FIX (was refused)
    [InlineData("t=True L=False s=<ws> o=[X] l=inf ops=[]", "-t", "--output-separator=X")]
    [InlineData("t=True L=False s=<ws> o=[  ] l=2 ops=[]", "-t", "-l", "2")]  // FIX (was refused as a file name)
    [InlineData("t=True L=True s=<ws> o=[  ] l=inf ops=[]", "-t", "-L")]
    [InlineData("t=True L=True s=<ws> o=[  ] l=inf ops=[]", "-t", "--table-e")]  // --table-empty-lines
    [InlineData("t=True L=False s=<ws> o=[  ] l=inf ops=[]", "-t", "-e")]
    [InlineData("t=True L=False s=<ws> o=[  ] l=inf ops=[]", "-t", "-d")]
    [InlineData("t=True L=False s=<ws> o=[  ] l=inf ops=[f]", "f", "-t")]  // FIX (options after operands)
    [InlineData("t=True L=False s=<ws> o=[  ] l=inf ops=[-t]", "-t", "--", "-t")]
    [InlineData("t=False L=False s=<ws> o=[  ] l=inf ops=[-]", "-")]
    // usage errors (exit 1)
    [InlineData("ERR column: invalid option -- 'z'", "-z")]  // FIX (was: a file name)
    [InlineData("ERR column: unrecognized option '--zzz'", "--zzz")]
    [InlineData("ERR column: invalid option -- 'w'", "-w", "20")]
    [InlineData("ERR column: option requires an argument -- 's'", "-t", "-s")]  // FIX (was: ignored)
    [InlineData("ERR column: option requires an argument -- 's'", "-ts")]
    [InlineData("ERR column: option requires an argument -- 'o'", "-o")]
    [InlineData("ERR column: option '--table' doesn't allow an argument", "--table=1")]
    [InlineData("ERR column: invalid columns limit argument: 'x'", "-t", "-l", "x")]
    [InlineData("ERR column: columns limit must be greater than zero", "-t", "-l", "0")]
    [InlineData("ERR column: option '--o' is ambiguous; possibilities: '--output-separator' '--output-width'", "--o")]
    [InlineData("ERR column: option '--table-h' is ambiguous; possibilities: '--table-hide' '--table-header-repeat'", "--table-h")]
    [InlineData("ERR column: option '--table-n' is ambiguous; possibilities: '--table-name' '--table-noextreme' '--table-noheadings'", "--table-n")]
    [InlineData("ERR column: option '--tab' is ambiguous; possibilities: '--table' '--table-columns' '--table-column' '--table-columns-limit' '--table-hide' '--table-name' '--table-maxout' '--table-noextreme' '--table-noheadings' '--table-order' '--table-right' '--table-truncate' '--table-wrap' '--table-empty-lines' '--table-header-repeat'", "--tab")]
    // util-linux options ps-bash refuses (exit 2)
    [InlineData("ERR column: option '-x' is recognized but not supported by ps-bash", "-x")]
    [InlineData("ERR column: option '-c' is recognized but not supported by ps-bash", "-c", "20")]
    [InlineData("ERR column: option '--output-width' is recognized but not supported by ps-bash", "--output-w", "20")]
    [InlineData("ERR column: option '-N' is recognized but not supported by ps-bash", "-t", "-N", "a,b")]
    [InlineData("ERR column: option '-R' is recognized but not supported by ps-bash", "-t", "-R", "1")]
    [InlineData("ERR column: option '-J' is recognized but not supported by ps-bash", "-J")]
    [InlineData("ERR column: option '--json' is recognized but not supported by ps-bash", "--json")]
    [InlineData("ERR column: option '-H' is recognized but not supported by ps-bash", "-t", "-H", "1")]
    [InlineData("ERR column: option '--tree' is recognized but not supported by ps-bash", "--tree", "1")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void ExitStatus_UsageIs1_UnsupportedIs2()
    {
        Assert.Equal(1, InvokeBashColumnCommand.ScanArgs(new[] { "-z" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashColumnCommand.ScanArgs(new[] { "-x" }).ErrorExitCode);
        Assert.True(InvokeBashColumnCommand.ScanArgs(new[] { "-V" }).Has("version"));
        Assert.True(InvokeBashColumnCommand.ScanArgs(new[] { "-h" }).Has("help"));
    }

    private static string Table(string[] lines, string? seps = null, string outSep = "  ", int limit = int.MaxValue, bool keepEmpty = false)
        => string.Join("|", InvokeBashColumnCommand.RenderTable(lines, seps, outSep, limit, keepEmpty));

    // Expected strings are byte-for-byte util-linux 2.39 output (`column -t ... | cat -A`), `|` joins rows.
    [Fact]
    public void Table_Whitespace_PadsShortRowAndDropsBlankLines()
        => Assert.Equal("a,,b   |c,d,e  |x      y", Table(new[] { "a,,b", "c,d,e", "", "x y" }));

    [Fact]
    public void Table_KeepEmptyLines_BlankRowIsAllPadding()
        => Assert.Equal("a,,b   |c,d,e  |       |x      y", Table(new[] { "a,,b", "c,d,e", "", "x y" }, keepEmpty: true));

    [Fact]
    public void Table_SeparatorSet_KeepsEmptyFields()
        => Assert.Equal("a       b|c    d  e|x y     ", Table(new[] { "a,,b", "c,d,e", "", "x y" }, seps: ","));

    [Fact]
    public void Table_SeparatorIsASetOfCharacters()
        => Assert.Equal("a       b|c    d  e|x y     ", Table(new[] { "a,,b", "c;d,e", "", "x y" }, seps: ",;"));

    [Fact]
    public void Table_ColumnsLimit_LastColumnKeepsRestOfLine()
        => Assert.Equal("a  b c d|e  f", Table(new[] { "a b c d", "e f" }, limit: 2));

    [Fact]
    public void Table_OutputSeparator_ShortRowStillGetsItsTrailingSeparator()
        => Assert.Equal("a:b:c d|e:f:", Table(new[] { "a b c d", "e f" }, outSep: ":", limit: 3));

    [Fact]
    public void Table_LeadingBlanksAndTabsAreSeparators()
        => Assert.Equal("a  b|c  d", Table(new[] { " a  b", "\tc\td" }));

    [Fact]
    public void Table_LimitOneWithSeparator_KeepsWholeLine()
        => Assert.Equal("a:b", Table(new[] { "a:b" }, seps: ":", limit: 1));
}
