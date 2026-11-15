using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A function called as a pipe stage runs in that stage's subshell: <c>exit</c> in its body ends only the stage
/// (<c>f | cat</c> → PIPESTATUS <c>3 0</c>), and its assignments stay there. The same holds for <c>(f)</c> and
/// <c>$(f)</c>. ps-bash emits the body once, so its <c>exit</c> decides at run time (<c>PsBuild.DynamicExit</c>);
/// before, every one of these ended the whole script.
/// </summary>
public class FunctionPipeStageExitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task Differential_ExitInFunctionFirstStage_EndsOnlyTheStage()
        => await AssertOracle.EqualAsync("f() { echo x; exit 3; }; f | cat; echo \"after ${PIPESTATUS[*]}\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInFunctionLastStage_EndsOnlyTheStage()
        => await AssertOracle.EqualAsync("g() { cat; exit 4; }; echo a | g; echo \"after ${PIPESTATUS[*]}\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInFunctionMiddleStage_EndsOnlyTheStage()
        => await AssertOracle.EqualAsync("g() { cat; exit 4; }; echo b | g | cat; echo \"after ${PIPESTATUS[*]}\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInNestedFunctionCallOfAStage_EndsOnlyTheStage()
        => await AssertOracle.EqualAsync("f() { echo x; exit 3; }; n() { f; echo not-here; }; n | cat; echo \"after ${PIPESTATUS[*]}\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitStatusInFunctionStage_KeepsTheLowEightBits()
        => await AssertOracle.EqualAsync("k() { exit 300; }; k | cat; echo \"wrap ${PIPESTATUS[*]}\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInFunctionStage_UnderPipefail()
        => await AssertOracle.EqualAsync("set -o pipefail; f() { echo x; exit 3; }; f | cat; echo \"after $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_FunctionStage_DoesNotLeakAssignment()
        => await AssertOracle.EqualAsync("s() { x=2; echo s; }; x=1; s | cat; echo \"x=$x\"; echo z | s; echo \"x=$x\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_FunctionLastStageUnderLastpipe_RunsInTheShell()
        => await AssertOracle.EqualAsync("shopt -s lastpipe; l() { y=7; }; y=1; echo q | l; echo \"y=$y\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInFunctionInSubshellAndSubstitution_EndsOnlyThem()
        => await AssertOracle.EqualAsync("f() { echo x; exit 3; }; (f); echo \"paren $?\"; v=$(f); echo \"sub $? $v\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ErrexitInFunctionInSubshell_EndsOnlyTheSubshell()
        => await AssertOracle.EqualAsync("h() { false; echo no; }; ( set -e; h; echo after-h ); echo \"rc $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInFunctionCalledPlainly_StillEndsTheScript()
        => await AssertOracle.EqualAsync("f() { echo x; exit 3; }; f | cat; f; echo never", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ReturnInFunctionStage_IsTheStageStatus()
        => await AssertOracle.EqualAsync("r() { echo r; return 5; }; r | cat; echo \"after ${PIPESTATUS[*]}\"", timeout: Timeout);
}
