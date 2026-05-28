using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Shell.Tests;

public sealed class CommandAssistProviderTests
{
    [Fact]
    public void DefaultConfig_UsesClaudePromptProvider()
    {
        var provider = new CommandAssistConfig().ResolveDefault();

        Assert.Equal("claude", provider.Name);
        Assert.Equal("claude", provider.Executable);
        Assert.Equal(["-p", "{{prompt}}"], provider.Args);
        Assert.Contains("{{buffer}}", provider.PromptTemplate);
        Assert.True(provider.TimeoutMs > 0);
        Assert.True(provider.OutputLimit > 0);
    }

    [Fact]
    public void RenderTemplate_IncludesLineCursorAndCwd()
    {
        var rendered = CommandAssistProviderRunner.RenderTemplate(
            "line={{buffer}} cursor={{cursor}} cwd={{cwd}}",
            new CommandAssistRequest("git st", 6),
            @"C:\repo");

        Assert.Equal(@"line=git st cursor=6 cwd=C:\repo", rendered);
    }

    [Fact]
    public void NormalizeProviderOutput_StripsMarkdownFence()
    {
        var command = CommandAssistProviderRunner.NormalizeProviderOutput("""
            ```bash
            git status --short
            ```
            """);

        Assert.Equal("git status --short", command);
    }

    [Fact]
    public async Task RunAsync_MissingExecutableReportsActionableProviderError()
    {
        var config = new CommandAssistConfig
        {
            DefaultProvider = "missing",
            Providers =
            [
                new CommandAssistProviderConfig
                {
                    Name = "missing",
                    Executable = "definitely-not-a-ps-bash-ai-provider",
                    Args = ["{{prompt}}"],
                    PromptTemplate = "{{buffer}}",
                    TimeoutMs = 1000,
                    OutputLimit = 1024,
                }
            ],
        };
        var runner = new CommandAssistProviderRunner(config);

        var ex = await Assert.ThrowsAsync<CommandAssistProviderException>(
            () => runner.RunAsync(new CommandAssistRequest("echo hi", 7), Environment.CurrentDirectory, CancellationToken.None));

        Assert.Contains("missing", ex.Message);
        Assert.Contains("definitely-not-a-ps-bash-ai-provider", ex.Message);
    }

    [Fact]
    public void Load_ReadsMultipleProvidersFromConfigFile()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_AI_CONFIG");
        var path = Path.Combine(Path.GetTempPath(), "psbash-ai-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {
                  "defaultProvider": "mock",
                  "providers": [
                    {
                      "name": "claude",
                      "executable": "claude",
                      "args": ["-p", "{{prompt}}"]
                    },
                    {
                      "name": "mock",
                      "executable": "mock-ai",
                      "args": ["--ask", "{{prompt}}"],
                      "timeoutMs": 5000,
                      "outputLimit": 2048
                    }
                  ]
                }
                """);
            Environment.SetEnvironmentVariable("PSBASH_AI_CONFIG", path);

            var config = CommandAssistConfig.Load();
            var provider = config.ResolveDefault();

            Assert.Equal(2, config.Providers.Count);
            Assert.Equal("mock", provider.Name);
            Assert.Equal("mock-ai", provider.Executable);
            Assert.Equal(["--ask", "{{prompt}}"], provider.Args);
            Assert.Equal(5000, provider.TimeoutMs);
            Assert.Equal(2048, provider.OutputLimit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_AI_CONFIG", prior);
            try { File.Delete(path); } catch { }
        }
    }
}
