using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-scan table for touch. The pre-migration scan had NO classifier and no bundling: only
/// the exact tokens -d VALUE / -r / --reference[=] / -m / -a / -c / -v were flags and EVERYTHING
/// else became an operand — so `touch -t 202401011200 f` created files named "-t" and
/// "202401011200" at exit 0, `touch -am f` created a file named "-am", and a trailing `-d` was
/// silently ignored (the "(was: …)" rows). Each FIX row was checked against GNU touch 9.4 (`wsl
/// bash`). GNU's options: -a -c -d -f -h -m -r -t, --no-create --date --reference --time
/// --no-dereference (+ --help --version); it has NO -v and its long options are case sensitive
/// (`--a`/`--m`/`--c` are unrecognized). Accepted: -a -m -c -d -r -f (-f is ignored, as in GNU).
/// Refused loudly as valid-but-unsupported (exit 2): -t, -h/--no-dereference, --time.
/// `-v` was a silent no-op inherited from the psm1 oracle; GNU rejects it, so it is now a usage
/// error (exit 1, GNU's status — see OptSpecSet). Abbreviation includes refused names (`--no` is
/// no-create/no-dereference; `--t` = --time).
/// </summary>
public class TouchArgScanTests
{
    private static string Scan(string[] argv)
    {
        var p = InvokeBashTouchCommand.ScanArgs(argv);
        if (p.Error is { } e) return "ERR " + e.Message("touch");
        static int B(bool b) => b ? 1 : 0;
        return $"a={B(p.Has("access"))} m={B(p.Has("modify"))} c={B(p.Has("no-create"))} f={B(p.Has("ignored"))} " +
               $"d={p.Last("date")?.Value ?? "-"} r={p.Last("reference")?.Value ?? "-"} ops=[{string.Join(",", p.Operands())}]";
    }

