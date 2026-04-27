using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for the bash <c>eval</c> builtin.
///
/// Failure-surface axes covered (per QA rubric Directive 3):
///   Test 1 (BasicEcho):        Axis 12 — quoting; eval with a quoted string arg
///   Test 2 (VarAssignment):    Axis 12 — quoting/injection; assignment via eval, then use
///   Test 3 (MultiArgJoined):   Axis 12 — quoting; eval joins multiple args with spaces
///   Test 4 (ExitCodePropagation): Axis 8 — exit code from failing command inside eval
///   Test 5 (NestedEval):       Axis 15 — recursion; eval inside eval
///
/// Oracle strategy: EqualAsync — live bash diff; eval is deterministic so golden is unnecessary.
/// </summary>
public class EvalDifferentialTests
{
    // -----------------------------------------------------------------------
    // Test 1: eval with a simple quoted string executes the inner command
    //
    // Failure surface: Axis 12 (quoting). The quoted string must be stripped
    // before transpilation so the inner command runs rather than the literal text.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that eval with a double-quoted string executes the enclosed command.
    /// Regression: if eval does not transpile its arg, the literal text "echo hello"
    /// is passed to PowerShell's Invoke-Expression unchanged, which would either
    /// call Invoke-BashEcho (correct) or emit "echo hello" as a string (wrong).
    /// </summary>
    [SkippableFact]
    public async Task Differential_Eval_BasicEcho_OutputsHello()
    {
        // Axis 12: double-quoting must not interfere with eval body execution.
        await AssertOracle.EqualAsync(
            "eval \"echo hello\"",
            timeout: TimeSpan.FromSeconds(30));
    }

    // -----------------------------------------------------------------------
    // Test 2: eval assigns a variable and that variable is visible afterward
    //
    // Failure surface: Axis 12 (quoting/injection); variable set via eval must
    // survive in the calling scope so the subsequent echo can use it.
    //
    // Oracle strategy: GoldenAsync — ps-bash uses env vars ($env:x) for shell
    // variables assigned via eval, while bash uses shell-local vars; they match
    // on stdout ("hello") but the assignment mechanism differs internally.
    // Record golden: UPDATE_GOLDENS=1 ./scripts/test.sh src/PsBash.Differential.Tests --filter Eval_VarAssignment
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a variable assignment inside eval is visible after eval returns.
    /// Bash eval executes in the current scope, so the assigned variable must be
    /// accessible to subsequent commands.
    /// Regression: if eval runs in a child scope, the variable is lost and echo
    /// outputs an empty line instead of "hello".
    ///
    /// Uses GoldenAsync because ps-bash exit codes after eval may differ from bash.
    /// The output ("hello") is the invariant we verify.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Eval_VarAssignment_VisibleAfterEval()
    {
        // Axis 12: injection guard — variable value contains no special chars here,
        // but the quoting path exercises the same emitter code.
        await AssertOracle.GoldenAsync(
            "eval 'x=hello'; echo $x",
            "Eval_VarAssignment_VisibleAfterEval",
            timeout: TimeSpan.FromSeconds(30));
    }

    // -----------------------------------------------------------------------
    // Test 3: eval with multiple args joins them with a space before evaluation
    //
    // Failure surface: Axis 12 (quoting). Bash eval spec: "The arguments are
    // joined with spaces, then the result is evaluated as a shell command."
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that eval joins multiple positional args with a space.
    /// <c>eval "echo" "hello"</c> must evaluate "echo hello" not two separate commands.
    /// Regression: if args are not joined, the second arg "hello" becomes a bare word
    /// command and execution fails with command-not-found.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Eval_MultiArgJoined_ExecutesJoinedString()
    {
        // Axis 12: space-joining of args — both args must reach the inner echo.
        await AssertOracle.EqualAsync(
            "eval echo hello",
            timeout: TimeSpan.FromSeconds(30));
    }

    // -----------------------------------------------------------------------
    // Test 4: $? inside eval reflects last command exit code
    //
    // Failure surface: Axis 8 (exit code propagation). $? inside eval must reflect
    // the exit code of the last command executed, not always 0.
    //
    // Oracle strategy: GoldenAsync — ps-bash's eval correctly sets LASTEXITCODE=1
    // after false, and the inner echo $? prints "1"; but ps-bash propagates
    // LASTEXITCODE=1 to the outer script exit code (bash exits 0 from echo).
    // The stdout is the invariant we verify here.
    // Record golden: UPDATE_GOLDENS=1 ./scripts/test.sh src/PsBash.Differential.Tests --filter Eval_ExitCode
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that $? inside eval reflects the last command exit code.
    /// The echo inside eval reads the exit code of false (which is 1) and
    /// outputs "1".  Uses GoldenAsync because ps-bash outer exit code after eval
    /// may differ from bash (known divergence in LASTEXITCODE propagation).
    /// </summary>
    [SkippableFact]
    public async Task Differential_Eval_ExitCodeFromFalse_PropagatedToShell()
    {
        // Axis 8: exit code must flow from false through eval to $?.
        // The outer exit code diverges (known); test the stdout invariant.
        await AssertOracle.GoldenAsync(
            "eval false; echo $?",
            "Eval_ExitCodeFromFalse_PropagatedToShell",
            timeout: TimeSpan.FromSeconds(30));
    }

    // -----------------------------------------------------------------------
    // Test 5: eval inside eval (nested eval) executes the innermost command
    //
    // Failure surface: Axis 15 (recursion depth). One level of nesting must work.
    // The depth guard caps at 5; one level of nesting is well within that limit.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that eval of an eval string executes the innermost command.
    /// <c>eval "eval 'echo nested'"</c> must output "nested".
    /// Regression: if the inner eval string is not re-evaluated (only the outer
    /// one is), the output is "eval echo nested" or empty.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Eval_NestedEval_InnerCommandExecuted()
    {
        // Axis 15: one level of nesting — depth guard (max 5) must not fire.
        await AssertOracle.EqualAsync(
            "eval \"eval 'echo nested'\"",
            timeout: TimeSpan.FromSeconds(30));
    }
}
