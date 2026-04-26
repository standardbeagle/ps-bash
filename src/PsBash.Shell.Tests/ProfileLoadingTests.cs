using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Tests for the .psbashrc profile-loading mechanism.
///
/// These tests cover the mechanism (does it load, does --norc skip it, does a
/// missing file cause no error, does a syntax error surface to stderr) rather
/// than any specific rc content.
///
/// Env isolation: the harness now accepts an optional psBashHome directory.
/// When supplied, HOME and PSBASH_HOME are set to that directory so
/// InteractiveShell.SourceRcFileAsync finds the test-written .psbashrc there
/// instead of the real user profile.
/// </summary>
[Trait("Category", "Integration")]
public class ProfileLoadingTests
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

    // ── Case 1: rc sourced at startup — exported env var is visible ──────────

    [SkippableFact]
    public async Task RcFile_ExportedVar_VisibleAfterStartup()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        var tempHome = CreateTempHome();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempHome, ".psbashrc"),
                "export MY_RC_VAR=hello\n");

            await using var harness = await StartWithHomeAsync(tempHome, noProfile: false);

            await harness.SendLineAsync("printenv MY_RC_VAR");
            await harness.WaitForPromptAsync();

            var output = NormalizeOutput(harness.ReadSinceLastPrompt());
            Assert.Contains("hello", output);
        }
        finally
        {
            DeleteTempHome(tempHome);
        }
    }

    // ── Case 2: env var exported in rc visible in the first prompted command ──

    [SkippableFact]
    public async Task RcFile_ExportedVar_VisibleInFirstCommand()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        var tempHome = CreateTempHome();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempHome, ".psbashrc"),
                "export FIRST_CMD_VAR=world\n");

            await using var harness = await StartWithHomeAsync(tempHome, noProfile: false);

            // First command after startup; rc must already be applied.
            await harness.SendLineAsync("echo $FIRST_CMD_VAR");
            await harness.WaitForPromptAsync();

            var output = NormalizeOutput(harness.ReadSinceLastPrompt());
            Assert.Contains("world", output);
        }
        finally
        {
            DeleteTempHome(tempHome);
        }
    }

    // ── Case 3: --norc skips sourcing ────────────────────────────────────────

    [SkippableFact]
    public async Task RcFile_NoProfile_RcNotSourced()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        var tempHome = CreateTempHome();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempHome, ".psbashrc"),
                "export NOPROFILE_VAR=shouldnotappear\n");

            // noProfile: true adds --norc — rc must not be sourced.
            await using var harness = await StartWithHomeAsync(tempHome, noProfile: true);

            await harness.SendLineAsync("printenv NOPROFILE_VAR");
            await harness.WaitForPromptAsync();

            var output = NormalizeOutput(harness.ReadSinceLastPrompt());
            Assert.DoesNotContain("shouldnotappear", output);
        }
        finally
        {
            DeleteTempHome(tempHome);
        }
    }

    // ── Case 4: missing rc file — no error, shell starts normally ───────────

    [SkippableFact]
    public async Task RcFile_Missing_ShellStartsNormally()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        var tempHome = CreateTempHome();
        // Intentionally no .psbashrc — must be absent.
        try
        {
            // If the shell crashes or hangs, WaitForPromptAsync inside StartAsync
            // will throw TimeoutException and the test fails.
            await using var harness = await StartWithHomeAsync(tempHome, noProfile: false);

            await harness.SendLineAsync("echo alive");
            await harness.WaitForPromptAsync();

            var output = NormalizeOutput(harness.ReadSinceLastPrompt());
            Assert.Contains("alive", output);
        }
        finally
        {
            DeleteTempHome(tempHome);
        }
    }

    // ── Case 5: syntax error in rc — surfaced to stderr, shell still starts ─

    [SkippableFact]
    public async Task RcFile_SyntaxError_SurfacedToStderrShellStillStarts()
    {
        Skip.IfNot(CanRun, "ps-bash binary not found");

        var tempHome = CreateTempHome();
        try
        {
            // An `if` with no `then` or `fi` is a bash parse error — the
            // parser throws ParseException when it hits EOF expecting "then".
            await File.WriteAllTextAsync(
                Path.Combine(tempHome, ".psbashrc"),
                "if true\n");

            // Shell must still start — WaitForPromptAsync inside StartAsync would
            // throw TimeoutException if the shell failed to reach a prompt.
            await using var harness = await StartWithHomeAsync(tempHome, noProfile: false);

            // Issue a command to ensure the shell is responsive after the rc error.
            await harness.SendLineAsync("echo after-rc");
            await harness.WaitForPromptAsync();

            var stderr = harness.Stderr;
            Assert.True(
                stderr.Contains("syntax error", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("psbashrc", StringComparison.OrdinalIgnoreCase),
                $"Expected stderr to mention syntax error or psbashrc, got: {stderr}");
        }
        finally
        {
            DeleteTempHome(tempHome);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string CreateTempHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash-rc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempHome(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static string NormalizeOutput(string raw)
        => raw.Replace("\r\n", "\n").Trim();

    private Task<InteractiveShellHarness> StartWithHomeAsync(string tempHome, bool noProfile)
        => InteractiveShellHarness.StartAsync(
            PsBashPath!,
            workerScript: WorkerScript,
            noProfile: noProfile,
            psBashHome: tempHome);
}
