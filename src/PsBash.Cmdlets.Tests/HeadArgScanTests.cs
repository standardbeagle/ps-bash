using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for head (shared ordered parser + GNU NUM rules). The pre-migration
/// scan matched only the exact tokens -n / -c / -q / -nN / -cN / -N / --lines[=] / --bytes[=] and
/// sent EVERYTHING else — bundles (`-qn2`), abbreviations (`--li=1`), unknown flags — into the
/// operand list, and it silently ignored a non-numeric value (`head -n abc` printed 10 lines at
/// exit 0, `head -c 1K` printed 10 lines). Each FIX row was checked against GNU head 9.4 (`wsl
/// bash`): options -c -n -q -v -z + --bytes --lines --quiet --silent --verbose --zero-terminated
/// (+ --help --version); NUM takes a sign and a multiplier suffix (b kB K MB M GB G ...); the last
/// of -c/-n wins; the obsolete -NUM is only valid as the FIRST argument. Accepted here: -c -n -q
/// (+ long forms). Refused loudly as valid-but-unsupported (exit 2): -v/--verbose (no per-file
/// headers), -z/--zero-terminated. Usage errors are exit 1 (GNU EXIT_FAILURE).
/// The obsolete `-NUM[bkmlcqvz]*` first argument follows GNU's letter grammar (see TryExpandObsoleteNum).
/// The legacy ps-bash extension `head 5` (bare leading number = line count) is preserved because
/// the Pester gate pins it.
/// </summary>
public class HeadArgScanTests
{
    private static string Scan(string[] argv)
    {
        var h = InvokeBashHeadCommand.Plan(argv);
        if (h.Parsed.Error is { } e) return "ERR " + e.Message("head");
        if (h.Error is { } m) return "ERR " + m;
        string mode = h.BytesMode ? "c=" + h.ByteCount : "n=" + h.Count;
        return $"{mode} q={(h.Parsed.Has("quiet") ? 1 : 0)} ops=[{string.Join(",", h.Operands)}]";
    }

