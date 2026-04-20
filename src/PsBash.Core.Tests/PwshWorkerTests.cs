using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests;

[Trait("Category", "Integration")]
public class PwshWorkerTests : IAsyncLifetime
{
    private static readonly string? PwshPath = FindPwsh();
    private static readonly string WorkerScript = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "scripts", "ps-bash-worker.ps1"));

    private PwshWorker? _worker;

    private static string? FindPwsh()
    {
        try
        {
            return PwshLocator.Locate();
        }
        catch (PwshNotFoundException)
        {
            return null;
        }
    }

    public async Task InitializeAsync()
    {
        if (PwshPath is null) return;
        _worker = await PwshWorker.StartAsync(PwshPath, WorkerScript);
    }

    public async Task DisposeAsync()
    {
        if (_worker is not null)
            await _worker.DisposeAsync();
    }

    [Fact]
    public void WorkerScript_Exists()
    {
        Assert.True(File.Exists(WorkerScript),
            $"Worker script not found at {WorkerScript}");
    }

    [SkippableFact]
    public async Task StartAsync_SpawnsWorker_ReceivesReady()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        Assert.NotNull(_worker);
    }

    [SkippableFact]
    public async Task ExecuteAsync_WriteHostHello_ReturnsOutputAndExitZero()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await _worker!.ExecuteAsync("Write-Host 'hello'");
            Assert.Equal(0, exitCode);
            Assert.Contains("hello", output.ToString());
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_ExitCode1_ReturnsPropagatedExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await _worker!.ExecuteAsync("throw 'fail'");
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_MultipleCommands_MaintainsState()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var code1 = await _worker!.ExecuteAsync("$testVar = 42");
            Assert.Equal(0, code1);

            output.GetStringBuilder().Clear();
            var code2 = await _worker!.ExecuteAsync("Write-Host $testVar");
            Assert.Equal(0, code2);
            Assert.Contains("42", output.ToString());
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_MultilineCommand_ExecutesCorrectly()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await _worker!.ExecuteAsync(
                "1..3 | ForEach-Object {\n    Write-Host \"line $_\"\n}");
            Assert.Equal(0, exitCode);
            var text = output.ToString();
            Assert.Contains("line 1", text);
            Assert.Contains("line 2", text);
            Assert.Contains("line 3", text);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_OutputCallback_ReceivesOutputLines()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var lines = new List<string>();
        _worker!.OutputCallback = line => lines.Add(line);

        var exitCode = await _worker.ExecuteAsync("Write-Host 'callback-test'");
        Assert.Equal(0, exitCode);
        Assert.Contains("callback-test", lines);
    }

    [SkippableFact]
    public async Task ExecuteAsync_OutputCallback_BypassesConsole()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var lines = new List<string>();
        _worker!.OutputCallback = line => lines.Add(line);

        var consoleOutput = new StringWriter();
        Console.SetOut(consoleOutput);
        try
        {
            await _worker.ExecuteAsync("Write-Host 'only-callback'");
            Assert.Contains("only-callback", lines);
            Assert.DoesNotContain("only-callback", consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_NoCallback_UsesConsole()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        Assert.Null(_worker!.OutputCallback);

        var consoleOutput = new StringWriter();
        Console.SetOut(consoleOutput);
        try
        {
            await _worker.ExecuteAsync("Write-Host 'console-test'");
            Assert.Contains("console-test", consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    [SkippableFact]
    public async Task DisposeAsync_ClosesWorkerGracefully()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var worker = await PwshWorker.StartAsync(PwshPath!, WorkerScript);
        await worker.DisposeAsync();
    }

}
