using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class FileTestTransformTests
{
    private readonly FileTestTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void TestFile_Transforms()
    {
        Assert.Equal("(Test-Path \"myfile\" -PathType Leaf)", Apply("[ -f myfile ]"));
    }

    [Fact]
    public void TestDir_Transforms()
    {
        Assert.Equal("(Test-Path \"mydir\" -PathType Container)", Apply("[ -d mydir ]"));
    }

    [Fact]
    public void TestEmpty_Transforms()
    {
        Assert.Equal("([string]::IsNullOrEmpty($VAR))", Apply("[ -z \"$VAR\" ]"));
    }

    [Fact]
    public void TestNonEmpty_Transforms()
    {
        Assert.Equal("(-not [string]::IsNullOrEmpty($VAR))", Apply("[ -n \"$VAR\" ]"));
    }

    [Fact]
    public void TestFileWithPath_Transforms()
    {
        Assert.Equal("(Test-Path \"/etc/config\" -PathType Leaf)", Apply("[ -f /etc/config ]"));
    }

    [Fact]
    public void TestEmptyNoQuotes_Transforms()
    {
        Assert.Equal("([string]::IsNullOrEmpty($VAR))", Apply("[ -z $VAR ]"));
    }

    [Fact]
    public void NoFileTest_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }
}
