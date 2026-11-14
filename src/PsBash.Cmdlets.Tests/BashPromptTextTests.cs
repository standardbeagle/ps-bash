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

    // bash 5.2 (oracle): the promptvars pass removes a backslash before $ ` " \ after decoding, and \$ decodes to \$.
    [Theory]
    [InlineData("$", "$")]
    [InlineData("\\$", "$")]
    [InlineData("\\\\$", "$")]
    [InlineData("\\\\\\$", "\\$")]
    [InlineData("\\\\\\\\$", "\\$")]
    [InlineData("a\\\"b", "a\"b")]
    [InlineData("a\\qb", "a\\qb")]
    public void Expand_DollarAndBackslash_QuoteRemovalLikeBash(string input, string expected) =>
        Assert.Equal(expected, BashPromptText.Expand(input));

    [Fact]
    public void Expand_InsertedWorkingDir_KeepsBackslashes()
    {
        var saved = Environment.GetEnvironmentVariable("PWD");
        try
        {
            Environment.SetEnvironmentVariable("PWD", @"C:\a\$b");
            Assert.Equal(@"[C:\a\$b]", BashPromptText.Expand("[\\w]"));
        }
        finally { Environment.SetEnvironmentVariable("PWD", saved); }
    }

    [Fact]
    public void Expand_EmptyStrftime_IsHourMinuteSecond() =>
        Assert.Matches(@"^foo \d\d:\d\d:\d\d bar$", BashPromptText.Expand("foo \\D{} bar"));

    [Theory]
    [InlineData("a\\tb", "a\tb")]
    [InlineData("\\x41\\101", "AA")]
    [InlineData("\\q", "\\q")]
    public void Unescape_ExpandsDollarQuoteEscapes(string input, string expected) =>
        Assert.Equal(expected, BashPromptText.Unescape(input));
}
