using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for strings (shared ordered parser; binutils 2.42 getopt_long, usage
/// errors exit 1). Implemented: -n N / -nN / --bytes=N / obsolete -N (last wins; decimal, at least
/// 1: `-n 0` "minimum string length is too small: 0", `-n x` "invalid integer argument x" — the old
/// scan ignored `-n x` and silently clamped 0 to 1), -a/--all (FIX: was refused; scanning the whole
/// input is what strings does here), -h/--help, -v/-V/--version, unique long prefixes (`--by=6`).
/// Refused loudly (exit 2): -d -f -t -o -w -e -T -s -U and their long forms.
/// </summary>
public class StringsArgScanTests
{
    private static string Scan(string[] argv)
    {
        var s = InvokeBashStringsCommand.Plan(argv);
        if (s.Parsed.Error is { } e) return "ERR " + e.Message("strings");
        if (s.Error is { } m) return "ERR " + m;
        string info = s.Parsed.Has("help") ? " help" : s.Parsed.Has("version") ? " version" : "";
        return $"n={s.MinLength} all={s.Parsed.Has("all")} ops=[{string.Join(",", s.Operands)}]{info}";
    }

    [Theory]
    [InlineData("n=4 all=False ops=[]")]
    [InlineData("n=6 all=False ops=[]", "-n", "6")]
    [InlineData("n=6 all=False ops=[]", "-n6")]
    [InlineData("n=6 all=False ops=[]", "--bytes=6")]
    [InlineData("n=6 all=False ops=[]", "--bytes", "6")]
    [InlineData("n=6 all=False ops=[]", "--by=6")]  // FIX (abbreviation)
    [InlineData("n=6 all=False ops=[]", "-6")]  // FIX: obsolete -N
    [InlineData("n=6 all=False ops=[]", "-n", "3", "-n", "6")]
    [InlineData("n=6 all=True ops=[]", "-an6")]  // FIX (bundle: n takes the rest)
    [InlineData("n=4 all=True ops=[]", "-a")]  // FIX (was: refused)
    [InlineData("n=4 all=True ops=[]", "--all")]
    [InlineData("n=4 all=True ops=[]", "--al")]
    [InlineData("n=4 all=False ops=[f]", "f")]
    [InlineData("n=6 all=False ops=[f]", "f", "-n6")]  // FIX (options may follow operands)
    [InlineData("n=4 all=False ops=[-n6]", "--", "-n6")]
    [InlineData("n=4 all=False ops=[-]", "-")]
    [InlineData("n=4 all=False ops=[] help", "-h")]
    [InlineData("n=4 all=False ops=[] help", "--help")]
    [InlineData("n=4 all=False ops=[] version", "-v")]
    [InlineData("n=4 all=False ops=[] version", "-V")]
    [InlineData("n=4 all=False ops=[] version", "--version")]
    [InlineData("ERR strings: minimum string length is too small: 0", "-n", "0")]  // FIX (was: clamped to 1)
    [InlineData("ERR strings: invalid integer argument x", "-n", "x")]  // FIX (was: ignored)
    [InlineData("ERR strings: invalid integer argument -3", "-n", "-3")]
    [InlineData("ERR strings: option requires an argument -- 'n'", "-n")]
    [InlineData("ERR strings: invalid option -- 'x'", "-x")]
    [InlineData("ERR strings: unrecognized option '--nope'", "--nope")]
    [InlineData("ERR strings: option '-d' is recognized but not supported by ps-bash", "-d")]
    [InlineData("ERR strings: option '--data' is recognized but not supported by ps-bash", "--data")]
    [InlineData("ERR strings: option '-f' is recognized but not supported by ps-bash", "-f")]
    [InlineData("ERR strings: option '-t' is recognized but not supported by ps-bash", "-t", "x")]
    [InlineData("ERR strings: option '--radix' is recognized but not supported by ps-bash", "--radix=x")]
    [InlineData("ERR strings: option '-o' is recognized but not supported by ps-bash", "-o")]
    [InlineData("ERR strings: option '-w' is recognized but not supported by ps-bash", "-w")]
    [InlineData("ERR strings: option '-e' is recognized but not supported by ps-bash", "-e", "S")]
    [InlineData("ERR strings: option '-T' is recognized but not supported by ps-bash", "-T", "binary")]
    [InlineData("ERR strings: option '-s' is recognized but not supported by ps-bash", "-s", ":")]
    [InlineData("ERR strings: option '--unicode' is recognized but not supported by ps-bash", "--unicode=hex")]
    [InlineData("ERR strings: option '-U' is recognized but not supported by ps-bash", "-U", "d")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void UsageErrors_Exit1_UnsupportedExit2()
    {
        Assert.Equal(1, InvokeBashStringsCommand.ScanArgs(new[] { "-x" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashStringsCommand.ScanArgs(new[] { "-d" }).ErrorExitCode);
    }
}
