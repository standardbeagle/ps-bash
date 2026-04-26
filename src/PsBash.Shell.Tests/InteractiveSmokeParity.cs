using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Interactive parity smoke suite — Phase 1 (simple commands + quoting).
///
/// These tests run ps-bash in interactive mode via <see cref="InteractiveShellHarness"/>
/// and verify that basic bash quoting and variable expansion behave correctly.
/// All tests use [SkippableFact] so they are skipped when the ps-bash binary is
/// not built — consistent with InteractiveShellHarnessTests patterns.
/// </summary>
[Trait("Category", "Integration")]
public class InteractiveSmokeParity
{
    private static readonly string? PsBashPath = InteractiveShellHarness.FindPsBashBinary();
    private static readonly string? PwshPath = FindPwsh();

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

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

    private bool CanRun => PsBashPath is not null && PwshPath is not null;

    private async Task<InteractiveShellHarness> StartAsync()
    {
        return await InteractiveShellHarness.StartAsync(
            PsBashPath!,
            workerScript: WorkerScript,
            noProfile: true);
    }

    // ── Phase 1: simple commands + quoting ──────────────────────────────────

    /// <summary>
    /// Sends <c>echo "hello $USER"</c> to the interactive shell and verifies
    /// the output contains "hello " followed by a non-empty username.
    ///
    /// $USER in the bash command expands to $env:USER in PowerShell (SimpleVarSub
    /// → EmitSimpleVar → env: prefix path). On Windows, USER is not set by default;
    /// the test seeds it from USERNAME so the harness inherits a non-empty value.
    ///
    /// Hand-written assert justified: this is ps-bash-specific behaviour (env var
    /// passthrough in interactive mode); no bash oracle available on Windows CI.
    /// </summary>
    [SkippableFact]
    public async Task Interactive_EchoQuotedVar_ExpandsUser()
    {
        Skip.IfNot(CanRun, "ps-bash binary or pwsh not found");

        // On Windows, USER is typically unset; seed it from USERNAME so the
        // harness child process inherits a non-empty value via env inheritance.
        var expectedUser = Environment.GetEnvironmentVariable("USER");
        if (string.IsNullOrEmpty(expectedUser))
        {
            expectedUser = Environment.GetEnvironmentVariable("USERNAME")
                ?? Environment.UserName;
            if (!string.IsNullOrEmpty(expectedUser))
                Environment.SetEnvironmentVariable("USER", expectedUser);
        }

        Skip.If(string.IsNullOrEmpty(expectedUser), "Cannot determine username");

        await using var harness = await StartAsync();

        // Send the command with double-quoted $USER so the emitter sees a
        // DoubleQuoted word containing a SimpleVarSub for USER.
        await harness.SendLineAsync(@"echo ""hello $USER""");
        await harness.WaitForPromptAsync();

        var output = harness.ReadSinceLastPrompt()
            .Replace("\r\n", "\n")
            .Trim();

        // Must contain "hello " (with space) to confirm both the literal and the
        // variable substitution are present.
        Assert.Contains("hello ", output);

        // The username portion must be non-empty and match the expected value.
        Assert.Contains(expectedUser, output);
    }
}
