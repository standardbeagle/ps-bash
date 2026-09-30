using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for split (shared ordered parser + GNU value validation). Checked
/// against GNU split 9.4 (`wsl bash`): -l/--lines, -b/--bytes (SIZE suffix K M G ... KB MB ...
/// KiB), -a/--suffix-length, -d and --numeric-suffixes[=FROM], --additional-suffix, obsolete
/// `-NUM` (= -l NUM); a bad count is exit 1 (`-l 0`, `-l x`, `-b 1x`, `-a x`; the old scan silently
/// used 1000 lines), `-l` with `-b` is "cannot split in more than one way", a 3rd operand is "extra
/// operand". Refused with exit 2: -n -C -t -e -x -u --filter --verbose (and their long forms).
/// (`-z`/`--null-data` are NOT split options in 9.4 — usage errors now, not "unsupported".)
/// </summary>
public class SplitArgScanTests
{
    private static string Scan(string[] argv)
    {
        var s = InvokeBashSplitCommand.Plan(argv);
        if (s.Parsed.Error is { } e) return "ERR " + e.Message("split");
        if (s.Error is { } m) return "ERR " + m;
        string by = s.Bytes is { } b ? b.ToString() : "-";
        return $"l={s.Lines} b={by} a={s.SuffixLength} d={s.Numeric}/{s.NumericStart} add={s.AdditionalSuffix} ops=[{string.Join(",", s.Operands)}]";
    }

    private const string D = "l=1000 b=- a=2 d=False/0 add= ops=[]";

