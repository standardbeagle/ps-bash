using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for fold (shared ordered parser). Each FIX row was checked against
/// GNU fold 9.4 (`wsl bash`): options -b -s -w + long forms and the obsolete `-NUM` width; unique
/// long prefixes are accepted (`--wid=4`, `--s`); a width must be a positive decimal (`-w 0`,
/// `-w x`, `-w 4k`, `-w -1` = "invalid number of columns", exit 1); usage errors exit 1.
/// The pre-migration scan silently kept 80 for a bad `-w x`, turned `-w 0` / `-w -1` into
/// "never wrap", and had no abbreviations, `-NUM`, or ordered `--`.
/// </summary>
public class FoldArgScanTests
{
    private static string Scan(string[] argv)
    {
        var f = InvokeBashFoldCommand.Plan(argv);
        if (f.Parsed.Error is { } e) return "ERR " + e.Message("fold");
        if (f.Error is { } m) return "ERR " + m;
        return $"w={f.Width} s={f.BreakSpaces} ops=[{string.Join(",", f.Operands)}]";
    }

    [Theory]
    [InlineData("w=80 s=False ops=[]")]
    [InlineData("w=4 s=False ops=[]", "-w", "4")]
    [InlineData("w=4 s=False ops=[]", "-w4")]
    [InlineData("w=4 s=False ops=[]", "--width=4")]
    [InlineData("w=4 s=False ops=[]", "--width", "4")]
    [InlineData("w=4 s=False ops=[]", "--wid=4")]  // FIX (abbreviation)
    [InlineData("w=4 s=False ops=[]", "--w=4")]  // FIX
    [InlineData("w=3 s=False ops=[]", "-w", "4", "-w", "3")]  // last wins
    [InlineData("w=5 s=False ops=[]", "-5")]  // FIX: obsolete -NUM width
    [InlineData("w=12 s=False ops=[]", "-12")]
    [InlineData("w=80 s=True ops=[]", "-s")]
    [InlineData("w=80 s=True ops=[]", "--spaces")]
    [InlineData("w=80 s=True ops=[]", "--s")]  // FIX
    [InlineData("w=80 s=False ops=[]", "-b")]
    [InlineData("w=4 s=True ops=[]", "-sb", "-w4")]
    [InlineData("w=4 s=True ops=[]", "-sw4")]  // FIX (w takes the rest of the bundle)
    [InlineData("w=4 s=True ops=[]", "-sbw", "4")]
    [InlineData("w=80 s=False ops=[f]", "f")]
    [InlineData("w=4 s=False ops=[f]", "f", "-w4")]  // FIX (options may follow operands)
    [InlineData("w=80 s=False ops=[-w4]", "--", "-w4")]
    [InlineData("w=80 s=False ops=[-]", "-")]
    [InlineData("w=99999 s=False ops=[]", "-w", "99999")]
    [InlineData("ERR fold: invalid number of columns: 'x'", "-w", "x")]  // FIX (was: silently 80)
    [InlineData("ERR fold: invalid number of columns: '0'", "-w", "0")]  // FIX (was: never wrap); GNU adds ": Numerical result out of range"
    [InlineData("ERR fold: invalid number of columns: '-1'", "-w", "-1")]  // FIX
    [InlineData("ERR fold: invalid number of columns: '4k'", "-w4k")]  // FIX
    [InlineData("ERR fold: invalid number of columns: ''", "-w", "")]
    [InlineData("ERR fold: option requires an argument -- 'w'", "-w")]
    [InlineData("ERR fold: option '--width' requires an argument", "--width")]
    [InlineData("ERR fold: invalid option -- 'c'", "-c")]
    [InlineData("ERR fold: invalid option -- 'z'", "-sz")]
    [InlineData("ERR fold: unrecognized option '--nope'", "--nope")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void Scan_HelpAndVersion_AbbreviationsResolve()
    {
        // `--ver` is unique to --version (fold has no --verbose) — GNU prints the version.
        var parsed = InvokeBashFoldCommand.ScanArgs(new[] { "--ver" });
        Assert.True(parsed.Has("version"));
        Assert.True(InvokeBashFoldCommand.ScanArgs(new[] { "--he" }).Has("help"));
    }

    [Fact]
    public void UsageErrors_Exit1()
    {
        Assert.Equal(1, InvokeBashFoldCommand.ScanArgs(new[] { "-z" }).ErrorExitCode);
    }
}
