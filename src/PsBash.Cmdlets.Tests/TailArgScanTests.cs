using System.Globalization;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure argv-resolution table for tail (shared ordered parser + GNU NUM rules). The pre-migration
/// scan matched only exact tokens and sent everything else — bundles (`-qn2`), abbreviations
/// (`--li=1`, `--fo`), `--follow=name`, unknown flags — into the operand list, silently ignored a
/// non-numeric value (`tail -n abc` printed 10 lines at exit 0) and read `-n -2` as a NEGATIVE
/// count. Each FIX row was checked against GNU tail 9.4 (`wsl bash`): options -c -f -F -n -q -s -v
/// -z + --bytes --lines --follow[=name|descriptor] --quiet --silent --sleep-interval --verbose
/// --zero-terminated --retry --pid --max-unchanged-stats (+ --help --version); NUM takes a sign
/// (`+N` = from the start) and a multiplier suffix; the last of -c/-n wins; the obsolete -NUM is
/// only valid as the first argument and alone; the obsolete `+NUM` first argument means -n +NUM.
/// Accepted here: -c -f -n -q -s (+ long forms). Refused loudly as valid-but-unsupported (exit 2):
/// -v/--verbose, -z/--zero-terminated, -F, --retry, --pid, --max-unchanged-stats. Usage errors are
/// exit 1 (GNU EXIT_FAILURE). A digit in option position (`-1n`, `-q5`) is GNU's "option used in
/// invalid context -- N". The legacy ps-bash extension `tail 5` (bare
/// leading number = line count) is preserved because the Pester gate pins it.
/// </summary>
public class TailArgScanTests
{
    private static string Scan(string[] argv)
    {
        var t = InvokeBashTailCommand.Plan(argv);
        if (t.Parsed.Error is { } e) return "ERR " + e.Message("tail");
        if (t.Error is { } m) return "ERR " + m;
        static int B(bool b) => b ? 1 : 0;
        string mode = t.BytesMode
            ? "c=" + (t.BytesFromStart ? "+" : "") + t.ByteCount
            : "n=" + (t.FromLine ? "+" : "") + t.Count;
        return $"{mode} f={B(t.Follow)} s={t.SleepInterval.ToString(CultureInfo.InvariantCulture)} " +
               $"q={B(t.Parsed.Has("quiet"))} ops=[{string.Join(",", t.Operands)}]";
    }

