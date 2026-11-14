using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// BashWordSplitter.AffixFields, checked against bash 5.2 (`wsl bash`, printf '&lt;%s&gt;'). The affix is literal text and
/// is never split; a quoted affix keeps its field even when empty (bash's quoted null). A='   abc   def   ':
/// <c>""$A</c> is &lt;&gt;&lt;abc&gt;&lt;def&gt; and <c>$A""</c> is &lt;abc&gt;&lt;def&gt;&lt;&gt;; the joined-text split dropped both empty fields.
/// </summary>
public class AffixFieldsTests
{
    private const string A = "   abc   def   ";

    [Theory]
    [InlineData("", "", A, true, false, "|abc|def")]
    [InlineData("", "", A, false, true, "abc|def|")]
    [InlineData("", "", A, true, true, "|abc|def|")]
    [InlineData("", "", "   ", false, true, "")]
    [InlineData("", "", "   ", true, true, "|")]
    [InlineData("a b", "", A, true, false, "a b|abc|def")]
    [InlineData("x", "", A, false, false, "x|abc|def")]
    [InlineData("x", "", "   ", false, false, "x")]
    public void AffixFields_QuotedAffixAndWhitespaceValue_MatchesBash(string prefix, string suffix, string value,
        bool prefixQuoted, bool suffixQuoted, string expected)
    {
        var fields = BashWordSplitter.AffixFields(prefix, suffix, new[] { value }, null, prefixQuoted, suffixQuoted);
        Assert.Equal(expected, string.Join("|", fields));
        Assert.Equal(expected.Split('|').Length, fields.Count);
    }

    [Theory]
    [InlineData("a:b", "", "c", false, false, "a:bc")]
    [InlineData("q", "r", ":a:", true, true, "q|a|r")]
    [InlineData("q", "", ":a:", true, false, "q|a")]
    [InlineData("", "", ":a:", false, true, "|a|")]
    public void AffixFields_ColonIfs_SplitsOnlyTheValue(string prefix, string suffix, string value,
        bool prefixQuoted, bool suffixQuoted, string expected)
        => Assert.Equal(expected, string.Join("|", BashWordSplitter.AffixFields(prefix, suffix, new[] { value }, ":", prefixQuoted, suffixQuoted)));

    [Fact]
    public void AffixFields_EmptyIfsQuotedEmpty_KeepsOneEmptyField()
        => Assert.Single(BashWordSplitter.AffixFields("", "", new[] { "" }, "", prefixQuoted: true));
}
