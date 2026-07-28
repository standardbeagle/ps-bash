using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-parity tests for the S2 <c>uniq</c> streaming core (<c>UniqStage</c>) of the
/// fused-pipeline lane, against the real <c>Invoke-BashUniq</c> (the parity oracle —
/// see <see cref="LineStreamParityHarness"/>).
///
/// <para>Coverage: every certified argv form (<c>-c -d -u -i</c> and the value flags
/// <c>-f/-s/-w</c> in separated, joined, and bundled spellings), empty input, single
/// line, adjacent vs non-adjacent duplicates, the <c>-c</c> count column width,
/// unicode, embedded CR, the DECLINE matrix, a laziness probe (uniq is NOT blocking),
/// and end-to-end cases through the real fused cmdlet.</para>
/// </summary>
public class LineStreamUniqParityTests : LineStreamParityHarness
{
    public LineStreamUniqParityTests(SharedPwshFixture fixture) : base(fixture) { }

    private void AssertUniq(string[] argv, IEnumerable<string> lines)
        => AssertCoreMatchesCmdlet("uniq", "Invoke-BashUniq", argv, lines);

    private static readonly string[] Dupes = { "a", "a", "b", "a", "a", "a", "c", "c" };
    private static readonly string[] KeyedFields = { "x aaa", "y aaa", "z bbb", "z bbb", "w aaa" };

    // ── certified argv × content ─────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("-c")]
    [InlineData("-d")]
    [InlineData("-u")]
    [InlineData("-i")]
    [InlineData("-cd")]      // bundles
    [InlineData("-cu")]
    [InlineData("-ci")]
    [InlineData("-c -d")]
    public void UniqCore_FlagForms_MatchCmdlet(string flags)
        => AssertUniq(Split(flags), Dupes);

    [Theory]
    [InlineData("-f 1")]
    [InlineData("-f1")]
    [InlineData("-s 1")]
    [InlineData("-s1")]
    [InlineData("-w 2")]
    [InlineData("-w2")]
    [InlineData("-cf1")]     // bundle whose value flag ends it
    [InlineData("-cw3")]
    public void UniqCore_SkipAndCompareForms_MatchCmdlet(string flags)
        => AssertUniq(Split(flags), KeyedFields);

    [Fact]
    public void UniqCore_CaseFold_MatchesCmdlet()
        => AssertUniq(new[] { "-i" }, new[] { "Alpha", "alpha", "ALPHA", "beta" });

    [Fact]
    public void UniqCore_CountFormat_MatchesCmdlet()
        // The count column is width-7 right-aligned; a >=10 run proves the padding.
        => AssertUniq(new[] { "-c" }, Enumerable.Repeat("rep", 12).Concat(new[] { "tail" }));

    [Fact]
    public void UniqCore_Unicode_MatchesCmdlet()
        => AssertUniq(new[] { "-c" }, new[] { "café", "café", "🚀", "🚀", "naïve" });

    [Fact]
    public void UniqCore_CarriageReturns_MatchCmdlet()
        => AssertUniq(new[] { "-c" }, new[] { "a\r", "a\r", "a", "b\r" });

    [Fact]
    public void UniqCore_EmptyInput_MatchesCmdlet()
        => AssertUniq(new[] { "-c" }, Array.Empty<string>());

    [Fact]
    public void UniqCore_SingleLine_MatchesCmdlet()
        => AssertUniq(new[] { "-c" }, new[] { "solo" });

    [Fact]
    public void UniqCore_NonAdjacentDuplicatesSurvive_MatchesCmdlet()
        // uniq only collapses ADJACENT runs — the classic sort-first requirement.
        => AssertUniq(Array.Empty<string>(), new[] { "a", "b", "a", "b" });

    [Fact]
    public void UniqCore_IsLazy_DoesNotDrainInfiniteProducer()
    {
        // uniq -c is NOT blocking (see UniqStage remarks): taking 3 records from an
        // endless producer must return, not hang. Guards the laziness claim — a
        // buffering re-implementation would hang here instead of failing politely.
        Assert.True(LineStreamRegistry.TryCreate("uniq", new[] { "-c" }, out var stage));
        var taken = stage.Run(Endless()).Take(3).ToList();
        Assert.Equal(3, taken.Count);

        static IEnumerable<string> Endless()
        {
            long i = 0;
            while (true) yield return "line" + (i++);
        }
    }

    // ── decline matrix ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("-D")]                    // cmdlet recovers this from the raw line
    [InlineData("--all-repeated")]
    [InlineData("--ignore-case")]
    [InlineData("--skip-fields=1")]
    [InlineData("--")]
    [InlineData("-z")]
    [InlineData("-C")]                    // uppercase: cmdlet dispatch is case-sensitive
    [InlineData("-f x")]                  // non-integer value
    [InlineData("-w")]                    // dangling value flag
    [InlineData("file.txt")]              // file operand
    [InlineData("-5")]                    // numeric-led token = operand to the cmdlet
    public void UniqCore_UncertifiedArgv_Declines(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("uniq", Split(flags), out _),
            $"uniq core must DECLINE '{flags}' rather than guess");

    // ── end-to-end: the real fused lane ──────────────────────────────────────

    [Fact]
    public void Streamed_GrepSortUniq_TheDominantShape_ByteIdenticalToUnfused()
        // filter → sort → dedupe, end-to-end with no per-line PSObject.
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 200 | Invoke-BashGrep 1 | Invoke-BashSort | Invoke-BashUniq",
            "@(@('seq','1','200'),@('grep','1'),@('sort'),@('uniq'))");

    [Fact]
    public void Streamed_SortUniqCount_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 40 | Invoke-BashSed 's/[0-9]$/X/' | Invoke-BashSort | Invoke-BashUniq -c",
            "@(@('seq','1','40'),@('sed','s/[0-9]$/X/'),@('sort'),@('uniq','-c'))");

    [Fact]
    public void Streamed_UniqUncertifiedFlag_FallsBackToScriptblock()
    {
        var inner = "Invoke-BashSeq 1 20 | Invoke-BashUniq -D";
        Assert.Equal(
            RenderScript(inner),
            RenderScript($"Invoke-BashFusedPipeline -Stages @(@('seq','1','20'),@('uniq','-D')) -Fallback {{ {inner} }}"));
    }
}
