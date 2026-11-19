using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A pipeline under pipefail in every position it can stand in, and the word <c>pipefail</c> as plain data. REGRESSION:
/// the emitter turned per-stage capture on for any text containing "pipefail" (a quoted grep pattern too), and the
/// capture was a statement list that PowerShell refused after <c>&amp;&amp;</c>, so <c>grep -c pipefail f &amp;&amp; echo y</c>
/// was "parse error: ... $__pf = [int[]] ... Unexpected token '='". The mode is now decided on the parsed commands
/// (PsEmitter.CanSetPipefail) and the capture is one pipeline element (PsBuild.PipefailPipeline).
/// </summary>
public class PipefailPositionDifferentialTests
{
    private const string Setup = "cd \"$(mktemp -d)\" || exit; printf 'a pipefail\\nb\\nc pipefail\\n' > f\n";

    [SkippableFact]
    public Task PipefailWord_AsQuotedData_RunsEveryPosition() => AssertOracle.EqualAsync(Setup
        + "grep -c pipefail f && echo y\n"
        + "x=$(grep pipefail f | head -1); echo \"x=$x\"\n"
        + "grep pipefail f | head -1 || echo no\n"
        + "if grep pipefail f | head -1; then echo then; fi\n"
        + "! grep pipefail f | head -1; echo \"neg=$?\"\n"
        + "grep -n 'set -o pipefail' f && grep b f | head -1\n");

    [SkippableFact]
    public Task Pipefail_PipelineInChainsAndConditions() => AssertOracle.EqualAsync(Setup
        + "set -o pipefail\n"
        + "false | true && echo bad || echo ok1\n"
        + "true && false | true; echo \"and=$?\"\n"
        + "false | true || echo \"or=$?\"\n"
        + "[ 1 ] && echo a | cat && echo b | cat\n"
        + "if false | true; then echo bad; else echo ok2; fi\n"
        + "while false | true; do break; done; echo \"while=$?\"\n"
        + "! false | true; echo \"neg=$?\"\n"
        + "{ false | true; } && echo bad3 || echo ok3\n");

    [SkippableFact]
    public Task Pipefail_PipelineInSubstitutionsFunctionsAndCompounds() => AssertOracle.EqualAsync(Setup
        + "set -o pipefail\n"
        + "x=$(false | true); echo \"sub=$?\"\n"
        + "y=$(echo hi | cat); echo \"y=$y\"\n"
        + "echo \"$(false | true; echo st=$?)\"\n"
        + "f() { false | true; }; f; echo \"fn=$?\"\n"
        + "f2() { false | true && echo inner; }; f2 || echo \"f2=$?\"\n"
        + "( false | true ); echo \"sub2=$?\"\n"
        + "case x in x) false | true ;; esac; echo \"case=$?\"\n"
        + "for i in 1; do false | true; done; echo \"for=$?\"\n"
        + "z=$( (false | true) && echo yes || echo no ); echo \"z=$z\"\n"
        + "echo pre | { false | true; echo \"grp=$?\"; }\n"
        + "true | false | true; echo \"ps=${PIPESTATUS[*]}\"\n");

    /// <summary>The option turned on at run time: a word known only then, eval text, and off again.</summary>
    [SkippableFact]
    public Task Pipefail_TurnedOnAtRunTime_StillCapturesEveryStage() => AssertOracle.EqualAsync(
        "false | true; echo \"off=$?\"\n"
        + "o=pipefail; set -o \"$o\"; false | true; echo \"dyn=$?\"\n"
        + "set +o pipefail\n"
        + "eval 'set -o pipefail'; false | true; echo \"eval=$?\"\n"
        + "set +o pipefail\n"
        + "run() { \"$@\"; }; run set -o pipefail; false | true; echo \"run=$?\"\n");
}
