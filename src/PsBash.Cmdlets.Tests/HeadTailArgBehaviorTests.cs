using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end behavior of the head/tail argv migration through the cmdlet, in the shape the
/// transpiler emits (every dash word single-quoted, so it reaches Arguments verbatim). Oracle:
/// GNU coreutils 9.4 (`wsl bash`) — each expectation below was checked there.
/// </summary>
public class HeadTailArgBehaviorTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public HeadTailArgBehaviorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-ht-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private const string Four = "'a','b','c','d'";

    // Flagged cat emits typed CatLine objects; project their BashText for comparison.
    private const string Text = " | ForEach-Object { if ($null -ne $_.BashText) { $_.BashText } else { $_ } }";

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string File1(string content)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, content);
        return p.Replace("'", "''");
    }

    // ── head ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("'-n' abc", "invalid number of lines: 'abc'")]
    [InlineData("'-n1x'", "invalid number of lines: '1x'")]
    [InlineData("'-c' abc", "invalid number of bytes: 'abc'")]
    [InlineData("'-c1kb'", "invalid number of bytes: '1kb'")]
    public void Head_InvalidNumber_IsAUsageErrorExit1(string args, string message)
    {
        Run($"{Four} | Invoke-BashHead {args}").AssertFailed(1, "head: " + message);
    }

    [Fact]
    public void Head_MissingValue_IsAUsageErrorExit1()
        => Run($"{Four} | Invoke-BashHead '-n'").AssertFailed(1, "option requires an argument -- 'n'");

    [Fact]
    public void Head_MisplacedObsoleteNumber_IsAUsageErrorExit1()
        => Run($"{Four} | Invoke-BashHead '-n1' '-5'").AssertFailed(1, "invalid trailing option -- 5");

    [Fact]
    public void Head_UnknownAndUnsupportedFlags_ExitStatuses()
    {
        Run($"{Four} | Invoke-BashHead '-x'").AssertFailed(1, "invalid option -- 'x'");
        Run($"{Four} | Invoke-BashHead '--bogus'").AssertFailed(1, "unrecognized option '--bogus'");
        Run($"{Four} | Invoke-BashHead '-v'").AssertFailed(2, "not supported");
        Run($"{Four} | Invoke-BashHead '--zero'").AssertFailed(2, "'--zero-terminated' is recognized but not supported");
    }

    [Theory]
    [InlineData("'-qn2'", "a,b")]                 // GNU: bundle
    [InlineData("'--li=2'", "a,b")]               // GNU: abbreviation
    [InlineData("'-n' '-1'", "a,b,c")]            // all but the last 1
    [InlineData("'-n' '+2'", "a,b")]
    [InlineData("'-n1K'", "a,b,c,d")]             // multiplier suffix
    [InlineData("'-n5' '-c3'", "a,b")]            // last of -c/-n wins: -c3 = first 3 bytes "a\nb" (now two text records, same bytes)
    [InlineData("'-c3' '-n2'", "a,b")]            // ... and here -n2
    public void Head_Pipeline_MatchesGnu(string args, string expected)
    {
        var r = Run($"{Four} | Invoke-BashHead {args}").AssertSuccess();
        Assert.Equal(expected, string.Join(",", r.Lines));
    }

    [Fact]
    public void Head_File_FlagAfterOperand()
    {
        var f = File1("l1\nl2\nl3\n");
        var r = Run($"Invoke-BashHead '{f}' '-n' 2").AssertSuccess();
        Assert.Equal(new[] { "l1", "l2" }, r.Lines);
    }

    // ── tail ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("'-n' abc", "invalid number of lines: 'abc'")]
    [InlineData("'-n1q'", "invalid number of lines: '1q'")]
    [InlineData("'-c' abc", "invalid number of bytes: 'abc'")]
    [InlineData("'-s' x", "invalid number of seconds: 'x'")]
    [InlineData("'--follow=foo'", "invalid argument 'foo' for '--follow'")]
    public void Tail_InvalidValue_IsAUsageErrorExit1(string args, string message)
    {
        Run($"{Four} | Invoke-BashTail {args}").AssertFailed(1, "tail: " + message);
    }

    [Fact]
    public void Tail_MisplacedObsoleteNumber_IsAUsageErrorExit1()
    {
        Run($"{Four} | Invoke-BashTail '-n1' '-5'").AssertFailed(1, "option used in invalid context -- 5");
        Run($"{Four} | Invoke-BashTail '-5' '-n1'").AssertFailed(1, "option used in invalid context -- 5");
    }

    [Fact]
    public void Tail_UnknownAndUnsupportedFlags_ExitStatuses()
    {
        Run($"{Four} | Invoke-BashTail '-x'").AssertFailed(1, "invalid option -- 'x'");
        Run($"{Four} | Invoke-BashTail '-v'").AssertFailed(2, "not supported");
        Run($"{Four} | Invoke-BashTail '-F'").AssertFailed(2, "not supported");
        Run($"{Four} | Invoke-BashTail '--retry'").AssertFailed(2, "not supported");
        Run($"{Four} | Invoke-BashTail '--pid=1'").AssertFailed(2, "'--pid' is recognized but not supported");
    }

    [Theory]
    [InlineData("'-qn2'", "c,d")]
    [InlineData("'--li=2'", "c,d")]
    [InlineData("'-n' '-2'", "c,d")]              // GNU: a leading - is the same as none (was: negative count)
    [InlineData("'-n' '+2'", "b,c,d")]
    [InlineData("'+2'", "b,c,d")]                 // GNU obsolete `tail +2`
    [InlineData("'-n' 0", "")]                    // GNU: nothing (was: ONE line)
    [InlineData("'-n' '-0'", "")]
    [InlineData("'-n' '+0'", "a,b,c,d")]
    [InlineData("'-n2' '-c1'", "")]               // last of -c/-n wins: -c 1 = the final newline byte
    [InlineData("'-c1' '-n2'", "c,d")]
    public void Tail_Pipeline_MatchesGnu(string args, string expected)
    {
        var r = Run($"{Four} | Invoke-BashTail {args}").AssertSuccess();
        Assert.Equal(expected, string.Join(",", r.Lines));
    }

    [Fact]
    public void Tail_PipelineBytes_WasSilentlyIgnored()
    {
        // `printf 'a\nb\nc\nd\n' | tail -c 3` = "\nd\n" -> the lines "" and "d". Before: the last 10
        // LINES (-c ignored on a pipe).
        var r = Run($"{Four} | Invoke-BashTail '-c' 3").AssertSuccess();
        Assert.Equal(new[] { "", "d" }, r.Lines);
        var r2 = Run($"{Four} | Invoke-BashTail '-c' '+2'").AssertSuccess();
        Assert.Equal(new[] { "", "b", "c", "d" }, r2.Lines);
    }

    [Fact]
    public void Tail_FileBytes_PlusN_IsOneBased()
    {
        // GNU: `tail -c +2` starts AT byte 2 ("bcdef"); the old code skipped 2 bytes ("cdef").
        var f = File1("abcdef");
        var r = Run($"Invoke-BashTail '-c' '+2' '{f}'").AssertSuccess();
        Assert.Equal(new[] { "bcdef" }, r.Lines);
        var r1 = Run($"Invoke-BashTail '-c' '+1' '{f}'").AssertSuccess();
        Assert.Equal(new[] { "abcdef" }, r1.Lines);
    }

    [Fact]
    public void Tail_File_NZero_PrintsNothing()
    {
        var f = File1("x\ny\n");
        Assert.Empty(Run($"Invoke-BashTail '-n' 0 '{f}'").AssertSuccess().Lines);
    }

    [Fact]
    public void Tail_File_FlagAfterOperandAndBundle()
    {
        var f = File1("l1\nl2\nl3\n");
        var r = Run($"Invoke-BashTail '{f}' '-qn' 2").AssertSuccess();
        Assert.Equal(new[] { "l2", "l3" }, r.Lines);
    }

    // ── cat ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Cat_UIsIgnoredAsInGnu_AndBundlesResolve()
    {
        Assert.Equal(new[] { "a", "b" }, Run("'a','b' | Invoke-BashCat '-u'").AssertSuccess().Lines);
        var r = Run("'x' | Invoke-BashCat '-nE'" + Text).AssertSuccess();
        Assert.Equal(new[] { "     1\tx$" }, r.Lines);
    }

    [Fact]
    public void Cat_AbbreviatedLongOptions_MatchGnu()
    {
        // --squeeze = --squeeze-blank (was: a file operand -> "No such file")
        var r = Run("'a','','','b' | Invoke-BashCat '--squeeze'" + Text).AssertSuccess();
        Assert.Equal(new[] { "a", "", "b" }, r.Lines);
        var b = Run("'a','','b' | Invoke-BashCat '-bn'" + Text).AssertSuccess();   // GNU: -b overrides -n
        Assert.Equal(new[] { "     1\ta", "", "     2\tb" }, b.Lines);
    }

    [Fact]
    public void Cat_ErrorsAndExitStatuses()
    {
        Run("'x' | Invoke-BashCat '-A'").AssertFailed(2, "option '-A' is recognized but not supported");
        Run("'x' | Invoke-BashCat '-v'").AssertFailed(2, "not supported");
        Run("'x' | Invoke-BashCat '--bogus'").AssertFailed(1, "unrecognized option '--bogus'");
        Run("'x' | Invoke-BashCat '-x'").AssertFailed(1, "invalid option -- 'x'");
        Run("'x' | Invoke-BashCat '--num'").AssertFailed(1, "option '--num' is ambiguous");
    }

    [Fact]
    public void Cat_DoubleDash_MakesFlagLikeWordsFileNames()
    {
        Run("'x' | Invoke-BashCat '--' '-n'").AssertFailed(1, "-n: No such file or directory"); // a FILE named -n, not a flag
    }

    // ── tac ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Tac_JoinedAbbreviatedAndDanglingSeparator()
    {
        // GNU: -sX / --sep X are the separator (were: file operands); a dangling -s is a usage error.
        Assert.Equal(new[] { "c", "b", "a" }, Run("'a,b,c' | Invoke-BashTac '-s,'" + Text).AssertSuccess().Lines);
        Assert.Equal(new[] { "c", "b", "a" }, Run("'a,b,c' | Invoke-BashTac '--sep' ','" + Text).AssertSuccess().Lines);
        Run("'a' | Invoke-BashTac '-s'").AssertFailed(1, "option requires an argument -- 's'");
        Run("'a' | Invoke-BashTac '-b'").AssertFailed(2, "not supported");
        Run("'a' | Invoke-BashTac '--bogus'").AssertFailed(1, "unrecognized option '--bogus'");
    }

    // ── nl ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Nl_ValuesAreValidated_LikeGnu()
    {
        Run("'a' | Invoke-BashNl '-n' xx").AssertFailed(1, "invalid line numbering format: 'xx'");
        Run("'a' | Invoke-BashNl '-w' x").AssertFailed(1, "invalid line number field width: 'x'");
        Run("'a' | Invoke-BashNl '-bx'").AssertFailed(1, "invalid body numbering style: 'x'");
        Run("'a' | Invoke-BashNl '-bpfoo'").AssertFailed(2, "not supported");
        Run("'a' | Invoke-BashNl '-h' a").AssertFailed(2, "option '-h' is recognized but not supported");
        Run("'a' | Invoke-BashNl '--bogus'").AssertFailed(1, "unrecognized option '--bogus'");
    }

    [Fact]
    public void Nl_LongOptionsAndAbbreviations()
    {
        var r = Run("'a','','b' | Invoke-BashNl '--body' a '--number-f' rz '--number-w' 3 '-v' 5 '-i' 2" + Text).AssertSuccess();
        Assert.Equal(new[] { "005\ta", "007\t", "009\tb" }, r.Lines);
    }

    // ── uniq ────────────────────────────────────────────────────────────────

    private const string Runs = "'a','a','b','c','c'";

    [Fact]
    public void Uniq_AllRepeatedMethods_MatchGnu()
    {
        // oracle: separate -> blank BETWEEN groups; prepend -> blank before EACH group; none/plain -> none.
        Assert.Equal(new[] { "a", "a", "", "c", "c" },
            Run($"{Runs} | Invoke-BashUniq '--all-repeated=separate'" + Text).AssertSuccess().Lines);
        Assert.Equal(new[] { "", "a", "a", "", "c", "c" },
            Run($"{Runs} | Invoke-BashUniq '--all-repeated=prepend'" + Text).AssertSuccess().Lines);
        Assert.Equal(new[] { "a", "a", "c", "c" },
            Run($"{Runs} | Invoke-BashUniq '-D'" + Text).AssertSuccess().Lines);
    }

    [Fact]
    public void Uniq_LongFormsAndValueErrors()
    {
        Assert.Equal(new[] { "      2 a", "      1 b", "      2 c" },
            Run($"{Runs} | Invoke-BashUniq '--count'" + Text).AssertSuccess().Lines);   // was "unrecognized option"
        Run($"{Runs} | Invoke-BashUniq '-f' x").AssertFailed(1, "x: invalid number of fields to skip");
        Run($"{Runs} | Invoke-BashUniq '-cD'").AssertFailed(1, "printing all duplicated lines and repeat counts is meaningless");
        Run($"{Runs} | Invoke-BashUniq '--all-repeated=x'").AssertFailed(1, "invalid argument 'x' for '--all-repeated'");
        Run($"{Runs} | Invoke-BashUniq '-z'").AssertFailed(2, "not supported");
        Run($"{Runs} | Invoke-BashUniq '--bogus'").AssertFailed(1, "unrecognized option '--bogus'");
    }

    [Fact]
    public void Uniq_CheckCharsZero_ComparesNothing_AndObsoleteNumberSkipsFields()
    {
        // oracle: `uniq -w 0` = one line (was unlimited: 2); `uniq -1` = -f 1.
        Assert.Equal(new[] { "a x" }, Run("'a x','b y' | Invoke-BashUniq '-w' 0" + Text).AssertSuccess().Lines);
        Assert.Equal(new[] { "a x" }, Run("'a x','b x' | Invoke-BashUniq '-1'" + Text).AssertSuccess().Lines);
    }
}