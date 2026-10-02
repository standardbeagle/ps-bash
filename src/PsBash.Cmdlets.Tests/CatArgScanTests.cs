using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for cat. The pre-migration scan translated a hard-coded list of exact long
/// spellings to short flags (so `--squeeze` was a file operand), recovered bundles with a
/// patch-up, needed decoys for -E/-A/-v, and pushed every unknown flag through a second operand
/// classifier. Each FIX row was checked against GNU cat 9.4 (`wsl bash`): options -A -b -e -E -n -s
/// -t -T -u -v + --show-all --number-nonblank --show-ends --number --squeeze-blank --show-tabs
/// --show-nonprinting (+ --help --version); unique long prefixes are accepted; usage errors exit 1;
/// `-u` is "(ignored)" and is now accepted as a no-op (was refused). Refused loudly as
/// valid-but-unsupported (exit 2): -A -e -t -v --show-all --show-nonprinting — all need the `-v`
/// caret/M- notation. Known cosmetic gap: for an AMBIGUOUS prefix GNU lists the candidates in its
/// own table order, this parser lists them alphabetically.
/// </summary>
public class CatArgScanTests
{
    private static string Scan(string[] argv)
    {
        var c = InvokeBashCatCommand.Plan(argv);
        if (c.Parsed.Error is { } e) return "ERR " + e.Message("cat");
        static string F(bool b, char ch) => b ? ch.ToString() : "-";
        return $"{F(c.NumberAll, 'n')}{F(c.NumberNonBlank, 'b')}{F(c.Squeeze, 's')}{F(c.ShowEnds, 'E')}{F(c.ShowTabs, 'T')} " +
               $"ops=[{string.Join(",", c.Operands)}]";
    }

    [Theory]
    [InlineData("----- ops=[]")]
    [InlineData("n---- ops=[]", "-n")]
    [InlineData("-b--- ops=[]", "-b")]
    [InlineData("--s-- ops=[]", "-s")]
    [InlineData("---E- ops=[]", "-E")]
    [InlineData("----T ops=[]", "-T")]
    [InlineData("nb--- ops=[]", "-bn")]  // both scan; -b wins at emit time (GNU: -b overrides -n)
    [InlineData("n--E- ops=[]", "-nE")]
    [InlineData("-b-ET ops=[]", "-bET")]
    [InlineData("n-sET ops=[]", "-nsET")]
    [InlineData("n-sET ops=[f]", "-nsET", "f")]
    [InlineData("---E- ops=[]", "-uE")]  // FIX: -u is "(ignored)" in GNU (was: refused)
    [InlineData("----- ops=[]", "-u")]  // FIX
    [InlineData("n---- ops=[]", "--number")]
    [InlineData("-b--- ops=[]", "--number-nonblank")]
    [InlineData("--s-- ops=[]", "--squeeze-blank")]
    [InlineData("---E- ops=[]", "--show-ends")]
    [InlineData("----T ops=[]", "--show-tabs")]
    [InlineData("--s-- ops=[]", "--sq")]  // FIX (was: operands [--sq])
    [InlineData("--s-- ops=[]", "--squeeze")]  // FIX
    [InlineData("---E- ops=[]", "--show-e")]  // FIX
    [InlineData("----T ops=[]", "--show-t")]  // FIX
    [InlineData("-b--- ops=[]", "--number-n")]  // FIX
    [InlineData("n---- ops=[f]", "-n", "f")]
    [InlineData("n---- ops=[f]", "f", "-n")]  // FIX (options may follow operands)
    [InlineData("n-s-- ops=[a,b]", "a", "-n", "b", "--squeeze-blank")]  // FIX
    [InlineData("----- ops=[-]", "-")]
    [InlineData("n---- ops=[-,f]", "-n", "-", "f")]
    [InlineData("----- ops=[-n]", "--", "-n")]  // GNU: `cat -- -n` -> "cat: -n: No such file"
    [InlineData("n---- ops=[-E]", "-n", "--", "-E")]
    [InlineData("n--ET ops=[]", "-nA")]  // implemented: -A = -vET
    [InlineData("---E- ops=[]", "-e")]  // -e = -vE
    [InlineData("----T ops=[]", "-t")]  // -t = -vT
    [InlineData("----- ops=[]", "-v")]
    [InlineData("---ET ops=[]", "--show-all")]
    [InlineData("----- ops=[]", "--show-nonprinting")]
    [InlineData("---ET ops=[]", "--show-a")]
    [InlineData("ERR cat: option '--num' is ambiguous; possibilities: '--number-nonblank' '--number'", "--num")]  // GNU long_options[] order
    [InlineData("ERR cat: option '--n' is ambiguous; possibilities: '--number-nonblank' '--number'", "--n")]
    [InlineData("ERR cat: option '--show' is ambiguous; possibilities: '--show-nonprinting' '--show-ends' '--show-tabs' '--show-all'", "--show")]
    [InlineData("ERR cat: option '--s' is ambiguous; possibilities: '--squeeze-blank' '--show-nonprinting' '--show-ends' '--show-tabs' '--show-all'", "--s")]
    [InlineData("ERR cat: option '--number' doesn't allow an argument", "--number=1")]  // FIX
    [InlineData("ERR cat: unrecognized option '--bogus'", "--bogus")]
    [InlineData("ERR cat: invalid option -- 'x'", "-x")]
    [InlineData("ERR cat: invalid option -- 'x'", "-nx")]
    [InlineData("ERR cat: invalid option -- '5'", "-5")]
    public void Plan_MatchesGnuCat(string expected, params string[] argv)
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
        var p = InvokeBashCatCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-x", 1)]
    [InlineData("--n", 1)]
    [InlineData("--version=1", 1)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashCatCommand.ScanArgs(new[] { arg }).ErrorExitCode);
    }
}
