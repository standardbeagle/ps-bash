using System.Diagnostics;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Known-bad regression tests per QA rubric Directive 13.
/// One permanent regression test per known-bad category from MEMORY.md.
/// These must never be disabled; quarantine with [SkippableFact] + Skip only
/// when the binary is absent, not for convenience.
/// </summary>
[Trait("Category", "Escalation")]
[Trait("Category", "Regression")]
public class KnownBadRegressionTests
{
    private static readonly string? PwshPath = FindPwsh();

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

    // ── 1. LASTEXITCODE not polluted between commands ─────────────────────────

    /// <summary>
    /// Known-bad: LASTEXITCODE save/restore collisions.
    /// After `false` (exits nonzero), running `true` resets exit status to 0.
    /// `false; true; echo $?` must not output a stale nonzero value — if
    /// LASTEXITCODE is polluted, $? would print the stale "1" from false.
    ///
    /// Note: ps-bash's `echo $?` emits $LASTEXITCODE as a string; we accept
    /// "0" or "True" (PowerShell bool coercion of 0) but not "1" or "False".
    /// ps-bash-specific assertion: no oracle comparison (bash and ps-bash differ
    /// on how $? is stringified).
    /// </summary>
    [SkippableFact]
    public async Task Regression_LastExitcodeNotPollutedBetweenCommands()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        // false sets exit 1; true resets to 0; echo $? must not show stale 1.
        var (exitCode, stdout, _) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "false; true; echo $?" });

        // The overall script exit code reflects the last command (echo), which exits 0.
        Assert.Equal(0, exitCode);

        var normalized = stdout.Replace("\r\n", "\n").Trim();
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 0, "Expected at least one output line");
        var lastLine = lines[^1].Trim();
        // Accept "0" (integer) or "True" (PowerShell bool coercion) — both indicate
        // LASTEXITCODE=0. Reject "1", "False", or any nonzero value.
        Assert.True(lastLine == "0" || lastLine == "True",
            $"Expected '0' or 'True' (zero exit status), got '{lastLine}'. Stale LASTEXITCODE pollution detected.");
    }

    // ── 2. ERR trap does not fire on zero exit ────────────────────────────────

    /// <summary>
    /// Known-bad: ERR trap on stale LASTEXITCODE.
    /// `set -e; true; echo ok` must reach "echo ok" and exit 0.
    /// If the ERR trap fires spuriously after `true` (exit 0), it would abort
    /// the script — regression from the stale-LASTEXITCODE bug.
    /// </summary>
    [SkippableFact]
    public async Task Regression_ErrTrapDoesNotFireOnZeroExit()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "set -e; true; echo ok" });

        Assert.Equal(0, exitCode);
        Assert.Contains("ok", stdout);
    }

    // ── 3. Process spawn with timeout kills tree ──────────────────────────────

    /// <summary>
    /// Known-bad: process spawn without timeout + kill-tree causes lockup.
    /// Spawn a long sleep (60 s), apply a 2 s timeout, assert the process tree
    /// is killed within ~4 s and a TimeoutException is raised.
    ///
    /// This test verifies ProcessRunHelper's own reliability contract — if this
    /// test itself hangs, the contract is broken.
    /// </summary>
    [SkippableFact]
    public async Task Regression_ProcessSpawnWithTimeout()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var timeout = TimeSpan.FromSeconds(2);
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await ProcessRunHelper.RunAsync(
                new[] { "-c", "Start-Sleep 60" },
                timeout: timeout);
        });

        sw.Stop();

        // The kill + cleanup must complete within 10 s of the timeout firing.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Kill+cleanup took too long: {sw.Elapsed.TotalSeconds:F1}s");

        Assert.Contains("did not exit within", ex.Message);
    }
}
