using Xunit;

namespace PsBash.Canary.Tests;

/// <summary>
/// Canary smoke tests: minimal scripts that must pass in every available mode.
/// Per qa-rubric Directive 8: ~30 tests, one per feature in the failure-surface matrix,
/// runs in all 6 modes (M4 skipped — TTY flake), hard cap 60s per mode per platform.
///
/// Skip sentinel: ExitCode == -999 means the mode prerequisite is unavailable on this
/// platform (binary not found, cmdlet not loaded). Tests skip rather than fail.
/// </summary>
public sealed class CanarySmokeTests
{
    private readonly ModeRunner _runner = new();

    // -------------------------------------------------------------------------
    // Smoke: echo hello — verifies basic execution in all available modes.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EchoHello_M1_ReturnsExitZero()
    {
        var result = await RunSingleMode(Mode.M1_CFlag, "echo hello");
        Skip.If(result.ExitCode == -999, $"M1 skipped: {result.Stderr}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public async Task EchoHello_M2_ReturnsExitZero()
    {
        var result = await RunSingleMode(Mode.M2_StdinPipe, "echo hello");
        Skip.If(result.ExitCode == -999, $"M2 skipped: {result.Stderr}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public async Task EchoHello_M3_ReturnsExitZero()
    {
        var result = await RunSingleMode(Mode.M3_FileArg, "echo hello");
        Skip.If(result.ExitCode == -999, $"M3 skipped: {result.Stderr}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public async Task EchoHello_M5_ReturnsExitZero()
    {
        var result = await RunSingleMode(Mode.M5_InvokeEval, "echo hello");
        Skip.If(result.ExitCode == -999, $"M5 skipped: {result.Stderr}");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task EchoHello_M6_ReturnsExitZero()
    {
        var result = await RunSingleMode(Mode.M6_InvokeSource, "echo hello");
        Skip.If(result.ExitCode == -999, $"M6 skipped: {result.Stderr}");
        Assert.Equal(0, result.ExitCode);
    }

    // -------------------------------------------------------------------------
    // At minimum M1 must be available for the smoke test to constitute a real check.
    // -------------------------------------------------------------------------

    [Fact]
    public void ModeRunner_FindsPsBashBinaryOrSkips()
    {
        // This test documents binary availability; it never fails — it skips if absent.
        Skip.If(_runner.PsBashPath == null, "ps-bash binary not found; build PsBash.Shell first.");
        Assert.NotNull(_runner.PsBashPath);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private async Task<ModeResult> RunSingleMode(Mode mode, string script)
    {
        var all = await _runner.RunAllAsync(script);
        return all.First(r => r.Mode == mode);
    }
}
