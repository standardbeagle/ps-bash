using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-parity tests for the S3 <c>tr</c> streaming core (<c>TrStage</c>) against the
/// real <c>Invoke-BashTr</c> (the parity oracle — see <see cref="LineStreamParityHarness"/>).
///
/// <para><b>The whole-record regression is the headline here.</b> <c>tr</c> is a byte-stream
/// filter: the newline is an ORDINARY translatable character, not a record boundary. A core
/// that split each record on <c>\n</c> first would (a) ADD a line and (b) make <c>\n</c>
/// untranslatable, turning <c>tr -d '\n'</c> into a silent no-op — the exact historical bug
/// documented on the <c>tr</c> row of <c>docs/specs/runtime-command-reference.md</c>. The
/// <c>WholeRecord_*</c> tests below feed records that CARRY a trailing <c>\n</c>, so a
/// splitting core fails them instead of quietly passing.</para>
/// </summary>
public class LineStreamTrParityTests : LineStreamParityHarness
{
    public LineStreamTrParityTests(SharedPwshFixture fixture) : base(fixture) { }

    private void AssertTr(string[] argv, IEnumerable<string> lines)
        => AssertCoreMatchesCmdlet("tr", "Invoke-BashTr", argv, lines);

    private static readonly string[] Mixed = { "Hello World", "abc def", "AAA   bbb", "x1y2z3" };

    // ── certified argv × content ─────────────────────────────────────────────

    [Fact]
    public void TrCore_Translate_MatchesCmdlet()
        => AssertTr(new[] { "a-z", "A-Z" }, Mixed);

    [Fact]
    public void TrCore_SingleCharTranslate_MatchesCmdlet()
        => AssertTr(new[] { " ", "_" }, Mixed);

    [Fact]
    public void TrCore_PosixClasses_MatchCmdlet()
        => AssertTr(new[] { "[:lower:]", "[:upper:]" }, Mixed);

    [Fact]
    public void TrCore_DigitClassToHash_MatchesCmdlet()
        => AssertTr(new[] { "[:digit:]", "#" }, Mixed);

    [Fact]
    public void TrCore_Delete_MatchesCmdlet()
        => AssertTr(new[] { "-d", "aeiou" }, Mixed);

    [Fact]
    public void TrCore_DeleteClass_MatchesCmdlet()
        => AssertTr(new[] { "-d", "[:space:]" }, Mixed);

    [Fact]
    public void TrCore_SqueezeOnly_MatchesCmdlet()
        => AssertTr(new[] { "-s", " " }, Mixed);

    [Fact]
    public void TrCore_SqueezeAfterTranslate_MatchesCmdlet()
        => AssertTr(new[] { "-s", "a-z", "A-Z" }, Mixed);

    [Fact]
    public void TrCore_Complement_MatchesCmdlet()
        => AssertTr(new[] { "-c", "[:alpha:]", "." }, Mixed);

    [Fact]
    public void TrCore_ComplementDelete_MatchesCmdlet()
        => AssertTr(new[] { "-cd", "[:alpha:]" }, Mixed);

    [Fact]
    public void TrCore_TruncateSet1_MatchesCmdlet()
        => AssertTr(new[] { "-t", "abc", "xyzw" }, Mixed);

    [Fact]
    public void TrCore_ShortSet2Repeats_MatchesCmdlet()
        // SET2 shorter than SET1: the last SET2 char repeats (oracle behavior).
        => AssertTr(new[] { "abc", "x" }, Mixed);

    [Fact]
    public void TrCore_LongFormFlags_MatchCmdlet()
        => AssertTr(new[] { "--delete", "aeiou" }, Mixed);

    [Fact]
    public void TrCore_EmptyInput_MatchesCmdlet()
        => AssertTr(new[] { "a", "b" }, Array.Empty<string>());

    [Fact]
    public void TrCore_Unicode_MatchesCmdlet()
        => AssertTr(new[] { "a-z", "A-Z" }, new[] { "café", "naïve", "🚀 rocket" });

    [Fact]
    public void TrCore_CarriageReturn_MatchesCmdlet()
        => AssertTr(new[] { "-d", "\\r" }, new[] { "a\r", "b\r", "c" });

    // ── WHOLE-RECORD semantics (the historical bug) ──────────────────────────

    [Fact]
    public void TrCore_WholeRecord_DeleteNewline_MatchesCmdlet()
        // `printf 'a\nb\n' | tr -d '\n'` — records CARRY their trailing newline here.
        // A core that split the record first would emit 4 pieces where the cmdlet
        // emits 2, and would never see the '\n' to delete.
        => AssertTr(new[] { "-d", "\\n" }, new[] { "a\n", "b\n" });

    [Fact]
    public void TrCore_WholeRecord_TranslateNewline_MatchesCmdlet()
        => AssertTr(new[] { "\\n", "," }, new[] { "a\n", "b\n" });

    [Fact]
    public void TrCore_WholeRecord_DoesNotAddALine_MatchesCmdlet()
        // The other half of the bug: a no-op translate must not change the RECORD
        // COUNT (`printf "a\nb\n" | tr x y | wc -l` answered 3, not 2).
        => AssertTr(new[] { "x", "y" }, new[] { "a\n", "b\n" });

    [Fact]
    public void TrCore_WholeRecord_PipedToWc_CountsTwoLines()
    {
        // The `... | wc -l` half of the required regression, composed through the
        // streaming cores exactly as the fused lane composes them, and diffed against
        // the same two cmdlets in a real pipeline.
        var input = new[] { "a\n", "b\n" };
        var expected = RenderScript(
            "@(('a' + [char]10), ('b' + [char]10)) | Invoke-BashTr -d ([char]10) | Invoke-BashWc -l");

        Assert.True(LineStreamRegistry.TryCreate("tr", new[] { "-d", "\\n" }, out var tr));
        Assert.True(LineStreamRegistry.TryCreate("wc", new[] { "-l" }, out var wc));
        var actual = string.Concat(
            wc.Run(tr.Run(input)).Select(l => l + Environment.NewLine));

        Assert.Equal(expected, actual);
        // The count itself is the regression: a record-splitting tr answers 4.
        Assert.Equal("2", actual.Trim());
    }

    [Fact]
    public void TrCore_IsLazy_DoesNotDrainInfiniteProducer()
    {
        // tr is a pure per-record transform — never blocking.
        Assert.True(LineStreamRegistry.TryCreate("tr", new[] { "a", "b" }, out var stage));
        Assert.Equal(3, stage.Run(Endless()).Take(3).Count());

        static IEnumerable<string> Endless()
        {
            long i = 0;
            while (true) yield return "line" + (i++);
        }
    }

    // ── decline matrix ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("-z")]                 // unknown short flag → cmdlet emits the error
    [InlineData("--bogus")]            // unknown long flag → cmdlet emits the error
    public void TrCore_UncertifiedArgv_Declines(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("tr", Split(flags), out _),
            $"tr core must DECLINE '{flags}' rather than guess");

    [Fact]
    public void TrCore_ReverseRange_Declines()
        // `tr 'z-a' x` is exit-1 with a message the streaming lane has no stderr for.
        => Assert.False(LineStreamRegistry.TryCreate("tr", new[] { "z-a", "x" }, out _));
}
