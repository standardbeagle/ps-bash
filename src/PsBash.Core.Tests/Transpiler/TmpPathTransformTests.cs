using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class TmpPathTransformTests
{
    private readonly TmpPathTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void TmpPath_Transforms()
    {
        Assert.Equal("cat $env:TEMP\\file.txt", Apply("cat /tmp/file.txt"));
    }

    [Fact]
    public void MultipleTmpPaths_TransformsAll()
    {
        Assert.Equal("cp $env:TEMP\\a $env:TEMP\\b", Apply("cp /tmp/a /tmp/b"));
    }

    [Fact]
    public void NoTmpPath_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void TmpWithoutTrailingSlash_Unchanged()
    {
        var input = "echo /tmp";
        Assert.Equal(input, Apply(input));
    }
}
