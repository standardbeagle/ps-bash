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

    [SkippableFact]
    public async Task HasExited_AfterKill_ReturnsTrue()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var worker = await PwshWorker.StartAsync(PwshPath!, WorkerScript);
        Assert.False(worker.HasExited);

        // Kill the underlying process via reflection
        var processField = worker.GetType().GetField("_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var process = (System.Diagnostics.Process)processField.GetValue(worker)!;
        process.Kill();
        await process.WaitForExitAsync();

        Assert.True(worker.HasExited);
        await worker.DisposeAsync();
    }

    /// <summary>
    /// Regression: when DisposeAsync times out waiting for a stuck worker, the
    /// kill must take the entire process tree — not just the pwsh parent.
    /// A bare <c>_process.Kill()</c> orphans grandchildren; on Windows those
    /// orphans can hold file locks on src/PsBash.Shell/bin/Debug/ps-bash.exe
    /// and block the next build (the production leak that motivated this
    /// regression test).
    /// </summary>
    [SkippableFact]
    public async Task DisposeAsync_KillsEntireProcessTree_WhenWorkerIsStuck()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var worker = await PwshWorker.StartAsync(PwshPath!, WorkerScript);

        // Spawn a long-lived grandchild from the worker. Use Start-Process so
        // the grandchild is a real OS child of pwsh, not an inline scriptblock
        // that disappears with the parent. The PID is echoed back so we can
        // observe the grandchild after Dispose.
        var lines = new List<string>();
        worker.OutputCallback = line => { lock (lines) lines.Add(line); };

        var spawnExpr =
            "$p = Start-Process -FilePath '" + PwshPath!.Replace("'", "''") + "' " +
            "-ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' " +
            "-PassThru -WindowStyle Hidden; " +
            "Write-Host \"GRANDCHILD_PID=$($p.Id)\"";
        var exit = await worker.ExecuteAsync(spawnExpr);
        Assert.Equal(0, exit);

        int? grandchildPid = null;
        foreach (var l in lines)
        {
            const string marker = "GRANDCHILD_PID=";
            var idx = l.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0 && int.TryParse(l.AsSpan(idx + marker.Length).Trim(), out var pid))
            {
                grandchildPid = pid;
                break;
            }
        }
        Assert.NotNull(grandchildPid);

        // Confirm the grandchild is actually running before the kill.
        using (var live = System.Diagnostics.Process.GetProcessById(grandchildPid!.Value))
        {
            Assert.False(live.HasExited, "grandchild was not running before dispose");
        }

        // Make the worker stuck so DisposeAsync hits its 5 s timeout and falls
        // through to the kill path. Drop in a busy loop that ignores stdin EOF.
        // We do not await the Execute — we do not want to wait for it.
        _ = Task.Run(async () =>
        {
            try { await worker.ExecuteAsync("while ($true) { Start-Sleep -Milliseconds 100 }"); }
            catch { }
        });
        await Task.Delay(500); // let the busy loop start

        // Dispose: with the bug, pwsh dies but the grandchild keeps running.
        // With the fix (Kill(entireProcessTree:true)), the grandchild dies too.
        await worker.DisposeAsync();

        // Give the OS up to 3 s to reap the grandchild after the kill.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        bool exited = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var leftover = System.Diagnostics.Process.GetProcessById(grandchildPid.Value);
                if (leftover.HasExited) { exited = true; break; }
            }
            catch (ArgumentException) { exited = true; break; }
            await Task.Delay(100);
        }

        if (!exited)
        {
            // Best-effort kill so we do not leak from the test itself if the
            // assertion is about to fail.
            try
            {
                using var leftover = System.Diagnostics.Process.GetProcessById(grandchildPid.Value);
                if (!leftover.HasExited) leftover.Kill(entireProcessTree: true);
            }
            catch { }
        }

        Assert.True(exited,
            $"PwshWorker.DisposeAsync did not kill grandchild pid {grandchildPid} — " +
            "process tree leak. See task EvRRfm53eveg.");
    }

}