    [Theory]
    [InlineData("n=10 q=0 ops=[]")]
    [InlineData("n=3 q=0 ops=[]", "-n", "3")]
    [InlineData("n=3 q=0 ops=[]", "-n3")]
    [InlineData("n=3 q=0 ops=[]", "--lines=3")]
    [InlineData("n=3 q=0 ops=[]", "--lines", "3")]
    [InlineData("n=3 q=0 ops=[]", "--li=3")]  // FIX (was: operands [--li=3])
    [InlineData("n=3 q=0 ops=[]", "--lin", "3")]  // FIX (was: operands)
    [InlineData("n=3 q=0 ops=[]", "-3")]  // obsolete -NUM, first argument
    [InlineData("n=25 q=0 ops=[]", "-25")]
    [InlineData("n=-2 q=0 ops=[]", "-n", "-2")]  // all but the last 2
    [InlineData("n=-2 q=0 ops=[]", "-n-2")]
    [InlineData("n=-2 q=0 ops=[]", "--lines=-2")]
    [InlineData("n=2 q=0 ops=[]", "-n", "+2")]
    [InlineData("n=0 q=0 ops=[]", "-n", "0")]
    [InlineData("c=5 q=0 ops=[]", "-c", "5")]
    [InlineData("c=5 q=0 ops=[]", "-c5")]
    [InlineData("c=5 q=0 ops=[]", "--bytes=5")]
    [InlineData("c=5 q=0 ops=[]", "--by=5")]  // FIX (was: operands)
    [InlineData("c=-2 q=0 ops=[]", "-c", "-2")]
    [InlineData("c=-2 q=0 ops=[]", "--bytes=-2")]
    [InlineData("c=1024 q=0 ops=[]", "-c", "1K")]  // FIX (was: -c ignored, 10 lines printed)
    [InlineData("c=1024 q=0 ops=[]", "-c1K")]  // FIX (was: operands)
    [InlineData("c=1000 q=0 ops=[]", "-c", "1kB")]  // FIX
    [InlineData("c=512 q=0 ops=[]", "-c", "1b")]  // FIX
    [InlineData("n=1048576 q=0 ops=[]", "-n", "1M")]  // FIX
    [InlineData("n=1000000 q=0 ops=[]", "-n", "1MB")]  // FIX
    [InlineData("n=1024 q=0 ops=[]", "-n", "1KiB")]  // FIX
    [InlineData("n=1073741824 q=0 ops=[]", "-n", "1G")]  // FIX
    [InlineData("n=2147483647 q=0 ops=[]", "-n", "4G")]  // saturates: every real input is shorter
    [InlineData("c=3 q=0 ops=[]", "-n5", "-c3")]  // last of -c / -n wins (was: -c always won)
    [InlineData("n=2 q=0 ops=[]", "-c3", "-n2")]  // FIX (was: -c always won)
    [InlineData("n=2 q=1 ops=[]", "-q", "-n", "2")]
    [InlineData("n=2 q=1 ops=[]", "-qn2")]  // FIX (was: operands [-qn2])
    [InlineData("n=2 q=1 ops=[]", "-qn", "2")]  // FIX
    [InlineData("n=2 q=1 ops=[]", "-n2", "-q")]
    [InlineData("n=10 q=1 ops=[]", "--quiet")]
    [InlineData("n=10 q=1 ops=[]", "--silent")]
    [InlineData("n=10 q=1 ops=[]", "--q")]  // FIX (was: operands)
    [InlineData("n=10 q=1 ops=[]", "--s")]  // FIX
    [InlineData("n=10 q=0 ops=[f]", "f")]
    [InlineData("n=10 q=0 ops=[f,g]", "f", "g")]
    [InlineData("n=2 q=0 ops=[f]", "f", "-n", "2")]  // FIX (was: operands [f,-n,2]... options may follow operands)
    [InlineData("n=5 q=0 ops=[f]", "5", "f")]  // legacy ps-bash: bare leading number = line count
    [InlineData("n=5 q=0 ops=[]", "5")]
    [InlineData("n=10 q=0 ops=[f,5]", "f", "5")]  // only the FIRST operand qualifies
    [InlineData("n=10 q=0 ops=[5]", "--", "5")]  // after `--` nothing is an option or a count
    [InlineData("n=10 q=0 ops=[-n,3]", "--", "-n", "3")]
    [InlineData("n=2 q=0 ops=[-n]", "-n", "2", "--", "-n")]
    [InlineData("n=10 q=0 ops=[-]", "-")]
    [InlineData("n=10 q=0 ops=[--bogus,-v]", "--", "--bogus", "-v")]
    [InlineData("ERR head: invalid number of lines: 'abc'", "-n", "abc")]  // FIX (was: 10 lines, exit 0)
    [InlineData("ERR head: invalid number of lines: 'abc'", "-nabc")]  // FIX
    [InlineData("ERR head: invalid number of lines: '1x'", "-n", "1x")]  // FIX
    [InlineData("ERR head: invalid number of lines: ''", "-n", "")]  // FIX
    [InlineData("ERR head: invalid number of lines: 'x'", "--lines=x")]  // FIX
    [InlineData("ERR head: invalid number of bytes: 'abc'", "-c", "abc")]  // FIX
    [InlineData("ERR head: invalid number of bytes: '1Kx'", "--bytes", "1Kx")]  // FIX
    [InlineData("ERR head: option requires an argument -- 'n'", "-n")]  // FIX (was: silently ignored)
    [InlineData("ERR head: option requires an argument -- 'c'", "-c")]
    [InlineData("ERR head: option requires an argument -- 'n'", "-qn")]
    [InlineData("ERR head: option '--lines' requires an argument", "--lines")]
    [InlineData("ERR head: option '--bytes' requires an argument", "--bytes")]
    [InlineData("ERR head: invalid trailing option -- 5", "-n1", "-5")]  // FIX (was: last wins, n=5)
    [InlineData("ERR head: invalid trailing option -- 2", "-1", "-2")]  // FIX
    [InlineData("ERR head: invalid trailing option -- 5", "f", "-5")]  // FIX
    [InlineData("ERR head: option '-v' is recognized but not supported by ps-bash", "-v")]
    [InlineData("ERR head: option '-v' is recognized but not supported by ps-bash", "-qv")]
    [InlineData("ERR head: option '-z' is recognized but not supported by ps-bash", "-z")]
    [InlineData("ERR head: option '--verbose' is recognized but not supported by ps-bash", "--verbose")]
    [InlineData("ERR head: option '--zero-terminated' is recognized but not supported by ps-bash", "--zero-terminated")]
    [InlineData("ERR head: option '--zero-terminated' is recognized but not supported by ps-bash", "--zero")]  // FIX
    [InlineData("ERR head: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver")]  // GNU: identical
    [InlineData("ERR head: unrecognized option '--bogus'", "--bogus")]
    [InlineData("ERR head: unrecognized option '--bogus=1'", "--bogus=1")]
    [InlineData("ERR head: option '--quiet' doesn't allow an argument", "--quiet=1")]
    [InlineData("ERR head: invalid option -- 'x'", "-x")]  // FIX (was: operands [-x] -> file error)
    [InlineData("ERR head: invalid option -- 'l'", "-l")]
    [InlineData("ERR head: invalid trailing option -- 5", "-q5")]
    [InlineData("ERR head: invalid trailing option -- 5", "-c1", "-q5")]
    [InlineData("ERR head: invalid trailing option -- 2", "-q", "-q", "-2")]
    // Obsolete first argument -NUM[bkmlcqvz]*, every row checked against GNU head 9.4 (byte/line counts measured).
    [InlineData("n=2 q=1 ops=[]", "-2q")]  // FIX (was: invalid option -- '2'... a usage error)
    [InlineData("n=2 q=1 ops=[]", "-2qq")]
    [InlineData("c=2 q=0 ops=[]", "-2c")]
    [InlineData("c=2 q=1 ops=[]", "-2cq")]
    [InlineData("c=2 q=1 ops=[]", "-2qc")]
    [InlineData("n=2 q=1 ops=[]", "-2ql")]
    [InlineData("n=2 q=0 ops=[]", "-2l")]
    [InlineData("c=1024 q=0 ops=[]", "-2b")]  // b/k/m multiply AND select bytes
    [InlineData("c=2048 q=0 ops=[]", "-2k")]
    [InlineData("c=2097152 q=0 ops=[]", "-2m")]
    [InlineData("n=2048 q=0 ops=[]", "-2kl")]  // l selects lines, keeps the multiplier
    [InlineData("n=1024 q=0 ops=[]", "-2bl")]
    [InlineData("c=2 q=0 ops=[]", "-2kc")]  // c resets the multiplier
    [InlineData("c=2048 q=0 ops=[]", "-2ck")]
    [InlineData("n=2 q=0 ops=[]", "-2cl")]
    [InlineData("c=1024 q=0 ops=[]", "-2kb")]  // last multiplier wins
    [InlineData("c=2048 q=0 ops=[]", "-2bk")]
    [InlineData("n=2147483647 q=1 ops=[]", "-12345678901234567890q")]  // saturates (GNU prints everything)
    [InlineData("n=1 q=0 ops=[]", "-2", "-n1")]  // later options still apply
    [InlineData("c=1 q=0 ops=[]", "-5", "-c1")]
    [InlineData("n=2 q=0 ops=[f,g]", "-2", "f", "g")]
    [InlineData("ERR head: option '-v' is recognized but not supported by ps-bash", "-2v")]
    [InlineData("ERR head: option '-z' is recognized but not supported by ps-bash", "-2z")]
    [InlineData("ERR head: invalid trailing option -- n", "-5n")]  // FIX
    [InlineData("ERR head: invalid trailing option -- x", "-2x")]
    [InlineData("ERR head: invalid trailing option -- x", "-2cx")]
    [InlineData("ERR head: invalid trailing option -- x", "-2qx")]
    [InlineData("ERR head: invalid trailing option -- x", "-2xq")]  // the first offending letter
    [InlineData("ERR head: invalid trailing option -- x", "-2x", "-3")]  // the obsolete argument is judged first
    public void Plan_MatchesGnuHead(string expected, params string[] argv)
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
        var p = InvokeBashHeadCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-x", 1)]
    [InlineData("-n", 1)]
    [InlineData("--ver", 1)]
    [InlineData("-v", 2)]
    [InlineData("-z", 2)]
    [InlineData("--verbose", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashHeadCommand.ScanArgs(new[] { arg }).ErrorExitCode);
    }
}