    [Theory]
    [InlineData("n=10 f=0 s=1 q=0 ops=[]")]
    [InlineData("n=3 f=0 s=1 q=0 ops=[]", "-n", "3")]
    [InlineData("n=3 f=0 s=1 q=0 ops=[]", "-n3")]
    [InlineData("n=3 f=0 s=1 q=0 ops=[]", "--lines=3")]
    [InlineData("n=3 f=0 s=1 q=0 ops=[]", "--lines", "3")]
    [InlineData("n=3 f=0 s=1 q=0 ops=[]", "--li=3")]  // FIX (was: operands)
    [InlineData("n=3 f=0 s=1 q=0 ops=[]", "--l", "3")]  // FIX
    [InlineData("n=3 f=0 s=1 q=0 ops=[]", "-3")]  // obsolete -NUM, first argument, alone
    [InlineData("n=25 f=0 s=1 q=0 ops=[f]", "-25", "f")]
    [InlineData("n=+2 f=0 s=1 q=0 ops=[]", "-n", "+2")]
    [InlineData("n=+2 f=0 s=1 q=0 ops=[]", "-n+2")]
    [InlineData("n=+2 f=0 s=1 q=0 ops=[]", "--lines=+2")]
    [InlineData("n=+0 f=0 s=1 q=0 ops=[]", "-n", "+0")]
    [InlineData("n=2 f=0 s=1 q=0 ops=[]", "-n", "-2")]  // FIX (was: count -2 -> buffer of 1, wrong output)
    [InlineData("n=2 f=0 s=1 q=0 ops=[]", "-n-2")]  // FIX
    [InlineData("n=0 f=0 s=1 q=0 ops=[]", "-n", "0")]
    [InlineData("n=0 f=0 s=1 q=0 ops=[]", "-n", "-0")]
    [InlineData("n=+2 f=0 s=1 q=0 ops=[]", "+2")]  // FIX: GNU obsolete `tail +2` (was: a file named +2)
    [InlineData("n=+2 f=0 s=1 q=0 ops=[f]", "+2", "f")]  // FIX
    [InlineData("n=10 f=0 s=1 q=0 ops=[f,+2]", "f", "+2")]  // only as the very first argument
    [InlineData("n=10 f=0 s=1 q=0 ops=[+2]", "--", "+2")]
    [InlineData("n=5 f=0 s=1 q=0 ops=[]", "5")]  // legacy ps-bash: bare leading number = line count
    [InlineData("n=5 f=0 s=1 q=0 ops=[f]", "5", "f")]
    [InlineData("n=10 f=0 s=1 q=0 ops=[f,5]", "f", "5")]
    [InlineData("n=10 f=0 s=1 q=0 ops=[5]", "--", "5")]
    [InlineData("c=5 f=0 s=1 q=0 ops=[]", "-c", "5")]
    [InlineData("c=5 f=0 s=1 q=0 ops=[]", "-c5")]
    [InlineData("c=5 f=0 s=1 q=0 ops=[]", "--bytes=5")]
    [InlineData("c=5 f=0 s=1 q=0 ops=[]", "--by=5")]  // FIX
    [InlineData("c=+2 f=0 s=1 q=0 ops=[]", "-c", "+2")]
    [InlineData("c=+2 f=0 s=1 q=0 ops=[]", "-c+2")]
    [InlineData("c=2 f=0 s=1 q=0 ops=[]", "-c", "-2")]  // FIX (was: ignored)
    [InlineData("c=1024 f=0 s=1 q=0 ops=[]", "-c", "1K")]  // FIX (was: -c ignored)
    [InlineData("c=1000 f=0 s=1 q=0 ops=[]", "-c1kB")]  // FIX
    [InlineData("n=2 f=0 s=1 q=0 ops=[]", "-c3", "-n2")]  // last of -c / -n wins
    [InlineData("c=3 f=0 s=1 q=0 ops=[]", "-n2", "-c3")]
    [InlineData("n=1024 f=0 s=1 q=0 ops=[]", "-n", "1K")]  // FIX
    [InlineData("n=2 f=0 s=1 q=1 ops=[]", "-qn2")]  // FIX (was: operands)
    [InlineData("n=2 f=0 s=1 q=1 ops=[]", "-q", "-n", "2")]
    [InlineData("n=10 f=0 s=1 q=1 ops=[]", "--quiet")]
    [InlineData("n=10 f=0 s=1 q=1 ops=[]", "--silent")]
    [InlineData("n=10 f=0 s=1 q=1 ops=[]", "--q")]  // FIX
    [InlineData("n=10 f=1 s=1 q=0 ops=[]", "-f")]
    [InlineData("n=10 f=1 s=1 q=0 ops=[]", "--follow")]
    [InlineData("n=10 f=1 s=1 q=0 ops=[]", "--follow=name")]  // FIX (was: operands [--follow=name])
    [InlineData("n=10 f=1 s=1 q=0 ops=[]", "--follow=descriptor")]  // FIX
    [InlineData("n=10 f=1 s=1 q=0 ops=[]", "--follow=n")]  // FIX: argmatch prefix
    [InlineData("n=10 f=1 s=1 q=0 ops=[]", "--fo")]  // FIX
    [InlineData("n=10 f=1 s=1 q=0 ops=[]", "--f")]  // FIX
    [InlineData("n=2 f=1 s=1 q=0 ops=[]", "-fn2")]  // FIX (was: operands)
    [InlineData("n=2 f=1 s=1 q=1 ops=[f]", "-qfn", "2", "f")]  // FIX
    [InlineData("n=10 f=1 s=2 q=0 ops=[f]", "-f", "-s", "2", "f")]
    [InlineData("n=10 f=0 s=1 q=0 ops=[f]", "f")]
    [InlineData("n=10 f=0 s=1 q=0 ops=[f,g]", "f", "g")]
    [InlineData("n=2 f=0 s=1 q=0 ops=[f]", "f", "-n", "2")]  // options may follow operands
    [InlineData("n=10 f=0 s=2 q=0 ops=[]", "-s", "2")]
    [InlineData("n=10 f=0 s=1 q=0 ops=[]", "-s1")]
    [InlineData("n=10 f=0 s=0.5 q=0 ops=[]", "-s0.5")]  // FIX (was: -s0.5 ok, but -s 0.5x silently ignored)
    [InlineData("n=10 f=0 s=3 q=0 ops=[]", "--sleep-interval=3")]
    [InlineData("n=10 f=0 s=3 q=0 ops=[]", "--sl", "3")]  // FIX
    [InlineData("n=10 f=0 s=1 q=0 ops=[-n,3]", "--", "-n", "3")]
    [InlineData("n=10 f=0 s=1 q=0 ops=[-]", "-")]
    [InlineData("ERR tail: invalid number of lines: 'abc'", "-n", "abc")]  // FIX (was: 10 lines, exit 0)
    [InlineData("ERR tail: invalid number of lines: '1q'", "-n1q")]  // FIX
    [InlineData("ERR tail: invalid number of lines: '1x'", "--lines=1x")]  // FIX
    [InlineData("ERR tail: invalid number of lines: ''", "-n", "")]  // FIX
    [InlineData("ERR tail: invalid number of bytes: 'abc'", "-c", "abc")]  // FIX
    [InlineData("ERR tail: invalid number of seconds: 'x'", "-s", "x")]  // FIX
    [InlineData("ERR tail: invalid number of seconds: '-1'", "-s", "-1")]
    [InlineData("ERR tail: option requires an argument -- 'n'", "-n")]  // FIX (was: silently ignored)
    [InlineData("ERR tail: option requires an argument -- 's'", "-s")]
    [InlineData("ERR tail: option '--lines' requires an argument", "--lines")]
    [InlineData("ERR tail: option '--sleep-interval' requires an argument", "--sleep-interval")]
    [InlineData("ERR tail: invalid argument 'foo' for '--follow'\nValid arguments are:\n  - 'descriptor'\n  - 'name'", "--follow=foo")]  // FIX
    [InlineData("ERR tail: option used in invalid context -- 5", "-n1", "-5")]  // FIX (was: last wins)
    [InlineData("ERR tail: option used in invalid context -- 5", "-5", "-n1")]  // FIX
    [InlineData("ERR tail: option used in invalid context -- 5", "f", "-5")]  // FIX
    [InlineData("ERR tail: option '-v' is recognized but not supported by ps-bash", "-v")]
    [InlineData("ERR tail: option '-z' is recognized but not supported by ps-bash", "-z")]
    [InlineData("ERR tail: option '-F' is recognized but not supported by ps-bash", "-F")]
    [InlineData("ERR tail: option '--verbose' is recognized but not supported by ps-bash", "--verbose")]
    [InlineData("ERR tail: option '--zero-terminated' is recognized but not supported by ps-bash", "--zero")]  // FIX
    [InlineData("ERR tail: option '--retry' is recognized but not supported by ps-bash", "--retry")]
    [InlineData("ERR tail: option '--pid' is recognized but not supported by ps-bash", "--pid=1")]
    [InlineData("ERR tail: option '--pid' is recognized but not supported by ps-bash", "--pi", "1")]  // FIX
    [InlineData("ERR tail: option '--max-unchanged-stats' is recognized but not supported by ps-bash", "--max-unchanged-stats=1")]
    [InlineData("ERR tail: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", "--ver")]  // GNU: identical
    [InlineData("ERR tail: option '--s' is ambiguous; possibilities: '--silent' '--sleep-interval'", "--s")]  // GNU: identical
    [InlineData("ERR tail: unrecognized option '--bogus'", "--bogus")]
    [InlineData("ERR tail: option '--quiet' doesn't allow an argument", "--quiet=1")]
    [InlineData("ERR tail: invalid option -- 'x'", "-x")]  // FIX (was: operands [-x] -> file error)
    [InlineData("ERR tail: option used in invalid context -- 1", "-1n")]  // FIX (was: invalid option -- '1')
    [InlineData("ERR tail: option used in invalid context -- 1", "-1q")]
    [InlineData("ERR tail: option used in invalid context -- 1", "-12x")]  // the FIRST digit, as getopt returns it
    [InlineData("ERR tail: option used in invalid context -- 2", "-2k")]  // GNU tail has no k/m multiplier letters
    [InlineData("ERR tail: option used in invalid context -- 2", "-2bx")]
    [InlineData("ERR tail: option used in invalid context -- 5", "-q5")]
    [InlineData("ERR tail: option used in invalid context -- 5", "-f5")]
    [InlineData("ERR tail: option used in invalid context -- 2", "-2", "-q")]  // obsolete only when nothing else is an option
    [InlineData("ERR tail: option used in invalid context -- 2", "-2", "-f")]
    [InlineData("ERR tail: option used in invalid context -- 2", "-2", "a", "b")]  // ... and at most one file
    [InlineData("ERR tail: option used in invalid context -- 2", "-2", "a", "--")]
    [InlineData("ERR tail: option used in invalid context -- 2", "-2", "--", "a", "b")]
    [InlineData("ERR tail: option used in invalid context -- 2", "-2c", "a", "b")]
    [InlineData("ERR tail: invalid option -- 'x'", "-x", "-2")]  // getopt order: the earlier error wins
    [InlineData("ERR tail: option used in invalid context -- 2", "-2", "-x")]
    // Obsolete -NUM[bcl][f] / +NUM[bcl][f] (every row checked against GNU tail 9.4).
    [InlineData("c=2 f=0 s=1 q=0 ops=[]", "-2c")]
    [InlineData("n=2 f=0 s=1 q=0 ops=[]", "-2l")]
    [InlineData("c=1024 f=0 s=1 q=0 ops=[]", "-2b")]  // b = 512-byte blocks, bytes mode
    [InlineData("n=2 f=1 s=1 q=0 ops=[]", "-2f")]
    [InlineData("c=2 f=1 s=1 q=0 ops=[]", "-2cf")]
    [InlineData("n=2 f=1 s=1 q=0 ops=[f]", "-2lf", "f")]
    [InlineData("c=1024 f=1 s=1 q=0 ops=[]", "-2bf")]
    [InlineData("c=+2 f=0 s=1 q=0 ops=[]", "+2c")]
    [InlineData("n=+2 f=0 s=1 q=0 ops=[]", "+2l")]
    [InlineData("c=+1024 f=0 s=1 q=0 ops=[]", "+2b")]
    [InlineData("n=+2 f=1 s=1 q=0 ops=[f]", "+2f", "f")]
    [InlineData("n=2 f=0 s=1 q=0 ops=[]", "-2", "--")]
    [InlineData("n=2 f=0 s=1 q=0 ops=[-]", "-2", "-")]
    [InlineData("n=2 f=0 s=1 q=0 ops=[f]", "-2", "--", "f")]
    [InlineData("n=2 f=0 s=1 q=0 ops=[-x]", "-2", "--", "-x")]
    [InlineData("n=10 f=0 s=1 q=0 ops=[+2,f,g]", "+2", "f", "g")]  // +NUM with 2+ files is a FILE named +2
    [InlineData("n=1 f=0 s=1 q=0 ops=[+2]", "+2", "-n1")]  // ... and so is +NUM followed by an option
    [InlineData("n=10 f=0 s=1 q=1 ops=[+2]", "+2", "-q")]
    public void Plan_MatchesGnuTail(string expected, params string[] argv)
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
        var p = InvokeBashTailCommand.ScanArgs(new[] { arg });
        Assert.Null(p.Error);
        Assert.True(p.Has(id));
    }

    [Theory]
    [InlineData("--bogus", 1)]
    [InlineData("-x", 1)]
    [InlineData("-n", 1)]
    [InlineData("--s", 1)]
    [InlineData("-v", 2)]
    [InlineData("-F", 2)]
    [InlineData("--retry", 2)]
    [InlineData("--pid=1", 2)]
    public void ScanError_ExitStatus_IsGnuUsageStatusExceptOurOwnRefusal(string arg, int exit)
    {
        Assert.Equal(exit, InvokeBashTailCommand.ScanArgs(new[] { arg }).ErrorExitCode);
    }

    // FusedLane follow guard: an abbreviated --follow must be recognised as unbounded (before the
    // shared parser `--fo` was a file operand and could not hang).
    [Theory]
    [InlineData("--follow", true)]
    [InlineData("--follow=name", true)]
    [InlineData("--fo", true)]
    [InlineData("--f", true)]
    [InlineData("--foll=name", true)]
    [InlineData("--lines=3", false)]
    [InlineData("--fx", false)]
    [InlineData("--", false)]
    public void TailStage_DeclinesEveryFollowSpelling(string arg, bool follow)
    {
        var created = LineStreamRegistry.TryCreate("tail", new[] { arg }, out _);
        if (follow) Assert.False(created);
    }
}
