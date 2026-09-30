using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for nl (shared ordered parser + GNU value validation). The
/// pre-migration scan recognized `-b` only as the 3-char `-bX` or the split `-b X` (one letter),
/// silently ignored bad values (`nl -n xx` = right-aligned, `-w x` = width 6, `-bx` = default), had
/// no abbreviations and classified unsupported flags in a second operand pass (it even listed
/// `-bt`/`-bn` — which it implements — as unsupported). Each FIX row was checked against GNU nl 9.4
/// (`wsl bash`): options -b -d -f -h -i -l -n -p -s -v -w + long forms; unique long prefixes are
/// accepted; usage errors exit 1. Accepted here: -b a|t|n, -n ln|rn|rz, -s, -w (>=1), -v, -i.
/// Refused loudly as valid-but-unsupported (exit 2): -d -f -h -l -p (sections / blank-line
/// grouping), and regex body numbering `-bpREGEX`. Known gap: GNU words the `-w 0` refusal
/// "...: Numerical result out of range"; here it is the plain "invalid line number field width".
/// </summary>
public class NlArgScanTests
{
    private static string Scan(string[] argv)
    {
        var n = InvokeBashNlCommand.Plan(argv);
        if (n.Parsed.Error is { } e) return "ERR " + e.Message("nl");
        if (n.Error is { } m) return "ERR " + m;
        string b = n.NumberAll ? "a" : n.NumberNone ? "n" : "t";
        return $"b={b} n={n.Style} s={n.Sep.Replace("\t", "\\t")} w={n.Width} v={n.Start} i={n.Incr} ops=[{string.Join(",", n.Operands)}]";
    }

    private const string D = "b=t n=rn s=\\t w=6 v=1 i=1 ops=[]";

