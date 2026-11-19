using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Under <c>set -o pipefail</c> (or when a unit reads PIPESTATUS) a multi-stage pipeline records each stage's status
/// (PsBuild.PipefailPipeline, one dot-sourced block), so every position a pipeline can stand in must still parse
/// (ParseabilityContract branch A); so must a line that only mentions the word (it once switched the mode on).
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
    // Every position a pipeline stands in, with pipefail on (PsBuild.PipefailPipeline is one pipeline element).
    [InlineData("set -o pipefail; x=$(grep pipefail f | head -1); echo \"$x\"")]
    [InlineData("set -o pipefail; if grep pipefail f | head -1; then echo t; fi")]
    [InlineData("set -o pipefail; ! grep pipefail f | head -1; echo $?")]
    [InlineData("set -o pipefail; grep -c pipefail f | cat && echo y || echo n")]
    [InlineData("set -o pipefail; while false | true; do break; done")]
    [InlineData("set -o pipefail; until true | false; do break; done")]
    [InlineData("set -o pipefail; echo \"$(false | true; echo st=$?)\"")]
    [InlineData("set -o pipefail; [ 1 ] && echo a | cat && echo b | cat")]
    [InlineData("set -o pipefail; case x in x) false | true ;; esac")]
    [InlineData("set -o pipefail; for i in 1; do false | true; done")]
    [InlineData("set -o pipefail; z=$( (false | true) && echo yes || echo no )")]
    [InlineData("set -o pipefail; echo pre | { false | true; echo $?; }")]
    [InlineData("set -o pipefail; time false | true")]
    [InlineData("set -o pipefail; echo $(echo a | cat) $(echo b | cat)")]
    [InlineData("set -o pipefail; cat <(echo a | cat)")]
    [InlineData("set -o pipefail; f() { echo a | cat; } ; f | cat")]
    [InlineData("set -o pipefail; echo a | cat > /dev/null && echo ok")]
    [InlineData("set -o pipefail; x=1 echo a | cat")]
    [InlineData("set -o pipefail; local_fn() { local v=$(echo a | cat); echo $v; }; local_fn")]
    [InlineData("set -o pipefail; true | false; echo \"${PIPESTATUS[@]}\"")]
    // The word alone, quoted or as data, with no `set`.
    [InlineData("grep -c pipefail f && echo y")]
    [InlineData("x=$(grep pipefail f | head -1)")]
    [InlineData("grep pipefail f | head -1 || echo no")]
    [InlineData("if grep pipefail f | head -1; then echo t; fi")]
    [InlineData("! grep pipefail f | head -1")]
    [InlineData("grep -n \"_pipefailMode|InheritedPipefail\" f && grep x f | head")]
    public void Transpile_UnderPipefail_PipelineInEveryPosition_EmitsParseablePowerShell(string bash)
        => ParseabilityContract.Assert(bash, "pipefail emission");

    /// <summary>The run-time runner of a builtin named at run time (BashBuiltins.RunAsSource) is valid PowerShell for every name.</summary>
    [Fact]
    public void BuiltinAsSourceScript_ParsesForEveryRunAsSourceBuiltin()
    {
        foreach (var name in PsBash.Core.Parser.BashBuiltins.RunAsSource)
        {
            System.Management.Automation.Language.Parser.ParseInput(PsBash.Core.Parser.PsBuild.BuiltinAsSourceScript(name), out _, out var errors);
            Assert.True(errors.Length == 0, name + ": " + string.Join("; ", errors.Select(e => e.Message)));
        }
    }
}
