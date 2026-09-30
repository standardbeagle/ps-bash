using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for mkdir. Every row was run through the pre-migration hand scan (exact
/// -p/-v/--parents/--verbose, a pure p/v-only bundle rule, classify-before-`--`) and, for the FIX
/// rows, against GNU mkdir 9.4 (`wsl bash`). FIX rows are GNU-correct changes: unique-prefix long
/// options (`--par`), ambiguity with GNU's candidate list (`--ver` = verbose/version), "doesn't
/// allow an argument", the OFFENDING letter in a bad bundle (`-pz` names 'z'; the old scan named
/// 'p'), abbreviations of refused options (`--mo`, `--con`) reported as unsupported. Remaining
/// divergences: valid-but-unimplemented `-m/--mode`, `-Z/--context` are refused with exit 2
/// instead of honored; the scan-error exit status is GNU's 1 (see OptSpecSet).
/// </summary>
public class MkdirArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashMkdirCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("mkdir");
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
    [InlineData("p=1 v=0 ops=[d]", "--par", "d")]  // FIX (was: ERR mkdir: unrecognized option '--par')
    [InlineData("p=1 v=0 ops=[d]", "--p", "d")]  // FIX (was: ERR mkdir: unrecognized option '--p')
    [InlineData("p=0 v=1 ops=[d]", "--verb", "d")]  // FIX (was: ERR mkdir: unrecognized option '--verb')
    [InlineData("ERR mkdir: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver", "d")]  // FIX (was: ERR mkdir: unrecognized option '--ver')
    [InlineData("ERR mkdir: option '--v' is ambiguous; possibilities: '--verbose' '--version'", "--v", "d")]  // FIX (was: ERR mkdir: unrecognized option '--v')
    [InlineData("ERR mkdir: option '--parents' doesn't allow an argument", "--parents=1", "d")]  // FIX (was: ERR mkdir: unrecognized option '--parents=1')
    [InlineData("ERR mkdir: option '--verbose' doesn't allow an argument", "--verbose=1", "d")]  // FIX (was: ERR mkdir: unrecognized option '--verbose=1')
    [InlineData("p=0 v=0 ops=[-foo]", "--", "-foo")]
    [InlineData("p=1 v=0 ops=[-p,-v]", "-p", "--", "-p", "-v")]
    [InlineData("p=0 v=0 ops=[--bogus,-m,-Z]", "--", "--bogus", "-m", "-Z")]
    [InlineData("p=0 v=0 ops=[-]", "-")]
    [InlineData("p=1 v=0 ops=[-]", "-p", "-")]
    [InlineData("p=0 v=0 ops=[]")]
    [InlineData("p=1 v=0 ops=[]", "-p")]
    [InlineData("ERR mkdir: option '-Z' is recognized but not supported by ps-bash", "-Z", "d")]
    [InlineData("ERR mkdir: option '-Z' is recognized but not supported by ps-bash", "-vZ", "d")]
    [InlineData("ERR mkdir: option '--context' is recognized but not supported by ps-bash", "--context", "d")]
    [InlineData("ERR mkdir: option '--context' is recognized but not supported by ps-bash", "--context=x", "d")]
    [InlineData("ERR mkdir: option '--context' is recognized but not supported by ps-bash", "--con", "d")]  // FIX (was: ERR mkdir: unrecognized option '--con')
    [InlineData("ERR mkdir: invalid option -- 'z'", "-z", "d")]
    [InlineData("ERR mkdir: invalid option -- 'z'", "-pz", "d")]  // FIX (was: ERR mkdir: invalid option -- 'p')
    [InlineData("ERR mkdir: invalid option -- 'z'", "-zp", "d")]
    [InlineData("ERR mkdir: invalid option -- '-'", "-p-", "d")]  // FIX (was: ERR mkdir: invalid option -- 'p')
    [InlineData("ERR mkdir: invalid option -- '5'", "-5", "d")]
    [InlineData("ERR mkdir: unrecognized option '--bogus'", "--bogus", "d")]
    [InlineData("ERR mkdir: unrecognized option '--bogus=1'", "--bogus=1", "d")]
    [InlineData("ERR mkdir: unrecognized option '--='", "--=", "d")]
    public void ScanArgs_MatchesGnuMkdir(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    // -m MODE / --mode=MODE is implemented: the scan only records the raw mode string (the cmdlet
    // compiles it), last occurrence wins, and a missing argument is getopt's usage error.
    [Theory]
    [InlineData("755", "-m", "755", "d")]
    [InlineData("755", "-m755", "d")]
    [InlineData("755", "-pm", "755", "d")]
    [InlineData("u=rwx,go=rx", "--mode=u=rwx,go=rx", "d")]
    [InlineData("700", "--mode", "700", "d")]
    [InlineData("700", "--mo", "700", "d")]
    [InlineData("700", "--m", "700", "d")]
    [InlineData("700", "-m", "755", "-m", "700", "d")]
    public void ScanArgs_RecordsTheModeString(string expectedMode, params string[] argv)
    {
        var p = InvokeBashMkdirCommand.ScanArgs(argv);
        Assert.Null(p.Error);
        Assert.Equal(expectedMode, p.Last("mode")?.Value);
        Assert.Equal(new[] { "d" }, p.Operands());
    }

    [Theory]
    [InlineData("ERR mkdir: option requires an argument -- 'm'", "-m")]
    [InlineData("ERR mkdir: option requires an argument -- 'm'", "d", "-m")]
    [InlineData("ERR mkdir: option '--mode' requires an argument", "--mode")]
    public void ScanArgs_ModeWithoutAnArgumentIsAUsageError(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
        Assert.Equal(1, InvokeBashMkdirCommand.ScanArgs(argv).ErrorExitCode);
    }
    [Theory]
    [InlineData("--version", "version")]
    [InlineData("--vers", "version")]
    [InlineData("--help", "help")]
    [InlineData("--he", "help")]
    public void InfoOptions_AreResolvedThroughAbbreviation(string arg, string id)
    {
        var p = InvokeBashMkdirCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-z", 1)]
    [InlineData("--parents=1", 1)]
    [InlineData("--v", 1)]
    [InlineData("--context", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashMkdirCommand.ScanArgs(new[] { arg, "d" }).ErrorExitCode);
    }
}
