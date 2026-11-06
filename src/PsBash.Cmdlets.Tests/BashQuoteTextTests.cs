using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class BashQuoteTextTests
{
    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("", "''")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a\nb", "$'a\\nb'")]
    [InlineData("a\u0001c'd", "$'a\\001c\\'d'")]
    public void ForReuse_MatchesBashAtQ(string input, string expected)
        => Assert.Equal(expected, BashQuoteText.ForReuse(input));
}
