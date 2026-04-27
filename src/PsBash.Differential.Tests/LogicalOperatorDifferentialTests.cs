using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for logical operators:
///   &amp;&amp; / || chains      (Command.AndOrList, standalone — uses PS && / ||)
///   ! negation           (Command.Pipeline.Negated — emits LASTEXITCODE flip)
///   $? after chain       (exit-code propagation from the last arm)
///   set -e interaction   (false || true must survive errexit)
///   compound with braces ([[ ]] test + brace group in an && chain)
///
/// Each test runs the script in real bash AND ps-bash and diffs bytes.
/// Tests skip when no bash oracle is available (e.g. Windows without WSL).
///
/// Failure-surface axes targeted per test (Directive 3):
///   Axis 8:  exit-code propagation through && / || chains and ! negation
///   Axis 11: environment leak — VAR=x cmd must not bleed into outer scope
///   Axis 12: quoting / injection — args with spaces in echo args
/// </summary>
public class LogicalOperatorDifferentialTests
{
    // -----------------------------------------------------------------------
    // Basic && (AND-IF)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Basic &amp;&amp;: both sides succeed → second command runs.
    /// Failure-surface axis 8: both exit codes must be 0; echo must fire.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_BasicAnd_BothSucceed()
    {
        await AssertOracle.EqualAsync(
            "true && echo yes",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Short-circuit &amp;&amp;: first side fails → second command must NOT run.
    /// Failure-surface axis 8: false exits 1; && must suppress echo.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_And_ShortCircuitOnFailure()
    {
        await AssertOracle.EqualAsync(
            "false && echo nope",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Basic || (OR-IF)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Basic ||: first side fails → second command runs as fallback.
    /// Failure-surface axis 8: false exits 1; || must trigger echo.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_BasicOr_FallbackOnFailure()
    {
        await AssertOracle.EqualAsync(
            "false || echo fallback",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Short-circuit ||: first side succeeds → second command must NOT run.
    /// Failure-surface axis 8: true exits 0; || must suppress echo.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_Or_ShortCircuitOnSuccess()
    {
        await AssertOracle.EqualAsync(
            "true || echo nope",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Mixed chain (left-associative, NOT ternary)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Mixed chain: `false &amp;&amp; echo a || echo b`.
    /// bash parses this left-associatively: (false &amp;&amp; echo a) || echo b.
    /// false &amp;&amp; echo a is a failed chain (exit 1); || echo b fires.
    /// Result: only "b" on stdout.
    /// Failure-surface axis 8: chain exit code drives the || arm selection.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_MixedChain_LeftAssociative()
    {
        await AssertOracle.EqualAsync(
            "false && echo a || echo b",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Mixed chain where first arm succeeds: `true &amp;&amp; echo a || echo b`.
    /// (true &amp;&amp; echo a) succeeds (exit 0 from echo); || echo b must NOT fire.
    /// Result: only "a" on stdout.
    /// Failure-surface axis 8: successful && chain suppresses the || fallback.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_MixedChain_FirstArmSucceeds()
    {
        await AssertOracle.EqualAsync(
            "true && echo a || echo b",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // ! negation
    // -----------------------------------------------------------------------

    /// <summary>
    /// ! negation on a failing command: `! false &amp;&amp; echo yes`.
    /// ! false inverts exit code to 0; &amp;&amp; echo yes fires.
    /// Failure-surface axis 8: negated exit code must drive the && arm.
    ///
    /// KNOWN BUG: `! cmd` in an AndOrList chain causes a PowerShell syntax error.
    /// The emitter appends `; $global:LASTEXITCODE = if ($?) { 1 } else { 0 }`
    /// to the negated command, which makes the `&&` appear after `;` — an
    /// expression context where PS pipeline-chain operators are not allowed.
    /// Golden file captures the current (broken) ps-bash output for regression tracking.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Not_NegatesFalseAndChainsAnd()
    {
        // GoldenAsync: known bug — ps-bash produces a PS syntax error instead of "yes".
        await AssertOracle.GoldenAsync(
            "! false && echo yes",
            "Not_NegatesFalseAndChainsAnd",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ! negation on a pipeline: `! echo x | grep y; echo $?`.
    /// echo x | grep y exits 1 (no match); ! inverts to 0; echo $? must print 0.
    /// Failure-surface axis 8: negation of a pipeline exit code.
    ///
    /// KNOWN BUG: ps-bash emits $LASTEXITCODE (without $global: prefix) in the echo
    /// expression, so the variable resolves as 1 (the raw grep exit code) rather than
    /// the negated 0. bash outputs "0"; ps-bash outputs "1".
    /// Golden file captures the current (broken) ps-bash output for regression tracking.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Not_NegatesPipelineExitCode()
    {
        // GoldenAsync: known bug — ps-bash prints 1 instead of 0 after negated pipeline.
        await AssertOracle.GoldenAsync(
            "! echo x | grep y; echo $?",
            "Not_NegatesPipelineExitCode",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // $? after a chain
    // -----------------------------------------------------------------------

    /// <summary>
    /// $? after a successful chain reflects 0.
    /// `true &amp;&amp; true; echo $?` must print 0.
    /// Failure-surface axis 8: LASTEXITCODE must be 0 after a fully-successful chain.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_ExitCodeAfterSuccessfulChain()
    {
        await AssertOracle.EqualAsync(
            "true && true; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// $? after a chain where the last arm fails.
    /// `true &amp;&amp; false; echo $?` must print 1 (false exits 1).
    /// Failure-surface axis 8: LASTEXITCODE must carry the exit code of the last
    /// executed arm, even when earlier arms succeeded.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_ExitCodeAfterFailedLastArm()
    {
        await AssertOracle.EqualAsync(
            "true && false; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // set -e interaction
    // -----------------------------------------------------------------------

    /// <summary>
    /// set -e: `false || true` must NOT abort the script.
    /// In bash, a command that is part of an || list is exempt from errexit.
    /// The script must survive and print "survived".
    /// Failure-surface axis 8: errexit exemption for || arms.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_SetE_FalseOrTrueSurvives()
    {
        await AssertOracle.EqualAsync(
            "set -e; false || true; echo survived",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Compound statements in chains
    // -----------------------------------------------------------------------

    /// <summary>
    /// BoolExpr ([ ... ]) on the left of &amp;&amp;, brace group on the right.
    /// `[ 1 -eq 1 ] &amp;&amp; { echo in_block; }` — test succeeds, brace group runs.
    /// Failure-surface axis 8: BoolExpr wrapped in [void](...) must preserve exit
    /// code so the && arm fires correctly.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_TestExprAndBraceGroup()
    {
        await AssertOracle.EqualAsync(
            "[ 1 -eq 1 ] && { echo in_block; }",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Three-command chain with mixed operators:
    /// `true &amp;&amp; false || echo recovered`.
    /// (true &amp;&amp; false) fails → || echo recovered fires.
    /// Failure-surface axis 8: three-arm chain exit code propagation.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AndOr_ThreeCommandChain_Recovers()
    {
        await AssertOracle.EqualAsync(
            "true && false || echo recovered",
            timeout: TimeSpan.FromSeconds(15));
    }
}
