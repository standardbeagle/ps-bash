using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The pure halves of <see cref="BashSubscriptExpansion"/>: which run-time subscripts need a full word expansion, and
/// how the text becomes the body of the double-quoted word that is transpiled. A top-level <c>"</c> must never end the
/// word (that would splice the rest of the subscript into the command as unquoted text).
/// </summary>
public class BashSubscriptExpansionTests
{
    [Theory]
    [InlineData("$(echo 1)", false, true)]
    [InlineData("$(( 3 - 2 ))", false, true)]
    [InlineData("`echo 1`", false, true)]
    [InlineData("${c:-1}", false, false)]
    [InlineData("${c:-1}", true, true)]
    [InlineData("$b", true, false)]
    [InlineData("i+1", true, false)]
    [InlineData("<(echo x)", true, false)]
    [InlineData("{1,0}", true, false)]
    [InlineData(null, true, false)]
    public void NeedsWordExpansion_OnlyForSubstitutions(string? subscript, bool braced, bool expected)
        => Assert.Equal(expected, BashSubscriptExpansion.NeedsWordExpansion(subscript, braced));

    [Theory]
    [InlineData("1", true)]
    [InlineData("\"K\"", true)]
    [InlineData("'it\"s'", true)]
    [InlineData("\\\"", true)]
    [InlineData("$(echo \"x\")", true)]
    [InlineData("1\"", false)]
    [InlineData("'1", false)]
    [InlineData("\"a'", false)]
    public void QuotesBalanced_RejectsAnOpenQuote(string subscript, bool expected)
        => Assert.Equal(expected, BashSubscriptExpansion.QuotesBalanced(subscript));

    [Theory]
    [InlineData("$(echo 0 | tee PWNED)", "$(echo 0 | tee PWNED)")]
    [InlineData("\"K\"", "K")]
    [InlineData("\"$k x\" $(echo 1)", "$k x $(echo 1)")]
    [InlineData("$(echo \"a)b\")", "$(echo \"a)b\")")]
    [InlineData("$(echo ')' \"x\") \"y\"", "$(echo ')' \"x\") y")]
    [InlineData("${m[\"k\"]}", "${m[\"k\"]}")]
    [InlineData("`echo \"q\"`\"", "`echo \"q\"`")]
    [InlineData("\\\"", "\\\"")]
    public void DoubleQuotedBody_DropsOnlyTopLevelDoubleQuotes(string text, string expected)
        => Assert.Equal(expected, BashSubscriptExpansion.DoubleQuotedBody(text));
}
