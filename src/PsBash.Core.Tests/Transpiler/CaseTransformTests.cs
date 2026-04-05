using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class CaseTransformTests
{
    private readonly CaseTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void BasicCase_TranspilesCorrectly()
    {
        Assert.Equal(
            "switch ($x) { 'a' { echo a } 'b' { echo b } default { echo other } }",
            Apply("case $x in a) echo a;; b) echo b;; *) echo other;; esac"));
    }

    [Fact]
    public void SingleCase_NoDefault()
    {
        Assert.Equal(
            "switch ($x) { 'yes' { echo yes } }",
            Apply("case $x in yes) echo yes;; esac"));
    }

    [Fact]
    public void DefaultOnly()
    {
        Assert.Equal(
            "switch ($x) { default { echo fallback } }",
            Apply("case $x in *) echo fallback;; esac"));
    }

    [Fact]
    public void NoCase_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void FullPipeline_EnvVarReplaced()
    {
        var result = BashTranspiler.Transpile(
            "case $input in start) echo \"starting\";; stop) echo \"stopping\";; *) echo \"unknown\";; esac");
        Assert.Equal(
            "switch ($env:input) { 'start' { echo \"starting\" } 'stop' { echo \"stopping\" } default { echo \"unknown\" } }",
            result);
    }

    [Fact]
    public void FullPipeline_MultiplePatterns()
    {
        var result = BashTranspiler.Transpile(
            "case $x in a) echo a;; b) echo b;; *) echo other;; esac");
        Assert.Equal(
            "switch ($env:x) { 'a' { echo a } 'b' { echo b } default { echo other } }",
            result);
    }
}
