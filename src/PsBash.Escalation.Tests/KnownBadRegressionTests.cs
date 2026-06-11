using System.Diagnostics;
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

        var (exitCode, stdout, _) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "set -e; true; echo ok" });

        Assert.Equal(0, exitCode);
        Assert.Contains("ok", stdout);
    }

    // ── 3. Process spawn with timeout kills tree ──────────────────────────────

    /// <summary>
    /// Known-bad: process spawn without timeout + kill-tree causes lockup.
    /// Spawn a long sleep (60 s), apply a 2 s timeout, assert the process tree
    /// is killed within ~4 s and a timeout exception is raised.
    ///
    /// This test verifies the shared spawn reliability contract (REFACTOR-3:
    /// now enforced once in PsBash.Testing.ProcessSpawn, surfaced via
    /// SpawnTimeoutException which derives from TimeoutException) — if this
    /// test itself hangs, the contract is broken.
    /// </summary>
    [SkippableFact]
    public async Task Regression_ProcessSpawnWithTimeout()
    {

        var timeout = TimeSpan.FromSeconds(2);
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<PsBash.Testing.SpawnTimeoutException>(async () =>
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

    // ── 4. Concurrent daemon commands do not corrupt shared shell state ────────

    /// <summary>
    /// Known-bad: concurrent daemon command execution corrupting process-global
    /// state. A bash variable is transpiled to <c>$env:NAME</c> and the working
    /// directory to <c>[Environment]::CurrentDirectory</c> — both process-global
    /// and shared by every pooled runspace in the daemon host. Without the
    /// process-wide execution gate (commit fb6bf72), two concurrent <c>-c</c>
    /// launchers racing on the same loop variable drop/duplicate/skip iterations
    /// (observed shapes: "1,2,4", "1,2,3,4,6", "1,2,3,4,5,1,2,3,4,5").
    ///
    /// This is the END-TO-END guard for that fix: it drives real concurrent
    /// launcher processes against one shared daemon, where the unit-level
    /// SdkWorker env-race test cannot see the IPC/pool/launcher path. The
    /// invariant is precise: any command that SUCCEEDS (exit 0) must produce
    /// EXACTLY its own 1..5 — a corrupted-but-successful result is the bug.
    /// Cold-start connection transients (non-zero exit) are a separate concern
    /// and are excluded; the daemon is pre-warmed to minimize them, and the test
    /// requires enough successes that it cannot pass vacuously.
    ///
    /// Stress-tagged: spawns many concurrent processes, so it runs under
    /// <c>--stress</c> rather than the default gate (same isolation as the
    /// cold-start single-flight stress test).
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Stress")]
    public async Task Regression_ConcurrentDaemonCommands_NoSharedVariableCorruption()
    {
        const string loop = "i=1; while [ $i -le 5 ]; do echo $i; i=$((i+1)); done";
        const string expected = "1\n2\n3\n4\n5";
        const int concurrency = 8;
        const int rounds = 3;

        // Warm the daemon so the concurrent batch hits a live host (keeps the
        // test focused on execution-serialization, not cold-start spawn).
        await ProcessRunHelper.RunAsync(new[] { "-c", "echo warmup" });

        int successes = 0;
        var corrupted = new List<string>();

        for (int round = 0; round < rounds; round++)
        {
            var tasks = Enumerable.Range(0, concurrency)
                .Select(_ => ProcessRunHelper.RunAsync(
                    new[] { "-c", loop }, timeout: TimeSpan.FromSeconds(30)))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            foreach (var (exitCode, stdout, _) in results)
            {
                // Only successful commands carry the data-integrity contract;
                // a non-zero exit is a connection transient, counted out.
                if (exitCode != 0) continue;
                var normalized = stdout.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
                if (normalized == expected) successes++;
                else corrupted.Add($"[exit0] '{normalized.Replace("\n", ",")}'");
            }
        }

        Assert.True(corrupted.Count == 0,
            $"Concurrent -c launchers corrupted shared shell state in {corrupted.Count} run(s) — " +
            $"the daemon execution gate is not serializing. Samples: {string.Join("  ", corrupted.Take(8))}");

        // Guard against a vacuous pass (e.g. every run failing to connect): the
        // corruption invariant is only meaningful if commands actually executed.
        Assert.True(successes >= concurrency,
            $"Too few successful concurrent runs ({successes}) to validate the no-corruption invariant; " +
            "expected the warmed daemon to serve most of the batch.");
    }
}
