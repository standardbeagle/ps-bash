using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// exit / break / continue / return / errexit stop at every bash subshell boundary: <c>( )</c>, <c>$( )</c>,
/// <c>&lt;( )</c>, a pipe stage, and eval/source/trap text running in one. PowerShell's <c>exit</c> ends the whole
/// script and its <c>break</c>/<c>continue</c> are dynamically scoped (they cross script blocks to the caller's loop),
/// so before these fixes the cells below left the outer loop or ended the script. A sweep of 7 constructs x 51
/// contexts against bash failed 94 of 357; ScopeControlMatrixTests (Core) checks the emitted shape of the same cells.
/// </summary>
public class ScopeControlDifferentialTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string Tmp = "cd \"$(mktemp -d)\" || exit 99\n";

    // bash's diagnostics carry `bash: line N:` (or `./s.sh: line N:`); ps-bash keeps no line numbers at run time
    // (docs/specs/intentional-differences.md). Collect stderr and compare the messages without that location.
    private static string Diag(string script) => Tmp + "{ " + script + "\n} 2>e.txt; sed 's/^.*line [0-9]*: //' e.txt";

    // ---- break / continue with no loop of their own inside a subshell (R1) ----

    [SkippableFact]
    public async Task Differential_BreakInCommandSubstitutionInALoop_EndsOnlyTheSubstitution()
        => await AssertOracle.EqualAsync("for i in 1 2; do v=$( break; echo in ); echo \"after $? [$v]\"; echo \"loop $i\"; done\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ContinueInProcessSubstitutionInALoop_EndsOnlyThatSubshell()
        => await AssertOracle.EqualAsync("for i in 1 2; do cat <( continue; echo in ); echo \"after $?\"; echo \"loop $i\"; done\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_BreakInACompoundPipeStage_WarnsAndTheLoopGoesOn()
        => await AssertOracle.EqualAsync(Diag("for i in 1 2; do { break; echo in; } | cat; echo \"after ${PIPESTATUS[*]}\"; echo \"loop $i\"; done\necho \"end $?\""), timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ContinueInAPipeStageOfAnIfCondition_WarnsAndTheLoopGoesOn()
        => await AssertOracle.EqualAsync(Diag("for i in 1 2; do if { continue; echo in; } | cat; then echo yes; else echo no; fi; echo \"loop $i\"; done\necho \"end $?\""), timeout: Timeout);

    [SkippableFact]
    public async Task Differential_BreakInEvalTextInACommandSubstitution_EndsOnlyTheSubstitution()
        => await AssertOracle.EqualAsync("for i in 1 2; do v=$( eval 'break; echo in'; echo in2 ); echo \"after $? [$v]\"; echo \"loop $i\"; done\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_BreakInEvalTextAsAFirstStage_EndsOnlyTheStage()
        => await AssertOracle.EqualAsync("for i in 1 2; do eval 'break; echo in' | cat; echo \"after ${PIPESTATUS[*]}\"; echo \"loop $i\"; done\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_BreakInSourcedTextInASubshell_WarnsAndTheLoopGoesOn()
        => await AssertOracle.EqualAsync(Diag("for i in 1 2; do printf '%s\\n' 'break; echo in' > s.sh; ( source ./s.sh; echo in2 ); echo \"after $?\"; echo \"loop $i\"; done\necho \"end $?\""), timeout: Timeout);

    [SkippableFact]
    public async Task Differential_BreakInAnExitTrapOfASubshell_WarnsAndTheLoopGoesOn()
        => await AssertOracle.EqualAsync(Diag("for i in 1 2; do ( trap 'break; echo in-trap' EXIT; echo sub ); echo \"after $?\"; echo \"loop $i\"; done\necho \"end $?\""), timeout: Timeout);

    // ---- exit in eval / source / trap text inside a subshell (R2), <( f ) (R5), first stages (R6) ----

    [SkippableFact]
    public async Task Differential_ExitInEvalTextInAFunctionInASubshell_EndsOnlyTheSubshell()
        => await AssertOracle.EqualAsync("f() { eval 'exit 3; echo in'; echo in-f; }; (f); echo \"after $?\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInSourcedTextInACommandSubstitution_EndsOnlyTheSubstitution()
        => await AssertOracle.EqualAsync(Tmp + "printf '%s\\n' 'exit 3; echo in' > s.sh; v=$( source ./s.sh; echo in2 ); echo \"after $? [$v]\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInASubshellsExitTrap_SetsTheSubshellStatusOnly()
        => await AssertOracle.EqualAsync("( trap 'exit 3; echo in-trap' EXIT; echo sub ); echo \"after $?\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_BareExitInASubshellsExitTrap_KeepsThePreTrapStatus()
        => await AssertOracle.EqualAsync("( trap 'exit' EXIT; echo sub; false ); echo \"after $?\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInAFunctionInAProcessSubstitution_EndsOnlyThatSubshell()
        => await AssertOracle.EqualAsync("f() { exit 3; echo in-f; }; cat <(f); echo \"after $?\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInEvalTextAsAFirstStage_EndsOnlyTheStage()
        => await AssertOracle.EqualAsync("eval 'exit 3; echo in' | cat; echo \"after ${PIPESTATUS[*]}\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ExitInSourcedTextOfAFunctionStage_EndsOnlyTheStage()
        => await AssertOracle.EqualAsync(Tmp + "printf '%s\\n' 'exit 3; echo in' > s.sh; f() { source ./s.sh; echo in-f; }; f | cat; echo \"after ${PIPESTATUS[*]}\"\necho \"end $?\"", timeout: Timeout);

    // ---- errexit turned on inside a subshell (R3) ----

    [SkippableFact]
    public async Task Differential_ErrexitInAFunctionInACommandSubstitution_EndsOnlyTheSubstitution()
        => await AssertOracle.EqualAsync("f() { set -e; false; echo in-f; }; v=$(f); echo \"after $? [$v]\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ErrexitInASubshellInACommandSubstitution_EndsOnlyTheInnerSubshell()
        => await AssertOracle.EqualAsync("v=$( ( set -e; false; echo in ); echo \"inner $?\" ); echo \"after $? [$v]\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ErrexitInANegatedCompoundStage_EndsOnlyTheStage()
        => await AssertOracle.EqualAsync("! { set -e; false; echo in; } | cat; echo \"after $?\"\necho \"end $?\"", timeout: Timeout);

    [SkippableFact]
    public async Task Differential_ErrexitInSourcedTextInASubshell_EndsOnlyTheSubshell()
        => await AssertOracle.EqualAsync(Tmp + "printf '%s\\n' 'set -e; false; echo in' > s.sh; ( source ./s.sh; echo in2 ); echo \"after $?\"\necho \"end $?\"", timeout: Timeout);

    // ---- return outside a function in eval text at the top (R4) ----

    [SkippableFact]
    public async Task Differential_ReturnInEvalTextAtTheTop_ErrorsAndTheScriptGoesOn()
        => await AssertOracle.EqualAsync(Diag("eval 'return 4; echo in'; echo \"after $?\"\necho \"end $?\""), timeout: Timeout);
}
