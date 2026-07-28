using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-parity tests for the S3 <c>tac</c> and <c>nl</c> streaming cores against the real
/// <c>Invoke-BashTac</c> / <c>Invoke-BashNl</c> (the parity oracles).
///
/// <para><c>tac</c> is the lane's SECOND blocking core (after <c>sort</c>): reversing needs
/// the last line before it can emit the first, so a downstream <c>head</c> cannot early-exit
/// past it. <c>nl</c> is a lazy per-line transform whose numbering column must match the
/// cmdlet byte-for-byte — width, padding, separator, and the unnumbered-empty-line rule.</para>
/// </summary>
public class LineStreamTacNlParityTests : LineStreamParityHarness
{
    public LineStreamTacNlParityTests(SharedPwshFixture fixture) : base(fixture) { }

    private void AssertTac(string[] argv, IEnumerable<string> lines)
        => AssertCoreMatchesCmdlet("tac", "Invoke-BashTac", argv, lines);

    private void AssertNl(string[] argv, IEnumerable<string> lines)
        => AssertCoreMatchesCmdlet("nl", "Invoke-BashNl", argv, lines);

    private static readonly string[] Lines = { "alpha", "beta", "gamma", "delta" };
    private static readonly string[] WithBlanks = { "a", "", "b", "", "", "c" };

    // ── tac ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TacCore_Reverses_MatchesCmdlet() => AssertTac(Array.Empty<string>(), Lines);

    [Fact]
    public void TacCore_EmptyInput_MatchesCmdlet() => AssertTac(Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void TacCore_SingleLine_MatchesCmdlet() => AssertTac(Array.Empty<string>(), new[] { "solo" });

    [Fact]
    public void TacCore_BlankLines_MatchCmdlet() => AssertTac(Array.Empty<string>(), WithBlanks);

    [Fact]
    public void TacCore_Unicode_MatchesCmdlet()
        => AssertTac(Array.Empty<string>(), new[] { "café", "naïve", "🚀 rocket" });

    [Fact]
    public void TacCore_Separator_MatchesCmdlet()
        => AssertTac(new[] { "-s", "," }, new[] { "a,b,c", "d,e" });

    [Fact]
    public void TacCore_LongSeparator_MatchesCmdlet()
        => AssertTac(new[] { "--separator=," }, new[] { "a,b,c", "d,e" });

    [Fact]
    public void TacCore_IsBlocking_BuffersWholeInput()
    {
        // Documented cost of reversal: tac cannot yield before its producer ends, so a
        // downstream head cannot early-exit past it. Pinned so a future "optimization"
        // that quietly changes the contract fails here.
        Assert.True(LineStreamRegistry.TryCreate("tac", Array.Empty<string>(), out var stage));
        int pulled = 0;
        var taken = stage.Run(Counted(10, () => pulled++)).Take(1).ToList();
        Assert.Single(taken);
        Assert.Equal("line9", taken[0]);
        Assert.Equal(10, pulled);   // the WHOLE producer was drained for ONE output line

        static IEnumerable<string> Counted(int n, Action tick)
        {
            for (int i = 0; i < n; i++) { tick(); yield return "line" + i; }
        }
    }

    [Theory]
    [InlineData("-r")]
    [InlineData("-b")]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("-s")]                    // dangling value flag
    [InlineData("file.txt")]              // file operand → file mode
    public void TacCore_UncertifiedArgv_Declines(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("tac", Split(flags), out _),
            $"tac core must DECLINE '{flags}' rather than guess");

    // ── nl ───────────────────────────────────────────────────────────────────

    [Fact]
    public void NlCore_Default_MatchesCmdlet() => AssertNl(Array.Empty<string>(), Lines);

    [Fact]
    public void NlCore_DefaultSkipsBlankNumbering_MatchesCmdlet()
        // -b t (default): an empty line is emitted BARE, and does not consume a number.
        => AssertNl(Array.Empty<string>(), WithBlanks);

    [Theory]
    [InlineData("-ba")]
    [InlineData("-b a")]
    [InlineData("-bn")]
    [InlineData("-b n")]
    [InlineData("-bt")]
    public void NlCore_BodyStyles_MatchCmdlet(string flags)
        => AssertNl(Split(flags), WithBlanks);

    [Theory]
    [InlineData("-nln")]
    [InlineData("-nrn")]
    [InlineData("-nrz")]
    [InlineData("-n ln")]
    [InlineData("-n rz")]
    public void NlCore_NumberStyles_MatchCmdlet(string flags)
        => AssertNl(Split(flags), Lines);

    [Theory]
    [InlineData("-w3")]
    [InlineData("-w10")]
    [InlineData("-v5")]
    [InlineData("-i2")]
    [InlineData("-s:")]
    [InlineData("-s ::")]
    public void NlCore_WidthStartIncrementSeparator_MatchCmdlet(string flags)
        => AssertNl(Split(flags), Lines);

    [Fact]
    public void NlCore_TenthLinePadding_MatchesCmdlet()
        // The 6-wide right-aligned column shifts at 10 — pins the padding.
        => AssertNl(Array.Empty<string>(), Enumerable.Range(1, 12).Select(i => "l" + i));

    [Fact]
    public void NlCore_EmptyInput_MatchesCmdlet() => AssertNl(Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void NlCore_Unicode_MatchesCmdlet()
        => AssertNl(Array.Empty<string>(), new[] { "café", "🚀 rocket" });

    [Fact]
    public void NlCore_IsLazy_DoesNotDrainInfiniteProducer()
    {
        Assert.True(LineStreamRegistry.TryCreate("nl", Array.Empty<string>(), out var stage));
        Assert.Equal(3, stage.Run(Endless()).Take(3).Count());

        static IEnumerable<string> Endless()
        {
            long i = 0;
            while (true) yield return "line" + (i++);
        }
    }

    [Theory]
    [InlineData("-w 3")]                  // bare -w: binder-bound decoy parameter, not argv
    [InlineData("-v 5")]
    [InlineData("-i 2")]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("--number-width=3")]
    [InlineData("--")]
    [InlineData("-z")]
    [InlineData("file.txt")]              // file operand → file mode
    public void NlCore_UncertifiedArgv_Declines(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("nl", Split(flags), out _),
            $"nl core must DECLINE '{flags}' rather than guess");
}
