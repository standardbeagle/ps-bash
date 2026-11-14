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
    // An unquoted < > ( is an operator token, never an operand (bash 5.2: status 2).
    [InlineData("[[ -f < ]]")]
    [InlineData("[[ -n > ]]")]
    [InlineData("[[ -z ( ]]")]
    [InlineData("[[ < ]]")]
    [InlineData("[[ ! < ]]")]
    // `;` and `&` outside a regex group are still shell operators (bash 5.2: status 2).
    [InlineData("[[ x =~ ; ]]")]
    [InlineData("[[ x =~ & ]]")]
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
    [InlineData("[[ -n '<' ]]")]
    [InlineData("[[ -n \\> ]]")]
    [InlineData("[[ x =~ (;) ]]")]
    [InlineData("[[ x =~ ';' ]]")]
    [InlineData("[[ x =~ $'|' ]]")]
    [InlineData("[[ a -eq 3 ]]")]
    [InlineData("[[ ! ! a ]]")]
    [InlineData("[ ]")]
    public void Transpile_WellFormedConditional_Parses(string bash) => Assert.NotNull(PsEmitter.Transpile(bash));
}
