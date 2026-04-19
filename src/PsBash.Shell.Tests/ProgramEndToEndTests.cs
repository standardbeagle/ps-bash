using System.Diagnostics;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class ProgramEndToEndTests
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        psi.Environment["PSBASH_WORKER"] = WorkerScript;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet run");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunShellWithStdinAsync(
        string stdinContent, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        psi.Environment["PSBASH_WORKER"] = WorkerScript;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet run");

        await process.StandardInput.WriteAsync(stdinContent);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout, stderr);
    }

    [SkippableFact]
    public async Task Command_WriteHostHello_OutputsHelloAndExitsZero()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync("-c", "Write-Host hello");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
    }

    [SkippableFact]
    public async Task Command_ThrowError_PropagatesExitCodeAndStderr()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, _, stderr) = await RunShellAsync("-c", "throw 'deliberate failure'");

        Assert.Equal(1, exitCode);
        Assert.Contains("deliberate failure", stderr);
    }

    // Regression: `ps-bash -c "git log --oneline -20"` was reported to fail
    // with "The term '-l' is not recognized". The command string must be
    // handed to the transpiler intact — long flags whose first char collides
    // with a recognized ps-bash short flag (-l / -o / -n / -i / -e) must not
    // be mistaken for shell-host flags.
    [SkippableTheory]
    [InlineData("echo --oneline -20", "--oneline -20")]
    [InlineData("echo --list --long", "--list --long")]
    [InlineData("echo --name --include", "--name --include")]
    public async Task Command_LongFlagStartingWithShortFlagLetter_PassesToTranspilerIntact(
        string command, string expectedOutput)
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, stderr) = await RunShellAsync("-c", command);

        Assert.Equal(0, exitCode);
        Assert.Contains(expectedOutput, stdout);
        Assert.DoesNotContain("is not recognized", stderr);
    }

    [SkippableFact]
    public async Task Stdin_ReadsAndExecutes()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellWithStdinAsync(
            "Write-Host 'from stdin'", "-s");

        Assert.Equal(0, exitCode);
        Assert.Contains("from stdin", stdout);
    }

    [SkippableFact]
    public async Task NoArgs_EntersInteractiveModeAndExitsCleanly()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, _, _) = await RunShellWithStdinAsync("");

        Assert.Equal(0, exitCode);
    }

    [SkippableFact]
    public async Task Debug_WritesToStderr()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Write-Host ok");

        psi.Environment["PSBASH_WORKER"] = WorkerScript;
        psi.Environment["PSBASH_DEBUG"] = "1";

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet run");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("[ps-bash] input:", stderr);
        Assert.Contains("[ps-bash] transpiled:", stderr);
        Assert.Contains("[ps-bash] pwsh:", stderr);
        Assert.Contains("[ps-bash] exit:", stderr);
    }
}
