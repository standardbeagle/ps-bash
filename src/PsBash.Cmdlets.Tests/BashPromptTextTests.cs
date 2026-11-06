using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class BashPromptTextTests
{
    [Theory]
    [InlineData("\\[foo\\]\\$", "foo$")]
    [InlineData("\\\\\\$", "\\$")]
    [InlineData("\\1004$", "@4$")]
    [InlineData("[\\045]", "[%]")]
    [InlineData("\\555$", "m$")]
    public void Expand_HandlesPromptEscapes(string input, string expected) =>
        Assert.Equal(expected, BashPromptText.Expand(input));

    [Theory]
    [InlineData("a\\tb", "a\tb")]
    [InlineData("\\x41\\101", "AA")]
    [InlineData("\\q", "\\q")]
    public void Unescape_ExpandsDollarQuoteEscapes(string input, string expected) =>
        Assert.Equal(expected, BashPromptText.Unescape(input));
}
