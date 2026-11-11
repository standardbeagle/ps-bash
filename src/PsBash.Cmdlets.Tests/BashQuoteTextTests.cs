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

    // Quote removal only, no expansion: `$x` stays literal, as in the subscript text bash re-reads.
    [Theory]
    [InlineData("\"al\"e", "ale")]
    [InlineData("'a b'", "a b")]
    [InlineData("\"a\\\"b\\x\"", "a\"b\\x")]
    [InlineData("a\\ b", "a b")]
    [InlineData("'it'\\''s'", "it's")]
    [InlineData("\"$x\"", "$x")]
    [InlineData("", "")]
    [InlineData("\"unterminated", "unterminated")]
    public void RemoveQuotes_RemovesQuotesWhereverTheyAre(string word, string expected)
        => Assert.Equal(expected, BashQuoteText.RemoveQuotes(word));
}
