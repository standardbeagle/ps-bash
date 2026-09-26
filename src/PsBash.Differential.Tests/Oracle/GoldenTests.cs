using Xunit;
using Xunit.Sdk;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Tests for golden-file mode.
///
/// Workflow:
///   UPDATE_GOLDENS=1 ./scripts/test.sh src/PsBash.Differential.Tests --filter Golden
///   ./scripts/test.sh src/PsBash.Differential.Tests --filter Golden
/// </summary>
/// <remarks>
/// Recording is enabled through the explicit <see cref="AssertOracle.BeginUpdateGoldens"/>
/// scope (an AsyncLocal toggle) rather than by mutating the process-global
/// UPDATE_GOLDENS environment variable. The environment variable is read once at
/// process start for the CLI workflow, so a test can never leak recording mode
/// into a concurrently running golden comparison.
/// </remarks>
[Trait("Category", "Golden")]
[Collection("GoldenTests-Sequential")]
public class GoldenTests
{
    // ── AssertOracle.GoldenAsync round-trip ───────────────────────────────────

    /// <summary>
    /// Verifies that recording mode writes a golden and a subsequent call
    /// matches it.
    /// </summary>
    [SkippableFact]
    public async Task GoldenAsync_RoundTrip_WriteThenRead_Matches()
    {
        Skip.If(new BashOracleFixture().PsBashPath is null,
            "ps-bash binary not found -- build PsBash.Shell first");

        // Use a unique test name to avoid collisions
        var testName = $"GoldenRoundTrip_{System.Guid.NewGuid():N}";

        try
        {
            // Phase 1: record the golden inside an explicit recording scope.
            using (AssertOracle.BeginUpdateGoldens())
                await AssertOracle.GoldenAsync("echo golden_test", testName);

            // Phase 2: compare against the recorded golden. Force compare mode so
            // a process-wide UPDATE_GOLDENS=1 cannot make this phase re-record.
            await AssertOracle.GoldenAsync("echo golden_test", testName, updateGoldens: false);
        }
        finally
        {
            TryDelete(testName);
        }
    }

    /// <summary>
    /// Verifies that a mismatch between ps-bash output and the golden throws
    /// XunitException with a diff bundle.
    /// </summary>
    [SkippableFact]
    public async Task GoldenAsync_Mismatch_ThrowsXunitException()
    {
        Skip.If(new BashOracleFixture().PsBashPath is null,
            "ps-bash binary not found -- build PsBash.Shell first");

        var testName = $"GoldenMismatch_{System.Guid.NewGuid():N}";

        try
        {
            // Record a golden with "echo first"
            using (AssertOracle.BeginUpdateGoldens())
                await AssertOracle.GoldenAsync("echo first", testName);

            // Now compare with "echo second" — must throw XunitException
            var ex = await Assert.ThrowsAsync<XunitException>(async () =>
                await AssertOracle.GoldenAsync("echo second", testName, updateGoldens: false));

            Assert.Contains("Golden Diff Bundle", ex.Message);
        }
        finally
        {
            TryDelete(testName);
        }
    }

    /// <summary>
    /// Verifies that when a golden file is missing and recording is not active,
    /// GoldenAsync throws a skip-type exception containing the golden path.
    /// </summary>
    [SkippableFact]
    public async Task GoldenAsync_MissingGolden_ThrowsSkipWithPath()
    {
        Skip.If(new BashOracleFixture().PsBashPath is null,
            "ps-bash binary not found -- build PsBash.Shell first");

        var testName = $"GoldenMissing_{System.Guid.NewGuid():N}";

        try
        {
            // Force compare mode; Skip.If throws Xunit.SkipException which inherits
            // from Exception. Record.ExceptionAsync captures any thrown exception.
            var ex = await Record.ExceptionAsync(async () =>
                await AssertOracle.GoldenAsync("echo hello", testName, updateGoldens: false));

            Assert.NotNull(ex);
            Assert.Contains("golden file missing", ex!.Message);
        }
        finally
        {
            TryDelete(testName);
        }
    }

    // ── Recording refuses a crashed spawn ─────────────────────────────────────

    /// <summary>
    /// Recording mode must refuse to overwrite a golden with the output of a
    /// spawn that did not succeed (non-zero exit, or an unhandled-exception /
    /// oracle-timeout stderr). The R05 gate corrupted
    /// CommandSubstitution_NestedQuoting.golden.txt to zero bytes this way: a
    /// launcher that crashed at startup printed nothing, and the recording branch
    /// wrote that empty output over the tracked golden.
    /// </summary>
    [SkippableFact]
    public async Task GoldenAsync_RecordingMode_CrashedSpawn_DoesNotWriteGolden()
    {
        // Pure seam: a crashed spawn is not recordable, a healthy one is.
        Assert.NotNull(AssertOracle.RecordableSpawnFailure(
            new OracleResult("", "Unhandled exception. System.Exception: boom\n", 1, 10)));
        Assert.NotNull(AssertOracle.RecordableSpawnFailure(
            new OracleResult("", "boom\n", 7, 10)));
        Assert.Null(AssertOracle.RecordableSpawnFailure(
            new OracleResult("ok\n", "", 0, 10)));

        Skip.If(new BashOracleFixture().PsBashPath is null,
            "ps-bash binary not found -- build PsBash.Shell first");

        var testName = $"GoldenCrash_{System.Guid.NewGuid():N}";
        var goldenPath = AssertOracle.GoldenFilePath(testName);

        try
        {
            // A sentinel stands in for the tracked golden the crash would corrupt.
            await File.WriteAllTextAsync(goldenPath, "sentinel");

            using (AssertOracle.BeginUpdateGoldens())
            {
                var ex = await Assert.ThrowsAsync<XunitException>(() =>
                    AssertOracle.GoldenAsync("exit 3", testName));
                Assert.Contains("refused", ex.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal("sentinel", await File.ReadAllTextAsync(goldenPath));
        }
        finally
        {
            TryDelete(testName);
        }
    }

    // ── Recording never leaks to concurrent callers ───────────────────────────

    /// <summary>
    /// A GoldenAsync caller running concurrently on another async flow must stay
    /// in compare mode while this test records. The flag is an AsyncLocal toggle,
    /// so entering the recording scope here cannot change the ExecutionContext
    /// another test captured before the scope opened.
    /// </summary>
    [SkippableFact]
    public async Task GoldenAsync_RecordingFlag_DoesNotLeakToOtherCallers()
    {
        Skip.If(new BashOracleFixture().PsBashPath is null,
            "ps-bash binary not found -- build PsBash.Shell first");

        var testName = $"GoldenLeak_{System.Guid.NewGuid():N}";

        try
        {
            // Seed a golden so a compare-mode caller has something to diff against.
            using (AssertOracle.BeginUpdateGoldens())
                await AssertOracle.GoldenAsync("echo first", testName);

            // Schedule the concurrent caller BEFORE opening the recording scope:
            // Task.Run captures the ambient ExecutionContext now, so the caller's
            // flow never sees the recording flag set below.
            var other = Task.Run(async () =>
                await Record.ExceptionAsync(async () =>
                    await AssertOracle.GoldenAsync("echo second", testName)));

            using (AssertOracle.BeginUpdateGoldens())
            {
                // While this flow records, the other flow must stay in compare
                // mode and fail the mismatch — proving the flag is not global.
                var ex = await other;
                Assert.IsType<XunitException>(ex);
            }
        }
        finally
        {
            TryDelete(testName);
        }
    }

    private static void TryDelete(string testName)
    {
        try { File.Delete(AssertOracle.GoldenFilePath(testName)); } catch { /* best-effort */ }
    }
}
