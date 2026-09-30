using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for rmdir. Every row was run through the pre-migration hand scan (exact
/// -p/-v/--parents/--verbose, a pure p/v-only bundle rule, classify-before-`--`) and, for the FIX
/// rows, against GNU rmdir 9.4 (`wsl bash`). FIX rows: unique-prefix long options (`--par`),
/// ambiguity with GNU's candidate list (`--ver`), "doesn't allow an argument", the OFFENDING letter
/// in a bad bundle (`-pz` names 'z', was 'p'), an abbreviation of the refused option (`--ign`)
/// reported as unsupported. `-Z` / `--context` were WRONGLY listed as valid-but-unsupported: GNU
/// coreutils 9.4 rmdir has no such option (`rmdir -Z` is "invalid option -- 'Z'"), so they are now
/// plain usage errors. Remaining divergence: `--ignore-fail-on-non-empty` is refused with exit 2
/// instead of honored; the scan-error exit status is GNU's 1 (see OptSpecSet).
/// </summary>
public class RmdirArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashRmdirCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("rmdir");
        static int B(bool b) => b ? 1 : 0;
        return $"p={B(p.Has("parents"))} v={B(p.Has("verbose"))} ops=[{string.Join(",", p.Operands())}]";
    }

    [Theory]
    [InlineData("p=0 v=0 ops=[d]", "d")]
    [InlineData("p=1 v=0 ops=[d]", "-p", "d")]
    [InlineData("p=0 v=1 ops=[d]", "-v", "d")]
    [InlineData("p=1 v=1 ops=[d]", "-pv", "d")]
    [InlineData("p=1 v=1 ops=[d]", "-vp", "d")]
    [InlineData("p=1 v=0 ops=[d]", "-pp", "d")]
    [InlineData("p=0 v=1 ops=[d]", "-vv", "d")]
    [InlineData("p=1 v=1 ops=[d]", "-p", "-v", "d")]
    [InlineData("p=1 v=0 ops=[d]", "d", "-p")]
    [InlineData("p=1 v=0 ops=[a,b]", "a", "-p", "b")]
    [InlineData("p=1 v=0 ops=[d]", "--parents", "d")]
    [InlineData("p=0 v=1 ops=[d]", "--verbose", "d")]
    [InlineData("p=1 v=0 ops=[d]", "--par", "d")]  // FIX (was: ERR rmdir: unrecognized option '--par')
    [InlineData("p=1 v=0 ops=[d]", "--p", "d")]  // FIX (was: ERR rmdir: unrecognized option '--p')
    [InlineData("p=0 v=1 ops=[d]", "--verb", "d")]  // FIX (was: ERR rmdir: unrecognized option '--verb')
    [InlineData("ERR rmdir: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver", "d")]  // FIX (was: ERR rmdir: unrecognized option '--ver')
    [InlineData("ERR rmdir: option '--v' is ambiguous; possibilities: '--verbose' '--version'", "--v", "d")]  // FIX (was: ERR rmdir: unrecognized option '--v')
    [InlineData("ERR rmdir: option '--parents' doesn't allow an argument", "--parents=1", "d")]  // FIX (was: ERR rmdir: unrecognized option '--parents=1')
    [InlineData("ERR rmdir: option '--verbose' doesn't allow an argument", "--verbose=1", "d")]  // FIX (was: ERR rmdir: unrecognized option '--verbose=1')
    [InlineData("p=0 v=0 ops=[-foo]", "--", "-foo")]
    [InlineData("p=1 v=0 ops=[-p,-v]", "-p", "--", "-p", "-v")]
    [InlineData("p=0 v=0 ops=[--bogus,-Z]", "--", "--bogus", "-Z")]
    [InlineData("p=0 v=0 ops=[-]", "-")]
    [InlineData("p=0 v=0 ops=[]")]
    [InlineData("p=1 v=1 ops=[]", "-pv")]
    [InlineData("ERR rmdir: option '--ignore-fail-on-non-empty' is recognized but not supported by ps-bash", "--ignore-fail-on-non-empty", "d")]
    [InlineData("ERR rmdir: option '--ignore-fail-on-non-empty' is recognized but not supported by ps-bash", "--ign", "d")]  // FIX (was: ERR rmdir: unrecognized option '--ign')
    [InlineData("ERR rmdir: option '--ignore-fail-on-non-empty' is recognized but not supported by ps-bash", "--i", "d")]  // FIX (was: ERR rmdir: unrecognized option '--i')
    [InlineData("ERR rmdir: option '--ignore-fail-on-non-empty' is recognized but not supported by ps-bash", "--ignore-fail-on-non-empty=1", "d")]  // GNU: "doesn't allow an argument" (both are errors)
    [InlineData("ERR rmdir: invalid option -- 'Z'", "-Z", "d")]  // FIX (was: ERR rmdir: option '-Z' is recognized but not supported by ps-bash) — GNU rmdir has no -Z
    [InlineData("ERR rmdir: unrecognized option '--context'", "--context", "d")]  // FIX (was: unsupported) — GNU rmdir has no --context
    [InlineData("ERR rmdir: unrecognized option '--c'", "--c", "d")]  // FIX (was: unrecognized) — same wording, now via the parser
    [InlineData("ERR rmdir: invalid option -- 'z'", "-z", "d")]
    [InlineData("ERR rmdir: invalid option -- 'z'", "-pz", "d")]  // FIX (was: ERR rmdir: invalid option -- 'p')
    [InlineData("ERR rmdir: invalid option -- 'z'", "-zp", "d")]
    [InlineData("ERR rmdir: invalid option -- '-'", "-p-", "d")]  // FIX (was: ERR rmdir: invalid option -- 'p')
    [InlineData("ERR rmdir: invalid option -- '5'", "-5", "d")]
    [InlineData("ERR rmdir: unrecognized option '--bogus'", "--bogus", "d")]
    [InlineData("ERR rmdir: unrecognized option '--bogus=1'", "--bogus=1", "d")]
    [InlineData("ERR rmdir: unrecognized option '--='", "--=", "d")]
    public void ScanArgs_MatchesGnuRmdir(string expected, params string[] argv)
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
        var p = InvokeBashRmdirCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-Z", 1)]
    [InlineData("--parents=1", 1)]
    [InlineData("--v", 1)]
    [InlineData("--ignore-fail-on-non-empty", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashRmdirCommand.ScanArgs(new[] { arg, "d" }).ErrorExitCode);
    }
}
