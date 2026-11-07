using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>Malformed `[[ ]]` conditions are bash syntax errors (status 2), well-formed ones still transpile.</summary>
public class DoubleBracketSyntaxTests
{
    [Theory]
    [InlineData("[[ ]]")]
    [InlineData("[[ -z ]]")]
    [InlineData("[[ -n ]]")]
    [InlineData("[[ && ]]")]
    [InlineData("[[ a && ]]")]
    [InlineData("[[ && a ]]")]
    [InlineData("[[ ! ]]")]
    [InlineData("[[ a b ]]")]
    [InlineData("[[ ( a ]]")]
    [InlineData("[[ a ) ]]")]
    [InlineData("[[ a || ]]")]
    [InlineData("[[ == a ]]")]
    [InlineData("[[ a == ]]")]
    public void Transpile_MalformedConditional_IsSyntaxError(string bash) => Assert.ThrowsAny<ParseException>(() => PsEmitter.Transpile(bash));

    [Theory]
    [InlineData("[[ a ]]")]
    [InlineData("[[ ! a ]]")]
    [InlineData("[[ ! -z a ]]")]
    [InlineData("[[ a == b ]]")]
    [InlineData("[[ a < b ]]")]
    [InlineData("[[ -z a && ( b == c || -n d ) ]]")]
    [InlineData("[[ $x =~ ^(a|b)$ ]]")]
    [InlineData("[[ '(' == '(' ]]")]
    [InlineData("[[ a -eq 3 ]]")]
    [InlineData("[[ ! ! a ]]")]
    [InlineData("[ ]")]
    public void Transpile_WellFormedConditional_Parses(string bash) => Assert.NotNull(PsEmitter.Transpile(bash));
}