    [Theory]
    [InlineData(D)]
    [InlineData("b=a n=rn s=\\t w=6 v=1 i=1 ops=[]", "-ba")]
    [InlineData("b=a n=rn s=\\t w=6 v=1 i=1 ops=[]", "-b", "a")]
    [InlineData("b=n n=rn s=\\t w=6 v=1 i=1 ops=[]", "-bn")]
    [InlineData("b=t n=rn s=\\t w=6 v=1 i=1 ops=[]", "-bt")]
    [InlineData("b=a n=rn s=\\t w=6 v=1 i=1 ops=[]", "--body-numbering=a")]
    [InlineData("b=a n=rn s=\\t w=6 v=1 i=1 ops=[]", "--body", "a")]  // FIX (was: operands)
    [InlineData("b=a n=rn s=\\t w=6 v=1 i=1 ops=[]", "--b", "a")]  // FIX
    [InlineData("b=t n=rn s=\\t w=6 v=1 i=1 ops=[]", "-ba", "-bt")]  // last wins
    [InlineData("b=a n=rz s=\\t w=6 v=1 i=1 ops=[]", "-ba", "-nrz")]
    [InlineData("b=t n=rz s=\\t w=6 v=1 i=1 ops=[]", "-nrz")]
    [InlineData("b=t n=rz s=\\t w=6 v=1 i=1 ops=[]", "-n", "rz")]
    [InlineData("b=t n=ln s=\\t w=6 v=1 i=1 ops=[]", "-nln")]
    [InlineData("b=t n=rz s=\\t w=6 v=1 i=1 ops=[]", "--number-format=rz")]
    [InlineData("b=t n=rz s=\\t w=6 v=1 i=1 ops=[]", "--number-f", "rz")]  // FIX
    [InlineData("b=t n=rn s=: w=6 v=1 i=1 ops=[]", "-s:")]
    [InlineData("b=t n=rn s=: w=6 v=1 i=1 ops=[]", "-s", ":")]
    [InlineData("b=t n=rn s= w=6 v=1 i=1 ops=[]", "-s", "")]
    [InlineData("b=t n=rn s=: w=6 v=1 i=1 ops=[]", "--number-separator=:")]
    [InlineData("b=t n=rn s=- w=6 v=1 i=1 ops=[]", "-s", "-")]  // getopt: value option takes the next element
    [InlineData("b=t n=rn s=\\t w=3 v=1 i=1 ops=[]", "-w3")]
    [InlineData("b=t n=rn s=\\t w=3 v=1 i=1 ops=[]", "-w", "3")]
    [InlineData("b=t n=rn s=\\t w=3 v=1 i=1 ops=[]", "--number-width=3")]
    [InlineData("b=t n=rn s=\\t w=6 v=5 i=1 ops=[]", "-v5")]
    [InlineData("b=t n=rn s=\\t w=6 v=-5 i=1 ops=[]", "-v-5")]
    [InlineData("b=t n=rn s=\\t w=6 v=-5 i=1 ops=[]", "-v", "-5")]
    [InlineData("b=t n=rn s=\\t w=6 v=7 i=1 ops=[]", "--starting-line-number=7")]
    [InlineData("b=t n=rn s=\\t w=6 v=1 i=2 ops=[]", "-i2")]
    [InlineData("b=t n=rn s=\\t w=6 v=1 i=0 ops=[]", "-i0")]
    [InlineData("b=t n=rn s=\\t w=6 v=1 i=4 ops=[]", "--line-increment", "4")]
    [InlineData("b=a n=rz s=: w=3 v=5 i=2 ops=[f]", "-ba", "-nrz", "-s:", "-w3", "-v5", "-i2", "f")]
    [InlineData("b=t n=rn s=\\t w=6 v=1 i=1 ops=[f]", "f")]
    [InlineData("b=a n=rn s=\\t w=6 v=1 i=1 ops=[f]", "f", "-ba")]  // FIX (options may follow operands)
    [InlineData("b=t n=rn s=\\t w=6 v=1 i=1 ops=[-ba]", "--", "-ba")]
    [InlineData("b=t n=rn s=\\t w=6 v=1 i=1 ops=[-]", "-")]
    [InlineData("ERR nl: invalid body numbering style: 'x'", "-bx")]  // FIX (was: silently the default)
    [InlineData("ERR nl: invalid body numbering style: 'ab'", "-b", "ab")]  // FIX
    [InlineData("ERR nl: invalid body numbering style: ''", "-b", "")]  // FIX
    [InlineData("ERR nl: body numbering style 'pfoo' (regex numbering) is recognized but not supported by ps-bash", "-bpfoo")]
    [InlineData("ERR nl: invalid line numbering format: 'xx'", "-n", "xx")]  // FIX (was: silently rn)
    [InlineData("ERR nl: invalid line numbering format: 'r'", "-nr")]  // FIX
    [InlineData("ERR nl: invalid line number field width: 'x'", "-w", "x")]  // FIX (was: width 6)
    [InlineData("ERR nl: invalid line number field width: '0'", "-w0")]  // GNU: same, plus ": Numerical result out of range"
    [InlineData("ERR nl: invalid line number field width: '-1'", "-w", "-1")]
    [InlineData("ERR nl: invalid starting line number: 'x'", "-v", "x")]  // FIX
    [InlineData("ERR nl: invalid line number increment: '1.5'", "-i", "1.5")]  // FIX
    [InlineData("ERR nl: option requires an argument -- 'n'", "-n")]  // FIX (was: -n became a file)
    [InlineData("ERR nl: option requires an argument -- 'w'", "-w")]
    [InlineData("ERR nl: option requires an argument -- 'b'", "-b")]
    [InlineData("ERR nl: option '--number-width' requires an argument", "--number-width")]
    [InlineData("ERR nl: option '-h' is recognized but not supported by ps-bash", "-h", "a")]
    [InlineData("ERR nl: option '-f' is recognized but not supported by ps-bash", "-fa")]
    [InlineData("ERR nl: option '-d' is recognized but not supported by ps-bash", "-d:")]
    [InlineData("ERR nl: option '-l' is recognized but not supported by ps-bash", "-l2")]
    [InlineData("ERR nl: option '-p' is recognized but not supported by ps-bash", "-p")]
    [InlineData("ERR nl: invalid body numbering style: 'ap'", "-bap")]  // -b takes the rest of the bundle as its value
    [InlineData("ERR nl: option '--header-numbering' is recognized but not supported by ps-bash", "--header-numbering=a")]
    [InlineData("ERR nl: option '--footer-numbering' is recognized but not supported by ps-bash", "--footer", "a")]  // FIX
    [InlineData("ERR nl: option '--no-renumber' is recognized but not supported by ps-bash", "--no-renumber")]
    [InlineData("ERR nl: option '--no-renumber' is recognized but not supported by ps-bash", "--no")]  // FIX
    [InlineData("ERR nl: option '--join-blank-lines' is recognized but not supported by ps-bash", "--join", "2")]  // FIX
    [InlineData("ERR nl: option '--he' is ambiguous; possibilities: '--header-numbering' '--help'", "--he")]  // GNU: same set
    [InlineData("ERR nl: option '--n' is ambiguous; possibilities: '--no-renumber' '--number-format' '--number-separator' '--number-width'", "--n")]
    [InlineData("ERR nl: option '--s' is ambiguous; possibilities: '--section-delimiter' '--starting-line-number'", "--s")]
    [InlineData("ERR nl: unrecognized option '--w=4'", "--w=4")]
    [InlineData("ERR nl: unrecognized option '--bogus'", "--bogus")]
    [InlineData("ERR nl: invalid option -- 'x'", "-x")]
    [InlineData("ERR nl: invalid option -- 'z'", "-zx")]
    public void Plan_MatchesGnuNl(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    [Theory]
    [InlineData("--version", "version")]
    [InlineData("--vers", "version")]
    [InlineData("--v", "version")]
    [InlineData("--help", "help")]
    public void InfoOptions_AreResolvedThroughAbbreviation(string arg, string id)
    {
        var p = InvokeBashNlCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-x", 1)]
    [InlineData("-n", 1)]
    [InlineData("--n", 1)]
    [InlineData("-h", 2)]
    [InlineData("-p", 2)]
    [InlineData("--no-renumber", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashNlCommand.ScanArgs(new[] { arg }).ErrorExitCode);
    }

    [Fact]
    public void ValueErrors_ExitStatus()
    {
        Assert.Equal(1, InvokeBashNlCommand.Plan(new[] { "-n", "xx" }).ErrorExit);
        Assert.Equal(2, InvokeBashNlCommand.Plan(new[] { "-bpfoo" }).ErrorExit);
    }
}
