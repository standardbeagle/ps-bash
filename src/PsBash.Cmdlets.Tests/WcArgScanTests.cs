using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for wc. The pre-migration scan (BashRuntime.ConvertFromBashArgs + a
/// bundle-recovery patch-up) accepted only the exact tokens, needed a decoy for -w/-c, turned
/// every unknown flag into an operand that a second classifier then re-examined, and did not know
/// abbreviations (`--li`), `--` or options after operands. Each FIX row was checked against GNU wc
/// 9.4 (`wsl bash`): options -c -m -l -L -w + --bytes --chars --lines --max-line-length --words
/// --files0-from --total (+ --help --version); unique long-option prefixes are accepted; usage
/// errors exit 1. Accepted here: the five column selectors. Refused loudly as valid-but-unsupported
/// (exit 2): --files0-from, --total.
/// </summary>
public class WcArgScanTests
{
    private static string Scan(string[] argv)
    {
        var w = InvokeBashWcCommand.Plan(argv);
        if (w.Parsed.Error is { } e) return "ERR " + e.Message("wc");
        static string F(bool b, char c) => b ? c.ToString() : "-";
        return $"{F(w.Lines, 'l')}{F(w.Words, 'w')}{F(w.Bytes, 'c')}{F(w.Chars, 'm')}{F(w.MaxLine, 'L')} " +
               $"ops=[{string.Join(",", w.Operands)}]";
    }

    [Theory]
    [InlineData("----- ops=[]")]
    [InlineData("l---- ops=[]", "-l")]
    [InlineData("-w--- ops=[]", "-w")]
    [InlineData("--c-- ops=[]", "-c")]
    [InlineData("---m- ops=[]", "-m")]
    [InlineData("----L ops=[]", "-L")]
    [InlineData("lw--- ops=[]", "-lw")]
    [InlineData("lw--- ops=[]", "-wl")]
    [InlineData("-wc-- ops=[]", "-wc")]
    [InlineData("lwcmL ops=[]", "-lwcmL")]
    [InlineData("lw--- ops=[]", "-l", "-w")]
    [InlineData("l---- ops=[]", "-ll")]
    [InlineData("l---- ops=[f]", "-l", "f")]
    [InlineData("l---- ops=[f]", "f", "-l")]  // FIX (was: operands [f,-l] classified per operand; options may follow operands)
    [InlineData("l---- ops=[f,g]", "f", "-l", "g")]  // FIX
    [InlineData("l---- ops=[]", "--lines")]
    [InlineData("-w--- ops=[]", "--words")]
    [InlineData("--c-- ops=[]", "--bytes")]
    [InlineData("---m- ops=[]", "--chars")]
    [InlineData("----L ops=[]", "--max-line-length")]
    [InlineData("l---- ops=[]", "--li")]  // FIX (was: operands [--li] -> "unrecognized option")
    [InlineData("--c-- ops=[]", "--by")]  // FIX
    [InlineData("---m- ops=[]", "--ch")]  // FIX
    [InlineData("----L ops=[]", "--max")]  // FIX
    [InlineData("-w--- ops=[]", "--w")]  // FIX
    [InlineData("l---- ops=[]", "--l")]  // FIX
    [InlineData("----- ops=[-l]", "--", "-l")]
    [InlineData("l---- ops=[-w]", "-l", "--", "-w")]
    [InlineData("----- ops=[-]", "-")]
    [InlineData("----- ops=[a,b]", "a", "b")]
    [InlineData("ERR wc: option '--files0-from' is recognized but not supported by ps-bash", "--files0-from=f")]
    [InlineData("ERR wc: option '--files0-from' is recognized but not supported by ps-bash", "--files0-from", "f")]
    [InlineData("ERR wc: option '--files0-from' is recognized but not supported by ps-bash", "--f", "x")]  // FIX (was: operands)
    [InlineData("ERR wc: option '--total' is recognized but not supported by ps-bash", "--total=always")]  // FIX (was: operands)
    [InlineData("ERR wc: option '--total' is recognized but not supported by ps-bash", "--t=never")]  // FIX
    [InlineData("ERR wc: unrecognized option '--bogus'", "--bogus")]
    [InlineData("ERR wc: unrecognized option '--bogus=1'", "--bogus=1")]
    [InlineData("ERR wc: option '--lines' doesn't allow an argument", "--lines=1")]  // FIX
    [InlineData("ERR wc: invalid option -- 'x'", "-x")]
    [InlineData("ERR wc: invalid option -- 'x'", "-lx")]
    [InlineData("ERR wc: invalid option -- 'x'", "-xl")]
    public void Plan_MatchesGnuWc(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    [Theory]
    [InlineData("--version", "version")]
    [InlineData("--vers", "version")]
    [InlineData("--v", "version")]
    [InlineData("--help", "help")]
    [InlineData("--he", "help")]
    [InlineData("--h", "help")]
    public void InfoOptions_AreResolvedThroughAbbreviation(string arg, string id)
    {
        var p = InvokeBashWcCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-x", 1)]
    [InlineData("--lines=1", 1)]
    [InlineData("--files0-from=f", 2)]
    [InlineData("--total=always", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashWcCommand.ScanArgs(new[] { arg }).ErrorExitCode);
    }
}
