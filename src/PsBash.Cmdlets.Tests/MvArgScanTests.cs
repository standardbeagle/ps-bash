using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for mv. Every row was run through the pre-migration hand scan
/// (a switch over exact tokens + classify-before-`--`) and against GNU mv 9.4 (`wsl bash`
/// for the FIX rows). Rows marked FIX are GNU-correct changes; unmarked rows are
/// byte-identical to the old behavior. The headline fix: the old scan never de-bundled, so
/// `mv -fv a b` (and -nv, -vv, ...) died with "invalid option -- 'f'". Others: unique-prefix
/// long options, ambiguity with GNU's candidate list (--ver is verbose/version), "doesn't
/// allow an argument", the offending letter in a bad bundle. Remaining policy divergences:
/// valid-but-unimplemented options (-i, -b, -t, -u, ...) are refused with exit 2 instead of
/// honored, and the scan-error exit status is 2 (GNU: 1).
/// </summary>
public class MvArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashMvCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("mv");
        static int B(bool b) => b ? 1 : 0;
        return $"n={B(p.Has("no-clobber"))} v={B(p.Has("verbose"))} ops=[{string.Join(",", p.Operands())}]";
    }

    [Theory]
    [InlineData("n=0 v=0 ops=[a,b]", "a", "b")]
    [InlineData("n=1 v=0 ops=[a,b]", "-n", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "-f", "a", "b")]
    [InlineData("n=0 v=1 ops=[a,b]", "-v", "a", "b")]
    [InlineData("n=0 v=1 ops=[a,b]", "-fv", "a", "b")]  // FIX (was: ERR mv: invalid option -- 'f')
    [InlineData("n=1 v=1 ops=[a,b]", "-nv", "a", "b")]  // FIX (was: ERR mv: invalid option -- 'n')
    [InlineData("n=1 v=1 ops=[a,b]", "-vn", "a", "b")]  // FIX (was: ERR mv: invalid option -- 'v')
    [InlineData("n=1 v=1 ops=[a,b]", "-fnv", "a", "b")]  // FIX (was: ERR mv: invalid option -- 'f')
    [InlineData("n=0 v=1 ops=[a,b]", "-vv", "a", "b")]  // FIX (was: ERR mv: invalid option -- 'v')
    [InlineData("n=0 v=0 ops=[a,b]", "-ff", "a", "b")]  // FIX (was: ERR mv: invalid option -- 'f')
    [InlineData("n=0 v=1 ops=[a,b]", "a", "b", "-v")]
    [InlineData("n=1 v=1 ops=[a,b]", "-n", "-f", "-v", "a", "b")]
    [InlineData("n=1 v=0 ops=[a,b]", "--no-clobber", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "--force", "a", "b")]
    [InlineData("n=0 v=1 ops=[a,b]", "--verbose", "a", "b")]
    [InlineData("ERR mv: option '--no-c' is ambiguous; possibilities: '--no-clobber' '--no-copy'", "--no-c", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--no-c')
    [InlineData("n=0 v=0 ops=[a,b]", "--forc", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--forc')
    [InlineData("n=0 v=1 ops=[a,b]", "--verb", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--verb')
    [InlineData("ERR mv: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--ver')
    [InlineData("ERR mv: option '--no' is ambiguous; possibilities: '--no-clobber' '--no-copy' '--no-target-directory'", "--no", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--no')
    [InlineData("ERR mv: option '--n' is ambiguous; possibilities: '--no-clobber' '--no-copy' '--no-target-directory'", "--n", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--n')
    [InlineData("n=0 v=0 ops=[a,b]", "--f", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--f')
    [InlineData("ERR mv: option '--s' is ambiguous; possibilities: '--strip-trailing-slashes' '--suffix'", "--s", "a", "b")]
    [InlineData("ERR mv: option '--v' is ambiguous; possibilities: '--verbose' '--version'", "--v", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--v')
    [InlineData("ERR mv: option '--verbose' doesn't allow an argument", "--verbose=1", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--verbose=1')
    [InlineData("ERR mv: option '--force' doesn't allow an argument", "--force=1", "a", "b")]  // FIX (was: ERR mv: unrecognized option '--force=1')
    [InlineData("n=0 v=0 ops=[-a,b]", "--", "-a", "b")]
    [InlineData("n=0 v=1 ops=[-n,-v]", "-v", "--", "-n", "-v")]
    [InlineData("n=0 v=0 ops=[--bogus,-i,-fv]", "--", "--bogus", "-i", "-fv")]
    [InlineData("n=0 v=0 ops=[-,b]", "--", "-", "b")]
    [InlineData("n=0 v=0 ops=[-,b]", "-", "b")]
    [InlineData("n=0 v=1 ops=[-,b]", "-v", "-", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "-i", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "-b", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "-S", "x", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "-u", "a", "b")]
    [InlineData("n=0 v=0 ops=[a]", "-t", "d", "a")]
    [InlineData("n=0 v=0 ops=[a,b]", "-T", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "--interactive", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "--backup=numbered", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "--suffix=.b", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "--update=older", "a", "b")]
    [InlineData("n=0 v=0 ops=[a]", "--target-directory", "d", "a")]
    [InlineData("n=0 v=0 ops=[a,b]", "--no-target-directory", "a", "b")]
    [InlineData("n=0 v=0 ops=[a,b]", "-fi", "a", "b")]
    [InlineData("n=0 v=1 ops=[a,b]", "-vb", "a", "b")]
    [InlineData("ERR mv: option requires an argument -- 'S'", "-S")]
    [InlineData("ERR mv: option '--no-target-directory' doesn't allow an argument", "--no-target-directory=x", "a")]
    [InlineData("ERR mv: option '--strip-trailing-slashes' is recognized but not supported by ps-bash", "--strip-trailing-slashes", "a", "b")]
    [InlineData("ERR mv: option '-Z' is recognized but not supported by ps-bash", "-Z", "a", "b")]
    [InlineData("ERR mv: option '--context' is recognized but not supported by ps-bash", "--context", "a", "b")]
    [InlineData("ERR mv: option '--no-copy' is recognized but not supported by ps-bash", "--no-copy", "a", "b")]
    [InlineData("ERR mv: option '--debug' is recognized but not supported by ps-bash", "--debug", "a", "b")]
    [InlineData("ERR mv: unrecognized option '--exchange'", "--exchange", "a", "b")]
    [InlineData("ERR mv: invalid option -- 'z'", "-fz", "a", "b")]  // FIX (was: ERR mv: invalid option -- 'f')
    [InlineData("ERR mv: invalid option -- 'z'", "-zf", "a", "b")]
    [InlineData("ERR mv: invalid option -- 'z'", "-z", "a", "b")]
    [InlineData("ERR mv: invalid option -- 'r'", "-r", "a", "b")]
    [InlineData("ERR mv: invalid option -- 'R'", "-R", "a", "b")]
    [InlineData("ERR mv: invalid option -- 'V'", "-V", "a", "b")]
    [InlineData("ERR mv: invalid option -- '5'", "-5", "a", "b")]
    [InlineData("ERR mv: unrecognized option '--bogus'", "--bogus", "a", "b")]
    [InlineData("ERR mv: unrecognized option '--bogus=1'", "--bogus=1", "a", "b")]
    [InlineData("ERR mv: invalid option -- '-'", "-f-", "a", "b")]  // FIX (was: ERR mv: invalid option -- 'f')
    [InlineData("ERR mv: unrecognized option '--='", "--=", "a")]
    [InlineData("n=0 v=0 ops=[]")]
    [InlineData("n=0 v=1 ops=[]", "-v")]
    [InlineData("n=0 v=0 ops=[a]", "a")]

    public void ScanArgs_MatchesGnuMv(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    [Fact]
    public void Force_IsAcceptedAsAnOption_AndHasNoEffectOnTheScanState()
    {
        var p = InvokeBashMvCommand.ScanArgs(new[] { "-f", "a", "b" });
        Assert.Null(p.Error);
        Assert.True(p.Has("force"));
    }
}
