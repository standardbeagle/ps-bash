using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential tests for T10 process-substitution 3-path classifier:
/// temp-file path (external consumers), string-capture path (source),
/// and pipeline-object path (mapped commands).
///
/// Failure-surface axes covered (per QA rubric Directive 3):
///   Test 1  (TempFilePath):     Axis 8 — exit code propagation through diff (identical)
///   Test 2  (CatMultiple):      Axis 8 — multiple &lt;() on one command
///   Tests 4-5 (SourcePath):     Axis 8 — source &lt;(...) string-capture path;
///                                         variable scoping via Invoke-ProcessSubSource
///   Test 6  (SourceFunction):   Axis 8 — function defined in source &lt;(...) is callable
///   Test 7  (NegatedDiff):      Axis 8 — exit code negation through process sub
/// </summary>
public class ProcessSubFixturesTests
{
    // -----------------------------------------------------------------------
    // Tests 1-2: temp-file path — diff with two process substitutions
    //
    // Failure surface: Axis 8 (exit code propagation).
    // Invoke-ProcessSub writes temp file; Invoke-BashDiff reads file paths.
    // Already works before T10; here as regression guard.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ProcessSub_TempFile_DiffIdentical_ExitsZero()
    {
        await AssertOracle.EqualAsync(
            "diff <(seq 1 5) <(seq 1 5)",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 3: cat with multiple process substitutions
    //
    // Failure surface: Axis 8 — multiple temp-file paths combined.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ProcessSub_TempFile_CatMultiple()
    {
        await AssertOracle.EqualAsync(
            "cat <(echo one) <(echo two)",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Tests 4-5: string-capture path — source <(...)
    //
    // Failure surface: Axis 8 — Invoke-ProcessSubSource captures bash text,
    // transpiles, and executes in caller scope.
    // These tests fail before T10 because Invoke-BashSource is not in psm1.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ProcessSub_Source_ExportsVariable()
    {
        // source <(echo 'PSBASH_T10_VAR=hello') then echo $PSBASH_T10_VAR -> hello
        await AssertOracle.EqualAsync(
            "source <(echo 'PSBASH_T10_VAR=hello'); echo $PSBASH_T10_VAR",
            timeout: TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task ProcessSub_Source_MultiLineOutput()
    {
        // source <(printf 'A=first\nB=second\n') then echo both variables
        await AssertOracle.EqualAsync(
            "source <(printf 'A=first\\nB=second\\n'); echo $A $B",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 6: function defined inside source <(...) is callable after source
    //
    // Failure surface: Axis 8 — caller-scope execution via useLocalScope=false.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ProcessSub_Source_FunctionDefinition()
    {
        // source <(echo 'greet() { echo "hi $1"; }') then call greet
        await AssertOracle.EqualAsync(
            "source <(echo 'greet() { echo \"hi $1\"; }'); greet world",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 7: exit code through negated command with process sub
    //
    // Failure surface: Axis 8 — exit code propagation.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ProcessSub_TempFile_NegatedDiff()
    {
        // '! diff <(echo a) <(echo a)' negates exit 0 -> exit 1
        await AssertOracle.EqualAsync(
            "! diff <(echo a) <(echo a); echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }
}
