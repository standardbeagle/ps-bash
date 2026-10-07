using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// BashPattern: bash pattern text (with extglob) → .NET regex. Every expectation is bash 5.2's
/// `[[ $s == pat ]]` (oracle matrix, `shopt -s extglob`); end to end in ExtGlobDifferentialTests.
/// </summary>
public class BashPatternTests
{
    [Theory]
    [InlineData("b", "@(a|b)", true)]
    [InlineData("c", "@(a|b)", false)]
    [InlineData("", "?(a)", true)]
    [InlineData("a", "?(a)", true)]
    [InlineData("aa", "?(a)", false)]
    [InlineData("", "*(ab)", true)]
    [InlineData("abab", "*(ab)", true)]
    [InlineData("aba", "*(ab)", false)]
    [InlineData("", "+(ab)", false)]
    [InlineData("abab", "+(ab)", true)]
    [InlineData("foo.txt", "!(*.log)", true)]
    [InlineData("foo.log", "!(*.log)", false)]
    [InlineData("foo.log.txt", "!(*.log)", true)]
    // The exact-negation cases the usual lookahead approximation gets wrong.
    [InlineData("a", "!(a)*", true)]
    [InlineData("ab", "!(a)b", false)]
    [InlineData("b", "!(a)b", true)]
    [InlineData("ab", "@(a)!(c)", true)]
    [InlineData("ac", "@(a)!(c)", false)]
    [InlineData("foo", "!(f*|b*)", false)]
    [InlineData("zoo", "!(f*|b*)", true)]
    [InlineData("xyz", "x@(y|q)z", true)]
    [InlineData("xz", "x?(y)z", true)]
    [InlineData("a)", "@('a)'|z)", true)]
    [InlineData("x(y", "@(x\\(y)", true)]
    [InlineData("a|b", "@('a|b')", true)]
    [InlineData("axc", "@(a@(b|x)c)", true)]
    [InlineData("file.c", "*.@(c|h)", true)]
    [InlineData("file.o", "*.@(c|h)", false)]
    [InlineData("A", "@(a)", false)]           // case-sensitive
    [InlineData("a\nb", "a*(?)", true)]        // ? spans a newline
    [InlineData("a]", "@([]a])*", true)]       // `]` first in a class is literal
    [InlineData("x", "@([!a-c])", true)]
    [InlineData("b", "@([!a-c])", false)]
    [InlineData("7", "@([[:digit:]])", true)]
    [InlineData("a*b", "@(a\"*\"b)", true)]    // quoted * is literal
    [InlineData("axb", "@(a\"*\"b)", false)]
    public void ToAnchoredRegex_MatchesLikeBash(string subject, string pattern, bool expected)
        => Assert.Equal(expected, Regex.IsMatch(subject, BashPattern.ToAnchoredRegex(pattern)));

    [Theory]
    [InlineData("@(a|b)", true)]
    [InlineData("!(x)", true)]
    [InlineData("*(x)", true)]
    [InlineData("'@(a)'", false)]   // quoted: literal text
    [InlineData("\\@(a)", false)]   // escaped
    [InlineData("*.txt", false)]
    [InlineData("a(b)", false)]
    public void HasExtGlob_FindsOnlyUnquotedOperators(string pattern, bool expected)
        => Assert.Equal(expected, BashPattern.HasExtGlob(pattern));

    [Fact]
    public void ToRegex_UnterminatedList_IsLiteral()
        => Assert.Matches(new Regex(BashPattern.ToAnchoredRegex("@(a")), "@(a");
}
