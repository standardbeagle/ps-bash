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

    // ── 3b. A surviving grandchild must not hang the drain forever ─────────────

    /// <summary>
    /// Known-bad: a spawn whose child EXITS but whose output pipe never reaches EOF.
    /// <c>ReadToEndAsync</c> completes on pipe EOF, not on child exit, so a grandchild
    /// that inherited the write end blocks the drain — AFTER the timeout has already
    /// been satisfied by the child's exit, so no bound applied. That is how a persisted
    /// <c>ps-bash-host</c> holding a launcher's stdout hung whole test runs with no
    /// diagnostic ("running PsBash.Host.Tests immediately after another suite can hang
    /// the test harness").
    ///
    /// The repro is exact rather than simulated: a pwsh that starts a long-lived
    /// <c>-NoNewWindow</c> grandchild (which therefore inherits our stdout pipe) and
    /// then exits 0 immediately. The contract is that this REPORTS within the drain
    /// grace instead of hanging — if this test itself hangs, the contract is broken.
    /// </summary>
    [SkippableFact]
    public async Task Regression_ProcessSpawnDrainBoundedWhenGrandchildHoldsPipe()
    {
        var pwsh = Environment.ProcessPath is { } p && p.Contains("pwsh", StringComparison.OrdinalIgnoreCase)
            ? p
            : "pwsh";

        var grace = TimeSpan.FromSeconds(3);
        // The grandchild records its PID so this test can kill it deterministically.
        // Leaving a sleeping process behind is not acceptable: the escalation suite's
        // scale//timeout tests are contention-sensitive, and a stray pwsh made two of
        // them exceed their 30 s launcher bound.
        var pidFile = Path.Combine(Path.GetTempPath(), $"psbash-draintest-{Guid.NewGuid():N}.pid");
        var sw = Stopwatch.StartNew();

        try
        {
            var ex = await Assert.ThrowsAsync<PsBash.Testing.SpawnDrainTimeoutException>(async () =>
            {
                await PsBash.Testing.ProcessSpawn.RunAsync(
                    pwsh,
                    new[]
                    {
                        "-NoProfile", "-NonInteractive", "-Command",
                        // -NoNewWindow makes the grandchild inherit OUR stdout pipe; the
                        // parent exits at once, so only the grandchild holds the write end.
                        // Sleeps far longer than any bound below, so "the drain bound fired"
                        // is distinguishable from "we simply waited the grandchild out" no
                        // matter how slow the box is.
                        "$p = Start-Process pwsh -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep 600' "
                        + $"-NoNewWindow -PassThru; Set-Content -LiteralPath '{pidFile}' -Value $p.Id; exit 0",
                    },
                    // Generous exit timeout: the child exits immediately, so reaching the
                    // drain path is what is under test, not the exit bound.
                    timeout: TimeSpan.FromSeconds(120),
                    drainGrace: grace);
            });

            sw.Stop();

            // The claim is "bounded, not infinite" — NOT "fast". Elapsed is dominated by
            // spawning two pwsh processes, which took 20.5 s on a loaded box and blew an
            // earlier 20 s assert even though the 3 s grace had fired correctly. The bound
            // therefore only has to sit far below the grandchild's 600 s sleep to prove the
            // drain gave up rather than waiting the grandchild out.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120),
                $"Drain bound did not fire: {sw.Elapsed.TotalSeconds:F1}s");
            Assert.Equal(0, ex.ExitCode);
            Assert.Contains("never reached EOF", ex.Message);
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var grandchildPid))
                {
                    try { Process.GetProcessById(grandchildPid).Kill(entireProcessTree: true); }
                    catch { /* already gone */ }
                }
                try { File.Delete(pidFile); } catch { /* best effort */ }
            }
        }
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
