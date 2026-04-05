using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class ExtendedTestTransformTests
{
    private readonly ExtendedTestTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void StringEquals_Transforms()
    {
        Assert.Equal("($a -eq \"foo\")", Apply("[[ $a == \"foo\" ]]"));
    }

    [Fact]
    public void StringNotEquals_Transforms()
    {
        Assert.Equal("($a -ne \"bar\")", Apply("[[ $a != \"bar\" ]]"));
    }

    [Fact]
    public void RegexMatch_Transforms()
    {
        Assert.Equal("($a -match '^[0-9]+$')", Apply("[[ $a =~ ^[0-9]+$ ]]"));
    }

    [Fact]
    public void GlobPattern_UsesLike()
    {
        Assert.Equal("($a -like 'foo*')", Apply("[[ $a == foo* ]]"));
    }

    [Fact]
    public void FileTest_Transforms()
    {
        Assert.Equal("(Test-Path \"file\" -PathType Leaf)", Apply("[[ -f file ]]"));
    }

    [Fact]
    public void DirTest_Transforms()
    {
        Assert.Equal("(Test-Path \"dir\" -PathType Container)", Apply("[[ -d dir ]]"));
    }

    [Fact]
    public void EmptyStringTest_Transforms()
    {
        Assert.Equal("([string]::IsNullOrEmpty($a))", Apply("[[ -z $a ]]"));
    }

    [Fact]
    public void NonEmptyStringTest_Transforms()
    {
        Assert.Equal("(-not [string]::IsNullOrEmpty($a))", Apply("[[ -n $a ]]"));
    }

    [Fact]
    public void LogicalAnd_Transforms()
    {
        Assert.Equal(
            "(Test-Path \"file\" -PathType Leaf -and Test-Path \"dir\" -PathType Container)",
            Apply("[[ -f file && -d dir ]]"));
    }

    [Fact]
    public void LogicalOr_Transforms()
    {
        Assert.Equal(
            "($a -eq \"x\" -or $b -eq \"y\")",
            Apply("[[ $a == \"x\" || $b == \"y\" ]]"));
    }

    [Fact]
    public void LessThan_Transforms()
    {
        Assert.Equal("($a -lt $b)", Apply("[[ $a < $b ]]"));
    }

    [Fact]
    public void GreaterThan_Transforms()
    {
        Assert.Equal("($a -gt $b)", Apply("[[ $a > $b ]]"));
    }

    [Fact]
    public void NoExtendedTest_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void SingleBracket_Unchanged()
    {
        var input = "[ -f file ]";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void GlobWithQuestionMark_UsesLike()
    {
        Assert.Equal("($a -like 'file?.txt')", Apply("[[ $a == file?.txt ]]"));
    }

    [Fact]
    public void EmptyStringTestQuoted_Transforms()
    {
        Assert.Equal("([string]::IsNullOrEmpty($a))", Apply("[[ -z \"$a\" ]]"));
    }

    [Fact]
    public void IntegrationWithPipeline_TransformsEnvVars()
    {
        var result = BashTranspiler.Transpile("[[ $a == \"foo\" ]]");
        Assert.Equal("($env:a -eq \"foo\")", result);
    }

    [Fact]
    public void IntegrationFileTestAndComparison_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("[[ -f /etc/config ]]");
        Assert.Equal("(Test-Path \"/etc/config\" -PathType Leaf)", result);
    }

    [Fact]
    public void IntegrationLogicalAnd_TransformsAll()
    {
        var result = BashTranspiler.Transpile("[[ $a == \"x\" && $b == \"y\" ]]");
        Assert.Equal("($env:a -eq \"x\" -and $env:b -eq \"y\")", result);
    }
}
