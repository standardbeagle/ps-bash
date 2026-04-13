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

public class AliasExpansionTests
{
    [Fact]
    public void ExpandAliases_NoAliases_ReturnsInputUnchanged()
    {
        var result = InteractiveShell.ExpandAliases("ls -la");
        Assert.Equal("ls -la", result);
    }

    [Fact]
    public void ExpandAliases_SimpleAlias_ExpandsFirstWord()
    {
        // This test relies on the static Aliases dictionary being populated
        // We can't easily test this in isolation without refactoring,
        // so we test through ProcessAliasCommand + ExpandAliases
        InteractiveShell.ProcessAliasCommand("alias ll='ls -la'");
        var result = InteractiveShell.ExpandAliases("ll /tmp");
        Assert.Equal("ls -la /tmp", result);
    }

    [Fact]
    public void ExpandAliases_UnknownCommand_ReturnsInputUnchanged()
    {
        InteractiveShell.ProcessAliasCommand("alias ll='ls -la'");
        var result = InteractiveShell.ExpandAliases("cat file.txt");
        Assert.Equal("cat file.txt", result);
    }

    [Fact]
    public void ExpandAliases_AliasWithPipe_ExpandsBeforePipe()
    {
        InteractiveShell.ProcessAliasCommand("alias lc='ls -la | wc -l'");
        var result = InteractiveShell.ExpandAliases("lc");
        Assert.Equal("ls -la | wc -l", result);
    }

    [Fact]
    public void ProcessAliasCommand_SetsAlias()
    {
        InteractiveShell.ProcessAliasCommand("alias gs='git status'");
        var result = InteractiveShell.ExpandAliases("gs");
        Assert.Equal("git status", result);
    }

    [Fact]
    public void ProcessAliasCommand_UnaliasRemovesAlias()
    {
        InteractiveShell.ProcessAliasCommand("alias temp='echo hi'");
        InteractiveShell.ProcessAliasCommand("unalias temp");
        var result = InteractiveShell.ExpandAliases("temp something");
        Assert.Equal("temp something", result);
    }

    [Fact]
    public void ProcessAliasCommand_NotAliasCommand_ReturnsOriginal()
    {
        var result = InteractiveShell.ProcessAliasCommand("ls -la");
        Assert.Equal("ls -la", result);
    }
}
