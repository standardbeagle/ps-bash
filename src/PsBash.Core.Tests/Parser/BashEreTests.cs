using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// BashEre: the regex of <c>[[ s =~ re ]]</c> (POSIX ERE, glibc regcomp) in .NET syntax. Expectations are bash 5.2's
/// <c>[[ $s =~ $re ]]; echo $?</c> (0 match, 1 no match, 2 the regex does not compile).
/// </summary>
public class BashEreTests
{
    private static int Status(string text, string ere)
    {
        try { return Regex.IsMatch(text, BashEre.ToDotNet(ere)) ? 0 : 1; }
        catch (ArgumentException) { return 2; }
    }

    [Theory]
    [InlineData("{", "{", 2)]
    [InlineData("a{", "a{", 2)]
    [InlineData("x{1", "x{1", 2)]
    [InlineData("{", "^{", 2)]
    [InlineData("a", "a{1", 2)]
    [InlineData("x", "(){", 2)]
    [InlineData("x", "x|{", 2)]
    [InlineData("a{b", "a{b", 2)]
    [InlineData("a", "a{1,2", 2)]
    [InlineData("{", "\\{", 0)]
    [InlineData("a}", "a}", 0)]
    [InlineData("aa", "^a{,2}$", 0)]
    [InlineData("aaa", "^a{,2}$", 1)]
    [InlineData("ab", "a{1}{2}", 1)]
    [InlineData("aa", "^a{1}{2}$", 0)]
    [InlineData("aaaa", "^a{2}*$", 0)]
    [InlineData("aaa", "^a{2}*$", 1)]
    [InlineData("abab", "^(ab){1}{2}$", 0)]
    [InlineData("aa", "^a{2}?$", 0)]
    [InlineData("", "^a{2}?$", 0)]
    [InlineData("aab", "^a{2}b$", 0)]
    [InlineData("aaab", "^a{2,}b$", 0)]
    [InlineData("$PA", "^(\\$\\{?)([A-Za-z0-9_]*)$", 0)]
    [InlineData("5", "^[[:digit:]]$", 0)]
    [InlineData("x", "^[^[:digit:]]$", 0)]
    [InlineData("{", "[{]", 0)]
    [InlineData("]", "[]]", 0)]
    [InlineData("-", "^[[:punct:]]{1}$", 0)]
    public void ToDotNet_MatchesLikeBash(string text, string ere, int expected)
        => Assert.Equal(expected, Status(text, ere));

    [Fact]
    public void ToDotNet_ClassNameOutsideABracket_IsLeftAlone()
        => Assert.Equal("[:digit:]x", BashEre.ToDotNet("[:digit:]x"));   // a bracket of the members : d i g t
}