    [Theory]
    [InlineData(D)]
    [InlineData("l=3 b=- a=2 d=False/0 add= ops=[]", "-l", "3")]
    [InlineData("l=3 b=- a=2 d=False/0 add= ops=[]", "-l3")]
    [InlineData("l=3 b=- a=2 d=False/0 add= ops=[]", "--lines=3")]
    [InlineData("l=3 b=- a=2 d=False/0 add= ops=[]", "--lines", "3")]
    [InlineData("l=1000 b=- a=3 d=False/0 add= ops=[]", "--suffix=3")]  // FIX (abbreviation)
    [InlineData("l=1000 b=6 a=2 d=False/0 add= ops=[]", "--by=6")]
    [InlineData("ERR split: option '--li' is ambiguous; possibilities: '--line-bytes' '--lines'", "--li=3")]
    [InlineData("l=3 b=- a=2 d=False/0 add= ops=[]", "-3")]  // FIX: obsolete -NUM
    [InlineData("l=4 b=- a=2 d=False/0 add= ops=[]", "-l", "3", "-l", "4")]
    [InlineData("l=1000 b=6 a=2 d=False/0 add= ops=[]", "-b", "6")]
    [InlineData("l=1000 b=1024 a=2 d=False/0 add= ops=[]", "-b", "1K")]
    [InlineData("l=1000 b=1000 a=2 d=False/0 add= ops=[]", "-b", "1KB")]  // FIX (was: 1)
    [InlineData("l=1000 b=1048576 a=2 d=False/0 add= ops=[]", "--bytes=1M")]
    [InlineData("l=1000 b=1048576 a=2 d=False/0 add= ops=[]", "-b1MiB")]
    [InlineData("l=1000 b=512 a=2 d=False/0 add= ops=[]", "-b", "1b")]
    [InlineData("l=1000 b=- a=3 d=False/0 add= ops=[]", "-a", "3")]
    [InlineData("l=1000 b=- a=3 d=False/0 add= ops=[]", "--suffix-length=3")]
    [InlineData("l=1000 b=- a=2 d=False/0 add= ops=[]", "-a", "0")]  // GNU: auto-length; kept at 2
    [InlineData("l=1000 b=- a=2 d=True/0 add= ops=[]", "-d")]
    [InlineData("l=1000 b=- a=2 d=True/0 add= ops=[]", "--numeric-suffixes")]
    [InlineData("l=1000 b=- a=2 d=True/0 add= ops=[]", "--numeric")]  // FIX
    [InlineData("l=1000 b=- a=2 d=True/5 add= ops=[]", "--numeric-suffixes=5")]  // FIX (was: unknown)
    [InlineData("l=3 b=- a=2 d=True/0 add= ops=[]", "-l3", "-d")]
    [InlineData("l=3 b=- a=1 d=True/0 add= ops=[]", "-dl3", "-a1")]  // -dl3: d then l takes "3"
    [InlineData("l=1000 b=- a=2 d=False/0 add=.txt ops=[]", "--additional-suffix=.txt")]
    [InlineData("l=1000 b=- a=2 d=False/0 add=.txt ops=[]", "--add=.txt")]  // FIX
    [InlineData("l=1000 b=- a=2 d=False/0 add= ops=[f]", "f")]
    [InlineData("l=1000 b=- a=2 d=False/0 add= ops=[f,y]", "f", "y")]
    [InlineData("l=3 b=- a=2 d=False/0 add= ops=[f]", "f", "-l3")]  // FIX (options may follow operands)
    [InlineData("l=1000 b=- a=2 d=False/0 add= ops=[-l,3]", "--", "-l", "3")]
    [InlineData("l=1000 b=- a=2 d=False/0 add= ops=[-]", "-")]
    [InlineData("ERR split: invalid number of lines: '0'", "-l", "0")]  // FIX (was: silently 1000)
    [InlineData("ERR split: invalid number of lines: 'x'", "-l", "x")]  // FIX
    [InlineData("ERR split: invalid number of lines: '1K'", "-l", "1K")]
    [InlineData("ERR split: invalid number of bytes: '0'", "-b", "0")]  // FIX
    [InlineData("ERR split: invalid number of bytes: '1x'", "-b", "1x")]  // FIX
    [InlineData("ERR split: invalid number of bytes: '-5'", "-b", "-5")]
    [InlineData("ERR split: invalid suffix length: 'x'", "-a", "x")]  // FIX
    [InlineData("ERR split: invalid suffix start: 'x'", "--numeric-suffixes=x")]
    [InlineData("ERR split: cannot split in more than one way", "-l", "4", "-b", "3")]  // FIX
    [InlineData("ERR split: extra operand 'z'", "f", "y", "z")]  // FIX (was: ignored)
    [InlineData("ERR split: option requires an argument -- 'l'", "-l")]
    [InlineData("ERR split: option '--bytes' requires an argument", "--bytes")]
    [InlineData("ERR split: invalid option -- 'q'", "-q")]
    [InlineData("ERR split: invalid option -- 'z'", "-z")]  // FIX (was: classified "unsupported"; not a split option)
    [InlineData("ERR split: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver")]
    [InlineData("ERR split: option '-n' is recognized but not supported by ps-bash", "-n", "3")]
    [InlineData("ERR split: option '--number' is recognized but not supported by ps-bash", "--number=3")]
    [InlineData("ERR split: option '-C' is recognized but not supported by ps-bash", "-C", "6")]
    [InlineData("ERR split: option '-t' is recognized but not supported by ps-bash", "-t", ":")]
    [InlineData("ERR split: option '-e' is recognized but not supported by ps-bash", "-e")]
    [InlineData("ERR split: option '-x' is recognized but not supported by ps-bash", "-x")]
    [InlineData("ERR split: option '--hex-suffixes' is recognized but not supported by ps-bash", "--hex-suffixes")]
    [InlineData("ERR split: option '-u' is recognized but not supported by ps-bash", "-u")]
    [InlineData("ERR split: option '--filter' is recognized but not supported by ps-bash", "--filter=cat")]
    [InlineData("ERR split: option '--verbose' is recognized but not supported by ps-bash", "--verbose")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void UsageErrors_Exit1_UnsupportedExit2()
    {
        Assert.Equal(1, InvokeBashSplitCommand.ScanArgs(new[] { "-q" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashSplitCommand.ScanArgs(new[] { "-n", "3" }).ErrorExitCode);
    }
}
