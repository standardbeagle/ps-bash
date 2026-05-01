using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential tests for T10 process-substitution 3-path classifier.
///
/// Architecture intent (locked in by T10a):
///   1. Temp-file path (CURRENT — Invoke-ProcessSub): writes producer output to a
///      temp file under $TEMP/ps-bash/proc-sub/ and returns the path. Used by all
///      external consumers (diff, cat, paste, comm, wc -l). Cannot deadlock — the
///      producer fully drains into the file before the consumer opens it. This is
///      the safe default and stays as the fallback path forever.
///   2. Pipeline-object path (T10c — Invoke-ProcessSubPipeline): yields producer
///      output objects directly into the PowerShell pipeline. Activated by the
///      emitter only when the consumer is a mapped Invoke-Bash* command that
///      accepts pipeline objects (sort, uniq, grep). Preserves typed objects.
///   3. Streaming bridge path (T10b — ProcessSubBridge): when running inside
///      ps-bash-host, routes large producer output through a named-pipe bridge
///      to avoid temp-file overhead while preserving file-arg semantics.
///
/// Failure-surface axes covered (per QA rubric Directive 3):
///   Test 1  (TempFilePath):     Axis 8 — exit code propagation through diff (identical)
///   Test 2  (CatMultiple):      Axis 8 — multiple &lt;() on one command
///   Tests 4-5 (SourcePath):     Axis 8 — source &lt;(...) string-capture path;
///                                         variable scoping via Invoke-ProcessSubSource
///   Test 6  (SourceFunction):   Axis 8 — function defined in source &lt;(...) is callable
///   Test 7  (NegatedDiff):      Axis 8 — exit code negation through process sub
///   Test 8  (PasteTwoSubs):     Axis 2 (multi-line) + Axis 8 — paste &lt;() &lt;() locks in
///                                         that two simultaneous &lt;() args via temp-file
///                                         path cannot deadlock.
///   Test 9  (CatMultiLine):     Axis 2 — multi-line output from each &lt;() preserved.
///   Test 10 (WcSingle):         Axis 2 — single &lt;() as file arg with 100 lines.
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

    // -----------------------------------------------------------------------
    // Tests 8-10: lock in temp-file path coverage for the existing consumers.
    //
    // T10a scope: prove the temp-file path handles two simultaneous <() args,
    // multi-line producer output, and 100-line file-arg input without deadlock.
    // These tests intentionally exercise the CURRENT working path so any
    // regression introduced by T10b (streaming bridge) or T10c (pipeline-object
    // routing) shows up immediately.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ProcessSub_TempFile_PasteTwoSubs()
    {
        // paste with two <() args — temp-file path must not deadlock since both
        // producers fully drain into temp files before paste opens either.
        await AssertOracle.EqualAsync(
            "paste <(seq 1 5) <(seq 6 10)",
            timeout: TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task ProcessSub_TempFile_CatMultiLineEach()
    {
        // Each <() emits multi-line output via echo -e; cat must preserve all
        // lines from both substitutions in order.
        await AssertOracle.EqualAsync(
            "cat <(echo -e \"line1\\nline2\") <(echo -e \"line3\\nline4\")",
            timeout: TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task ProcessSub_TempFile_WcSingleLargeInput()
    {
        // Single <() as file arg with 100 lines exercises Axis 2 (large input)
        // for the temp-file path. wc -l must report 100.
        await AssertOracle.EqualAsync(
            "wc -l <(seq 1 100)",
            timeout: TimeSpan.FromSeconds(15));
    }
}
