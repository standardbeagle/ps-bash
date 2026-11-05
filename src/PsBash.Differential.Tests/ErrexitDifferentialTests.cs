using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for <c>set -e</c> (errexit). Each script runs in real bash AND ps-bash and
/// stdout/stderr/exit are diffed.
///
/// Covered regression: errexit only fired for the <c>false</c> builtin and PowerShell-terminating
/// errors — an ordinary failing cmdlet, a native's non-zero exit, or a failing function never ended
/// the script (<c>set -e; cat /nofile; echo after</c> printed "after", exit 0). And once checks exist,
/// bash's exemptions must hold: conditions, non-final <c>&amp;&amp;</c>/<c>||</c> operands, <c>!</c>,
/// pipeline stages and command substitutions never end the script — including inside a FUNCTION
/// called from one (the old <c>false</c> path aborted <c>g &amp;&amp; echo ok</c> from inside g).
///
/// <c>F</c> is a failing command with its stderr silenced (ps-bash names the operand C:/nofile on
/// Windows; stderr is part of the comparison).
/// </summary>
public class ErrexitDifferentialTests
{
    private const string F = "cat /nofile 2>/dev/null";

    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    // ── a failure at statement position ends the script ──────────────────────
    [SkippableFact]
    public Task Errexit_FailingCmdlet_Exits() => Eq($"set -e; echo a; {F}; echo never");

    [SkippableFact]
    public Task Errexit_FailingChildProcess_ExitsWithItsStatus() => Eq("set -e; bash -c 'exit 3'; echo never");

    [SkippableFact]
    public Task Errexit_InsideFunction_Exits() => Eq($"f() {{ {F}; echo in; }}; set -e; f; echo never");

    [SkippableFact]
    public Task Errexit_InsideIfBodyAndLoop_Exits()
        => Eq($"set -e; if true; then echo t; fi; for i in 1 2; do echo i=$i; {F}; done; echo never");

    [SkippableFact]
    public Task Errexit_BraceGroupAndCaseArm_Exit()
        => Eq($"set -e; case a in a) echo arm; {{ {F}; echo in; }};; esac; echo never");

    [SkippableFact]
    public Task Errexit_Subshell_ExitsThenParentExits() => Eq($"set -e; ({F}; echo in); echo never");

    [SkippableFact]
    public Task Errexit_TestAndArithStatements_Exit() => Eq("set -e; echo a; [ -f /nofile ]; echo never");

    [SkippableFact]
    public Task Errexit_AssignmentFromFailingSubstitution_Exits() => Eq($"set -e; x=$({F}); echo never");

    [SkippableFact]
    public Task Errexit_FinalOperandOfList_Exits() => Eq($"set -e; true && {F}; echo never");

    [SkippableFact]
    public Task Errexit_LastPipelineStage_Exits() => Eq($"set -e; echo hi | {F}; echo never");

    [SkippableFact]
    public Task Errexit_FunctionReturnStatus_Exits() => Eq("set -e; f() { return 2; }; f; echo never");

    [SkippableFact]
    public Task Errexit_SubshellOfFailedList_Exits() => Eq("set -e; (false && true); echo never");

    // ── bash's exemptions ────────────────────────────────────────────────────
    [SkippableFact]
    public Task Errexit_Conditions_AreExempt()
        => Eq($"set -e; if {F}; then :; elif {F}; then :; fi; while {F}; do :; done; until true; do :; done; echo after");

    [SkippableFact]
    public Task Errexit_FunctionCalledFromCondition_RunsToTheEnd()
        => Eq($"f() {{ {F}; echo in; }}; set -e; if f >/dev/null; then echo yes; fi; f || echo caught; ! f; echo after");

    [SkippableFact]
    public Task Errexit_NonFinalListOperands_AreExempt()
        => Eq($"set -e; {F} || true; false && true; {F} || {F} || echo third; echo after");

    [SkippableFact]
    public Task Errexit_NestedFunctionsUnderList_RunToTheEnd()
        => Eq("f() { false; echo in-f; }; g() { f; echo in-g; }; set -e; g && echo ok; echo after");

    [SkippableFact]
    public Task Errexit_NegatedAndNonFinalPipelineStage_AreExempt()
        => Eq($"set -e; ! {F}; {F} | cat; false | true; echo after");

    [SkippableFact]
    public Task Errexit_CommandSubstitutionBody_DoesNotInherit()
        => Eq($"set -e; x=$({F}; echo in); echo \"[$x]\"; export Y=$(false); echo after");

