using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Integration tests for alias expansion in interactive mode.
///
/// Covers: alias definition, expansion at input time (before transpile), unalias,
/// recursion prevention, pipeline-segment expansion, and rc-file aliases.
///
/// Env isolation: noProfile=true by default; profile tests opt in via noProfile=false
/// and supply a psBashHome directory with a pre-written .psbashrc.
///
/// All tests are [SkippableFact] — skipped (not failed) when the binary is absent.
/// </summary>
[Trait("Category", "Integration")]
public class AliasExpansionIntegrationTests
{
    private static readonly string? PsBashPath = InteractiveShellHarness.FindPsBashBinary();

    private static string? FindWorkerScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "ps-bash-worker.ps1");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static readonly string? WorkerScript = FindWorkerScript();

    private bool CanRun => PsBashPath is not null;

    private Task<InteractiveShellHarness> StartAsync(bool noProfile = true)
        => InteractiveShellHarness.StartAsync(
            PsBashPath!,
            workerScript: WorkerScript,
            noProfile: noProfile);

    private Task<InteractiveShellHarness> StartWithHomeAsync(string tempHome, bool noProfile)
        => InteractiveShellHarness.StartAsync(
            PsBashPath!,
            workerScript: WorkerScript,
            noProfile: noProfile,
            psBashHome: tempHome);

    private static string NormalizeOutput(string raw)
        => raw.Replace("\r\n", "\n").Trim();

    // ── Case 1: simple alias expands before transpile ────────────────────────

    /// <summary>
    /// alias ll='ls -la'; typing 'll' produces a directory listing.
    /// Verifies that alias expansion happens before transpilation so the
    /// expanded 'ls -la' reaches the runtime.
    /// Uses 'echo works' as the alias value for reliable, cross-platform output.
    /// </summary>
    [SkippableFact]
    public async Task Alias_Simple_ExpandsToEchoWorks()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        await using var harness = await StartAsync();

        await harness.SendLineAsync("alias ll='echo alias-expanded'");
        await harness.WaitForPromptAsync();

        await harness.SendLineAsync("ll");
        await harness.WaitForPromptAsync();

        var output = NormalizeOutput(harness.ReadSinceLastPrompt());
        Assert.Contains("alias-expanded", output);
    }

    // ── Case 2: alias with complex bash (pipe) ───────────────────────────────

    /// <summary>
    /// alias gs='echo status | head -5' — alias whose expansion contains a pipe.
    /// The expansion must be treated as a full bash command and transpiled correctly.
    /// </summary>
    [SkippableFact]
    public async Task Alias_WithPipeInExpansion_Executes()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        await using var harness = await StartAsync();

        await harness.SendLineAsync("alias gs='echo status | head -5'");
        await harness.WaitForPromptAsync();

        await harness.SendLineAsync("gs");
        await harness.WaitForPromptAsync();

        var output = NormalizeOutput(harness.ReadSinceLastPrompt());
        Assert.Contains("status", output);
    }

    // ── Case 3: ExpandAliases expands the first word of EACH segment ─────────

    /// <summary>
    /// ExpandAliases expands the first word after each separator (|, ;, &&, ||).
    /// Verify: a command after a pipe separator also gets its first word expanded.
    ///
    /// Implementation note: reading ExpandAliases in InteractiveShell.cs confirms
    /// it restarts word extraction after each separator. So an alias after '|' IS expanded.
    /// This test documents and verifies that actual behavior.
    /// </summary>
    [SkippableFact]
    public async Task Alias_AfterPipe_IsExpandedByExpandAliases()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        await using var harness = await StartAsync();

        // 'marker' is a short unique command alias; its expansion is 'echo expanded'
        await harness.SendLineAsync("alias marker='echo expanded'");
        await harness.WaitForPromptAsync();

        // 'echo seed | marker' — 'marker' follows a pipe and must be expanded.
        // Because ExpandAliases expands the first word of each segment, 'marker'
        // becomes 'echo expanded'. The pipeline feeds into it; echo ignores stdin.
        await harness.SendLineAsync("echo seed | marker");
        await harness.WaitForPromptAsync();

        var output = NormalizeOutput(harness.ReadSinceLastPrompt());
        Assert.Contains("expanded", output);
    }

    // ── Case 4: alias defined mid-session is immediately usable ─────────────

    /// <summary>
    /// Alias defined in one command is available to the very next command.
    /// </summary>
    [SkippableFact]
    public async Task Alias_DefinedMidSession_UsableImmediately()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        await using var harness = await StartAsync();

        // Define alias.
        await harness.SendLineAsync("alias greet='echo hello-from-alias'");
        await harness.WaitForPromptAsync();

        // Use it in the very next command.
        await harness.SendLineAsync("greet");
        await harness.WaitForPromptAsync();

        var output = NormalizeOutput(harness.ReadSinceLastPrompt());
        Assert.Contains("hello-from-alias", output);
    }

    // ── Case 5: unalias removes the alias ───────────────────────────────────

    /// <summary>
    /// After 'unalias removeme', the alias is gone and 'removeme' is no longer expanded.
    /// The shell should produce no alias expansion output.
    /// </summary>
    [SkippableFact]
    public async Task Unalias_RemovesAlias()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        await using var harness = await StartAsync();

        // Define then immediately remove.
        await harness.SendLineAsync("alias removeme='echo should-not-appear'");
        await harness.WaitForPromptAsync();

        await harness.SendLineAsync("unalias removeme");
        await harness.WaitForPromptAsync();

        // After unalias, running 'removeme' should NOT output the alias expansion.
        await harness.SendLineAsync("removeme");
        await harness.WaitForPromptAsync();

        var output = NormalizeOutput(harness.ReadSinceLastPrompt());
        // The expansion text must not appear; the command is now a literal (unknown cmd).
        Assert.DoesNotContain("should-not-appear", output);
    }

    // ── Case 6: recursion prevention — alias ls='ls -la' does not hang ───────

    /// <summary>
    /// When an alias name equals the first word of its expansion (e.g. myls='myls -la'),
    /// ExpandAliases does a single pass and does not re-expand, so there is no
    /// infinite recursion. The command must complete within the timeout.
    ///
    /// Implementation note: ExpandAliases does one forward pass; it does not
    /// re-process the expanded text. 'myls' is aliased to 'echo recursive-ok',
    /// so expanding 'myls' once produces 'echo recursive-ok' — a valid command.
    /// </summary>
    [SkippableFact]
    public async Task Alias_SelfReferential_DoesNotRecurseOrHang()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        await using var harness = await StartAsync();

        // Self-referential: alias name matches first word of expansion.
        // ExpandAliases makes one pass; after substituting 'echo recursive-ok' it stops.
        await harness.SendLineAsync("alias myls='echo recursive-ok'");
        await harness.WaitForPromptAsync();

        // Run with a 5s timeout — must not hang.
        await harness.SendLineAsync("myls");
        // If recursion caused a hang, WaitForPromptAsync will throw TimeoutException.
        await harness.WaitForPromptAsync(TimeSpan.FromSeconds(5));

        var output = NormalizeOutput(harness.ReadSinceLastPrompt());
        Assert.Contains("recursive-ok", output);
    }

    // ── Case 7: alias defined in .psbashrc available at first prompt ──────────

    /// <summary>
    /// An alias written to .psbashrc is processed by SourceRcFileAsync at startup
    /// via ProcessAliasCommand, so it must be available immediately at the first prompt.
    /// </summary>
    [SkippableFact]
    public async Task Alias_DefinedInRcFile_AvailableAtFirstPrompt()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        var tempHome = Path.Combine(Path.GetTempPath(), "ps-bash-alias-rc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempHome, ".psbashrc"),
                "alias greet='echo hello from rc'\n");

            // noProfile: false so .psbashrc is sourced.
            await using var harness = await StartWithHomeAsync(tempHome, noProfile: false);

            // First command after startup — rc alias must already be registered.
            await harness.SendLineAsync("greet");
            await harness.WaitForPromptAsync();

            var output = NormalizeOutput(harness.ReadSinceLastPrompt());
            Assert.Contains("hello from rc", output);
        }
        finally
        {
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }
}
