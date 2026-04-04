using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class PipeTransformTests
{
    private readonly PipeTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void PipeGrep_Transforms()
    {
        Assert.Equal("cmd | Invoke-Grep \"pattern\"", Apply("cmd | grep pattern"));
    }

    [Fact]
    public void PipeGrepInvert_Transforms()
    {
        Assert.Equal("cmd | Invoke-Grep -NotMatch \"pattern\"", Apply("cmd | grep -v pattern"));
    }

    [Fact]
    public void PipeGrepCaseInsensitive_Transforms()
    {
        Assert.Equal("cmd | Invoke-Grep -CaseInsensitive \"pattern\"", Apply("cmd | grep -i pattern"));
    }

    [Fact]
    public void PipeGrepRecurse_Transforms()
    {
        Assert.Equal("cmd | Invoke-Grep -Recurse \"pattern\" dir", Apply("cmd | grep -r pattern dir"));
    }

    [Fact]
    public void PipeHead_Transforms()
    {
        Assert.Equal("cmd | Select-Object -First 10", Apply("cmd | head -n 10"));
    }

    [Fact]
    public void PipeTail_Transforms()
    {
        Assert.Equal("cmd | Select-Object -Last 5", Apply("cmd | tail -n 5"));
    }

    [Fact]
    public void PipeWcL_Transforms()
    {
        Assert.Equal("cmd | Measure-Object -Line | Select-Object -Expand Lines", Apply("cmd | wc -l"));
    }

    [Fact]
    public void PipeSort_Transforms()
    {
        Assert.Equal("cmd | Sort-Object", Apply("cmd | sort"));
    }

    [Fact]
    public void PipeSortReverse_Transforms()
    {
        Assert.Equal("cmd | Sort-Object -Descending", Apply("cmd | sort -r"));
    }

    [Fact]
    public void PipeUniq_Transforms()
    {
        Assert.Equal("cmd | Get-Unique", Apply("cmd | uniq"));
    }

    [Fact]
    public void PipeSed_Transforms()
    {
        Assert.Equal("cmd | Invoke-Sed 's/x/y/'", Apply("cmd | sed 's/x/y/'"));
    }

    [Fact]
    public void PipeAwk_Transforms()
    {
        Assert.Equal("cmd | Invoke-Awk '{print $1}'", Apply("cmd | awk '{print $1}'"));
    }

    [Fact]
    public void PipeCut_Transforms()
    {
        Assert.Equal("cmd | Invoke-Cut -Delimiter : -Field 1", Apply("cmd | cut -d: -f1"));
    }

    [Fact]
    public void PipeXargs_Transforms()
    {
        Assert.Equal("cmd | Invoke-Xargs", Apply("cmd | xargs"));
    }

    [Fact]
    public void PipeTr_Transforms()
    {
        Assert.Equal("cmd | Invoke-Tr 'a' 'b'", Apply("cmd | tr 'a' 'b'"));
    }

    [Fact]
    public void PipeTee_Transforms()
    {
        Assert.Equal("cmd | Tee-Object output.txt", Apply("cmd | tee output.txt"));
    }

    [Fact]
    public void NoPipe_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }
}
