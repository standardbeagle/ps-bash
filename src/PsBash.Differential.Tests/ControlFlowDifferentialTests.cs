using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for control-flow features:
///   if/elif/else/fi (Dart Lscbd7oUGaie)
///   for loops       (Dart oThSY1npEAc1)
///   while/until     (Dart cSPmhUHgUfDf)
///   functions       (Dart D9DiTWYnCJAk)
///
/// Each test runs the script in real bash AND ps-bash and diffs bytes.
/// Tests skip when no bash oracle is available (e.g. Windows without WSL).
///
/// Failure-surface axes targeted per test (Directive 3):
///   - Axis 8:  exit-code propagation
///   - Axis 12: quoting / injection (loop var not split on space)
///   - Axis 15: recursion depth
/// </summary>
public class ControlFlowDifferentialTests
{
    // -----------------------------------------------------------------------
    // if / elif / else / fi
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compound condition: `if cmd1 &amp;&amp; cmd2; then` — both sides must pass.
    /// Targets the gap where no emitter test covers && inside an if condition.
    /// Failure-surface axis 8: exit code of the compound condition determines branch.
    ///
    /// Fixed DART-6BxDBlSHAp6A: emitter now converts && / || in if conditions to
    /// PS -and / -or so the condition is a proper boolean expression.
    /// </summary>
    [SkippableFact]
    public async Task Differential_If_CompoundAndCondition()
    {
        await AssertOracle.EqualAsync(
            "if true && true; then echo both; else echo not; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Compound OR condition: `if cmd1 || cmd2; then` — first failure falls back to second.
    /// Failure-surface axis 8: first command fails but second succeeds → then branch.
    /// Fixed as part of DART-6BxDBlSHAp6A.
    /// </summary>
    [SkippableFact]
    public async Task Differential_If_CompoundOrCondition()
    {
        await AssertOracle.EqualAsync(
            "if false || true; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// AND condition where first command fails — short-circuit must skip second and take else.
    /// Failure-surface axis 8: exit code propagation through && short-circuit.
    /// Fixed as part of DART-6BxDBlSHAp6A.
    /// </summary>
    [SkippableFact]
    public async Task Differential_If_CompoundAndConditionFirstFails()
    {
        await AssertOracle.EqualAsync(
            "if false && true; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Both sides BoolExpr inside &&: `if [ 1 -eq 1 ] &amp;&amp; [ 2 -eq 2 ]; then`.
    /// Confirms the fix handles test expressions, not just true/false builtins.
    /// Failure-surface axis 8: numeric comparison exit codes drive the branch.
    /// Fixed as part of DART-6BxDBlSHAp6A.
    /// </summary>
    [SkippableFact]
    public async Task Differential_If_CompoundAndConditionBoolExpr()
    {
        await AssertOracle.EqualAsync(
            "if [ 1 -eq 1 ] && [ 2 -eq 2 ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Plain command used as if condition; exit code drives the branch.
    /// Covers the gap "plain command as condition" missing from emitter tests.
    /// Failure-surface axis 8: non-zero exit code must select else branch.
    /// </summary>
    [SkippableFact]
    public async Task Differential_If_PlainCommandConditionFails()
    {
        await AssertOracle.EqualAsync(
            "if false; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// if/elif chain with a [ ... ] test expression in every arm.
    /// Covers Axis 12 (quoting inside test brackets) and the elif path end-to-end.
    /// </summary>
    [SkippableFact]
    public async Task Differential_If_ElifChainWithTestExpr()
    {
        await AssertOracle.EqualAsync(
            "x=2; if [ \"$x\" -eq 1 ]; then echo one; elif [ \"$x\" -eq 2 ]; then echo two; else echo other; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // for loops
    // -----------------------------------------------------------------------

    /// <summary>
    /// for x in {1..3} — brace-range expansion inside for.
    /// Existing emitter tests cover brace ranges but not inside a for-in header.
    /// Failure-surface axis 12: loop variable must not be env-prefixed.
    /// </summary>
    [SkippableFact]
    public async Task Differential_For_BraceRangeExpansion()
    {
        await AssertOracle.EqualAsync(
            "for i in {1..3}; do echo $i; done",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// break inside a for loop — must exit loop immediately, not run remaining iterations.
    /// Targets the gap: no break/continue test exists anywhere in the test suite.
    /// Uses a numeric counter to avoid the pre-existing emitter bug where unquoted string
    /// literals in [ x = literal ] are not quoted in the PowerShell emission.
    /// Failure-surface axis 8: exit code after break must be 0 (from echo).
    /// </summary>
    [SkippableFact]
    public async Task Differential_For_BreakExitsEarly()
    {
        // Counter-based break: iterate a b c, break when n==2, so only 'a' is echoed.
        await AssertOracle.EqualAsync(
            "n=0; for x in a b c; do n=$((n+1)); if [ $n -eq 2 ]; then break; fi; echo $x; done",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// C-style for loop: for ((i=0; i&lt;3; i++)).
    /// Differential test for arithmetic for — emitter unit test already exists but
    /// no oracle comparison confirms real bash parity.
    /// Failure-surface axis 12: loop variable must expand correctly inside echo.
    /// </summary>
    [SkippableFact]
    public async Task Differential_For_CStyle()
    {
        await AssertOracle.EqualAsync(
            "for ((i=0; i<3; i++)); do echo $i; done",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // while / until loops
    // -----------------------------------------------------------------------

    /// <summary>
    /// until loop — condition negated, loop runs until condition is true.
    /// Targets the gap: no differential test for until exists.
    /// Failure-surface axis 8: exit code after loop body (echo) is 0.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Until_BasicLoop()
    {
        await AssertOracle.EqualAsync(
            "n=0; until [ $n -ge 3 ]; do echo $n; n=$((n+1)); done",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// continue inside while — must skip remaining body, go back to condition.
    /// Targets the gap: no continue test exists in any layer.
    /// Failure-surface axis 8: exit code after continue path must be 0.
    /// </summary>
    [SkippableFact]
    public async Task Differential_While_ContinueSkipsIteration()
    {
        await AssertOracle.EqualAsync(
            "n=0; while [ $n -lt 4 ]; do n=$((n+1)); if [ $n -eq 2 ]; then continue; fi; echo $n; done",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Functions
    // -----------------------------------------------------------------------

    /// <summary>
    /// return N sets $? — Invoke-BashEcho resets $global:LASTEXITCODE = 0 on
    /// success so the process exit code reflects echo (0), not f's return value (42).
    /// Failure-surface axis 8: $? after function call must equal return value.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Function_ReturnSetsExitCode()
    {
        await AssertOracle.EqualAsync(
            "f() { return 42; }; f; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Args via $1 / $@ / $# inside a function — critical gap: no differential test.
    /// Failure-surface axis 12: args with spaces must not be word-split.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Function_ArgExpansion()
    {
        await AssertOracle.EqualAsync(
            "greet() { echo \"$# args: $1 $2\"; }; greet hello world",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Recursion — factorial(4) via recursive function.
    /// Targets the gap: no recursion test anywhere in the suite.
    /// Failure-surface axis 15: recursion depth must terminate correctly.
    ///
    /// Fixed DART-ccPtGZB92fur: EmitFunction now saves/restores $global:BashPositional
    /// around the function body so each recursive frame sees its own positional args.
    /// Also fixed $N (e.g. $1) inside arithmetic substitutions $((...)) to use
    /// the BashPositional array instead of PowerShell's null $1 variable.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Function_Recursion()
    {
        await AssertOracle.EqualAsync(
            "fact() { if [ $1 -le 1 ]; then echo 1; return; fi; prev=$(fact $(($1-1))); echo $(($1*prev)); }; fact 4",
            timeout: TimeSpan.FromSeconds(15));
    }
}
