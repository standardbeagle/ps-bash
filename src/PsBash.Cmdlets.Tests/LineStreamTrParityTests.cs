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
/// splitting core fails them instead of quietly passing. A table that touches the newline itself is
/// declined by the core (see <c>TrCore_NewlineTouchingTable_Declines</c>).</para>
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
        => AssertTr(new[] { "-d", "[:digit:]" }, Mixed);

    [Fact]
    public void TrCore_SqueezeOnly_MatchesCmdlet()
        => AssertTr(new[] { "-s", " " }, Mixed);

    [Fact]
    public void TrCore_SqueezeAfterTranslate_MatchesCmdlet()
        => AssertTr(new[] { "-s", "a-z", "A-Z" }, Mixed);

    [Fact]
    public void TrCore_Complement_MatchesCmdlet()
        // SET1 names the newline, so the complement leaves the terminator alone.
        => AssertTr(new[] { "-c", "[:alpha:]\\n", "." }, Mixed);

    [Fact]
    public void TrCore_ComplementDelete_MatchesCmdlet()
        => AssertTr(new[] { "-cd", "[:alpha:]\\n" }, Mixed);

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
    public void TrCore_WholeRecord_DoesNotAddALine_MatchesCmdlet()
        // A no-op translate must not change the RECORD COUNT
        // (`printf "a\nb\n" | tr x y | wc -l` answered 3, not 2).
        => AssertTr(new[] { "x", "y" }, new[] { "a\n", "b\n" });

    [Fact]
    public void TrCore_WholeRecord_PipedToWc_CountsTwoLines()
    {
        // The `... | wc -l` half of that regression, composed through the streaming cores
        // exactly as the fused lane composes them, and diffed against the same two cmdlets in
        // a real pipeline.
        var input = new[] { "a\n", "b\n" };
        var expected = RenderScript(
            "@(('a' + [char]10), ('b' + [char]10)) | Invoke-BashTr x y | Invoke-BashWc -l");

        Assert.True(LineStreamRegistry.TryCreate("tr", new[] { "x", "y" }, out var tr));
        Assert.True(LineStreamRegistry.TryCreate("wc", new[] { "-l" }, out var wc));
        var actual = string.Concat(
            wc.Run(tr.Run(input)).Select(l => l + Environment.NewLine));

        Assert.Equal(expected, actual);
        // The count itself is the regression: a record-splitting tr answers 4.
        Assert.Equal("2", actual.Trim());
    }

    // ── newline-touching tables: the cmdlet owns them ─────────────────────────
    // Records reach tr WITHOUT their terminator (seq/cat/echo emit bare lines; the serializer adds
    // the "\n"), so deleting / translating / squeezing "\n" is only right on the reconstructed byte
    // stream — which the line-stream core does not have. It must DECLINE and let the cmdlet (which
    // appends the terminator itself) answer. End-to-end bytes are pinned in EscapeExpansionTests
    // (`seq 1 3 | tr -d '\n'` is 123).

    [Theory]
    [InlineData("-d", "\\n")]
    [InlineData("-d", "[:space:]")]
    [InlineData("-cd", "[:alpha:]")]      // the complement deletes the newline
    [InlineData("-c", "[:alpha:]", ".")]  // ... or translates it
    [InlineData("\\n", ",")]
    [InlineData("-s", "\\n")]
    [InlineData("-s", "[:space:]", " ")]
    public void TrCore_NewlineTouchingTable_Declines(params string[] argv)
        => Assert.False(LineStreamRegistry.TryCreate("tr", argv, out _),
            $"tr core must DECLINE newline-touching '{string.Join(' ', argv)}'");

    [Theory]
    [InlineData("-cd", "[:alpha:]\\n")]  // SET1 names the newline: it is kept
    [InlineData("\\r", "\\n")]           // INTO a newline leaves the terminator alone
    [InlineData("-s", " ")]
    public void TrCore_NewlineUntouchingTable_StillStreams(params string[] argv)
        => Assert.True(LineStreamRegistry.TryCreate("tr", argv, out _));

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
