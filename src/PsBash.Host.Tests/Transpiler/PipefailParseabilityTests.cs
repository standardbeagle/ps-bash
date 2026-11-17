using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Under <c>set -o pipefail</c> a multi-stage pipeline emits a statement LIST (<c>$__pf = …; stages; …</c>) instead of
/// one PowerShell pipeline, so every position a pipeline can stand in must still parse (ParseabilityContract branch A).
/// REGRESSION: as an <c>&amp;&amp;</c> / <c>||</c> operand only its first statement, an assignment, joined the chain, and
/// <c>set -o pipefail; scp a b &amp;&amp; ssh h cmd | grep x</c> was "Unexpected token '='" for the whole script. Claude
/// Code's Bash tool runs with pipefail, so this broke an ordinary agent command. The oracle is PowerShell's parser
/// (see ParseabilityContract); the behavior is covered against bash by the Differential pipefail chain test.
/// </summary>
public class PipefailParseabilityTests
{
    [Theory]
    [InlineData("set -o pipefail; true && echo hi | cat")]
    [InlineData("set -o pipefail; false || echo hi | cat")]
    [InlineData("set -o pipefail; echo hi | cat && echo ok")]
    [InlineData("set -o pipefail; echo a | cat || echo b | cat && echo c | cat")]
    [InlineData("set -o pipefail; true && ! echo hi | cat")]
    [InlineData("set -o pipefail; ! echo hi | cat && echo ok")]
    [InlineData("set -o pipefail; cd /nodir && ls | head -1")]
    [InlineData("set -o pipefail; true && echo hi | cat > /tmp/psb_out")]
    [InlineData("set -o pipefail; true && echo hi |& cat")]
    [InlineData("set -o pipefail; true && echo hi | cat &")]
    [InlineData("set -o pipefail; if true && echo hi | cat; then :; fi")]
    [InlineData("set -o pipefail; while echo hi | cat && false; do break; done")]
    [InlineData("set -o pipefail; x=$(true && echo hi | cat); echo \"$x\"")]
    [InlineData("set -o pipefail; { true && echo hi | cat; } && echo ok")]
    [InlineData("set -o pipefail; f() { true && echo hi | cat; }; f && echo ok")]
    [InlineData("set -eo pipefail; true && echo hi | cat; echo after")]
    [InlineData("set -euo pipefail; false || echo hi | grep -q h && echo found")]
    public void Transpile_UnderPipefail_PipelineInEveryPosition_EmitsParseablePowerShell(string bash)
        => ParseabilityContract.Assert(bash, "pipefail emission");
}
