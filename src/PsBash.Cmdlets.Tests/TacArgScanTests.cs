using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for tac. The pre-migration scan knew only the exact tokens `-s SEP` and
/// `--separator=SEP`: a dangling `-s` became a FILE operand, the joined `-sX` was a file named
/// "-sX", abbreviations (`--sep X`) were files, and `-b/-r` were classified by a second pass over
/// the operand list. Each FIX row was checked against GNU tac 9.4 (`wsl bash`): options -b -r -s +
/// --before --regex --separator=STRING (+ --help --version); unique long prefixes are accepted;
/// usage errors exit 1. Accepted here: -s/--separator. Refused loudly as valid-but-unsupported
/// (exit 2): -b/--before, -r/--regex.
/// </summary>
public class TacArgScanTests
{
    private static string Scan(string[] argv)
    {
        var t = InvokeBashTacCommand.Plan(argv);
        if (t.Parsed.Error is { } e) return "ERR " + e.Message("tac");
        return $"s={t.Separator ?? "-"} ops=[{string.Join(",", t.Operands)}]";
    }

    [Theory]
    [InlineData("s=- ops=[]")]
    [InlineData("s=- ops=[f]", "f")]
    [InlineData("s=- ops=[f,g]", "f", "g")]
    [InlineData("s=x ops=[]", "-s", "x")]
    [InlineData("s=x ops=[]", "-sx")]  // FIX (was: operands [-sx])
    [InlineData("s=x ops=[f]", "-s", "x", "f")]
    [InlineData("s=x ops=[f]", "f", "-s", "x")]  // FIX (options may follow operands)
    [InlineData("s=x ops=[]", "--separator=x")]
    [InlineData("s=x ops=[]", "--separator", "x")]  // FIX (was: operands [--separator,x])
    [InlineData("s=x ops=[]", "--sep=x")]  // FIX (was: operands)
    [InlineData("s=x ops=[]", "--se", "x")]  // FIX
    [InlineData("s=x ops=[]", "--s=x")]  // FIX
    [InlineData("s=b ops=[]", "-s", "a", "-s", "b")]  // last wins
    [InlineData("s= ops=[]", "-s", "")]  // empty separator (the cmdlet keeps its plain-reverse fallback)
    [InlineData("s=-b ops=[]", "-s", "-b")]  // getopt: a value option takes the next element even with a dash
    [InlineData("s=- ops=[-s,x]", "--", "-s", "x")]
    [InlineData("s=- ops=[-]", "-")]
    [InlineData("ERR tac: option requires an argument -- 's'", "-s")]  // FIX (was: -s became a file)
    [InlineData("ERR tac: option '--separator' requires an argument", "--separator")]  // FIX
    [InlineData("ERR tac: option requires an argument -- 's'", "f", "-s")]  // FIX
    [InlineData("ERR tac: option '-b' is recognized but not supported by ps-bash", "-b")]
    [InlineData("ERR tac: option '-r' is recognized but not supported by ps-bash", "-r")]
    [InlineData("ERR tac: option '-r' is recognized but not supported by ps-bash", "-rs", "x")]
    [InlineData("ERR tac: option '-b' is recognized but not supported by ps-bash", "-bs", "x")]
    [InlineData("ERR tac: option '--before' is recognized but not supported by ps-bash", "--before")]
    [InlineData("ERR tac: option '--before' is recognized but not supported by ps-bash", "--b")]  // FIX
    [InlineData("ERR tac: option '--regex' is recognized but not supported by ps-bash", "--regex")]
    [InlineData("ERR tac: option '--regex' is recognized but not supported by ps-bash", "--r")]  // FIX
    [InlineData("ERR tac: unrecognized option '--bogus'", "--bogus")]
    [InlineData("ERR tac: option '--before' is recognized but not supported by ps-bash", "--before=1")]  // unsupported wins over the value check
    [InlineData("ERR tac: invalid option -- 'x'", "-x")]
    [InlineData("ERR tac: invalid option -- 'x'", "-sx", "-x")]
    public void Plan_MatchesGnuTac(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    [Theory]
    [InlineData("--version", "version")]
    [InlineData("--vers", "version")]
    [InlineData("--help", "help")]
    [InlineData("--he", "help")]
    public void InfoOptions_AreResolvedThroughAbbreviation(string arg, string id)
    {
        var p = InvokeBashTacCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-x", 1)]
    [InlineData("-s", 1)]
    [InlineData("-b", 2)]
    [InlineData("--regex", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashTacCommand.ScanArgs(new[] { arg }).ErrorExitCode);
    }
}
