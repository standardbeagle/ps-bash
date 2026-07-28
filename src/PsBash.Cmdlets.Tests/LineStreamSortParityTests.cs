using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-parity tests for the S2 <c>sort</c> streaming core (<c>SortStage</c>) of the
/// fused-pipeline lane, against the real <c>Invoke-BashSort</c> (the parity oracle —
/// see <see cref="LineStreamParityHarness"/>).
///
/// <para>Coverage: every certified argv form (<c>-r -n -u -f -t -k</c> incl. joined,
/// separated, and bundled spellings), empty input, single line, duplicate keys,
/// unicode, embedded CR (CRLF-sourced), numeric vs lexical ordering, <c>-u</c> dedupe,
/// the KNOWN keyless-<c>-n</c> divergence, the DECLINE matrix, and end-to-end cases
/// through the real fused cmdlet.</para>
/// </summary>
public class LineStreamSortParityTests : LineStreamParityHarness
{
    public LineStreamSortParityTests(SharedPwshFixture fixture) : base(fixture) { }

    private void AssertSort(string[] argv, IEnumerable<string> lines)
        => AssertCoreMatchesCmdlet("sort", "Invoke-BashSort", argv, lines);

    private static readonly string[] Words = { "banana", "Apple", "cherry", "apple", "Banana", "date" };
    private static readonly string[] Numbers = { "10", "9", "100", "2", "33", "1" };
    private static readonly string[] Fields = { "b 2 x", "a 10 y", "c 1 z", "a 3 w", "b 2 a" };
    private static readonly string[] Csv = { "b,2,x", "a,10,y", "c,1,z", "a,3,w" };
    private static readonly string[] Unicode = { "café", "naïve", "🚀 rocket", "zebra", "Ärger", "café" };
    // A CRLF-sourced stream leaves a bare CR on the line when the producer split on
    // \n only — the byte that most often diverges between two comparators.
    private static readonly string[] WithCr = { "beta\r", "alpha\r", "beta\r", "gamma" };

    // ── certified argv × content ─────────────────────────────────────────────

    [Theory]
    [InlineData("")]              // bare (lexical)
    [InlineData("-r")]
    [InlineData("-n")]
    [InlineData("-u")]
    [InlineData("-f")]
    [InlineData("-rn")]           // bundles
    [InlineData("-nu")]
    [InlineData("-rf")]
    [InlineData("-r -n -u")]      // separate flags
    public void SortCore_FlagForms_MatchCmdlet_OnWords(string flags)
        => AssertSort(Split(flags), Words);

    [Theory]
    [InlineData("")]
    [InlineData("-n")]
    [InlineData("-n -r")]
    [InlineData("-nu")]
    public void SortCore_NumericVsLexical_MatchCmdlet(string flags)
        => AssertSort(Split(flags), Numbers);

    [Fact]
    public void SortCore_KeylessNumericOnNonNumericTail_MatchesCmdletNotGnu()
        // Divergence guard. A KEYLESS `-n` parses the WHOLE line in the cmdlet
        // (double.TryParse), so "12abc" sorts as 0; GNU (and the cmdlet's own KEYED
        // path, below) takes the numeric PREFIX and would sort it as 12. The core must
        // reproduce the CMDLET, divergence included — a fused/unfused split is worse
        // than a documented divergence. Porting prefix-parsing here would fail this.
        => AssertSort(new[] { "-n" }, new[] { "12abc", "9", "3x", "100", "7" });

    [Fact]
    public void SortCore_KeyedNumericOnNonNumericTail_MatchesCmdlet()
        // …and the KEYED path uses the numeric-prefix parser instead. Same inputs,
        // different ordering, both pinned to the cmdlet.
        => AssertSort(new[] { "-k1n" }, new[] { "12abc", "9", "3x", "100", "7" });

    [Theory]
    [InlineData("-k2")]
    [InlineData("-k 2")]
    [InlineData("-k2n")]
    [InlineData("-k2,2")]
    [InlineData("-k2r")]
    [InlineData("-k1,1 -k2n")]
    [InlineData("-n -k2")]
    [InlineData("-k2.1")]
    [InlineData("-k2b")]
    public void SortCore_KeySpecForms_MatchCmdlet(string flags)
        => AssertSort(Split(flags), Fields);

    [Theory]
    [InlineData("-t, -k2")]        // joined separator
    [InlineData("-t, -k2n")]
    [InlineData("-t, -k1,1")]
    public void SortCore_JoinedFieldSeparatorForms_MatchCmdlet(string flags)
        => AssertSort(Split(flags), Csv);

