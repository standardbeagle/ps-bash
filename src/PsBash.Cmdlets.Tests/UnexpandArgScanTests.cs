using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for unexpand (shared ordered parser + TabStopList). Checked against
/// GNU unexpand 9.4 (`wsl bash`): options -a/--all, --first-only, -t/--tabs=N|LIST, obsolete -NUM;
/// `-t` ENABLES -a (FIX: the old scan kept leading-only, so `unexpand -t 4` never converted
/// mid-line blanks), the obsolete `-NUM` does NOT, and `--first-only` overrides -a/-t wherever it
/// appears (FIX: order used to decide). A bad/zero/non-ascending tab spec exits 1 (was: silently 8).
/// </summary>
public class UnexpandArgScanTests
{
    private static string Scan(string[] argv)
    {
        var u = InvokeBashUnexpandCommand.Plan(argv);
        if (u.Parsed.Error is { } pe) return "ERR " + pe.Message("unexpand");
        if (u.Error is { } m) return "ERR " + m;
        return $"stops={ExpandArgScanTests.Stops(u.Tabs)} all={u.AllBlanks} ops=[{string.Join(",", u.Operands)}]";
    }

    [Theory]
    [InlineData("stops=8,16,24,32,40 all=False ops=[]")]
    [InlineData("stops=8,16,24,32,40 all=True ops=[]", "-a")]
    [InlineData("stops=8,16,24,32,40 all=True ops=[]", "--all")]
    [InlineData("stops=8,16,24,32,40 all=True ops=[]", "--al")]  // FIX (abbreviation)
    [InlineData("stops=4,8,12,16,20 all=True ops=[]", "-t", "4")]  // FIX: -t implies -a
    [InlineData("stops=4,8,12,16,20 all=True ops=[]", "-t4")]
    [InlineData("stops=4,8,12,16,20 all=True ops=[]", "--tabs=4")]
    [InlineData("stops=4,8 all=True ops=[]", "-t", "4,8")]
    [InlineData("stops=2,4,8,12,16 all=True ops=[]", "-t", "2,/4")]
    [InlineData("stops=2,4,6,8,10 all=False ops=[]", "-2")]  // -NUM: tab size only, no -a (GNU)
    [InlineData("stops=8,16,24,32,40 all=False ops=[]", "--first-only")]
    [InlineData("stops=8,16,24,32,40 all=False ops=[]", "--f")]  // FIX
    [InlineData("stops=8,16,24,32,40 all=False ops=[]", "-a", "--first-only")]
    [InlineData("stops=8,16,24,32,40 all=False ops=[]", "--first-only", "-a")]  // FIX (was: last wins = all)
    [InlineData("stops=4,8,12,16,20 all=False ops=[]", "--tabs=4", "--first-only")]
    [InlineData("stops=4,8,12,16,20 all=True ops=[f]", "f", "-t4")]  // FIX (options may follow operands)
    [InlineData("stops=8,16,24,32,40 all=False ops=[-a]", "--", "-a")]
    [InlineData("stops=8,16,24,32,40 all=False ops=[-]", "-")]
    [InlineData("ERR unexpand: tab size cannot be 0", "-t", "0")]  // FIX
    [InlineData("ERR unexpand: tab size contains invalid character(s): 'x'", "-t", "x")]  // FIX
    [InlineData("ERR unexpand: tab sizes must be ascending", "-t", "8,4")]  // FIX
    [InlineData("ERR unexpand: option requires an argument -- 't'", "-t")]
    [InlineData("ERR unexpand: invalid option -- 'z'", "-z")]
    [InlineData("ERR unexpand: unrecognized option '--nope'", "--nope")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Theory]
    [InlineData("\t\thello", "        hello", 4)]
    [InlineData("\thello", "        hello", 8)]
    [InlineData("\t   hello", "           hello", 8)]
    [InlineData("   hello", "   hello", 4)]
    public void UnexpandLeading_Uniform(string expected, string line, int size)
        => Assert.Equal(expected, InvokeBashUnexpandCommand.UnexpandLeading(line, TabStopList.Uniform(size)));

    [Fact]
    public void UnexpandAll_Uniform_MatchesTheOldModulo()
    {
        // Runs of >=2 blanks reaching a tab stop convert; the oracle rule is preserved.
        Assert.Equal("a\tb", InvokeBashUnexpandCommand.UnexpandAll("a       b", TabStopList.Default));
        Assert.Equal("a b", InvokeBashUnexpandCommand.UnexpandAll("a b", TabStopList.Default));
    }

    [Fact]
    public void UnexpandAll_List_StopsConvertingPastTheLastStop()
    {
        // GNU `unexpand -t 4,8`: blanks up to columns 4 and 8 convert, nothing beyond.
        Assert.True(TabStopList.TryParse(new[] { "4,8" }, out var list, out _));
        Assert.Equal("\t\ta" + "        " + "b", InvokeBashUnexpandCommand.UnexpandAll("        a        b", list));
    }
}
