using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for ln. The pre-migration scan had NO classifier: exact -s/-f/-v plus an
/// s/f/v-only bundle rule, and EVERYTHING else became an operand. So `ln -T a b`, `ln -sfn a b`,
/// `ln --bogus a b` silently made the flag the link TARGET and created a wrongly named link at
/// exit 0 (the "(was: operand …)" rows). Each FIX row was checked against GNU ln 9.4 (`wsl bash`):
/// GNU ln's options are -b -d -F -f -i -L -n -P -r -s -S -t -T -v (+ long forms, --help,
/// --version) and it has NO -Z/--context. Accepted here: -s -f -v and -n/--no-dereference (the
/// cmdlet already treats an existing symlink-to-directory LINK_NAME as a plain name, which is
/// exactly what -n requests). Refused loudly as valid-but-unsupported (exit 2): the rest.
/// Long abbreviation includes refused names (`--s` is ambiguous suffix/symbolic; `--n` is
/// no-dereference/no-target-directory). Scan-error exit status is GNU's 1 (see OptSpecSet).
/// </summary>
public class LnArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashLnCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("ln");
        static int B(bool b) => b ? 1 : 0;
        return $"s={B(p.Has("symbolic"))} f={B(p.Has("force"))} v={B(p.Has("verbose"))} n={B(p.Has("no-dereference"))} ops=[{string.Join(",", p.Operands())}]";
    }

    [Theory]
    [InlineData("s=0 f=0 v=0 n=0 ops=[a,b]", "a", "b")]
    [InlineData("s=1 f=0 v=0 n=0 ops=[a,b]", "-s", "a", "b")]
    [InlineData("s=0 f=1 v=0 n=0 ops=[a,b]", "-f", "a", "b")]
    [InlineData("s=0 f=0 v=1 n=0 ops=[a,b]", "-v", "a", "b")]
    [InlineData("s=1 f=1 v=0 n=0 ops=[a,b]", "-sf", "a", "b")]
    [InlineData("s=1 f=1 v=1 n=0 ops=[a,b]", "-svf", "a", "b")]
    [InlineData("s=1 f=1 v=0 n=1 ops=[a,b]", "-sfn", "a", "b")]  // FIX (was: operands [-sfn,a,b] — flag became the link target)
    [InlineData("s=1 f=1 v=0 n=1 ops=[a,b]", "-sf", "-n", "a", "b")]  // FIX (was: operands [-n,a,b])
    [InlineData("s=0 f=0 v=0 n=1 ops=[a,b]", "-n", "a", "b")]  // FIX (was: operands [-n,a,b])
    [InlineData("s=0 f=0 v=0 n=1 ops=[a,b]", "--no-dereference", "a", "b")]  // FIX (was: operands)
    [InlineData("s=0 f=0 v=0 n=1 ops=[a,b]", "--no-d", "a", "b")]  // FIX (was: operands)
    [InlineData("s=1 f=0 v=0 n=0 ops=[a,b]", "-ss", "a", "b")]  // FIX (was: operands [-ss,a,b])
    [InlineData("s=0 f=1 v=0 n=0 ops=[a,b]", "-ff", "a", "b")]  // FIX (was: operands [-ff,a,b])
    [InlineData("s=0 f=0 v=1 n=0 ops=[a,b]", "-vv", "a", "b")]  // FIX (was: operands [-vv,a,b])
    [InlineData("s=1 f=0 v=0 n=0 ops=[a,b]", "a", "b", "-s")]  // FIX (was: operands [a,b,-s])
    [InlineData("s=1 f=0 v=0 n=0 ops=[a,b]", "a", "-s", "b")]  // FIX (was: operands [a,-s,b])
    [InlineData("s=1 f=0 v=0 n=0 ops=[a,b]", "--symbolic", "a", "b")]  // FIX (was: operands [--symbolic,a,b])
    [InlineData("s=0 f=1 v=0 n=0 ops=[a,b]", "--force", "a", "b")]  // FIX (was: operands)
    [InlineData("s=0 f=0 v=1 n=0 ops=[a,b]", "--verbose", "a", "b")]  // FIX (was: operands)
    [InlineData("s=1 f=0 v=0 n=0 ops=[a,b]", "--sym", "a", "b")]  // FIX (was: operands)
    [InlineData("s=0 f=1 v=0 n=0 ops=[a,b]", "--f", "a", "b")]  // FIX (was: operands)
    [InlineData("s=0 f=0 v=1 n=0 ops=[a,b]", "--verb", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--s' is ambiguous; possibilities: '--suffix' '--symbolic'", "--s", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--n' is ambiguous; possibilities: '--no-dereference' '--no-target-directory'", "--n", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--no' is ambiguous; possibilities: '--no-dereference' '--no-target-directory'", "--no", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--v' is ambiguous; possibilities: '--verbose' '--version'", "--v", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--symbolic' doesn't allow an argument", "--symbolic=1", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--force' doesn't allow an argument", "--force=1", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--verbose' doesn't allow an argument", "--verbose=1", "a", "b")]  // FIX (was: operands)
    [InlineData("s=0 f=0 v=0 n=0 ops=[-a,b]", "--", "-a", "b")]
    [InlineData("s=1 f=0 v=0 n=0 ops=[-s,-f]", "-s", "--", "-s", "-f")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[--bogus,-T,-n]", "--", "--bogus", "-T", "-n")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[-,b]", "-", "b")]
    [InlineData("s=1 f=0 v=0 n=0 ops=[-,b]", "-s", "-", "b")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[]")]
    [InlineData("s=1 f=0 v=0 n=0 ops=[a]", "-s", "a")]
    [InlineData("ERR ln: option '-b' is recognized but not supported by ps-bash", "-b", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--backup' is recognized but not supported by ps-bash", "--backup", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--backup' is recognized but not supported by ps-bash", "--backup=numbered", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--backup' is recognized but not supported by ps-bash", "--b", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '-d' is recognized but not supported by ps-bash", "-d", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '-F' is recognized but not supported by ps-bash", "-F", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--directory' is recognized but not supported by ps-bash", "--directory", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--directory' is recognized but not supported by ps-bash", "--d", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '-i' is recognized but not supported by ps-bash", "-i", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--interactive' is recognized but not supported by ps-bash", "--interactive", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--interactive' is recognized but not supported by ps-bash", "--i", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '-L' is recognized but not supported by ps-bash", "-L", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--logical' is recognized but not supported by ps-bash", "--logical", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--logical' is recognized but not supported by ps-bash", "--l", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '-P' is recognized but not supported by ps-bash", "-P", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--physical' is recognized but not supported by ps-bash", "--physical", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--physical' is recognized but not supported by ps-bash", "--ph", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '-r' is recognized but not supported by ps-bash", "-r", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--relative' is recognized but not supported by ps-bash", "--relative", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--relative' is recognized but not supported by ps-bash", "--rel", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '-S' is recognized but not supported by ps-bash", "-S", ".bak", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--suffix' is recognized but not supported by ps-bash", "--suffix=.bak", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: option '--suffix' is recognized but not supported by ps-bash", "--suf", "a", "b")]  // FIX (was: operands)
    // -t DIR / -T are IMPLEMENTED now (operand forms 3/4): the scan accepts them; -t consumes DIR.
    [InlineData("s=0 f=0 v=0 n=0 ops=[a]", "-t", "dir", "a")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[a]", "-tdir", "a")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[a]", "--target-directory=dir", "a")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[a]", "--t", "dir", "a")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[a,b]", "-T", "a", "b")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[a,b]", "--no-target-directory", "a", "b")]
    [InlineData("s=0 f=0 v=0 n=0 ops=[a,b]", "--no-t", "a", "b")]
    [InlineData("ERR ln: option '-r' is recognized but not supported by ps-bash", "-sr", "a", "b")]  // FIX (was: operands [-sr,a,b])
    [InlineData("s=1 f=1 v=0 n=0 ops=[a,b]", "-sfT", "a", "b")]
    [InlineData("ERR ln: invalid option -- 'Z'", "-Z", "a", "b")]  // FIX (was: operands) — GNU ln 9.4 has no -Z
    [InlineData("ERR ln: unrecognized option '--context'", "--context", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: invalid option -- 'z'", "-z", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: invalid option -- 'z'", "-sz", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: invalid option -- 'z'", "-zs", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: invalid option -- '-'", "-s-", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: invalid option -- '5'", "-5", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: unrecognized option '--bogus'", "--bogus", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: unrecognized option '--bogus=1'", "--bogus=1", "a", "b")]  // FIX (was: operands)
    [InlineData("ERR ln: unrecognized option '--L'", "--L", "a", "b")]  // GNU: long names are case sensitive
    [InlineData("ERR ln: unrecognized option '--T'", "--T", "a", "b")]
    [InlineData("ERR ln: unrecognized option '--P'", "--P", "a", "b")]
    [InlineData("ERR ln: unrecognized option '--='", "--=", "a")]
    public void ScanArgs_MatchesGnuLn(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    [Theory]
    [InlineData("--version", "version")]
    [InlineData("--vers", "version")]
    [InlineData("--help", "help")]
    [InlineData("--he", "help")]
    [InlineData("--h", "help")]
    public void InfoOptions_AreResolvedThroughAbbreviation(string arg, string id)
    {
        var p = InvokeBashLnCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-z", 1)]
    [InlineData("--symbolic=1", 1)]
    [InlineData("--s", 1)]
    [InlineData("-r", 2)]
    [InlineData("--backup", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashLnCommand.ScanArgs(new[] { arg, "a", "b" }).ErrorExitCode);
    }
}
