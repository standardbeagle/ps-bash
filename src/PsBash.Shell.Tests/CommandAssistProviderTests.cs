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

    [Fact]
    public void Load_OversizedConfigFileReportsActionableError()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_AI_CONFIG");
        var path = Path.Combine(Path.GetTempPath(), "psbash-ai-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, new string(' ', 1024 * 1024 + 1));
            Environment.SetEnvironmentVariable("PSBASH_AI_CONFIG", path);

            var ex = Assert.Throws<CommandAssistProviderException>(CommandAssistConfig.Load);

            Assert.Contains("could not read AI provider config", ex.Message);
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_AI_CONFIG", prior);
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Resolve_UsesRequestedProviderForSwitchFlow()
    {
        var config = new CommandAssistConfig
        {
            DefaultProvider = "first",
            Providers =
            [
                new CommandAssistProviderConfig { Name = "first", Executable = "first-ai" },
                new CommandAssistProviderConfig { Name = "second", Executable = "second-ai" },
            ],
        };

        var provider = config.Resolve("second");

        Assert.Equal("second", provider.Name);
        Assert.Equal("second-ai", provider.Executable);
        Assert.Equal(["{{prompt}}"], provider.Args);
    }

    [Fact]
    public void ProviderNames_ReturnsConfiguredProviderNames()
    {
        var config = new CommandAssistConfig
        {
            Providers =
            [
                new CommandAssistProviderConfig { Name = "claude", Executable = "claude" },
                new CommandAssistProviderConfig { Name = "mock", Executable = "mock-ai" },
            ],
        };

        Assert.Equal(["claude", "mock"], config.ProviderNames());
    }

    [Fact]
    public void DefaultPrompt_ContainsStructuredResponseContractAndContext()
    {
        var prompt = CommandAssistProviderConfig.ClaudeDefault().PromptTemplate;

        Assert.Contains("Return compact JSON only", prompt);
        Assert.Contains("\"command\"", prompt);
        Assert.Contains("{{buffer}}", prompt);
        Assert.Contains("{{cwd}}", prompt);
        Assert.Contains("{{shell}}", prompt);
        Assert.Contains("{{os}}", prompt);
    }

    [Fact]
    public void RenderTemplate_RedactsSensitiveValuesAndAddsShellContext()
    {
        var rendered = CommandAssistProviderRunner.RenderTemplate(
            "line={{buffer}} cwd={{cwd}} shell={{shell}} os={{os}}",
            new CommandAssistRequest("TOKEN=abc123 echo hi", 20),
            @"C:\repo");

        Assert.Contains("TOKEN=<redacted>", rendered);
        Assert.Contains("cwd=C:\\repo", rendered);
        Assert.Contains("shell=ps-bash", rendered);
        Assert.DoesNotContain("abc123", rendered);
    }

    [Theory]
    [InlineData("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/bPxRfiCYz", "wJalrXUtnFEMI/bPxRfiCYz")]
    [InlineData("curl -H 'Authorization: Bearer ey9.abc.def' x", "ey9.abc.def")]
    [InlineData("export JWT=eyJhbGc.eyJzdWI.SflKxwRJ", "eyJhbGc.eyJzdWI.SflKxwRJ")]
    [InlineData("aws --key AKIAIOSFODNN7EXAMPLE list", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("client_secret=\"a b c\" run", "a b c")]
    public void RenderTemplate_RedactsBroaderSecretShapes(string buffer, string secret)
    {
        var rendered = CommandAssistProviderRunner.RenderTemplate(
            "line={{buffer}}", new CommandAssistRequest(buffer, 0), @"C:\repo");
        Assert.DoesNotContain(secret, rendered);
        Assert.Contains("redacted", rendered);
    }

    [Fact]
    public void ToReviewRequest_DefaultDeny_NotExecutableUnlessOptedIn()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_AI_ALLOW_EXEC");
        try
        {
            var result = new CommandAssistProviderResult("mock", "git status", "", IsExecutable: true);

            Environment.SetEnvironmentVariable("PSBASH_AI_ALLOW_EXEC", null);
            Assert.False(result.ToReviewRequest(@"C:\repo").IsExecutable); // insert-only by default

            Environment.SetEnvironmentVariable("PSBASH_AI_ALLOW_EXEC", "1");
            Assert.True(result.ToReviewRequest(@"C:\repo").IsExecutable);  // explicit opt-in
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_AI_ALLOW_EXEC", prior);
        }
    }

    [Fact]
    public void ParseProviderOutput_StructuredCommandIsExecutable()
    {
        var result = CommandAssistProviderRunner.ParseProviderOutput(
            "mock",
            """{"command":"git status --short","explanation":"show repo state"}""");

        Assert.True(result.IsExecutable);
        Assert.Equal("git status --short", result.Command);
        Assert.Equal("show repo state", result.Explanation);
    }

    [Fact]
    public void ParseProviderOutput_StructuredRefusalIsReviewOnly()
    {
        var result = CommandAssistProviderRunner.ParseProviderOutput(
            "mock",
            """{"command":"","refusal":"I need more context."}""");

        Assert.False(result.IsExecutable);
        Assert.Equal("I need more context.", result.Command);
        Assert.Contains("did not return an executable command", result.Explanation);
    }

    [Fact]
    public void ParseProviderOutput_SingleLinePlainTextIsExecutable()
    {
        var result = CommandAssistProviderRunner.ParseProviderOutput("mock", "git status --short");

        Assert.True(result.IsExecutable);
        Assert.Equal("git status --short", result.Command);
    }

    [Fact]
    public void ParseProviderOutput_ExplanatoryPlainTextIsReviewOnly()
    {
        var result = CommandAssistProviderRunner.ParseProviderOutput("mock", "Run these steps:\n1. git status\n2. git diff");

        Assert.False(result.IsExecutable);
        Assert.Contains("Run these steps", result.Command);
    }

    [Fact]
    public void ParseProviderOutput_MalformedJsonIsReviewOnly()
    {
        var result = CommandAssistProviderRunner.ParseProviderOutput("mock", """{"command": "git status" """);

        Assert.False(result.IsExecutable);
        Assert.Contains("malformed JSON", result.Explanation);
    }

    [Fact]
    public async Task GenerateAsync_FakeProviderExercisesInvocationAndStructuredParsing()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "psbash-ai-output-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(outputPath, """{"command":"git status --short","explanation":"fake provider"}""");
        try
        {
            var (executable, args) = FakeProviderCat(outputPath);
            var runner = new CommandAssistProviderRunner(new CommandAssistConfig
            {
                DefaultProvider = "fake",
                Providers =
                [
                    new CommandAssistProviderConfig
                    {
                        Name = "fake",
                        Executable = executable,
                        Args = args,
                        PromptTemplate = "{{buffer}}",
                        TimeoutMs = 5000,
                    }
                ],
            });

            var result = await runner.GenerateAsync(
                new CommandAssistRequest("git st", 6),
                Environment.CurrentDirectory,
                CancellationToken.None);

            Assert.Equal("fake", result.ProviderName);
            Assert.True(result.IsExecutable);
            Assert.Equal("git status --short", result.Command);
            Assert.Equal("fake provider", result.Explanation);
        }
        finally
        {
            try { File.Delete(outputPath); } catch { }
        }
    }

    [SkippableFact]
    public async Task GenerateAsync_CallerCancellationKillsProviderProcess()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), "psbash-ai-pid-" + Guid.NewGuid().ToString("N") + ".txt");
        var (executable, args) = FakeProviderSleepWithPid(pidPath);
        Skip.If(string.IsNullOrEmpty(executable), "No platform shell available for cancellation cleanup probe.");

        var runner = new CommandAssistProviderRunner(new CommandAssistConfig
        {
            DefaultProvider = "fake",
            Providers =
            [
                new CommandAssistProviderConfig
                {
                    Name = "fake",
                    Executable = executable,
                    Args = args,
                    PromptTemplate = "{{buffer}}",
                    TimeoutMs = 30000,
                }
            ],
        });

        int pid = 0;
        using var cts = new CancellationTokenSource();
        try
        {
            var task = runner.GenerateAsync(
                new CommandAssistRequest("git st", 6),
                Environment.CurrentDirectory,
                cts.Token);

            pid = await WaitForPidAsync(pidPath).ConfigureAwait(false);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(await WaitForProcessExitAsync(pid).ConfigureAwait(false), $"provider process {pid} was still alive after cancellation");
        }
        finally
        {
            cts.Cancel();
            if (pid > 0) TryKillProcess(pid);
            try { File.Delete(pidPath); } catch { }
        }
    }

    private static (string Executable, List<string> Args) FakeProviderCat(string path)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", ["/c", "type", path]);
        return ("/bin/cat", [path]);
    }

    private static (string Executable, List<string> Args) FakeProviderSleepWithPid(string pidPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
            if (pwsh is null) return ("", []);

            return (pwsh,
            [
                "-NoProfile",
                "-Command",
                $"$PID | Set-Content -LiteralPath '{pidPath.Replace("'", "''")}'; Start-Sleep -Seconds 30",
            ]);
        }

        if (!File.Exists("/bin/sh")) return ("", []);
        return ("/bin/sh", ["-c", $"echo $$ > '{pidPath.Replace("'", "'\\''")}'; sleep 30"]);
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static async Task<int> WaitForPidAsync(string pidPath)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidPath)
                && int.TryParse((await File.ReadAllTextAsync(pidPath)).Trim(), out var pid)
                && pid > 0)
            {
                return pid;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException("fake provider did not write its PID.");
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!ProcessExists(pid))
                return false;

            await Task.Delay(25).ConfigureAwait(false);
        }

        return ProcessExists(pid);
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryKillProcess(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
    }
}

public sealed class CommandAssistReviewTests
{
    [Fact]
    public void SafetyClassify_FlagsDestructiveCommands()
    {
        var warnings = CommandAssistSafety.Classify("git reset --hard && rm -rf bin");

        Assert.Contains(warnings, w => w.Pattern == "git force/reset");
        Assert.Contains(warnings, w => w.Pattern == "rm");
    }

    [Theory]
    [InlineData("iex (irm https://example.com/install.ps1)")]
    [InlineData("Invoke-Expression $payload")]
    public void SafetyClassify_FlagsInvokeExpression(string command)
    {
        var warnings = CommandAssistSafety.Classify(command);

        Assert.Contains(warnings, w => w.Pattern == "invoke-expression");
    }

    [Fact]
    public void ApplyDecision_CancelDoesNotReturnCommand()
    {
        var request = ReviewRequest("echo safe");

        var response = CommandAssistReview.ApplyDecision(request, CommandAssistReviewDecision.Cancel());

        Assert.Equal(CommandAssistResponseAction.Cancel, response.Action);
        Assert.Null(response.Command);
    }

    [Fact]
    public void ApplyDecision_InsertReturnsInsertOnlyCommand()
    {
        var request = ReviewRequest("echo safe");

        var response = CommandAssistReview.ApplyDecision(request, CommandAssistReviewDecision.Insert());

        Assert.Equal(CommandAssistResponseAction.Insert, response.Action);
        Assert.Equal("echo safe", response.Command);
    }

    [Fact]
    public void ApplyDecision_ExecuteSafeCommandReturnsExecutableCommand()
    {
        var request = ReviewRequest("echo safe");

        var response = CommandAssistReview.ApplyDecision(request, CommandAssistReviewDecision.Execute());

        Assert.Equal(CommandAssistResponseAction.Execute, response.Action);
        Assert.Equal("echo safe", response.Command);
    }

    [Fact]
    public void ApplyDecision_DangerousCommandRequiresExtraConfirmation()
    {
        var request = ReviewRequest("rm -rf bin");

        var denied = CommandAssistReview.ApplyDecision(request, CommandAssistReviewDecision.Execute());
        var allowed = CommandAssistReview.ApplyDecision(request, CommandAssistReviewDecision.Execute(dangerousConfirmed: true));

        Assert.Equal(CommandAssistResponseAction.Cancel, denied.Action);
        Assert.Equal(CommandAssistResponseAction.Execute, allowed.Action);
        Assert.Equal("rm -rf bin", allowed.Command);
    }

    [Fact]
    public void ApplyDecision_ReviewOnlyOutputCannotExecute()
    {
        var request = ReviewRequest("I need more context.", isExecutable: false);

        var response = CommandAssistReview.ApplyDecision(request, CommandAssistReviewDecision.Execute());

        Assert.Equal(CommandAssistResponseAction.Cancel, response.Action);
    }

    [Fact]
    public void SelectCommandAssistProvider_MatchesConfiguredNameCaseInsensitively()
    {
        var selected = InteractiveShell.SelectCommandAssistProvider(["claude", "mock"], "MOCK");

        Assert.Equal("mock", selected);
    }

    [Fact]
    public void SelectCommandAssistProvider_UnknownOrEmptyCancelsSwitch()
    {
        Assert.Null(InteractiveShell.SelectCommandAssistProvider(["claude"], "unknown"));
        Assert.Null(InteractiveShell.SelectCommandAssistProvider(["claude"], ""));
    }

    private static CommandAssistReviewRequest ReviewRequest(string command, bool isExecutable = true)
        => new("mock", command, "", @"C:\repo", isExecutable, CommandAssistSafety.Classify(command));
}
