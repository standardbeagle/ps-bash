using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class ProcessSubTransformTests
{
    private readonly ProcessSubTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void DiffWithTwoProcessSubs_Transforms()
    {
        Assert.Equal(
            "diff (Invoke-ProcessSub { sort file1 }) (Invoke-ProcessSub { sort file2 })",
            Apply("diff <(sort file1) <(sort file2)"));
    }

    [Fact]
    public void CommWithSortedFiles_Transforms()
    {
        Assert.Equal(
            "comm (Invoke-ProcessSub { sort a.txt }) (Invoke-ProcessSub { sort b.txt })",
            Apply("comm <(sort a.txt) <(sort b.txt)"));
    }

    [Fact]
    public void GrepWithProcessSub_Transforms()
    {
        Assert.Equal(
            "grep -f (Invoke-ProcessSub { cat patterns.txt }) data.txt",
            Apply("grep -f <(cat patterns.txt) data.txt"));
    }

    [Fact]
    public void SingleProcessSub_Transforms()
    {
        Assert.Equal(
            "wc -l (Invoke-ProcessSub { find . -name *.txt })",
            Apply("wc -l <(find . -name *.txt)"));
    }

    [Fact]
    public void HereString_NotMatched()
    {
        var input = "cat <<< \"hello\"";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void Heredoc_NotMatched()
    {
        var input = "cat <<EOF";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void CommandSubstitution_NotMatched()
    {
        var input = "echo $(date)";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void NoProcessSub_Unchanged()
    {
        var input = "echo hello world";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void RedirectLessThan_NotMatched()
    {
        var input = "cmd < file.txt";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void CommandWithPipeInside_Transforms()
    {
        Assert.Equal(
            "diff (Invoke-ProcessSub { sort file1 | uniq }) file2",
            Apply("diff <(sort file1 | uniq) file2"));
    }
}
