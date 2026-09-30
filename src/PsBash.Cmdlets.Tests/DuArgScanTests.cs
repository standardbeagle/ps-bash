using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for du (shared ordered parser). Checked against GNU du 9.4
/// (`wsl bash`): usage errors exit 1; -a with -s and -s with a non-zero -d are usage errors
/// ("cannot both summarize and show all entries", "summarizing conflicts with --max-depth=N");
/// the last of -h/-k/-m/-b wins; long options abbreviate (`--max=1`, `--ap`) and an ambiguous
/// prefix lists candidates in GNU's table order (`--s` = '--si' '--separate-dirs' '--summarize');
/// -k/-x/-l/--apparent-size are accepted no-ops (this du always reports apparent size).
/// Old behaviour: the per-character bundle decoder swallowed every unknown letter (`du -z d` ran),
/// `-d x` was ignored, `--` and options after operands were not honoured.
/// </summary>
public class DuArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashDuCommand.Plan(argv);
        if (p.Parsed.Error is { } e) return "ERR " + e.Message("du");
        if (p.Error is { } m) return "ERR " + m;
        string depth = p.MaxDepth == int.MaxValue ? "inf" : p.MaxDepth.ToString();
        return $"a={p.All} c={p.Total} s={p.Summarize} d={depth} mode={p.Mode} ex=[{string.Join(",", p.Excludes)}] ops=[{string.Join(",", p.Operands)}]";
    }

    [Theory]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "d")]
    [InlineData("a=True c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "-a", "d")]
    [InlineData("a=True c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "--all", "d")]
    [InlineData("a=True c=True s=False d=inf mode=Kilo ex=[] ops=[d]", "-ac", "d")]
    [InlineData("a=False c=True s=True d=0 mode=Kilo ex=[] ops=[d]", "-c", "-s", "d")]
    [InlineData("a=False c=False s=False d=1 mode=Kilo ex=[] ops=[d]", "-d", "1", "d")]
    [InlineData("a=False c=False s=False d=1 mode=Kilo ex=[] ops=[d]", "-d1", "d")]
    [InlineData("a=False c=False s=False d=1 mode=Kilo ex=[] ops=[d]", "--max-depth=1", "d")]
    [InlineData("a=False c=False s=False d=1 mode=Kilo ex=[] ops=[d]", "--max-depth", "1", "d")]
    [InlineData("a=False c=False s=False d=1 mode=Kilo ex=[] ops=[d]", "--max=1", "d")]  // FIX (abbreviation)
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "-d", "-1", "d")]  // GNU: -1 = unlimited
    [InlineData("a=False c=False s=True d=0 mode=Kilo ex=[] ops=[d]", "-sd0", "d")]  // -s with -d 0 is fine
    [InlineData("a=False c=False s=False d=1 mode=Kilo ex=[] ops=[d]", "d", "-d1")]  // FIX (options after operands)
    [InlineData("a=False c=False s=False d=inf mode=Human ex=[] ops=[d]", "-h", "d")]
    [InlineData("a=False c=False s=True d=0 mode=Human ex=[] ops=[d]", "-sh", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "-hk", "d")]  // last wins
    [InlineData("a=False c=False s=False d=inf mode=Human ex=[] ops=[d]", "-kh", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Mega ex=[] ops=[d]", "-m", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Bytes ex=[] ops=[d]", "-b", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "-bk", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "--apparent-size", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "-x", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[d]", "-l", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[*.o] ops=[d]", "--exclude=*.o", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[*.o,x] ops=[d]", "--exclude", "*.o", "--exclude=x", "d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[-d]", "--", "-d")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[-]", "-")]
    [InlineData("a=False c=False s=False d=inf mode=Kilo ex=[] ops=[]")]
    // usage errors (exit 1)
    [InlineData("ERR du: invalid option -- 'z'", "-z", "d")]  // FIX (was: ignored)
    [InlineData("ERR du: unrecognized option '--zzz'", "--zzz", "d")]
    [InlineData("ERR du: option requires an argument -- 'd'", "-d")]
    [InlineData("ERR du: option '--max-depth' requires an argument", "--max-depth")]
    [InlineData("ERR du: option '--exclude' requires an argument", "--exclude")]
    [InlineData("ERR du: invalid maximum depth 'x'", "-d", "x", "d")]  // FIX (was: ignored)
    [InlineData("ERR du: invalid maximum depth 's'", "-ds", "d")]
    [InlineData("ERR du: invalid maximum depth 'x'", "--max-depth=x", "d")]
    [InlineData("ERR du: option '--s' is ambiguous; possibilities: '--si' '--separate-dirs' '--summarize'", "--s", "d")]
    [InlineData("ERR du: option '--t' is ambiguous; possibilities: '--total' '--threshold' '--time' '--time-style'", "--t", "d")]
    [InlineData("ERR du: option '--b' is ambiguous; possibilities: '--block-size' '--bytes'", "--b", "d")]
    [InlineData("ERR du: option '--all' doesn't allow an argument", "--all=1", "d")]
    [InlineData("ERR du: cannot both summarize and show all entries", "-as", "d")]
    [InlineData("ERR du: warning: summarizing conflicts with --max-depth=1", "-s", "-d", "1", "d")]
    // valid GNU options ps-bash refuses (exit 2)
    [InlineData("ERR du: option '-B' is recognized but not supported by ps-bash", "-B", "1", "d")]
    [InlineData("ERR du: option '--block-size' is recognized but not supported by ps-bash", "--block-size=1", "d")]
    [InlineData("ERR du: option '-P' is recognized but not supported by ps-bash", "-P", "d")]
    [InlineData("ERR du: option '-L' is recognized but not supported by ps-bash", "-L", "d")]
    [InlineData("ERR du: option '-S' is recognized but not supported by ps-bash", "-S", "d")]
    [InlineData("ERR du: option '-t' is recognized but not supported by ps-bash", "-t", "1", "d")]
    [InlineData("ERR du: option '--si' is recognized but not supported by ps-bash", "--si", "d")]
    [InlineData("ERR du: option '--time' is recognized but not supported by ps-bash", "--time", "d")]
    [InlineData("ERR du: option '--inodes' is recognized but not supported by ps-bash", "--inodes", "d")]
    [InlineData("ERR du: option '-0' is recognized but not supported by ps-bash", "-0", "d")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void ExitStatus_UsageIs1_UnsupportedIs2()
    {
        Assert.Equal(1, InvokeBashDuCommand.ScanArgs(new[] { "-z" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashDuCommand.ScanArgs(new[] { "-B", "1" }).ErrorExitCode);
        Assert.True(InvokeBashDuCommand.ScanArgs(new[] { "--ver" }).Has("version"));
    }
}
