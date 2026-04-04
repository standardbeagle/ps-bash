using System.Diagnostics;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class InteractiveShellTests
{
    private static readonly string? PwshPath = FindPwsh();
    private static readonly string ProjectDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PsBash.Shell"));

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

    [SkippableFact]
    public async Task InteractiveMode_LaunchesPwshAndPassesThroughExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        // Launch ps-bash with -i flag, but pipe in an 'exit 42' command
        // so the interactive session terminates immediately with a known code.
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("-i");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet run");

        // Send exit command to the interactive pwsh session via stdin
        await process.StandardInput.WriteLineAsync("exit 42");
        process.StandardInput.Close();

        await process.WaitForExitAsync();

        Assert.Equal(42, process.ExitCode);
    }

    [SkippableFact]
    public async Task InteractiveMode_DoesNotRequireCommand()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("-i");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet run");

        await process.StandardInput.WriteLineAsync("exit 0");
        process.StandardInput.Close();

        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
    }
}
