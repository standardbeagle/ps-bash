using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The fused-lane streaming cores operate on strings, so the escaped-byte markers
/// (<see cref="PsBash.Core.RawBytes"/>, U+DC80..U+DCFF) must pass through them exactly as they pass through
/// the real cmdlets — a core that normalized, dropped or split a marker would make the fused and unfused
/// lanes disagree on binary data. The real cmdlet is the oracle (see <see cref="LineStreamParityHarness"/>);
/// the marker lines are built from raw bytes, never typed as literals.
/// </summary>
public class LineStreamRawBytesParityTests : LineStreamParityHarness
{
    public LineStreamRawBytesParityTests(SharedPwshFixture fixture) : base(fixture) { }

    private static string M(params byte[] bytes) => PsBash.Core.RawBytes.GetString(bytes);

    private static readonly string[] Lines =
    {
        "plain",
        "a" + M(0xE9) + "b",
        M(0xFF, 0xFE),
        "café \U0001F600",
        M(0xE2, 0x82) + "tail",
        "a" + M(0xE9) + "b",
        "zeta",
    };

    [Theory]
    [InlineData("cat", "Invoke-BashCat")]
    [InlineData("rev", "Invoke-BashRev")]
    [InlineData("tac", "Invoke-BashTac")]
    public void NoArgStages_PassMarkersThroughLikeTheCmdlet(string name, string cmdlet)
        => AssertCoreMatchesCmdlet(name, cmdlet, Array.Empty<string>(), Lines);

    [Fact] public void Head_KeepsMarkerLines() => AssertCoreMatchesCmdlet("head", "Invoke-BashHead", new[] { "-n", "3" }, Lines);
    [Fact] public void Tail_KeepsMarkerLines() => AssertCoreMatchesCmdlet("tail", "Invoke-BashTail", new[] { "-n", "3" }, Lines);
    [Fact] public void Sort_OrdersMarkerLinesLikeTheCmdlet() => AssertCoreMatchesCmdlet("sort", "Invoke-BashSort", Array.Empty<string>(), Lines);
    [Fact] public void SortUnique_DedupesMarkerLines() => AssertCoreMatchesCmdlet("sort", "Invoke-BashSort", new[] { "-u" }, Lines);
    [Fact] public void Uniq_CountsMarkerLines() => AssertCoreMatchesCmdlet("uniq", "Invoke-BashUniq", new[] { "-c" }, Lines);
    [Fact] public void Nl_NumbersMarkerLines() => AssertCoreMatchesCmdlet("nl", "Invoke-BashNl", Array.Empty<string>(), Lines);

    [Fact] public void Grep_MatchesAroundMarkers() => AssertCoreMatchesCmdlet("grep", "Invoke-BashGrep", new[] { "a.*b" }, Lines);
    [Fact] public void GrepInvert_KeepsTheRest() => AssertCoreMatchesCmdlet("grep", "Invoke-BashGrep", new[] { "-v", "plain" }, Lines);
    [Fact] public void Sed_Substitute_LeavesMarkersAlone() => AssertCoreMatchesCmdlet("sed", "Invoke-BashSed", new[] { "s/plain/PLAIN/" }, Lines);

    [Theory]
    [InlineData("-l")]
    [InlineData("-c")]
    [InlineData("-m")]
    [InlineData("-w")]
    [InlineData("-L")]
    public void Wc_CountsMarkersLikeTheCmdlet(string flag)
        => AssertCoreMatchesCmdlet("wc", "Invoke-BashWc", new[] { flag }, Lines);

    [Fact] public void Cut_Bytes_SlicesMarkers() => AssertCoreMatchesCmdlet("cut", "Invoke-BashCut", new[] { "-b", "1-2" }, Lines);
    [Fact] public void Cut_Chars_SlicesMarkers() => AssertCoreMatchesCmdlet("cut", "Invoke-BashCut", new[] { "-c", "2-3" }, Lines);
    [Fact] public void Tr_Translate_LeavesMarkersAlone() => AssertCoreMatchesCmdlet("tr", "Invoke-BashTr", new[] { "a-z", "A-Z" }, Lines);
    [Fact] public void Tr_OctalHighByte_MatchesTheMarkerLine() => AssertCoreMatchesCmdlet("tr", "Invoke-BashTr", new[] { "\\351", "X" }, Lines);
}
