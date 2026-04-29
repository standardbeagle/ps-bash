using System.Diagnostics;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// T07 end-to-end fallback tests: spawn the real ps-bash launcher and verify
/// that PSBASH_DISABLE_HOST=1 and a missing host binary produce identical
/// stdout to the in-process PwshWorker path. Output equivalence is the
/// rollout safety net — host-backed and in-process paths must produce the
/// same bytes for the launcher's three non-interactive modes.
/// </summary>
[Trait("Category", "Integration")]
public class ProgramFallbackTests
{
    private static readonly string? PwshPath = FindPwsh();
    private static readonly string ProjectDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PsBash.Shell"));
    private static readonly string WorkerScript = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "scripts", "ps-bash-worker.ps1"));

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

    private static ProcessStartInfo BuildPsi(string[] arguments, IDictionary<string, string?>? envOverrides = null)
    {
        var psi = new ProcessStartInfo { FileName = "dotnet" };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        psi.Environment["PSBASH_WORKER"] = WorkerScript;
        // Default off so tests that don't set it can't accidentally pick up
        // a side-by-side host binary from a prior build.
        psi.Environment["PSBASH_DISABLE_HOST"] = "1";
        if (envOverrides is not null)
        {
            foreach (var kv in envOverrides)
                psi.Environment[kv.Key] = kv.Value;
        }
        return psi;
    }

    [SkippableFact]
    public async Task DashC_DisableHost_RunsViaPwshFallback()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var psi = BuildPsi(["-c", "echo hello"], new Dictionary<string, string?>
        {
            ["PSBASH_DISABLE_HOST"] = "1",
        });
        var (exit, stdout, _) = await ProcessRunHelper.RunAsync(psi, stdinContent: null);

        Assert.Equal(0, exit);
        Assert.Contains("hello", stdout);
    }

    [SkippableFact]
    public async Task DashC_PsbashHostNonexistent_FallsBackAndExitsZeroWithSingleWarning()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var bogus = Path.Combine(Path.GetTempPath(), "ps-bash",
            "no-host-" + Guid.NewGuid().ToString("N") + ".exe");

        var psi = BuildPsi(["-c", "echo hello"], new Dictionary<string, string?>
        {
            ["PSBASH_HOST"] = bogus,
            ["PSBASH_DISABLE_HOST"] = null, // override the default-off so the host path is exercised
        });
        var (exit, stdout, stderr) = await ProcessRunHelper.RunAsync(psi, stdinContent: null);

        Assert.Equal(0, exit);
        Assert.Contains("hello", stdout);
        Assert.Contains("host unavailable", stderr);

        // Single warning even though the launcher creates one worker per -c
        // invocation. (The rate-limit is per-process, so this is one line.)
        var occurrences = 0;
        var idx = 0;
        while ((idx = stderr.IndexOf("host unavailable", idx, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            idx += 1;
        }
        Assert.Equal(1, occurrences);
    }

    [SkippableFact]
    public async Task Stdin_DisableHost_RunsViaPwshFallback()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var psi = BuildPsi(["-s"], new Dictionary<string, string?>
        {
            ["PSBASH_DISABLE_HOST"] = "1",
        });
        var (exit, stdout, _) = await ProcessRunHelper.RunAsync(psi, stdinContent: "echo from-stdin");

        Assert.Equal(0, exit);
        Assert.Contains("from-stdin", stdout);
    }

    [SkippableFact]
    public async Task ScriptMode_DisableHost_RunsViaPwshFallback()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var scriptPath = Path.Combine(Path.GetTempPath(), "ps-bash",
            "script-fallback-" + Guid.NewGuid().ToString("N") + ".sh");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "echo from-script\n");

        try
        {
            var psi = BuildPsi([scriptPath], new Dictionary<string, string?>
            {
                ["PSBASH_DISABLE_HOST"] = "1",
            });
            var (exit, stdout, _) = await ProcessRunHelper.RunAsync(psi, stdinContent: null);

            Assert.Equal(0, exit);
            Assert.Contains("from-script", stdout);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// Output-equivalence canary across the three non-interactive modes:
    /// the same bash command must produce the same stdout whether host-routing
    /// is disabled or in fallback. Locks down the rollout safety promise.
    /// </summary>
    [SkippableFact]
    public async Task DashC_OutputEqualsBetween_DisableHost_And_MissingHostBinary()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var disablePsi = BuildPsi(["-c", "echo a; echo b"], new Dictionary<string, string?>
        {
            ["PSBASH_DISABLE_HOST"] = "1",
        });
        var (exitA, stdoutA, _) = await ProcessRunHelper.RunAsync(disablePsi, stdinContent: null);

        var bogus = Path.Combine(Path.GetTempPath(), "ps-bash",
            "no-host-eq-" + Guid.NewGuid().ToString("N") + ".exe");
        var fallbackPsi = BuildPsi(["-c", "echo a; echo b"], new Dictionary<string, string?>
        {
            ["PSBASH_HOST"] = bogus,
            ["PSBASH_DISABLE_HOST"] = null,
        });
        var (exitB, stdoutB, _) = await ProcessRunHelper.RunAsync(fallbackPsi, stdinContent: null);

        Assert.Equal(exitA, exitB);
        Assert.Equal(NormalizeLineEndings(stdoutA), NormalizeLineEndings(stdoutB));
    }

    private static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");
}
