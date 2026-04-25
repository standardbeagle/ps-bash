using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Five seed differential tests that prove the oracle harness catches real divergences.
/// These are smoke tests for the harness itself — not comprehensive feature coverage.
///
/// Failure-surface axes covered (per QA rubric Directive 3):
///   Test 1 (SimpleEcho):           Axis 12 — quoting / injection
///   Test 2 (VariableExpansion):    Axis 12 — quoting / injection; Axis 14 — missing target (:-default form)
///   Test 3 (Pipes_TwoStage):       Axis 8 — exit code propagation through pipeline; Axis 3 — unicode-safe sort
///   Test 4 (CommandSubstitution):  Axis 12 — nested quoting in double-quoted context (Golden mode: date non-deterministic)
///   Test 5 (ExitCode_PipefailOff): Axis 8 — exit code propagation; pipefail OFF (default), last-command wins
/// </summary>
public class SeedDifferentialTests
{
    // -----------------------------------------------------------------------
    // Test 1: quoting — single-quoted string with space must be one word
    //
    // Failure surface: Axis 12 (quoting/injection).
    // Covers: emitter preserves single quotes so the space is not word-split.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a single-quoted string containing a space is emitted as one
    /// argument.  Regression: if the emitter loses the quotes, bash sees two words
    /// ("hello" and "world") while ps-bash outputs a single "hello world".
    /// </summary>
    [SkippableFact]
    public async Task Differential_SimpleEcho_QuotingPreserved()
    {
        // Allow 15 s: WSL process startup can be slow when running concurrently.
        await AssertOracle.EqualAsync(
            "echo 'hello world'",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 2: variable expansion — ${VAR:-default} and ${#VAR}
    //
    // Failure surface: Axis 12 (quoting/injection), Axis 14 (:-default fallback).
    // Covers: BracedVarSub emission for the two most common parameter expansions.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that ${x:-default} and ${#x} expand correctly.
    /// Catches emitter regressions in EmitBracedVar for the :- and # forms.
    /// </summary>
    [SkippableFact]
    public async Task Differential_VariableExpansion_BraceForms()
    {
        // Allow 15 s: WSL process startup can be slow when running concurrently.
        await AssertOracle.EqualAsync(
            "x=1; echo \"${x:-default}\" \"${#x}\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 3: two-stage pipeline
    //
    // Failure surface: Axis 8 (exit code through pipeline), pipeline object flow.
    // Covers: EmitPipeline correctness — if the pipe operator is dropped, sort -r
    // and head -n 2 each run stand-alone against empty stdin and output nothing.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a two-stage pipeline (printf | sort -r | head -n 2) produces
    /// the correct output.  This test is the primary regression sentinel for
    /// EmitPipeline: drop the | and the test fails with a clear diff bundle.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Pipes_TwoStage()
    {
        // Allow 15 s: pwsh pipeline teardown after head -n 2 closes its stdin can be slow.
        await AssertOracle.EqualAsync(
            "printf 'a\\nb\\nc\\n' | sort -r | head -n 2",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 4: command substitution inside double quotes — Golden mode
    //
    // Failure surface: Axis 12 (nested quoting inside double-quoted context).
    // Uses Golden mode because $(date +%Y) is non-deterministic.
    // Record golden: UPDATE_GOLDENS=1 ./scripts/test.sh --filter Differential_CommandSubstitution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that $(date +%Y) inside a double-quoted echo emits the correct year.
    /// Uses golden mode because the output changes each year.  Record with:
    ///   UPDATE_GOLDENS=1 ./scripts/test.sh src/PsBash.Differential.Tests --filter Differential_CommandSubstitution
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSubstitution_NestedQuoting()
    {
        // Allow 15 s: WSL process startup can be slow when running concurrently.
        await AssertOracle.GoldenAsync(
            "echo \"today is $(date +%Y)\"",
            "CommandSubstitution_NestedQuoting",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 5: exit code propagation with pipefail OFF (default)
    //
    // Failure surface: Axis 8 (exit code propagation; pipefail default=off).
    // With pipefail OFF, `false | true` exits 0 because the last command (true)
    // succeeds.  $? should be 0.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that with pipefail off (the default), `false | true` reports $?=0
    /// because the last command in the pipeline determines the exit status.
    /// </summary>
    [SkippableFact]
    public async Task Differential_ExitCodePropagation_PipefailOff()
    {
        // Allow 15 s: WSL process startup can be slow when running concurrently.
        await AssertOracle.EqualAsync(
            "false | true; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }
}
