using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Whether a unit pays for per-stage pipeline status (<see cref="PsBuild.PipefailPipeline"/>) is decided on the parsed
/// commands (<c>PsEmitter.CanSetPipefail</c> / <c>ReadsPipestatus</c>), never on the source text. REGRESSION: a raw
/// <c>bash.Contains("pipefail")</c> turned the mode on for <c>grep -c pipefail f &amp;&amp; echo y</c>, whose capture was
/// then invalid PowerShell, so a quoted word broke the parse of the whole line. Emitted-text surface (which form a
/// pipeline takes); behavior vs bash is in Differential.Tests/PipefailPositionDifferentialTests.
/// </summary>
public class PipefailModeEmitterTests
{
    private static bool Captures(string bash) =>
        PsEmitter.Transpile(bash)!.Contains("Invoke-BashPipeStage", StringComparison.Ordinal);

    [Theory]
    [InlineData("grep -c pipefail f | head -1")]
    [InlineData("grep 'set -o pipefail' f | head -1")]
    [InlineData("echo \"PIPESTATUS pipefail\" | cat")]
    [InlineData("x='set -o pipefail'; echo a | cat")]
    [InlineData("grep PIPESTATUS f | head -1")]
    [InlineData("# set -o pipefail\necho a | cat")]
    [InlineData("cat <<'EOF' | cat\n${PIPESTATUS[0]}\nEOF")]   // a quoted here-document does not expand
    [InlineData("\"$@\" | cat")]                                // a quoted run-time name with no operand
    [InlineData("$SUDO rm \"$f\"; echo a | cat")]               // a run-time name whose operands name no option
    [InlineData("trap 'echo bye' EXIT; echo a | cat")]
    public void Transpile_PipefailOnlyAsData_KeepsThePlainPipeline(string bash)
        => Assert.False(Captures(bash));

    [Theory]
    [InlineData("set -o pipefail; echo a | cat")]
    [InlineData("set -eo pipefail; echo a | cat")]
    [InlineData("set -euo pipefail\necho a | cat")]
    [InlineData("shopt -so pipefail; echo a | cat")]
    [InlineData("command set -o pipefail; echo a | cat")]
    [InlineData("o=pipefail; set -o \"$o\"; echo a | cat")]       // a run-time option word
    [InlineData("set -o $(echo pipefail); echo a | cat")]
    [InlineData("eval 'set -o pipefail'; echo a | cat")]           // static eval text is judged too
    [InlineData("eval \"$cmd\"; echo a | cat")]                     // run-time eval text: anything

    [InlineData("alias p='set -o pipefail'; echo a | cat")]
    [InlineData("run() { \"$@\"; }; run set -o pipefail; echo a | cat")]
    [InlineData("c=set; $c -o pipefail; echo a | cat")]
    [InlineData("run() { \"$@\"; }; run set -o \"$o\"; echo a | cat")]
    [InlineData("trap 'set -o pipefail' EXIT; echo a | cat")]
    [InlineData("trap 'echo ${PIPESTATUS[@]}' ERR\nfalse | true")]   // a trap action reads it
    [InlineData("x=$(set -o pipefail; false | true; echo $?)")]   // inside a command substitution
    [InlineData("true | false; echo ${PIPESTATUS[1]}")]
    [InlineData("true | false; echo \"${PIPESTATUS[@]}\"")]
    [InlineData("true | false; s=(\"${PIPESTATUS[@]}\")")]
    [InlineData("true | false; echo $(( PIPESTATUS[1] + 1 ))")]
    [InlineData("true | false; cat <<EOF\n${PIPESTATUS[1]}\nEOF")]
    [InlineData("true | false; eval 'echo ${PIPESTATUS[1]}'")]
    public void Transpile_UnitThatCanTurnPipefailOnOrReadsPipestatus_CapturesEveryStage(string bash)
        => Assert.True(Captures(bash));

    /// <summary>The sibling modes share the predicate (PsEmitter.CanSetShellOption): only an operand can turn them on.</summary>
    [Theory]
    [InlineData("set -x; echo a", true)]
    [InlineData("set -eux; echo a", true)]
    [InlineData("set -o xtrace; echo a", true)]
    [InlineData("c=set; $c -x; echo a", true)]
    [InlineData("trap 'set -x' EXIT; echo a", true)]
    [InlineData("echo 'set -x'; grep -x a f", false)]
    [InlineData("tar -xf a.tar", false)]
    [InlineData("eval \"$cmd\"", false)]                         // run-time text counts for pipefail only
    public void Transpile_Xtrace_DecidedOnTheParsedCommands(string bash, bool traces)
        => Assert.Equal(traces, PsEmitter.CanSetXtrace(BashParser.Parse(bash)));

    [Theory]
    [InlineData("set -u; echo a", true)]
    [InlineData("set -euo pipefail", true)]
    [InlineData("o=nounset; set -o \"$o\"", true)]
    [InlineData("run() { \"$@\"; }; run set -u", true)]
    [InlineData("echo 'set -u nounset'", false)]
    [InlineData("sort -u f", false)]
    public void Transpile_Nounset_DecidedOnTheParsedCommands(string bash, bool checks)
        => Assert.Equal(checks, PsEmitter.CanSetNounset(BashParser.Parse(bash)));

    [Fact]
    public void Transpile_PipefailPipeline_IsOnePipelineElement()
    {
        // Dot-sourced block: current scope (PIPESTATUS, $__pf), streaming output, valid after && / in $( ) / as a condition.
        var ps = PsEmitter.Transpile("set -o pipefail; true && false | true")!;
        Assert.Contains("&& . { " + PsBuild.PipeStatusSlots + " = [int[]]::new(2); ", ps);
    }
}
