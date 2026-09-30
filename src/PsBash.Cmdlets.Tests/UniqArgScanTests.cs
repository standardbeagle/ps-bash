using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for uniq (shared ordered parser + GNU value rules). The pre-migration
/// scan hand-dispatched bundles, understood only `--ignore-case`/`--all-repeated`/`--skip-fields=`/
/// `--skip-chars=`/`--check-chars=` as long options (so GNU's `--count`, `--repeated`, `--unique`
/// and every abbreviation were "unrecognized"), silently ignored a bad value (`uniq -f x`), read
/// `-5` as a FILE, and recovered a bare `-D` by regex-scanning the raw invocation line. Each FIX row
/// was checked against GNU uniq 9.4 (`wsl bash`): options -c -d -D -f -i -s -u -w -z + --count
/// --repeated --all-repeated[=none|prepend|separate] --skip-fields --group[=METHOD] --ignore-case
/// --skip-chars --unique --check-chars --zero-terminated (+ --help --version); obsolete `-N` =
/// `-f N`; unique long prefixes are accepted; usage errors exit 1. Refused loudly as
/// valid-but-unsupported (exit 2): -z/--zero-terminated, --group. GNU's obsolete `+N` (= -s N; off only for
/// _POSIX2_VERSION 200112..200808) and its "extra operand" error for a third operand are
/// implemented; `--all-repeated` with a METHOD is implemented (prepend/separate group separators).
/// Known gap: the second operand is GNU's OUTPUT file, ps-bash reads it as another input.
/// </summary>
/// <remarks>Env-dependent only through _POSIX2_VERSION, which must be unset here.</remarks>
public class UniqArgScanTests
{
    private static string Scan(string[] argv)
    {
        var u = InvokeBashUniqCommand.Plan(argv);
        if (u.Parsed.Error is { } e) return "ERR " + e.Message("uniq");
        if (u.Error is { } m) return "ERR " + m;
        static string F(bool b, char c) => b ? c.ToString() : "-";
        return $"{F(u.Count, 'c')}{F(u.Repeated, 'd')}{F(u.AllRepeated, 'D')}{F(u.Unique, 'u')}{F(u.IgnoreCase, 'i')} " +
               $"m={u.AllRepeatedMethod} f={u.SkipFields} s={u.SkipChars} w={(u.CheckChars < 0 ? "-" : u.CheckChars.ToString())} " +
               $"ops=[{string.Join(",", u.Operands)}]";
    }

    private const string Z = "m=none f=0 s=0 w=- ops=[]";

