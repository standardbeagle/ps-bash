using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Parser;

/// <summary>`! cmd || x` / `! cmd && x`: the negated command still writes its stdout; only its status is inverted
/// (oils pipeline "! with ||"). It was wrapped in [void], which discarded the output.</summary>
public class NegatedChainEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("! echo hi || echo FAILED")]
    [InlineData("! { echo 1; echo 2; } || echo FAILED")]
    [InlineData("! ( echo 1; echo 2 ) && echo ok")]
    public void NegatedPipelineInChain_KeepsItsOutput(string bash)
    {
        var ps = T(bash);
        Assert.DoesNotContain("[void]", ps);
        Assert.Contains("$global:LASTEXITCODE -ne 0", ps); // status inverted after the command ran
    }
}
