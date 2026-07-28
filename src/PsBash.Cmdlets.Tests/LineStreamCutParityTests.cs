using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-parity tests for the S3 <c>cut</c> streaming core (<c>CutStage</c>) against the
/// real <c>Invoke-BashCut</c> (the parity oracle).
///
/// <para><b>The <c>-d</c> binder collision bounds the certified subset.</b> A BARE
/// <c>-d</c> is a declared value-bearing parameter on the cmdlet (<c>-d</c>
/// prefix-collides with <c>-Debug</c>), and the joined <c>-d:</c> form is recovered by
/// re-scanning the raw invocation line — a surface a streaming stage has neither. So the
/// core certifies the JOINED <c>-dX</c> spelling only (identical on both lanes: argv
/// token and raw-line scan agree) and DECLINES bare <c>-d</c> / bare <c>-c</c>. Same
/// rule as everywhere in this lane: a core that guesses is a correctness bug, a core
/// that declines is merely slower.</para>
/// </summary>
public class LineStreamCutParityTests : LineStreamParityHarness
{
    public LineStreamCutParityTests(SharedPwshFixture fixture) : base(fixture) { }

    private void AssertCut(string[] argv, IEnumerable<string> lines)
        => AssertCoreMatchesCmdlet("cut", "Invoke-BashCut", argv, lines);

    private static readonly string[] Tabbed =
    {
        "alpha\tbeta\tgamma",
        "one\ttwo\tthree\tfour",
        "solo",
        "\tleading\tfields",
    };

    private static readonly string[] Commad =
    {
        "a,b,c",
        "1,2,3,4",
        "nodelim",
        ",lead,ing",
    };

    // ── certified argv × content ─────────────────────────────────────────────

    [Theory]
    [InlineData("-f1")]
    [InlineData("-f2")]
    [InlineData("-f1,3")]
    [InlineData("-f2-3")]
    [InlineData("-f2-")]
    [InlineData("-f-2")]
    [InlineData("-f9")]          // past the end of every line
    public void CutCore_JoinedFieldForms_MatchCmdlet(string flags)
        => AssertCut(Split(flags), Tabbed);

    [Theory]
    [InlineData("-f 1")]
    [InlineData("-f 2,3")]
    [InlineData("-f 1-2")]
    public void CutCore_SeparatedFieldForms_MatchCmdlet(string flags)
        => AssertCut(Split(flags), Tabbed);

    [Theory]
    [InlineData("-d, -f1")]
    [InlineData("-d, -f2,3")]
    [InlineData("-d, -f2-")]
    public void CutCore_JoinedDelimiter_MatchesCmdlet(string flags)
        => AssertCut(Split(flags), Commad);

    [Fact]
    public void CutCore_OnlyDelimited_MatchesCmdlet()
        => AssertCut(new[] { "-d,", "-s", "-f1" }, Commad);

    [Fact]
    public void CutCore_OutputDelimiter_MatchesCmdlet()
        => AssertCut(new[] { "-d,", "-f1,3", "--output-delimiter=|" }, Commad);

    [Theory]
    [InlineData("-c1")]
    [InlineData("-c1-3")]
    [InlineData("-c2,4")]
    [InlineData("-c3-")]
    public void CutCore_JoinedCharForms_MatchCmdlet(string flags)
        => AssertCut(Split(flags), Tabbed);

    [Fact]
    public void CutCore_LongFormAliases_MatchCmdlet()
        => AssertCut(new[] { "--delimiter=,", "--fields=2" }, Commad);

    [Fact]
    public void CutCore_EmptyInput_MatchesCmdlet()
        => AssertCut(new[] { "-f1" }, Array.Empty<string>());

    [Fact]
    public void CutCore_EmptyLines_MatchCmdlet()
        => AssertCut(new[] { "-f1" }, new[] { "", "a\tb", "" });

    [Fact]
    public void CutCore_Unicode_MatchesCmdlet()
        => AssertCut(new[] { "-d,", "-f2" }, new[] { "café,naïve", "🚀,rocket" });

    [Fact]
    public void CutCore_NoSpec_PassesLinesThrough_MatchesCmdlet()
        => AssertCut(Array.Empty<string>(), Tabbed);

    [Fact]
    public void CutCore_IsLazy_DoesNotDrainInfiniteProducer()
    {
        Assert.True(LineStreamRegistry.TryCreate("cut", new[] { "-f1" }, out var stage));
        Assert.Equal(3, stage.Run(Endless()).Take(3).Count());

        static IEnumerable<string> Endless()
        {
            long i = 0;
            while (true) yield return "a\tb" + (i++);
        }
    }

    // ── decline matrix ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("-d")]                    // bare -d: binder-bound parameter, not argv
    [InlineData("-c")]                    // bare -c: binder-bound parameter, not argv
    [InlineData("-d , -f1")]
    [InlineData("-c 1-3")]
    [InlineData("-b1")]                   // byte mode: valid-but-unsupported → cmdlet error
    [InlineData("--complement")]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("--")]
    [InlineData("-f0")]                   // zero position: cmdlet error + exit 1
    [InlineData("-f3-1")]                 // decreasing range: cmdlet error
    [InlineData("-fx")]                   // invalid list: cmdlet error
    [InlineData("-z")]
    [InlineData("file.txt")]              // file operand → file mode
    [InlineData("-dab")]                  // multi-char joined delimiter is not the ^-d(.)$ form
    public void CutCore_UncertifiedArgv_Declines(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("cut", Split(flags), out _),
            $"cut core must DECLINE '{flags}' rather than guess");
}