    [Theory]
    [InlineData("----- " + Z)]
    [InlineData("c---- " + Z, "-c")]
    [InlineData("-d--- " + Z, "-d")]
    [InlineData("---u- " + Z, "-u")]
    [InlineData("----i " + Z, "-i")]
    [InlineData("--D-- " + Z, "-D")]  // a quoted '-D' reaches Arguments distinct from -d (no raw-line scan)
    [InlineData("cd--- " + Z, "-cd")]
    [InlineData("c---i " + Z, "-ic")]
    [InlineData("cd-ui " + Z, "-cdui")]
    [InlineData("c---- " + Z, "--count")]  // FIX (was: "unrecognized option")
    [InlineData("-d--- " + Z, "--repeated")]  // FIX
    [InlineData("---u- " + Z, "--unique")]  // FIX
    [InlineData("----i " + Z, "--ignore-case")]
    [InlineData("c---- " + Z, "--coun")]  // FIX
    [InlineData("---u- " + Z, "--u")]  // FIX
    [InlineData("----i " + Z, "--ig")]  // FIX
    [InlineData("--D-- " + Z, "--all-repeated")]
    [InlineData("--D-- " + Z, "--a")]  // FIX
    [InlineData("--D-- " + Z, "--all")]  // FIX
    [InlineData("--D-- m=none f=0 s=0 w=- ops=[]", "--all-repeated=none")]
    [InlineData("--D-- m=separate f=0 s=0 w=- ops=[]", "--all-repeated=separate")]
    [InlineData("--D-- m=separate f=0 s=0 w=- ops=[]", "--all-repeated=sep")]  // FIX: argmatch prefix
    [InlineData("--D-- m=prepend f=0 s=0 w=- ops=[]", "--all-repeated=prepend")]
    [InlineData("--D-- m=none f=0 s=0 w=- ops=[sep]", "--all-repeated", "sep")]  // METHOD is attached-only: `sep` is a file
    [InlineData("-dD-- " + Z, "-dD")]
    [InlineData("--Du- " + Z, "-uD")]
    [InlineData("---u- m=none f=0 s=0 w=- ops=[f]", "-u", "f")]
    [InlineData("---u- m=none f=0 s=0 w=- ops=[f]", "f", "-u")]  // options may follow operands
    [InlineData("----- m=none f=1 s=0 w=- ops=[]", "-f", "1")]
    [InlineData("----- m=none f=1 s=0 w=- ops=[]", "-f1")]
    [InlineData("----- m=none f=1 s=0 w=- ops=[]", "--skip-fields=1")]
    [InlineData("----- m=none f=1 s=0 w=- ops=[]", "--skip-fields", "1")]  // FIX (was: operands [--skip-fields,1])
    [InlineData("----- m=none f=1 s=0 w=- ops=[]", "--skip-f", "1")]  // FIX
    [InlineData("----- m=none f=5 s=0 w=- ops=[]", "-5")]  // FIX: obsolete -N = -f N (was: a FILE named -5)
    [InlineData("----- m=none f=2 s=0 w=- ops=[]", "-f", "1", "-f", "2")]  // last wins
    [InlineData("----- m=none f=0 s=1 w=- ops=[]", "-s", "1")]
    [InlineData("----- m=none f=0 s=1 w=- ops=[]", "-s1")]
    [InlineData("----- m=none f=0 s=1 w=- ops=[]", "--skip-chars=1")]
    [InlineData("----- m=none f=0 s=1 w=- ops=[]", "--skip-c", "1")]  // FIX
    [InlineData("----- m=none f=0 s=0 w=1 ops=[]", "-w", "1")]
    [InlineData("----- m=none f=0 s=0 w=1 ops=[]", "-w1")]
    [InlineData("----- m=none f=0 s=0 w=1 ops=[]", "--check-chars=1")]
    [InlineData("----- m=none f=0 s=0 w=1 ops=[]", "--check", "1")]  // FIX
    [InlineData("----- m=none f=0 s=0 w=0 ops=[]", "-w", "0")]  // GNU: compare NOTHING (was: unlimited)
    [InlineData("-d--- m=none f=1 s=2 w=3 ops=[]", "-df1", "-s2", "-w3")]
    [InlineData("c---- m=none f=1 s=0 w=- ops=[]", "-cf1")]  // value option ends the bundle
    [InlineData("----- m=none f=2147483647 s=0 w=- ops=[]", "-f99999999999999999999")]  // GNU saturates
    [InlineData("----- m=none f=0 s=0 w=- ops=[f,g]", "f", "g")]
    [InlineData("----- m=none f=0 s=0 w=- ops=[-c]", "--", "-c")]
    [InlineData("c---- m=none f=0 s=0 w=- ops=[-]", "-c", "-")]
    [InlineData("ERR uniq: x: invalid number of fields to skip", "-f", "x")]  // FIX (was: silently 0)
    [InlineData("ERR uniq: -1: invalid number of fields to skip", "-f", "-1")]  // FIX
    [InlineData("ERR uniq: 1x: invalid number of fields to skip", "-f1x")]  // FIX
    [InlineData("ERR uniq: x: invalid number of bytes to skip", "-s", "x")]  // FIX
    [InlineData("ERR uniq: x: invalid number of bytes to compare", "-w", "x")]  // FIX
    [InlineData("ERR uniq: -1: invalid number of bytes to compare", "-w", "-1")]  // FIX
    [InlineData("ERR uniq: x: invalid number of fields to skip", "--skip-fields=x")]  // FIX
    [InlineData("ERR uniq: option requires an argument -- 'f'", "-f")]  // FIX (was: silently ignored)
    [InlineData("ERR uniq: option '--skip-fields' requires an argument", "--skip-fields")]
    [InlineData("ERR uniq: invalid argument 'x' for '--all-repeated'\nValid arguments are:\n  - 'none'\n  - 'prepend'\n  - 'separate'", "--all-repeated=x")]  // FIX
    [InlineData("ERR uniq: printing all duplicated lines and repeat counts is meaningless", "-cD")]  // FIX
    [InlineData("ERR uniq: printing all duplicated lines and repeat counts is meaningless", "-c", "--all-repeated")]  // FIX
    [InlineData("ERR uniq: option '-z' is recognized but not supported by ps-bash", "-z")]
    [InlineData("ERR uniq: option '--zero-terminated' is recognized but not supported by ps-bash", "--zero")]  // FIX
    [InlineData("ERR uniq: option '--group' is recognized but not supported by ps-bash", "--group")]
    [InlineData("ERR uniq: option '--group' is recognized but not supported by ps-bash", "--group=prepend")]
    [InlineData("ERR uniq: option '--group' is recognized but not supported by ps-bash", "--gr")]  // FIX
    [InlineData("ERR uniq: option '--s' is ambiguous; possibilities: '--skip-fields' '--skip-chars'", "--s", "1")]  // GNU long_options[] order
    [InlineData("ERR uniq: option '--c' is ambiguous; possibilities: '--count' '--check-chars'", "--c")]
    // Obsolete +N (= -s N) and the third-operand error, every row checked against GNU uniq 9.4.
    [InlineData("----- m=none f=0 s=1 w=- ops=[]", "+1")]  // FIX (was: a file named +1)
    [InlineData("c---- m=none f=0 s=1 w=- ops=[]", "-c", "+1")]
    [InlineData("----- m=none f=0 s=1 w=- ops=[a]", "a", "+1")]  // anywhere among the operands
    [InlineData("----- m=none f=0 s=1 w=- ops=[a,b]", "a", "b", "+1")]  // +N is not an operand: no extra-operand error
    [InlineData("----- m=none f=0 s=2 w=- ops=[]", "+1", "+2")]  // later wins
    [InlineData("----- m=none f=0 s=2 w=- ops=[]", "-s", "1", "+2")]  // ... against -s too
    [InlineData("----- m=none f=0 s=1 w=- ops=[]", "+2", "-s1")]
    [InlineData("----- m=none f=0 s=2147483647 w=- ops=[]", "+18446744073709551615")]  // fits 64 bits: saturates
    [InlineData("----- m=none f=0 s=0 w=- ops=[+18446744073709551616]", "+18446744073709551616")]  // 2^64: a file
    [InlineData("----- m=none f=0 s=0 w=- ops=[+x]", "+x")]  // not +DIGITS: a file
    [InlineData("----- m=none f=0 s=0 w=- ops=[+]", "+")]
    [InlineData("----- m=none f=0 s=0 w=- ops=[+1x]", "+1x")]
    [InlineData("----- m=none f=0 s=0 w=- ops=[+-1]", "+-1")]
    [InlineData("----- m=none f=0 s=0 w=- ops=[+99999999999999999999999]", "+99999999999999999999999")]  // > 64 bits: a file
    [InlineData("----- m=none f=0 s=0 w=- ops=[+1]", "--", "+1")]  // after `--` it is a file
    [InlineData("ERR uniq: extra operand 'c'", "a", "b", "c")]
    [InlineData("ERR uniq: extra operand 'c'", "-c", "a", "b", "c", "d")]
    [InlineData("ERR uniq: extra operand 'c'", "--", "a", "b", "c")]
    [InlineData("ERR uniq: extra operand 'c'", "a", "b", "+1", "c")]
    [InlineData("ERR uniq: option '--count' doesn't allow an argument", "--count=1")]  // FIX
    [InlineData("ERR uniq: option '--ignore-case' doesn't allow an argument", "--ignore-case=1")]
    [InlineData("ERR uniq: unrecognized option '--bogus'", "--bogus")]
    [InlineData("ERR uniq: invalid option -- 'x'", "-x")]
    [InlineData("ERR uniq: invalid option -- 'x'", "-cx")]
    [InlineData("ERR uniq: invalid option -- 'n'", "-n")]
    public void Plan_MatchesGnuUniq(string expected, params string[] argv)
    {
        Assert.Equal(expected, Scan(argv));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("199209", true)]
    [InlineData("200111", true)]
    [InlineData("200112", false)]  // oracle: +N is a file name for 2001..2008
    [InlineData("200113", false)]
    [InlineData("200808", false)]
    [InlineData("200809", true)]
    [InlineData("999999", true)]
    [InlineData("junk", true)]
    public void ObsoletePlus_FollowsPosix2Version(string? env, bool allowed)
    {
        Assert.Equal(allowed, InvokeBashUniqCommand.ObsoletePlusAllowed(env));
    }

    [Theory]
    [InlineData("--version", "version")]
    [InlineData("--vers", "version")]
    [InlineData("--v", "version")]
    [InlineData("--help", "help")]
    [InlineData("--he", "help")]
    public void InfoOptions_AreResolvedThroughAbbreviation(string arg, string id)
    {
        var p = InvokeBashUniqCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-x", 1)]
    [InlineData("-f", 1)]
    [InlineData("--s", 1)]
    [InlineData("-z", 2)]
    [InlineData("--group", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashUniqCommand.ScanArgs(new[] { arg }).ErrorExitCode);
    }
}
