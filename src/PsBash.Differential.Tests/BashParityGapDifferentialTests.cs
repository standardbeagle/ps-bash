using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Known bash-parity gaps found by the 2026 project review. Each case is red until its gap is closed;
/// run or exclude them with <c>--filter "Category=KnownGap"</c>. Cases are live-only (no cassette) so
/// they never fail a replay run for a missing recording. Move a case to its feature's own test class
/// when it turns green.
/// </summary>
[Trait("Category", "KnownGap")]
public class BashParityGapDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30), liveOnly: true);

    // A deterministic case (no env, timing, PID or filesystem dependence) replays from its cassette, so it runs in every gate.
    private static Task Recorded(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    // --- Variable model: scalars live in $env:, with a static "local" overlay ---------------------

    [SkippableFact] public Task Local_IsVisibleToCallee()
        => Same("f(){ local v=1; g; }; g(){ echo \"v=$v\"; }; f");

    [SkippableFact] public Task Local_DoesNotLeakAfterReturn()
        => Same("f(){ local v=1; }; f; echo \"[${v-unset}]\"");

    [SkippableFact] public Task Declare_InFunction_IsLocal()
        => Same("f(){ declare y=5; }; f; echo \"[${y-unset}]\"");

    [SkippableFact] public Task Readonly_RejectsReassignment()
        => Same("readonly r=1; r=2; echo \"r=$r\"");

    [SkippableFact] public Task VariableNames_AreCaseSensitive()
        => Same("x=1; X=2; echo \"$x $X\"");

    [SkippableFact] public Task LargeValue_SurvivesAssignment()
        => Same("big=$(head -c 40000 /dev/zero | tr '\\0' a); echo ${#big}");

    [SkippableFact] public Task DeclareInteger_EvaluatesArithmetic()
        => Same("declare -i n=3; n+=2; echo $n");

    [SkippableFact] public Task DeclareNameref_WritesThrough()
        => Same("v=a; declare -n ref=v; ref=b; echo $v");

    [SkippableFact] public Task LocalIfs_AffectsStarJoin()
        => Same("f(){ local IFS=,; echo \"$*\"; }; f a b c");

    [SkippableFact] public Task UnexportedVariable_IsNotInChildEnvironment()
        => Same("x=1; env | grep -c '^x='");

    // --- Expansion order: word-split splat hoisted ahead of side-effecting words ------------------

    [SkippableFact] public Task ArithPostIncrement_ThenPlainExpansion()
        => Same("x=5; echo $((x++)) $x");

    [SkippableFact] public Task ArithAssign_ThenPlainExpansion()
        => Same("x=1; echo $((x+=2)) $x");

    [SkippableFact] public Task DefaultAssign_ThenPlainExpansion()
        => Same("unset y; echo ${y:=d} $y");

    [SkippableFact] public Task ArraySliceNextToLength()
        => Same("a=(1 2 3); echo ${#a[@]} ${a[@]:1}");

    // --- Silent wrong output that should be correct or exit 2 -------------------------------------

    [SkippableFact] public Task Printf_PercentQ()
        => Same("printf '%q\\n' 'a b'");

    [SkippableFact] public Task Printf_TimeFormat()
        => Same("printf '%(%Y)T\\n' 0");

    [SkippableFact] public Task SetU_UnboundVariableFails()
        => Same("set -u; echo ${undefined_var}; echo after");

    [SkippableFact] public Task SetF_DisablesGlob()
        => Same("cd \"$(mktemp -d)\"; touch a b; set -f; echo *");

    [SkippableFact] public Task ErrTrap_Fires()
        => Same("trap 'echo trapped' ERR; false; echo ok");

    [SkippableFact] public Task SignalTrap_Fires()
        => Same("trap 'echo U' USR1; kill -USR1 $$; echo ok");

    [SkippableFact] public Task Funcname_InFunction()
        => Same("f(){ echo \"$FUNCNAME\"; }; f");

    [SkippableFact] public Task Lineno_IsSet()
        => Same("echo $LINENO");


    // --- Missing builtins ------------------------------------------------------------------------

    [SkippableFact] public Task Getopts_Loop()
        => Same("set -- -a -b X; while getopts 'ab:' o; do echo \"$o $OPTARG\"; done");

    [SkippableFact] public Task Getopts_ExplicitArgs()
        => Same("getopts 'ab:' o -a; echo \"o=$o\"");

    [SkippableFact] public Task Pipestatus_AfterPipeline()
        => Same("true | false | true; echo \"${PIPESTATUS[@]}\"");

    [SkippableFact] public Task Pipefail_ReportsFailure()
        => Same("set -o pipefail; false | true; echo $?");

    // REGRESSION: a function is PowerShell code, whose binder consumed a bare `--` and split `-x:y` into `-x:` and `y`.
    [SkippableFact] public Task Function_ReceivesDashOperandsAsTyped()
        => Recorded("f() { printf '[%s]' \"$@\"; echo; }; g() { f \"$@\"; }; f -e -x:y --long -- 1 '--' -e; g -e -x:y -- 1 --%");

    // REGRESSION: under pipefail a pipeline after `&&` / `||` was a PowerShell parse error for the whole script.
    // Each operand's status, PIPESTATUS and output order must also match bash.
    [SkippableFact] public Task Pipefail_PipelineAsAndOrOperand()
        => Recorded("set -o pipefail; true && echo a | cat; false || echo b | cat; echo c | false && echo no; echo st=$?; "
                + "true && false | true; echo st=$? \"${PIPESTATUS[@]}\"; ! echo d | cat && echo no2; echo st=$?; "
                + "true && echo e | false || echo f | cat; echo st=$?");

    // REGRESSION: a `!` operand of `&&` / `||` flipped the status even when the chain skipped it (`false && ! true &&
    // echo no` printed no), and `true && ! x=1` was a PowerShell parse error for the whole line.
    [SkippableFact] public Task Negation_AsAndOrOperand_FlipsOnlyWhenItRuns()
        => Recorded("false && ! true && echo no1; echo st=$?; false && ! true; echo st=$?; true || ! false; echo st=$?; "
                + "true || ! false || echo no4; echo st=$?; false && ! echo no5; echo st=$?; "
                + "true && ! x=1; echo \"st=$? x=$x\"; ! false && ! true || echo fb; echo st=$?");
}
