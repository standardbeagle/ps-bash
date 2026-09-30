using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-parity tests for the <c>wc</c> streaming core (<c>WcStage</c>) against the real
/// <c>Invoke-BashWc</c>. The unicode corpus is the point: the core once passed its <c>-c</c> and
/// <c>-m</c> selectors to <c>FormatWcText</c> swapped, so a fused <c>wc -c</c> printed the CHAR
/// count — identical to the byte count for ASCII, which is why the ASCII corpus never noticed.
/// Since the cmdlet's own <c>Plan</c> now resolves the argv for both lanes, bundles and long forms
/// are certified too.
/// </summary>
public class LineStreamWcParityTests : LineStreamParityHarness
{
    public LineStreamWcParityTests(SharedPwshFixture fixture) : base(fixture) { }

    private static readonly string[] Ascii = { "alpha beta", "gamma", "", "  spaced   out  ", "z" };
    private static readonly string[] Unicode = { "café", "naïve façade", "🚀 rocket ship", "日本語 テキスト", "é" };

    [Theory]
    [InlineData("")]
    [InlineData("-l")]
    [InlineData("-w")]
    [InlineData("-c")]
    [InlineData("-m")]
    [InlineData("-L")]
    [InlineData("-lw")]
    [InlineData("-wc")]
    [InlineData("-cm")]
    [InlineData("-lwcmL")]
    [InlineData("-l -w")]
    [InlineData("--lines")]
    [InlineData("--words")]
    [InlineData("--bytes")]
    [InlineData("--chars")]
    [InlineData("--max-line-length")]
    [InlineData("--li")]
    [InlineData("--by")]
    public void WcCore_MatchesCmdlet(string flags)
    {
        AssertCoreMatchesCmdlet("wc", "Invoke-BashWc", Split(flags), Ascii);
        AssertCoreMatchesCmdlet("wc", "Invoke-BashWc", Split(flags), Unicode);
    }

    [Fact]
    public void WcCore_BytesVersusChars_AreNotSwapped()
    {
        var bytes = RunCore("wc", new[] { "-c" }, new[] { "é" });   // é = 2 bytes + newline
        var chars = RunCore("wc", new[] { "-m" }, new[] { "é" });   // é = 1 char + newline
        Assert.Equal(new[] { "3" }, bytes);
        Assert.Equal(new[] { "2" }, chars);
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("--bogus")]
    [InlineData("--total=always")]
    [InlineData("--files0-from=f")]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("file.txt")]
    public void WcCore_RejectedOrFileArgv_Declines(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("wc", Split(flags), out _));
}