    [Theory]
    [InlineData("a=0 m=0 c=0 f=0 d=- r=- ops=[f]", "f")]
    [InlineData("a=1 m=0 c=0 f=0 d=- r=- ops=[f]", "-a", "f")]
    [InlineData("a=0 m=1 c=0 f=0 d=- r=- ops=[f]", "-m", "f")]
    [InlineData("a=0 m=0 c=1 f=0 d=- r=- ops=[f]", "-c", "f")]
    [InlineData("a=1 m=1 c=0 f=0 d=- r=- ops=[f]", "-a", "-m", "f")]
    [InlineData("a=1 m=1 c=0 f=0 d=- r=- ops=[f]", "-am", "f")]  // FIX (was: operands [-am,f] — a file named "-am")
    [InlineData("a=1 m=1 c=0 f=0 d=- r=- ops=[f]", "-ma", "f")]  // FIX (was: operands)
    [InlineData("a=1 m=0 c=1 f=0 d=- r=- ops=[f]", "-ca", "f")]  // FIX (was: operands)
    [InlineData("a=1 m=0 c=0 f=0 d=- r=- ops=[f]", "-aa", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=1 f=0 d=- r=- ops=[f]", "-cc", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=1 c=0 f=0 d=- r=- ops=[f]", "-mm", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=0 f=1 d=- r=- ops=[f]", "-f", "f")]  // FIX (was: operands [-f,f])
    [InlineData("a=1 m=0 c=0 f=1 d=- r=- ops=[f]", "-fa", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=1 f=0 d=- r=- ops=[f]", "--no-create", "f")]  // FIX (was: operands [--no-create,f])
    [InlineData("a=0 m=0 c=1 f=0 d=- r=- ops=[f]", "--no-c", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=0 f=0 d=- r=- ops=[f,g]", "f", "g")]
    [InlineData("a=1 m=0 c=0 f=0 d=- r=- ops=[f]", "f", "-a")]  // FIX (was: operands [f,-a])
    [InlineData("a=1 m=0 c=0 f=0 d=- r=- ops=[f,g]", "f", "-a", "g")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=0 f=0 d=2020-01-01 r=- ops=[f]", "-d", "2020-01-01", "f")]
    [InlineData("a=0 m=0 c=0 f=0 d=2020-01-01 r=- ops=[f]", "-d2020-01-01", "f")]  // FIX (was: operands [-d2020-01-01,f])
    [InlineData("a=0 m=0 c=0 f=0 d=2020-01-01 r=- ops=[f]", "--date=2020-01-01", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=0 f=0 d=2020-01-01 r=- ops=[f]", "--date", "2020-01-01", "f")]  // FIX (was: operands [--date,2020-01-01,f])
    [InlineData("a=0 m=0 c=0 f=0 d=2020-01-01 r=- ops=[f]", "--da=2020-01-01", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=0 f=0 d=2020-01-01 r=- ops=[f]", "--d", "2020-01-01", "f")]  // FIX (was: operands)
    [InlineData("a=1 m=0 c=0 f=0 d=yesterday r=- ops=[f]", "-ad", "yesterday", "f")]  // FIX (was: operands)
    [InlineData("a=1 m=0 c=0 f=0 d=yesterday r=- ops=[f]", "-adyesterday", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=0 f=0 d=-a r=- ops=[f]", "-d", "-a", "f")]  // getopt: a value option takes the next element even when it starts with a dash
    [InlineData("a=0 m=0 c=0 f=0 d=b r=- ops=[f]", "-d", "a", "-d", "b", "f")]  // last -d wins
    [InlineData("a=0 m=0 c=0 f=0 d=- r=ref ops=[f]", "-r", "ref", "f")]
    [InlineData("a=0 m=0 c=0 f=0 d=- r=ref ops=[f]", "-rref", "f")]  // FIX (was: operands [-rref,f])
    [InlineData("a=0 m=0 c=0 f=0 d=- r=ref ops=[f]", "--reference=ref", "f")]
    [InlineData("a=0 m=0 c=0 f=0 d=- r=ref ops=[f]", "--reference", "ref", "f")]
    [InlineData("a=0 m=0 c=0 f=0 d=- r=ref ops=[f]", "--ref", "ref", "f")]  // FIX (was: operands)
    [InlineData("a=0 m=0 c=0 f=0 d=- r=ref ops=[f]", "--r=ref", "f")]  // FIX (was: operands)
    [InlineData("a=1 m=0 c=0 f=0 d=- r=ref ops=[f]", "-ar", "ref", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: option requires an argument -- 'd'", "f", "-d")]  // FIX (was: -d silently ignored, touched f)
    [InlineData("ERR touch: option requires an argument -- 'r'", "f", "-r")]  // FIX (was: -r silently ignored)
    [InlineData("ERR touch: option requires an argument -- 'd'", "-ad")]  // FIX (was: operands)
    [InlineData("ERR touch: option '--date' requires an argument", "f", "--date")]  // FIX (was: operands)
    [InlineData("ERR touch: option '--reference' requires an argument", "f", "--reference")]  // FIX (was: --reference silently ignored)
    [InlineData("a=0 m=0 c=0 f=0 d=- r=- ops=[-a,-d]", "--", "-a", "-d")]
    [InlineData("a=1 m=0 c=0 f=0 d=- r=- ops=[-a]", "-a", "--", "-a")]
    [InlineData("a=0 m=0 c=0 f=0 d=- r=- ops=[--bogus,-t,-v]", "--", "--bogus", "-t", "-v")]
    [InlineData("a=0 m=0 c=0 f=0 d=- r=- ops=[-]", "-")]
    [InlineData("a=0 m=0 c=0 f=0 d=- r=- ops=[]")]
    [InlineData("a=1 m=0 c=0 f=0 d=- r=- ops=[]", "-a")]
    [InlineData("ERR touch: invalid option -- 'v'", "-v", "f")]  // FIX (was: accepted as a silent no-op) — GNU touch has no -v
    [InlineData("ERR touch: invalid option -- 'v'", "-cv", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: option '--no' is ambiguous; possibilities: '--no-create' '--no-dereference'", "--no", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: option '--n' is ambiguous; possibilities: '--no-create' '--no-dereference'", "--n", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: option '--no-create' doesn't allow an argument", "--no-create=1", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: unrecognized option '--a'", "--a", "f")]  // GNU: touch has no long -a / -m / -c
    [InlineData("ERR touch: unrecognized option '--m'", "--m", "f")]
    [InlineData("ERR touch: unrecognized option '--c'", "--c", "f")]
    [InlineData("ERR touch: unrecognized option '--verbose'", "--verbose", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: invalid option -- 'Z'", "-Z", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: invalid option -- 'z'", "-z", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: invalid option -- 'z'", "-az", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: invalid option -- 'z'", "-za", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: invalid option -- '-'", "-a-", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: invalid option -- '5'", "-5", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: unrecognized option '--bogus'", "--bogus", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: unrecognized option '--bogus=1'", "--bogus=1", "f")]  // FIX (was: operands)
    [InlineData("ERR touch: unrecognized option '--='", "--=", "f")]
    public void ScanArgs_MatchesGnuTouch(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    // -t STAMP / -h (--no-dereference) / --time=WORD are implemented: the scan records them, the
    // cmdlet interprets them (TouchStampTests / TouchOptionsTests).
    [Theory]
    [InlineData("202401011200", "-t", "202401011200", "f")]
    [InlineData("202401011200", "-t202401011200", "f")]
    [InlineData("202401011200", "-at", "202401011200", "f")]
    [InlineData("202401011201", "-t", "202401011200", "-t", "202401011201", "f")]   // last wins
    public void ScanArgs_RecordsTheStamp(string expected, params string[] argv)
    {
        var p = InvokeBashTouchCommand.ScanArgs(argv);
        Assert.Null(p.Error);
        Assert.Equal(expected, p.Last("stamp")?.Value);
        Assert.Equal(new[] { "f" }, p.Operands());
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("-ha")]
    [InlineData("--no-dereference")]
    [InlineData("--no-d")]
    public void ScanArgs_AcceptsNoDereference(string flag)
    {
        var p = InvokeBashTouchCommand.ScanArgs(new[] { flag, "f" });
        Assert.Null(p.Error);
        Assert.True(p.Has("no-dereference"));
    }

    [Theory]
    [InlineData("atime", "--time=atime", "f")]
    [InlineData("mtime", "--time", "mtime", "f")]
    [InlineData("atime", "--ti", "atime", "f")]
    [InlineData("access", "--t", "access", "f")]
    [InlineData("use", "--tim=use", "f")]
    public void ScanArgs_RecordsTheTimeWord(string expected, params string[] argv)
    {
        var p = InvokeBashTouchCommand.ScanArgs(argv);
        Assert.Null(p.Error);
        Assert.Equal(expected, p.Last("time")?.Value);
        Assert.Equal(new[] { "f" }, p.Operands());
    }

    [Theory]
    [InlineData("ERR touch: option requires an argument -- 't'", "-t")]
    [InlineData("ERR touch: option '--time' requires an argument", "--time")]
    public void ScanArgs_MissingArgumentIsAUsageError(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
        Assert.Equal(1, InvokeBashTouchCommand.ScanArgs(argv).ErrorExitCode);
    }
    [Theory]
    [InlineData("--version", "version")]
    [InlineData("--vers", "version")]
    [InlineData("--ver", "version")]  // unique: touch has no --verbose, so unlike cp/mv this is NOT ambiguous
    [InlineData("--v", "version")]
    [InlineData("--help", "help")]
    [InlineData("--he", "help")]
    [InlineData("--h", "help")]
    public void InfoOptions_AreResolvedThroughAbbreviation(string arg, string id)
    {
        var p = InvokeBashTouchCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-v", 1)]
    [InlineData("--no", 1)]
    [InlineData("-d", 1)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashTouchCommand.ScanArgs(new[] { arg }).ErrorExitCode);
    }
}
