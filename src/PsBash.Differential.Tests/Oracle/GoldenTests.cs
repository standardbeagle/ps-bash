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
[Trait("Category", "Golden")]
public class GoldenTests
{
    // ── AssertOracle.GoldenAsync round-trip ───────────────────────────────────

    /// <summary>
    /// Verifies that UPDATE_GOLDENS=1 writes a golden and a subsequent call
    /// matches it.
    /// </summary>
    [SkippableFact]
    public async Task GoldenAsync_RoundTrip_WriteThenRead_Matches()
    {
        Skip.If(new BashOracleFixture().PsBashPath is null,
            "ps-bash binary not found -- build PsBash.Shell first");

        // Use a unique test name to avoid collisions
        var testName = $"GoldenRoundTrip_{System.Guid.NewGuid():N}";

        // Phase 1: write golden (UPDATE_GOLDENS=1)
        var origEnv = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");
        try
        {
            Environment.SetEnvironmentVariable("UPDATE_GOLDENS", "1");
            // GoldenAsync writes golden and passes unconditionally
            await AssertOracle.GoldenAsync("echo golden_test", testName);
        }
        finally
        {
            // Restore env to avoid leaking into other tests
            Environment.SetEnvironmentVariable("UPDATE_GOLDENS", origEnv);
        }

        // Phase 2: verify golden matches on subsequent run
        // UPDATE_GOLDENS is now unset/original — should compare and pass
        await AssertOracle.GoldenAsync("echo golden_test", testName);
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

        // Write a golden with "echo first"
        var origEnv = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");
        try
        {
            Environment.SetEnvironmentVariable("UPDATE_GOLDENS", "1");
            await AssertOracle.GoldenAsync("echo first", testName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UPDATE_GOLDENS", origEnv);
        }

        // Now compare with "echo second" — must throw XunitException
        var ex = await Assert.ThrowsAsync<XunitException>(async () =>
            await AssertOracle.GoldenAsync("echo second", testName));

        Assert.Contains("Golden Diff Bundle", ex.Message);
    }

    /// <summary>
    /// Verifies that when a golden file is missing and UPDATE_GOLDENS is not
    /// set, GoldenAsync throws a skip-type exception containing the golden path.
    /// </summary>
    [SkippableFact]
    public async Task GoldenAsync_MissingGolden_ThrowsSkipWithPath()
    {
        Skip.If(new BashOracleFixture().PsBashPath is null,
            "ps-bash binary not found -- build PsBash.Shell first");

        var testName = $"GoldenMissing_{System.Guid.NewGuid():N}";

        // UPDATE_GOLDENS must NOT be "1"
        var origEnv = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");
        Environment.SetEnvironmentVariable("UPDATE_GOLDENS", null);

        try
        {
            // Skip.If throws Xunit.SkipException which inherits from Exception.
            // Record.ExceptionAsync captures any thrown exception.
            var ex = await Record.ExceptionAsync(async () =>
                await AssertOracle.GoldenAsync("echo hello", testName));

            Assert.NotNull(ex);
            Assert.Contains("golden file missing", ex!.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UPDATE_GOLDENS", origEnv);
        }
    }
}
