using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Tests for SdkWorker: in-process PowerShell SDK worker that implements IWorker.
/// Oracle note (Directive 1): SdkWorker behavior is ps-bash-specific (no bash
/// oracle available in-process); hand-written asserts justified per exception list.
/// </summary>
[Collection("SdkHost")]
public class SdkWorkerTests : IAsyncLifetime
{
    private SdkWorker? _worker;

    public Task InitializeAsync()
    {
        _worker = SdkWorker.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_worker is not null)
        {
            try { await _worker.DisposeAsync(); } catch { }
        }
    }

    // AC1: Invoke-BashEcho hello emits "hello", exit 0
    [Fact]
    public async Task ExecuteAsync_InvokeBashEchoHello_EmitsHelloExitZero()
    {
        var lines = new List<string>();
        _worker!.OutputCallback = lines.Add;

        var exitCode = await _worker.ExecuteAsync("Invoke-BashEcho 'hello'");

        Assert.Equal(0, exitCode);
        Assert.Contains(lines, l => l.Contains("hello"));
    }

    // AC2: cd /tmp then pwd across two ExecuteAsync calls reports /tmp (or C:\Temp on Windows)
    [Fact]
    public async Task ExecuteAsync_CdThenPwd_CrossCallStatePreserved()
    {
        // Use a temp dir that exists on both Windows and Linux
        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Normalize to forward slashes for cd
        var cdPath = tempDir.Replace('\\', '/');

        await _worker!.ExecuteAsync($"Set-Location '{cdPath}'");
        var pwd = await _worker.QueryAsync("(Get-Location).Path");

        // Normalize both to compare without trailing slash / separator differences
        Assert.Contains(Path.GetFileName(tempDir), pwd, StringComparison.OrdinalIgnoreCase);
    }

    // AC3: Module init counter incremented exactly once across N calls
    [Fact]
    public async Task SdkRunspace_ModuleLoadedExactlyOnce_AcrossMultipleCalls()
    {
        // Reset the counter before this test (in case other tests ran first)
        var countBefore = SdkRunspace.ModuleLoadCount;

        // Create a brand-new worker (forces a new SdkRunspace + module load)
        await using var freshWorker = SdkWorker.Create();
        var countAfterCreate = SdkRunspace.ModuleLoadCount;
        Assert.Equal(countBefore + 1, countAfterCreate);

        // Execute N commands — counter must not increase further
        for (int i = 0; i < 5; i++)
            await freshWorker.ExecuteAsync($"Write-Output 'call-{i}'");

        Assert.Equal(countAfterCreate, SdkRunspace.ModuleLoadCount);
    }

    // Dispose guard: ExecuteAsync after DisposeAsync throws ObjectDisposedException
    [Fact]
    public async Task ExecuteAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        await _worker!.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await _worker.ExecuteAsync("Write-Output 'should not run'"));
        _worker = null;
    }

    // HasExited is true after dispose
    [Fact]
    public async Task HasExited_TrueAfterDispose()
    {
        await _worker!.DisposeAsync();
        Assert.True(_worker.HasExited);
        _worker = null;
    }

    // QueryAsync saves/restores OutputCallback
    [Fact]
    public async Task QueryAsync_PreservesCallerOutputCallback()
    {
        var callerLines = new List<string>();
        Action<string> callerCallback = l => callerLines.Add(l);
        _worker!.OutputCallback = callerCallback;

        var result = await _worker.QueryAsync("Write-Output 'query-result'");

        Assert.Same(callerCallback, _worker.OutputCallback);
        Assert.Contains("query-result", result);
    }

    // Diagnostic: exit 7 must return exit code 7
    [Fact]
    public async Task ExecuteAsync_Exit7_ReturnsExitCode7()
    {
        var exitCode = await _worker!.ExecuteAsync("exit 7");
        Assert.Equal(7, exitCode);
    }
}
