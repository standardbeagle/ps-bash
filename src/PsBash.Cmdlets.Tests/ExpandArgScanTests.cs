using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for expand (shared ordered parser + TabStopList). Each FIX row was
/// checked against GNU expand 9.4 (`wsl bash`): options -i/--initial, -t/--tabs=N|LIST (several -t
/// concatenate; LIST ascending, last element may be /N or +N), obsolete -NUM; unique long prefixes;
/// `-t 0` "tab size cannot be 0", `-t x` "tab size contains invalid character(s)", `-t 4,2` "tab
/// sizes must be ascending" (all exit 1). The old scan read ANY unparsable -t (including the list
/// `-t 4,8`) as 8 without a word, had no abbreviations or -NUM, and `-t` at the end became a file.
/// `--first-only` is a ps-bash spelling of --initial (GNU expand lacks it) kept for compatibility.
/// Tab stops are rendered as the first five stops after column 0.
/// </summary>
public class ExpandArgScanTests
{
    internal static string Stops(TabStopList t)
    {
        var l = new List<int>();
        int col = 0;
        for (int i = 0; i < 5; i++)
        {
            int n = t.NextStop(col);
            if (n < 0) break;
            l.Add(n);
            col = n;
        }
        return string.Join(",", l);
    }

    private static string Scan(string[] argv)
    {
        var e = InvokeBashExpandCommand.Plan(argv);
        if (e.Parsed.Error is { } pe) return "ERR " + pe.Message("expand");
        if (e.Error is { } m) return "ERR " + m;
        return $"stops={Stops(e.Tabs)} i={e.InitialOnly} ops=[{string.Join(",", e.Operands)}]";
    }

    [Theory]
    [InlineData("stops=8,16,24,32,40 i=False ops=[]")]
    [InlineData("stops=4,8,12,16,20 i=False ops=[]", "-t", "4")]
    [InlineData("stops=4,8,12,16,20 i=False ops=[]", "-t4")]
    [InlineData("stops=4,8,12,16,20 i=False ops=[]", "--tabs=4")]
    [InlineData("stops=4,8,12,16,20 i=False ops=[]", "--tabs", "4")]
    [InlineData("stops=4,8,12,16,20 i=False ops=[]", "--tab=4")]  // FIX (abbreviation)
    [InlineData("stops=4,8,12,16,20 i=False ops=[]", "--t=4")]  // FIX
    [InlineData("stops=4,8,12,16,20 i=False ops=[]", "-4")]  // FIX: obsolete -NUM
    [InlineData("stops=4,8 i=False ops=[]", "-t", "4,8")]  // FIX (was: silently 8)
    [InlineData("stops=4,8 i=False ops=[]", "-t4,8")]
    [InlineData("stops=4,8 i=False ops=[]", "-t", "4 8")]  // blanks also separate
    [InlineData("stops=3,5 i=False ops=[]", "-t", "3", "-t", "5")]  // FIX: several -t concatenate (GNU)
    [InlineData("stops=2,4,8,12,16 i=False ops=[]", "-t", "2,/4")]  // /N: then every N columns
    [InlineData("stops=2,6,10,14,18 i=False ops=[]", "-t", "2,+4")]  // +N: relative to the last stop
    [InlineData("stops=4,8,12,16,20 i=False ops=[]", "-t", "/4")]
    [InlineData("stops=8,16,24,32,40 i=True ops=[]", "-i")]
    [InlineData("stops=8,16,24,32,40 i=True ops=[]", "--initial")]
    [InlineData("stops=8,16,24,32,40 i=True ops=[]", "--ini")]  // FIX
    [InlineData("stops=8,16,24,32,40 i=True ops=[]", "--first-only")]  // ps-bash extension, kept
    [InlineData("stops=3,6,9,12,15 i=True ops=[]", "-it3")]  // FIX (bundle: t takes the rest)
    [InlineData("stops=3,6,9,12,15 i=True ops=[]", "--initial", "-t", "3")]
    [InlineData("stops=8,16,24,32,40 i=False ops=[f]", "f")]
    [InlineData("stops=4,8,12,16,20 i=False ops=[f]", "f", "-t4")]  // FIX (options may follow operands)
    [InlineData("stops=8,16,24,32,40 i=False ops=[-t4]", "--", "-t4")]
    [InlineData("stops=8,16,24,32,40 i=False ops=[-]", "-")]
    [InlineData("ERR expand: tab size cannot be 0", "-t", "0")]  // FIX (was: silently 8)
    [InlineData("ERR expand: tab size contains invalid character(s): 'x'", "-t", "x")]  // FIX
    [InlineData("ERR expand: tab size contains invalid character(s): '4x'", "-t4x")]  // FIX
    [InlineData("ERR expand: tab sizes must be ascending", "-t", "4,2")]  // FIX
    [InlineData("ERR expand: tab sizes must be ascending", "-t", "4,4")]
    [InlineData("ERR expand: '/' specifier only allowed with the last value", "-t", "2,/4,8")]
    [InlineData("ERR expand: '+' specifier only allowed with the last value", "-t", "+4,8")]
    [InlineData("ERR expand: option requires an argument -- 't'", "-t")]  // FIX (was: -t became a file)
    [InlineData("ERR expand: option '--tabs' requires an argument", "--tabs")]
    [InlineData("ERR expand: invalid option -- 'z'", "-z")]
    [InlineData("ERR expand: unrecognized option '--nope'", "--nope")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void Abbreviations_ResolveHelpAndVersion()
    {
        Assert.True(InvokeBashExpandCommand.ScanArgs(new[] { "--ver" }).Has("version"));
        Assert.True(InvokeBashExpandCommand.ScanArgs(new[] { "--he" }).Has("help"));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(8, 8)]
    public void TabStopList_Uniform_SpacesAt(int col, int spaces)
        => Assert.Equal(spaces, TabStopList.Default.SpacesAt(col));

    [Fact]
    public void TabStopList_PlainListPastLastStop_IsOneSpace()
    {
        Assert.True(TabStopList.TryParse(new[] { "4,8" }, out var list, out _));
        Assert.Equal(4, list.SpacesAt(0));
        Assert.Equal(1, list.SpacesAt(8));   // GNU: tabs after the last stop are single spaces
        Assert.Equal(1, list.SpacesAt(20));
        Assert.Equal(-1, list.NextStop(8));
    }

    [Fact]
    public void ExpandTabs_HonoursTheList()
    {
        // GNU `printf 'x\t\t\ty\tz\n' | expand -t 4,8` = x + 3 spaces (col 4), + 4 (col 8), then
        // single spaces past the last stop.
        Assert.True(TabStopList.TryParse(new[] { "4,8" }, out var list, out _));
        Assert.Equal("x   " + "    " + " " + "y" + " " + "z", InvokeBashExpandCommand.ExpandTabs("x\t\t\ty\tz", list));
    }
}
