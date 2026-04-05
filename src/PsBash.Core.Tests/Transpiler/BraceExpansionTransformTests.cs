using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class BraceExpansionTransformTests
{
    private readonly BraceExpansionTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void NumericRange_Transforms()
    {
        Assert.Equal("echo (1..5)", Apply("echo {1..5}"));
    }

    [Fact]
    public void NumericRange_LargeRange_Transforms()
    {
        Assert.Equal("echo (1..100)", Apply("echo {1..100}"));
    }

    [Fact]
    public void CommaExpansion_WithPrefix_Transforms()
    {
        Assert.Equal("touch file1.txt file2.txt file3.txt", Apply("touch file{1,2,3}.txt"));
    }

    [Fact]
    public void CommaExpansion_WithPrefixDirectory_Transforms()
    {
        Assert.Equal("mkdir dir/src dir/lib dir/test", Apply("mkdir dir/{src,lib,test}"));
    }

    [Fact]
    public void CommaExpansion_NoPrefixSuffix_Transforms()
    {
        Assert.Equal("echo a b c", Apply("echo {a,b,c}"));
    }

    [Fact]
    public void NoBraces_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void SingleItem_NoBraceExpansion()
    {
        var input = "echo {hello}";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void QuotedBraces_NotExpanded()
    {
        var input = "awk -F, '{print $1, $3}'";
        Assert.Equal(input, Apply(input));
    }
}
