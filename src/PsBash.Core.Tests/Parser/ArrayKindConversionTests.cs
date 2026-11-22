using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// `declare -A a` on an indexed array and `declare -a m` on an associative one are refused ("cannot convert indexed to
/// associative array", status 1); ps-bash replaced the array. Oracle: bash 5.2.
/// </summary>
public class ArrayKindConversionTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("declare -a A=(1 2)", true)]
    [InlineData("declare -ra A=(1 2)", true)]
    [InlineData("declare -A A=([k]=v)", false)]
    [InlineData("declare -aA A=([k]=v)", false)]
    [InlineData("declare A=(1 2)", false)]
    public void Parse_DeclareArrayFlag_SetsIsIndexed(string bash, bool indexed)
    {
        var cmd = Assert.IsType<Command.ShAssignment>(BashParser.Parse(bash));
        Assert.Equal(indexed, cmd.IsIndexed);
    }

    [Theory]
    [InlineData("declare -A a=([k]=v)", "IsIndexedValue((Get-Variable -Name 'a'")]
    [InlineData("declare -a a=(1 2)", "IsAssocValue((Get-Variable -Name 'a'")]
    [InlineData("declare -A q", "IsIndexedValue((Get-Variable -Name 'q'")]
    [InlineData("declare -a r", "IsAssocValue((Get-Variable -Name 'r'")]
    public void Transpile_DeclareKindAtTopLevel_GuardsTheConversion(string bash, string expected)
        => Assert.Contains(expected, T(bash));

    // In a function `declare` makes a new local: a caller's array of the other kind is not converted
    // (`q=(1); h() { declare -A q; }` is fine), the function's own is (`local -a q; declare -A q`: bash refuses).
    // Known gap (environment-model.md 8.1): the `=( )` form is not guarded.
    [Fact]
    public void Transpile_DeclareKindInFunction_GuardsOnlyTheFramesOwn()
    {
        string ps = T("f() { declare -A q; }");
        Assert.Contains("Get-Variable -Name 'q' -Scope 0", ps);
        Assert.Contains("declare: q: cannot convert indexed to associative array", ps);
    }

    // The builtin's redirects cover its error: `declare -A q 2>/dev/null`, `declare -A w v=(1 2) 2>/dev/null`.
    [Theory]
    [InlineData("declare -A q 2>/dev/null")]
    [InlineData("declare -A w v=(1 2) 2>/dev/null")]
    public void Transpile_DeclareWithRedirect_RedirectsTheDeclaration(string bash)
    {
        var ps = T(bash);
        Assert.StartsWith(". {", ps);
        Assert.Contains("2>$null", ps);
    }

    [Fact]
    public void Parse_DeclareArrayInitializerRedirect_IsKept()
    {
        var cmd = Assert.IsType<Command.ShAssignment>(BashParser.Parse("declare -a v=(1 2) 2>/dev/null"));
        Assert.Single(cmd.Redirects);
    }

    // `declare -A a b=(1 2)`: each name is checked on its own, and the statement's status starts at 0.
    [Fact]
    public void Transpile_DeclareSeveralNames_GuardsEach()
    {
        var ps = T("declare -A a b=(1 2)");
        Assert.StartsWith("$global:LASTEXITCODE = 0", ps);
        Assert.Contains("-Name 'a'", ps);
        Assert.Contains("-Name 'b'", ps);
    }
}
