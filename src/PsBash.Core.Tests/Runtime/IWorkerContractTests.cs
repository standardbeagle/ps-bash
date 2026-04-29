using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Contract tests that pin the <see cref="IWorker"/> behavioral surface:
/// every concrete implementation must satisfy them. Today the only
/// implementation is <see cref="PwshWorker"/>; future workers (e.g. an
/// in-process runspace impl from migration task T09+) plug into the same
/// fixture by adding a new <c>MemberData</c> row.
/// </summary>
[Trait("Category", "Integration")]
public class IWorkerContractTests : IAsyncLifetime
{
    private static readonly string? PwshPath = FindPwsh();
    private static readonly string WorkerScript = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "scripts", "ps-bash-worker.ps1"));

    private IWorker? _worker;

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

    public async Task InitializeAsync()
    {
        if (PwshPath is null) return;
        _worker = await PwshWorker.StartAsync(PwshPath, WorkerScript);
    }

    public async Task DisposeAsync()
    {
        if (_worker is not null)
        {
            try { await _worker.DisposeAsync(); } catch { /* idempotent */ }
        }
    }

    [Fact]
    public void IWorker_IsImplementedByPwshWorker()
    {
        // Compile-time + runtime guard: PwshWorker must satisfy IWorker so the
        // launcher's factory delegate can return it without a cast.
        Assert.True(typeof(IWorker).IsAssignableFrom(typeof(PwshWorker)));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IWorker)));
    }

    [SkippableFact]
    public async Task ExecuteAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        await _worker!.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await _worker.ExecuteAsync("Write-Host 'should not run'"));

        // Drop the field so the lifecycle fixture doesn't double-dispose.
        _worker = null;
    }

    [SkippableFact]
    public async Task QueryAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        await _worker!.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await _worker.QueryAsync("'unreachable'"));

        _worker = null;
    }

    [SkippableFact]
    public async Task QueryAsync_PreservesCallerOutputCallback()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        // Caller installs a callback (e.g. interactive shell prompt pump).
        var callerLines = new List<string>();
        Action<string> callerCallback = line => callerLines.Add(line);
        _worker!.OutputCallback = callerCallback;

        // QueryAsync internally swaps the callback; on completion it must put
        // the caller's callback back so subsequent ExecuteAsync calls keep
        // routing to the caller's sink.
        var queryResult = await _worker.QueryAsync("'hello-from-query'");

        Assert.Same(callerCallback, _worker.OutputCallback);
        Assert.Contains("hello-from-query", queryResult);

        // Verify the restored callback actually receives subsequent output.
        await _worker.ExecuteAsync("Write-Output 'hello-after-query'");
        Assert.Contains(callerLines, l => l.Contains("hello-after-query"));
    }

    [SkippableFact]
    public async Task QueryAsync_RestoresNullCallbackForCallerWhoNeverSetOne()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        Assert.Null(_worker!.OutputCallback);

        await _worker.QueryAsync("'probe'");

        Assert.Null(_worker.OutputCallback);
    }
}
