using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// xunit collection that serializes every PTY-harness test. Each test spawns a
/// full interactive <c>ps-bash -i</c> under a real pseudo-terminal; running the
/// seven of them in parallel — on top of the ~20 other process-spawning tests in
/// this assembly — starves shell startup and makes the prompt-wait flake. One
/// PTY harness alive at a time keeps every test deterministic-green.
/// </summary>
[CollectionDefinition("PtyHarness", DisableParallelization = true)]
public sealed class PtyHarnessCollection;

/// <summary>
/// PTY-8 acceptance tests for <see cref="PtyHarness"/>: the expect-style fixture
/// that spawns <c>ps-bash -i</c> against a real pseudo-terminal.
///
/// <para>The smoke test is the acceptance proof — spawn the shell under a PTY,
/// type <c>echo hi</c>, and assert <c>hi</c> appears between two prompts. It must
/// be deterministic-green on POSIX across repeated runs. The Windows ConPTY path
/// compiles here but is CI-gated for runtime verification.</para>
/// </summary>
[Collection("PtyHarness")]
public class PtyHarnessTests
{
    // 10s (not the rubric's nominal 5s): this assembly has a documented heavy
    // parallel-process-spawn baseline, and a contended box can take >5s to flush
    // a command's output through the PTY. Still a hard bound — no Sleep, the wait
    // returns the instant the pattern matches.
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex PromptRegex = new(PtyHarness.PromptPattern, RegexOptions.Compiled);

    /// <summary>
    /// Smoke test (the PTY-8 acceptance proof): launch <c>ps-bash -i</c> under a
    /// real PTY, type <c>echo hi</c>, and assert <c>hi</c> is rendered between two
    /// prompts. Verifies the full WriteKeys → PTY → shell → PTY → WaitForRegex
    /// loop with no <c>Sleep</c>.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Smoke_EchoHi_AppearsBetweenTwoPrompts()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only — Windows ConPTY runtime verification is CI-gated");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        await using var harness = await PtyHarness.StartAsync(psBash!);

        await harness.WriteKeysAsync("echo hi\n");

        // Wait for the `hi` output line, then for the prompt that the shell
        // renders after the command finishes. The transcript then holds:
        // prompt … echo hi (keystroke echo) … \nhi\n (output) … prompt.
        await harness.WaitForRegexAsync(@"\nhi\n", PromptTimeout);
        var transcript = await harness.WaitForRegexAsync(
            @"\nhi\n[^@]*?" + PtyHarness.PromptPattern, PromptTimeout);

        // `hi` is rendered between two prompts: at least one prompt precedes the
        // `\nhi\n` output line, and at least one follows it.
        int outputHi = transcript.IndexOf("\nhi\n", StringComparison.Ordinal);
        Assert.True(outputHi > 0, $"`hi` output line not found:\n{transcript}");

        var beforeHi = PromptRegex.Matches(transcript[..outputHi]);
        var afterHi = PromptRegex.Matches(transcript[(outputHi + 4)..]);
        Assert.True(beforeHi.Count >= 1,
            $"expected a prompt before the `hi` output:\n{transcript}");
        Assert.True(afterHi.Count >= 1,
            $"expected a prompt after the `hi` output:\n{transcript}");
    }

    /// <summary>
    /// Determinism check (Directive 6): the smoke flow must be green across
    /// repeated runs in-process, proving the harness has no timing flake and the
    /// teardown leaves no zombie PTY pair / leaked fd that would poison the next
    /// allocation.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Smoke_RepeatedRuns_AreDeterministicGreen()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        for (int i = 0; i < 5; i++)
        {
            await using var harness = await PtyHarness.StartAsync(psBash!);
            await harness.WriteKeysAsync("echo hi\n");
            var transcript = await harness.WaitForRegexAsync(@"\nhi\n", PromptTimeout);
            Assert.Contains("\nhi\n", transcript);
        }
    }

    /// <summary>
    /// Parallel-fixture check (the Windows handle-ownership risk, also exercises
    /// POSIX fd isolation): multiple harnesses alive at once must not cross-talk.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task MultipleHarnesses_RunInParallel_WithoutCrossTalk()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        var harnesses = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => PtyHarness.StartAsync(psBash!)));
        try
        {
            await Task.WhenAll(harnesses.Select(async (h, idx) =>
            {
                var marker = $"zmark{idx}q";
                await h.WriteKeysAsync($"echo {marker}\n");
                var transcript = await h.WaitForRegexAsync($@"\n{marker}\n", PromptTimeout);
                // No other harness's marker leaked into this transcript.
                for (int other = 0; other < harnesses.Length; other++)
                {
                    if (other == idx) continue;
                    Assert.DoesNotContain($"zmark{other}q", transcript);
                }
            }));
        }
        finally
        {
            foreach (var h in harnesses)
                await h.DisposeAsync();
        }
    }

    /// <summary>
    /// <see cref="PtyHarness.Resize"/> drives <c>TIOCSWINSZ</c> on the PTY master
    /// without error, and the shell stays responsive afterward — a command
    /// issued post-resize still runs and produces output. (Whether ps-bash
    /// re-renders on SIGWINCH is ps-bash's concern, exercised by PTY-9; here we
    /// verify the harness's resize plumbing.)
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Resize_DrivesWinsizeAndShellStaysResponsive()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        await using var harness = await PtyHarness.StartAsync(psBash!, cols: 120, rows: 40);

        // Resize must not throw, and may be called repeatedly.
        harness.Resize(100, 30);
        harness.Resize(80, 24);

        // The shell is still alive and processing input after the resizes.
        await harness.WriteKeysAsync("echo resized\n");
        var transcript = await harness.WaitForRegexAsync(@"\nresized\n", PromptTimeout);
        Assert.Contains("\nresized\n", transcript);
    }

    /// <summary>
    /// <see cref="PtyHarness.SendSignal"/> delivers a real signal to the spawned
    /// shell. SIGTERM (15) terminates it; the spawner reaps it (no zombie) and
    /// <see cref="PtyHarness.WaitForExitAsync"/> returns its exit code.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task SendSignal_Sigterm_TerminatesShell()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        var harness = await PtyHarness.StartAsync(psBash!);
        try
        {
            harness.SendSignal(15); // SIGTERM
            int exit = await harness.WaitForExitAsync(TimeSpan.FromSeconds(5));
            // The shell is gone — exit code is signal-derived or a normal code,
            // either way WaitForExitAsync returned rather than timing out.
            Assert.True(exit >= 0, $"expected a real exit code, got {exit}");
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    /// <summary>
    /// Negative path (Directive 7): <see cref="PtyHarness.WaitForRegexAsync"/>
    /// throws a <see cref="TimeoutException"/> with a transcript dump when the
    /// pattern never appears, rather than hanging.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task WaitForRegex_PatternNeverAppears_ThrowsWithTranscript()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        await using var harness = await PtyHarness.StartAsync(psBash!);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            harness.WaitForRegexAsync("THIS_PATTERN_NEVER_APPEARS_XYZZY", TimeSpan.FromSeconds(2)));
        Assert.Contains("transcript", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Negative path (Directive 7 + Directive 14: missing target):
    /// <see cref="PtyHarness.StartAsync"/> rejects an empty binary path before
    /// allocating a PTY.
    /// </summary>
    [Fact]
    public async Task StartAsync_EmptyBinaryPath_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => PtyHarness.StartAsync(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => PtyHarness.StartAsync(null!));
    }
}
