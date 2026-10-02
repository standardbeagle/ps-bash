using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for rm. Every row was run through the pre-migration hand scan (exact
/// tokens + an r/R/f/v-only bundle rule + classify-first-option-like-operand-before-`--`) and,
/// for the FIX rows, against GNU rm 9.4 (`wsl bash`). FIX rows are GNU-correct changes:
/// unique-prefix long options (`--rec`, `--forc`), ambiguity with GNU's candidate list
/// (`--ver` = verbose/version), "doesn't allow an argument", the OFFENDING letter named in a bad
/// bundle (`-rz` -> 'z', the old scan named 'r'), and abbreviations of refused options
/// (`--inter`) reported as unsupported instead of "unrecognized". Remaining divergences:
/// valid-but-unimplemented options (-i, -I, -d, --interactive, --dir, --one-file-system,
/// --preserve-root, --no-preserve-root) are refused with exit 2 instead of honored, and GNU's
/// special "you may not abbreviate the --no-preserve-root option" (`--no`, `--n`) surfaces here as
/// the ordinary unsupported refusal. The scan-error exit status is GNU's 1 (see OptSpecSet).
/// </summary>
public class RmArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashRmCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("rm");
        static int B(bool b) => b ? 1 : 0;
        return $"r={B(p.Has("recursive"))} f={B(p.Has("force"))} v={B(p.Has("verbose"))} ops=[{string.Join(",", p.Operands())}]";
    }

    [Theory]
    [InlineData("r=0 f=0 v=0 ops=[a]", "a")]
    [InlineData("r=1 f=0 v=0 ops=[a]", "-r", "a")]
    [InlineData("r=1 f=0 v=0 ops=[a]", "-R", "a")]
    [InlineData("r=0 f=1 v=0 ops=[a]", "-f", "a")]
    [InlineData("r=0 f=0 v=1 ops=[a]", "-v", "a")]
    [InlineData("r=1 f=1 v=0 ops=[a]", "-rf", "a")]
    [InlineData("r=1 f=1 v=0 ops=[a]", "-fr", "a")]
    [InlineData("r=1 f=1 v=1 ops=[a]", "-rvf", "a")]
    [InlineData("r=1 f=1 v=1 ops=[a,b]", "-Rfv", "a", "b")]
    [InlineData("r=0 f=0 v=1 ops=[a]", "-vv", "a")]
    [InlineData("r=0 f=1 v=0 ops=[a]", "-ff", "a")]
    [InlineData("r=1 f=0 v=0 ops=[a]", "-RR", "a")]
    [InlineData("r=1 f=1 v=0 ops=[a]", "-r", "-f", "a")]
    [InlineData("r=1 f=0 v=0 ops=[a]", "a", "-r")]
    [InlineData("r=1 f=1 v=0 ops=[a,b]", "a", "-rf", "b")]
    [InlineData("r=1 f=0 v=0 ops=[a]", "--recursive", "a")]
    [InlineData("r=0 f=1 v=0 ops=[a]", "--force", "a")]
    [InlineData("r=0 f=0 v=1 ops=[a]", "--verbose", "a")]
    [InlineData("r=1 f=0 v=0 ops=[a]", "--rec", "a")]  // FIX (was: ERR rm: unrecognized option '--rec')
    [InlineData("r=1 f=0 v=0 ops=[a]", "--r", "a")]  // FIX (was: ERR rm: unrecognized option '--r')
    [InlineData("r=0 f=1 v=0 ops=[a]", "--forc", "a")]  // FIX (was: ERR rm: unrecognized option '--forc')
    [InlineData("r=0 f=1 v=0 ops=[a]", "--f", "a")]  // FIX (was: ERR rm: unrecognized option '--f')
    [InlineData("r=0 f=0 v=1 ops=[a]", "--verb", "a")]  // FIX (was: ERR rm: unrecognized option '--verb')
    [InlineData("ERR rm: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver", "a")]  // FIX (was: ERR rm: unrecognized option '--ver')
    [InlineData("ERR rm: option '--v' is ambiguous; possibilities: '--verbose' '--version'", "--v", "a")]  // FIX (was: ERR rm: unrecognized option '--v')
    [InlineData("ERR rm: option '--recursive' doesn't allow an argument", "--recursive=1", "a")]  // FIX (was: ERR rm: unrecognized option '--recursive=1')
    [InlineData("ERR rm: option '--force' doesn't allow an argument", "--force=1", "a")]  // FIX (was: ERR rm: unrecognized option '--force=1')
    [InlineData("ERR rm: option '--verbose' doesn't allow an argument", "--verbose=yes", "a")]  // FIX (was: ERR rm: unrecognized option '--verbose=yes')
    [InlineData("r=0 f=0 v=0 ops=[-weird]", "--", "-weird")]
    [InlineData("r=1 f=0 v=0 ops=[-r,-f]", "-r", "--", "-r", "-f")]
    [InlineData("r=0 f=0 v=0 ops=[--bogus,-i,-I,-d]", "--", "--bogus", "-i", "-I", "-d")]
    [InlineData("r=0 f=0 v=0 ops=[-,b]", "-", "b")]
    [InlineData("r=1 f=0 v=0 ops=[-,b]", "-r", "-", "b")]
    [InlineData("r=0 f=0 v=0 ops=[]")]
    [InlineData("r=0 f=1 v=0 ops=[]", "-f")]
    [InlineData("r=0 f=0 v=0 ops=[a]", "--one-file-system", "a")]
    [InlineData("r=0 f=0 v=0 ops=[a]", "--one", "a")]
    [InlineData("r=0 f=0 v=0 ops=[a]", "--preserve-root", "a")]
    [InlineData("r=0 f=0 v=0 ops=[a]", "--preserve-root=all", "a")]
    [InlineData("r=0 f=0 v=0 ops=[a]", "--no-preserve-root", "a")]
    [InlineData("r=0 f=0 v=0 ops=[a]", "--no", "a")]  // the scan accepts the abbreviation; RmRootPolicyResolver refuses it (GNU: "you may not abbreviate ...")
    [InlineData("ERR rm: option '--no-preserve-root' doesn't allow an argument", "--no-preserve-root=x", "a")]
    [InlineData("ERR rm: invalid option -- 'z'", "-z", "a")]
    [InlineData("ERR rm: invalid option -- 'z'", "-rz", "a")]  // FIX (was: ERR rm: invalid option -- 'r')
    [InlineData("ERR rm: invalid option -- 'z'", "-zr", "a")]
    [InlineData("ERR rm: invalid option -- 'z'", "-rfz", "a")]  // FIX (was: ERR rm: invalid option -- 'r')
    [InlineData("ERR rm: invalid option -- '5'", "-5", "a")]
    [InlineData("ERR rm: invalid option -- '-'", "-r-", "a")]  // FIX (was: ERR rm: invalid option -- 'r')
    [InlineData("ERR rm: unrecognized option '--bogus'", "--bogus", "a")]
    [InlineData("ERR rm: unrecognized option '--bogus=1'", "--bogus=1", "a")]
    [InlineData("ERR rm: unrecognized option '--='", "--=", "a")]
    [InlineData("ERR rm: invalid option -- 'z'", "-r", "-z", "a")]
    public void ScanArgs_MatchesGnuRm(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    // The newly implemented options (-d/--dir, -i, -I, --interactive[=WHEN]): ids in ORIGINAL order,
    // because the last of -f/-i/-I/--interactive wins (`-if` differs from `-fi`).
    private static string OptionIds(params string[] argv)
    {
        var p = InvokeBashRmCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("rm");
        return string.Join(",", p.Tokens.Where(t => t.Kind == PsBash.Cmdlets.Args.ArgTokKind.Option)
            .Select(t => t.Value is null ? t.OptId : $"{t.OptId}={t.Value}"));
    }

    [Theory]
    [InlineData("dir", "-d", "a")]
    [InlineData("dir", "--dir", "a")]
    [InlineData("dir", "--d", "a")]
    [InlineData("recursive,dir", "-rd", "a")]
    [InlineData("prompt-always", "-i", "a")]
    [InlineData("prompt-once", "-I", "a")]
    [InlineData("force,prompt-always", "-fi", "a")]
    [InlineData("prompt-always,force", "-if", "a")]
    [InlineData("prompt-always,verbose", "-iv", "a")]
    [InlineData("interactive", "--interactive", "a")]
    [InlineData("interactive", "--inter", "a")]
    [InlineData("interactive", "--i", "a")]
    [InlineData("interactive=never", "--interactive=never", "a")]
    [InlineData("interactive=", "--interactive=", "a")]
    public void ScanArgs_ParsesTheInteractiveAndDirOptionsInOrder(string expected, params string[] argv)
    {
        Assert.Equal(expected, OptionIds(argv));
    }

    [Fact]
    public void ScanArgs_InteractiveTakesNoDetachedValue_SoTheNextWordIsAnOperand()
    {
        var p = InvokeBashRmCommand.ScanArgs(new[] { "--interactive", "never", "a" });
        Assert.Null(p.Error);
        Assert.Equal(new[] { "never", "a" }, p.Operands());
    }

    [Theory]
    [InlineData("never", "never", true)]
    [InlineData("no", "never", true)]
    [InlineData("none", "never", true)]
    [InlineData("n", "never", true)]      // prefix of never/no/none: one meaning, so accepted
    [InlineData("once", "once", true)]
    [InlineData("o", "once", true)]
    [InlineData("always", "always", true)]
    [InlineData("yes", "always", true)]
    [InlineData("a", "always", true)]
    [InlineData("y", "always", true)]
    [InlineData(null, "always", true)]    // bare --interactive
    [InlineData("bogus", null, false)]
    [InlineData("", null, false)]         // prefix of every name, several meanings: ambiguous
    [InlineData("nx", null, false)]
    public void TryParseInteractiveWhen_MatchesGnuXargmatch(string? arg, string? expectedMode, bool ok)
    {
        Assert.Equal(ok, InvokeBashRmCommand.TryParseInteractiveWhen(arg, out var mode, out var error));
        if (ok) Assert.Equal(expectedMode, mode.ToString().ToLowerInvariant());
        else Assert.Contains("for '--interactive'", error);
    }

    [Theory]
    [InlineData("bogus", "invalid argument 'bogus'")]
    [InlineData("", "ambiguous argument ''")]
    public void TryParseInteractiveWhen_ErrorNamesTheKindAndListsTheValidArguments(string arg, string fragment)
    {
        InvokeBashRmCommand.TryParseInteractiveWhen(arg, out _, out var error);
        Assert.Contains(fragment, error);
        Assert.Contains("'never', 'no', 'none'", error);
        Assert.EndsWith("Try 'rm --help' for more information.", error);
    }

    [Theory]
    [InlineData("--version", "version")]
    [InlineData("--vers", "version")]
    [InlineData("--help", "help")]
    [InlineData("--he", "help")]
    [InlineData("--h", "help")]
    public void InfoOptions_AreResolvedThroughAbbreviation(string arg, string id)
    {
        var p = InvokeBashRmCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-z", 1)]
    [InlineData("--recursive=1", 1)]
    [InlineData("--ver", 1)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashRmCommand.ScanArgs(new[] { arg, "a" }).ErrorExitCode);
    }
}