    [Fact]
    public void SortCore_SeparatedFieldSeparator_MatchesCmdlet()
        => AssertSort(new[] { "-t", ",", "-k2" }, Csv);

    [Fact]
    public void SortCore_Unicode_MatchesCmdlet() => AssertSort(new[] { "-u" }, Unicode);

    [Fact]
    public void SortCore_UnicodeFoldCase_MatchesCmdlet() => AssertSort(new[] { "-f" }, Unicode);

    [Fact]
    public void SortCore_CarriageReturns_MatchCmdlet() => AssertSort(new[] { "-u" }, WithCr);

    [Fact]
    public void SortCore_DuplicateKeys_TieBreakMatchesCmdlet()
        // Equal -k2 keys must fall through to the whole-line last-resort tie-break in
        // BOTH paths ("b 2 a" before "b 2 x").
        => AssertSort(new[] { "-k2" }, Fields);

    [Fact]
    public void SortCore_EmptyInput_MatchesCmdlet() => AssertSort(new[] { "-n" }, Array.Empty<string>());

    [Fact]
    public void SortCore_SingleLine_MatchesCmdlet() => AssertSort(Array.Empty<string>(), new[] { "solo" });

    [Fact]
    public void SortCore_AllIdenticalLines_MatchesCmdlet()
        => AssertSort(new[] { "-u" }, new[] { "same", "same", "same" });

    [Fact]
    public void SortCore_BlankAndWhitespaceLines_MatchCmdlet()
        => AssertSort(Array.Empty<string>(), new[] { "", "  b", "\ta", "", "z" });

    [Fact]
    public void SortCore_EmptyInput_ProducesNothing()
        => Assert.Empty(RunCore("sort", new[] { "-r" }, Array.Empty<string>()));

    // ── decline matrix ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("-V")]                       // version sort: comparator not ported
    [InlineData("-h")]
    [InlineData("-g")]
    [InlineData("-M")]
    [InlineData("-c")]                       // check mode: an exit-code path
    [InlineData("-b")]
    [InlineData("-d")]
    [InlineData("-s")]
    [InlineData("-i")]
    [InlineData("-rV")]                      // bundle containing a non-ported flag
    [InlineData("--reverse")]                // long forms are never certified
    [InlineData("--numeric-sort")]
    [InlineData("--key=2")]
    [InlineData("-o out.txt")]
    [InlineData("-t")]                       // dangling value flag
    [InlineData("-k")]
    [InlineData("--")]
    [InlineData("file.txt")]                 // file operand → file mode
    [InlineData("-")]                        // explicit stdin operand
    [InlineData("-x")]                       // unknown flag → cmdlet error path
    public void SortCore_UncertifiedArgv_Declines(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("sort", Split(flags), out _),
            $"sort core must DECLINE '{flags}' rather than guess");

    [Fact]
    public void SortCore_EmptyFieldSeparator_Declines()
        // `sort -t ''` is an exit-2 error in the cmdlet, not a sort.
        => Assert.False(LineStreamRegistry.TryCreate("sort", new[] { "-t", "" }, out _));

    // ── end-to-end: the real fused lane ──────────────────────────────────────

    [Fact]
    public void Streamed_SeqSort_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 30 | Invoke-BashSort",
            "@(@('seq','1','30'),@('sort'))");

    [Fact]
    public void Streamed_SeqSortNumericReverse_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 30 | Invoke-BashSort -n -r",
            "@(@('seq','1','30'),@('sort','-n','-r'))");

    [Fact]
    public void Streamed_GrepSort_ByteIdenticalToUnfused()
        // The shape this slice exists for: filter → sort now streams end-to-end with
        // no per-line PSObject.
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 200 | Invoke-BashGrep 1 | Invoke-BashSort",
            "@(@('seq','1','200'),@('grep','1'),@('sort'))");

    [Fact]
    public void Streamed_SortUncertifiedFlag_FallsBackToScriptblock()
    {
        // -V is outside the certified subset → the whole chain must use the fallback,
        // which here does the real work and must still match the unfused pipeline
        // (proving decline → correct fallback, not a throw).
        var inner = "Invoke-BashSeq 1 10 | Invoke-BashSort -V";
        Assert.Equal(
            RenderScript(inner),
            RenderScript($"Invoke-BashFusedPipeline -Stages @(@('seq','1','10'),@('sort','-V')) -Fallback {{ {inner} }}"));
    }
}
