using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for tree (shared ordered parser). `tree` is the Steve Baker package,
/// not coreutils, and is not installed in the WSL oracle, so the table follows tree(1) 2.1 and its
/// hand-rolled option loop (tree.c): NOT getopt_long (long options are exact names, no
/// abbreviation), short bundles, `-L`/`-I` take the joined or the next argument, `-I` repeats and
/// splits on `|`, `-L` below 1 or non-numeric is "Invalid level, must be greater than 0." (exit 1),
/// an unknown option is a usage error (exit 1). ps-bash refuses the rest of the documented option
/// set (colour, sizes, sorts, HTML/JSON/XML output, ...) with exit 2.
/// </summary>
public class TreeArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashTreeCommand.Plan(argv);
        if (p.Parsed.Error is { } e) return "ERR " + e.Message("tree");
        if (p.Error is { } m) return "ERR " + m;
        string depth = p.MaxDepth == int.MaxValue ? "inf" : p.MaxDepth.ToString();
        return $"a={p.ShowAll} d={p.DirsOnly} f={p.FullPath} df={p.DirsFirst} nr={p.NoReport} L={depth} I=[{string.Join(",", p.Excludes)}] ops=[{string.Join(",", p.Operands)}]";
    }

    [Theory]
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[] ops=[]")]
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[] ops=[dir]", "dir")]
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[] ops=[a,b]", "a", "b")]  // FIX (was: only the first)
    [InlineData("a=True d=False f=False df=False nr=False L=inf I=[] ops=[dir]", "-a", "dir")]
    [InlineData("a=True d=True f=False df=False nr=False L=inf I=[] ops=[dir]", "-ad", "dir")]
    [InlineData("a=True d=True f=True df=False nr=False L=inf I=[] ops=[]", "-adf")]
    [InlineData("a=False d=False f=False df=True nr=False L=inf I=[] ops=[dir]", "--dirsfirst", "dir")]
    [InlineData("a=False d=False f=False df=False nr=True L=inf I=[] ops=[dir]", "--noreport", "dir")]
    [InlineData("a=False d=False f=False df=False nr=False L=2 I=[] ops=[dir]", "-L", "2", "dir")]
    [InlineData("a=False d=False f=False df=False nr=False L=2 I=[] ops=[dir]", "-L2", "dir")]
    [InlineData("a=True d=False f=False df=False nr=False L=2 I=[] ops=[dir]", "-aL2", "dir")]
    [InlineData("a=False d=False f=False df=False nr=False L=3 I=[] ops=[dir]", "-L", "2", "-L", "3", "dir")]  // last wins
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[*.log] ops=[dir]", "-I", "*.log", "dir")]
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[*.log] ops=[dir]", "-I*.log", "dir")]  // FIX (joined)
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[*.log,*.tmp] ops=[dir]", "-I", "*.log|*.tmp", "dir")]  // FIX (| alternatives)
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[a,b] ops=[dir]", "-I", "a", "-I", "b", "dir")]  // FIX (repeatable)
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[] ops=[dir]", "-n", "dir")]  // colour is never on
    [InlineData("a=False d=False f=False df=False nr=False L=inf I=[] ops=[dir,-a]", "dir", "--", "-a")]  // FIX (-- ends options)
    [InlineData("a=True d=False f=False df=False nr=False L=inf I=[] ops=[dir]", "dir", "-a")]  // FIX (options after operands)
    // usage errors (exit 1)
    [InlineData("ERR tree: invalid option -- 'z'", "-z", "dir")]  // FIX (was: a directory operand)
    [InlineData("ERR tree: unrecognized option '--nope'", "--nope", "dir")]
    [InlineData("ERR tree: unrecognized option '--dirs'", "--dirs", "dir")]  // tree does not abbreviate
    [InlineData("ERR tree: option requires an argument -- 'L'", "-L")]
    [InlineData("ERR tree: option requires an argument -- 'I'", "-I")]
    [InlineData("ERR tree: Invalid level, must be greater than 0.", "-L", "0", "dir")]
    [InlineData("ERR tree: Invalid level, must be greater than 0.", "-L", "abc", "dir")]  // FIX (was: ignored)
    [InlineData("ERR tree: Invalid level, must be greater than 0.", "-L", "-1", "dir")]
    [InlineData("ERR tree: option '--noreport' doesn't allow an argument", "--noreport=1", "dir")]
    // documented tree options ps-bash refuses (exit 2)
    [InlineData("ERR tree: option '-C' is recognized but not supported by ps-bash", "-C", "dir")]
    [InlineData("ERR tree: option '-p' is recognized but not supported by ps-bash", "-p", "dir")]
    [InlineData("ERR tree: option '-s' is recognized but not supported by ps-bash", "-s", "dir")]
    [InlineData("ERR tree: option '-h' is recognized but not supported by ps-bash", "-h", "dir")]
    [InlineData("ERR tree: option '-P' is recognized but not supported by ps-bash", "-P", "*.c", "dir")]
    [InlineData("ERR tree: option '-J' is recognized but not supported by ps-bash", "-J", "dir")]
    [InlineData("ERR tree: option '--gitignore' is recognized but not supported by ps-bash", "--gitignore", "dir")]
    [InlineData("ERR tree: option '--prune' is recognized but not supported by ps-bash", "--prune", "dir")]
    [InlineData("ERR tree: option '--filesfirst' is recognized but not supported by ps-bash", "--filesfirst", "dir")]
    public void Resolves(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Fact]
    public void ExitStatus_UsageIs1_UnsupportedIs2()
    {
        Assert.Equal(1, InvokeBashTreeCommand.ScanArgs(new[] { "-z" }).ErrorExitCode);
        Assert.Equal(2, InvokeBashTreeCommand.ScanArgs(new[] { "-C" }).ErrorExitCode);
        Assert.True(InvokeBashTreeCommand.ScanArgs(new[] { "--version" }).Has("version"));
    }
}
