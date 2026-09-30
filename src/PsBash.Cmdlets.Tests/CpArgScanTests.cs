using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for cp. Every row was run through the pre-migration hand scan
/// (flag switch + `IsCpShortBundle` + `TryWriteOperandOptionError`) and against GNU cp
/// (`wsl bash`, oracle checked for the FIX rows: abbreviations, ambiguity wording,
/// "doesn't allow an argument", the offending letter in a bad bundle). Rows marked FIX
/// are GNU-correct changes; unmarked rows are byte-identical to the old behavior.
/// Known GNU divergences that REMAIN by policy: valid-but-unimplemented options (`--preserve`,
/// `-i`, `-t`, ...) are refused with exit 2 instead of honored, and the scan-error exit status
/// is 2 (GNU: 1).
/// </summary>
public class CpArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashCpCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("cp");
        bool archive = p.Has("archive");
        static int B(bool b) => b ? 1 : 0;
        return $"r={B(archive || p.Has("recursive"))} n={B(p.Has("no-clobber"))} f={B(p.Has("force"))} " +
               $"v={B(p.Has("verbose"))} p={B(archive || p.Has("preserve"))} u={B(p.Has("update"))} " +
               $"ops=[{string.Join(",", p.Operands())}]";
    }

    [Theory]
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=0 ops=[a,b]", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[a,b]", "-r", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[a,b]", "-R", "a", "b")]
    [InlineData("r=1 n=0 f=1 v=0 p=0 u=0 ops=[a,b]", "-rf", "a", "b")]
    [InlineData("r=0 n=0 f=1 v=1 p=0 u=0 ops=[a,b]", "-fv", "a", "b")]
    [InlineData("r=0 n=1 f=0 v=1 p=0 u=0 ops=[a,b]", "-nv", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=1 p=1 u=0 ops=[a,b]", "-rpv", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=1 u=0 ops=[a,b]", "-a", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=1 u=0 ops=[a,b]", "-ar", "a", "b")]
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=1 ops=[a,b]", "-u", "a", "b")]
    [InlineData("r=1 n=0 f=1 v=0 p=0 u=1 ops=[a,b]", "-rfu", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[a,b]", "a", "b", "-r")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[a,b]", "--recursive", "a", "b")]
    [InlineData("r=0 n=1 f=1 v=1 p=0 u=0 ops=[a,b]", "--force", "--verbose", "--no-clobber", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=1 u=0 ops=[a,b]", "--archive", "a", "b")]
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=1 ops=[a,b]", "--update", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[a,b]", "--rec", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--rec')
    [InlineData("ERR cp: option '--re' is ambiguous; possibilities: '--recursive' '--reflink' '--remove-destination'", "--re", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--re')
    [InlineData("ERR cp: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--ver')
    [InlineData("ERR cp: option '--no' is ambiguous; possibilities: '--no-clobber' '--no-dereference' '--no-preserve' '--no-target-directory'", "--no", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--no')
    [InlineData("r=0 n=1 f=0 v=0 p=0 u=0 ops=[a,b]", "--no-c", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--no-c')
    [InlineData("r=0 n=0 f=1 v=0 p=0 u=0 ops=[a,b]", "--forc", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--forc')
    [InlineData("r=1 n=0 f=0 v=0 p=1 u=0 ops=[a,b]", "--arc", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--arc')
    [InlineData("ERR cp: option '--a' is ambiguous; possibilities: '--archive' '--attributes-only'", "--a", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--a')
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=1 ops=[a,b]", "--u", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--u')
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=0 ops=[-a,b]", "--", "-a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[-a,-r]", "-r", "--", "-a", "-r")]
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=0 ops=[--bogus,-i]", "--", "--bogus", "-i")]
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=0 ops=[-,b]", "--", "-", "b")]
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=0 ops=[-,b]", "-", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[-,b]", "-r", "-", "b")]
    [InlineData("ERR cp: option '-i' is recognized but not supported by ps-bash", "-i", "a", "b")]
    [InlineData("ERR cp: option '-d' is recognized but not supported by ps-bash", "-d", "a", "b")]
    [InlineData("ERR cp: option '-l' is recognized but not supported by ps-bash", "-l", "a", "b")]
    [InlineData("ERR cp: option '-s' is recognized but not supported by ps-bash", "-s", "a", "b")]
    [InlineData("ERR cp: option '-b' is recognized but not supported by ps-bash", "-b", "a", "b")]
    [InlineData("ERR cp: option '-P' is recognized but not supported by ps-bash", "-P", "a", "b")]
    [InlineData("ERR cp: option '-L' is recognized but not supported by ps-bash", "-L", "a", "b")]
    [InlineData("ERR cp: option '-H' is recognized but not supported by ps-bash", "-H", "a", "b")]
    [InlineData("ERR cp: option '-t' is recognized but not supported by ps-bash", "-t", "dir", "a")]
    [InlineData("ERR cp: option '-T' is recognized but not supported by ps-bash", "-T", "a", "b")]
    [InlineData("ERR cp: option '-x' is recognized but not supported by ps-bash", "-x", "a", "b")]
    [InlineData("ERR cp: option '-Z' is recognized but not supported by ps-bash", "-Z", "a", "b")]
    [InlineData("ERR cp: option '--interactive' is recognized but not supported by ps-bash", "--interactive", "a", "b")]
    [InlineData("ERR cp: option '--link' is recognized but not supported by ps-bash", "--link", "a", "b")]
    [InlineData("ERR cp: option '--symbolic-link' is recognized but not supported by ps-bash", "--symbolic-link", "a", "b")]
    [InlineData("ERR cp: option '--backup' is recognized but not supported by ps-bash", "--backup", "a", "b")]
    [InlineData("ERR cp: option '--backup' is recognized but not supported by ps-bash", "--backup=numbered", "a", "b")]
    [InlineData("ERR cp: option '--reflink' is recognized but not supported by ps-bash", "--reflink", "a", "b")]
    [InlineData("ERR cp: option '--reflink' is recognized but not supported by ps-bash", "--reflink=auto", "a", "b")]
    [InlineData("ERR cp: option '--no-dereference' is recognized but not supported by ps-bash", "--no-dereference", "a", "b")]
    [InlineData("ERR cp: option '--dereference' is recognized but not supported by ps-bash", "--dereference", "a", "b")]
    [InlineData("ERR cp: option '--target-directory' is recognized but not supported by ps-bash", "--target-directory=d", "a")]
    [InlineData("ERR cp: option '--target-directory' is recognized but not supported by ps-bash", "--target-directory", "d", "a")]
    [InlineData("ERR cp: option '--no-target-directory' is recognized but not supported by ps-bash", "--no-target-directory", "a", "b")]
    [InlineData("ERR cp: option '--one-file-system' is recognized but not supported by ps-bash", "--one-file-system", "a", "b")]
    [InlineData("ERR cp: option '--sparse' is recognized but not supported by ps-bash", "--sparse=always", "a", "b")]
    [InlineData("ERR cp: option '--strip-trailing-slashes' is recognized but not supported by ps-bash", "--strip-trailing-slashes", "a", "b")]
    [InlineData("ERR cp: option '--context' is recognized but not supported by ps-bash", "--context", "a", "b")]
    [InlineData("ERR cp: option '--attributes-only' is recognized but not supported by ps-bash", "--attributes-only", "a", "b")]
    [InlineData("ERR cp: option '--parents' is recognized but not supported by ps-bash", "--parents", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--parents')
    [InlineData("ERR cp: option '--remove-destination' is recognized but not supported by ps-bash", "--remove-destination", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--remove-destination')
    [InlineData("ERR cp: option '--copy-contents' is recognized but not supported by ps-bash", "--copy-contents", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--copy-contents')
    [InlineData("ERR cp: option '--debug' is recognized but not supported by ps-bash", "--debug", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--debug')
    [InlineData("ERR cp: option '--interactive' is recognized but not supported by ps-bash", "--inter", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--inter')
    [InlineData("ERR cp: option '--symbolic-link' is recognized but not supported by ps-bash", "--sym", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--sym')
    [InlineData("ERR cp: invalid option -- 'z'", "-rz", "a", "b")]  // FIX (was: ERR cp: invalid option -- 'r')
    [InlineData("ERR cp: invalid option -- 'z'", "-zr", "a", "b")]
    [InlineData("ERR cp: option '-d' is recognized but not supported by ps-bash", "-rd", "a", "b")]
    [InlineData("ERR cp: option '-i' is recognized but not supported by ps-bash", "-ri", "a", "b")]
    [InlineData("ERR cp: invalid option -- 'z'", "-z", "a", "b")]
    [InlineData("ERR cp: unrecognized option '--bogus'", "--bogus", "a", "b")]
    [InlineData("ERR cp: unrecognized option '--bogus=1'", "--bogus=1", "a", "b")]
    [InlineData("ERR cp: option '--recursive' doesn't allow an argument", "--recursive=yes", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--recursive=yes')
    [InlineData("ERR cp: option '--verbose' doesn't allow an argument", "--verbose=1", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--verbose=1')
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=1 ops=[a,b]", "--update=older", "a", "b")]  // FIX (was: ERR cp: unrecognized option '--update=older')
    [InlineData("ERR cp: invalid option -- 'V'", "-V", "a", "b")]
    [InlineData("ERR cp: invalid option -- 'N'", "-N", "a", "b")]
    [InlineData("ERR cp: invalid option -- '5'", "-5", "a", "b")]
    [InlineData("ERR cp: invalid option -- '5'", "-r5", "a", "b")]  // FIX (was: ERR cp: invalid option -- 'r')
    [InlineData("r=0 n=0 f=0 v=1 p=0 u=0 ops=[a,b]", "-vv", "a", "b")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[a,b]", "-rr", "a", "b")]
    [InlineData("r=0 n=1 f=1 v=0 p=0 u=0 ops=[a,b]", "-fn", "a", "b")]
    [InlineData("ERR cp: invalid option -- '-'", "-a-", "a", "b")]  // FIX (was: ERR cp: invalid option -- 'a')
    [InlineData("ERR cp: unrecognized option '--='", "--=", "a")]
    [InlineData("r=1 n=0 f=1 v=1 p=0 u=0 ops=[a,b]", "-r", "-f", "-v", "a", "b")]
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=0 ops=[]")]
    [InlineData("r=1 n=0 f=0 v=0 p=0 u=0 ops=[]", "-r")]
    [InlineData("r=0 n=0 f=0 v=0 p=0 u=0 ops=[a]", "a")]

    public void ScanArgs_MatchesGnuCp(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    [Fact]
    public void UpdateOption_ValueIsCarriedToTheCmdlet()
    {
        var p = InvokeBashCpCommand.ScanArgs(new[] { "--update=none", "a", "b" });
        Assert.Equal("none", p.Last("update")!.Value.Value);
    }

    [Fact]
    public void ArchiveIsItsOwnOption_TheCmdletImpliesRecursiveAndPreserve()
    {
        var p = InvokeBashCpCommand.ScanArgs(new[] { "-a", "a", "b" });
        Assert.True(p.Has("archive"));
        Assert.False(p.Has("recursive"));
    }

    // --preserve[=LIST] / --no-preserve=LIST are implemented: the scan records the raw list in
    // command-line order (CpPreserveTests covers the semantics). --preserve's argument is OPTIONAL
    // (attached only); --no-preserve's is REQUIRED (it takes the next word).
    [Theory]
    [InlineData("preserve-list", "", "--preserve", "a", "b")]
    [InlineData("preserve-list", "all", "--preserve=all", "a", "b")]
    [InlineData("preserve-list", "mode,timestamps", "--preserve=mode,timestamps", "a", "b")]
    [InlineData("preserve-list", "", "--pre", "a", "b")]
    [InlineData("no-preserve", "mode", "--no-preserve=mode", "a", "b")]
    [InlineData("no-preserve", "mode", "--no-preserve", "mode", "a", "b")]
    [InlineData("no-preserve", "all", "--no-p=all", "a", "b")]
    public void ScanArgs_RecordsThePreserveLists(string id, string value, params string[] argv)
    {
        var p = InvokeBashCpCommand.ScanArgs(argv);
        Assert.Null(p.Error);
        Assert.Equal(value, p.Last(id)?.Value ?? "");
        Assert.Equal(new[] { "a", "b" }, p.Operands());
    }}