    [SkippableFact]
    public Task Errexit_CompoundStatusFromIgnoredFailure_DoesNotExit()
        => Eq("set -e; { false && true; }; for i in 1; do false && true; done; echo after");

    [SkippableFact]
    public Task Errexit_FunctionEndingInUntakenIfOrFinishedLoop_ReturnsZero()
        => Eq("set -e; f() { if false; then :; fi; }; g() { i=0; while [ $i -lt 2 ]; do i=$((i+1)); done; }; f; g; echo after");

    [SkippableFact]
    public Task Errexit_GuardIdioms_Survive()
        => Eq($"set -e; {F} || echo fallback; for i in 1 2; do [ $i = 2 ] && break; echo i=$i; done; echo after");

    [SkippableFact]
    public Task Errexit_OrExit_UsesItsOwnStatus() => Eq($"set -e; {F} || exit 4; echo never");

    [SkippableFact]
    public Task Errexit_SetPlusE_TurnsItOff() => Eq($"set -e; set +e; {F}; echo after");

    [SkippableFact]
    public async Task Nounset_UnsetRead_EndsTheScript()
    {
        // GNU bash 5.2: prints "before", reports `bash: line 1: x: unbound variable`, exits 1. The message prefix
        // differs by design (intentional-differences.md), so stdout and status are asserted directly.
        var result = await AssertOracle.RunPsBashOnlyAsync("set -eu; echo before; echo [$undefined_var_x]; echo never", TimeSpan.FromSeconds(30));
        Assert.Equal(127, result.ExitCode); // bash -c: a fatal unbound-variable read ends the shell with 127
        Assert.Equal("before", result.Stdout.Trim());
        Assert.Contains("undefined_var_x: unbound variable", result.Stderr);
    }

    // The report goes through the error stream where the read happens, so the group's own redirect silences it
    // (a thrown error used to bypass it). bash -c exits 127 here and a script file 1, so status is asserted directly.
    [SkippableTheory]
    [InlineData("set -u; echo before; { echo [$undefined_var_x]; } 2>/dev/null; echo never")]
    [InlineData("echo before; { echo ${undefined_var_x:?gone}; } 2>/dev/null; echo never")]
    [InlineData("set -u; echo before; { echo $((undefined_var_x + 1)); } 2>/dev/null; echo never")]
    [InlineData("set -u; echo before; { (( undefined_var_x++ )); } 2>/dev/null; echo never")]
    public async Task ParamError_UnderGroupRedirect_IsSilencedAndEndsTheScript(string script)
    {
        var result = await AssertOracle.RunPsBashOnlyAsync(script, TimeSpan.FromSeconds(30));
        Assert.Equal(127, result.ExitCode);
        Assert.Equal("before", result.Stdout.Trim());
        Assert.DoesNotContain(result.Stderr.Split('\n'), l => l.StartsWith("ps-bash: ", StringComparison.Ordinal)); // [ps-bash] lines are the harness trace
    }
    [SkippableFact]
    public Task TestAndArithCommands_AcceptTrailingRedirects()
        => Eq("[[ 1 -eq 1 ]] 2>/dev/null; echo a; (( 1 )) >/dev/null; echo b; [[ x == y ]] 2>/dev/null || echo no; (( 0 )) 2>/dev/null || echo zero; f=$(mktemp); [[ -n $f ]] > \"$f\"; wc -c < \"$f\"; rm -f \"$f\"");

    [SkippableFact]
    public Task Nounset_DefaultForms_AreNotChecked() => Eq("set -u; echo [${u1:-d}] [${u2-d}] [${u3+x}]; echo after");

    [SkippableFact]
    public Task Nounset_PlusU_TurnsItOff() => Eq("set -u; set +u; echo [$undefined_var_x]; echo after");

    // noclobber: `>` refuses an existing file (status 1, command output dropped), `>|` and `>>` do not.
    // The refusal message prefix differs by design, so stderr is silenced around the group.
    [SkippableFact]
    public Task Noclobber_RefusesOverwriteButAllowsForceAndAppend()
        => Eq("cd \"$(mktemp -d)\"; echo a > f; set -C; { echo b > f; } 2>/dev/null; echo s=$?; echo c >| f; echo d >> f; echo n > g; cat f g; set +C; echo e > f; cat f");

    [SkippableFact]
    public Task Noclobber_OptionNameForm_And_NewFileAllowed()
        => Eq("cd \"$(mktemp -d)\"; set -o noclobber; echo a > f; { echo b > f; } 2>/dev/null; echo s=$?; cat f");
    [SkippableFact]
    public Task Errexit_BackgroundJob_IsExempt() => Eq($"set -e; {F} & wait; echo after");
}
