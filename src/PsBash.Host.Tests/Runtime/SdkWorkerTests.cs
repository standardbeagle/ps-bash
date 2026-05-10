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

    [Fact]
    public async Task ExecuteAsync_InvokeBashLsLong_EmitsBashTextNotPsObjectSerialization()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"psbash-ls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "sample.txt"), "content");

        var lines = new List<string>();
        _worker!.OutputCallback = lines.Add;

        try
        {
            var psPath = tempDir.Replace("'", "''");
            var exitCode = await _worker.ExecuteAsync($"Set-Location '{psPath}'; Invoke-BashLs -la");

            Assert.Equal(0, exitCode);
            Assert.Contains(lines, l => l.Contains("sample.txt", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.StartsWith("@{", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
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

    [Fact]
    public async Task QueryAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        await _worker!.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await _worker.QueryAsync("'unreachable'"));
        _worker = null;
    }

    [Fact]
    public async Task QueryAsync_RestoresNullCallbackForCallerWhoNeverSetOne()
    {
        Assert.Null(_worker!.OutputCallback);

        await _worker.QueryAsync("'probe'");

        Assert.Null(_worker.OutputCallback);
    }

    // Diagnostic: exit 7 must return exit code 7
    [Fact]
    public async Task ExecuteAsync_Exit7_ReturnsExitCode7()
    {
        var exitCode = await _worker!.ExecuteAsync("exit 7");
        Assert.Equal(7, exitCode);
    }

    // Native PSObject pipeline rendering: Select-Object on a custom object
    // must produce a formatted table (header + separator + columns), matching
    // how native pwsh + Out-Default would render it. Regression for the
    // Get-PnpDevice repro on Windows: typed PSObjects fall through to the
    // formatter, not item.ToString() which would emit @{Name=...; Status=...}.
    // Oracle note (Directive 1): asserts on PowerShell formatter output, which
    // is platform-stable byte-for-byte across pwsh 7.x SDK on all OSes — using
    // a synthesized PSCustomObject (not Get-PnpDevice) keeps the test
    // cross-platform per Directive 5; the underlying rendering path is the
    // same one Get-PnpDevice flows through.
    [Fact]
    public async Task ExecuteAsync_NativePSObjectPipeline_RendersFormattedTable()
    {
        var lines = new List<string>();
        _worker!.OutputCallback = lines.Add;

        // Synthesize the same shape as `Get-PnpDevice | Where | Select Name,Status`:
        // PSCustomObjects with two named properties. Bare `[PSCustomObject]@{}`
        // is built-in to System.Management.Automation, no module-loader path,
        // so this works in test environments that don't have
        // Microsoft.PowerShell.Utility on PSModulePath. On Windows real
        // ps-bash these are exactly the shape Get-PnpDevice | Select emits.
        var script = @"
            [PSCustomObject]@{ FriendlyName = 'Intel USB 3.10'; Status = 'OK' }
            [PSCustomObject]@{ FriendlyName = 'USB Root Hub';   Status = 'OK' }
        ";

        var exitCode = await _worker.ExecuteAsync(script);
        Assert.Equal(0, exitCode);
        Assert.NotEmpty(lines);

        var joined = string.Join("\n", lines);

        // Hashtable-style ToString() means the formatter was bypassed — that's the bug.
        Assert.DoesNotContain("@{FriendlyName=", joined);

        // Header row must be present.
        Assert.Contains("FriendlyName", joined);
        Assert.Contains("Status", joined);

        // Separator line under the header (PowerShell formatter signature).
        Assert.Contains(lines, l => l.Contains("------------") && l.Contains("------"));

        // Both data rows must be rendered as formatted columns, not @{...}.
        Assert.Contains(lines, l => l.Contains("Intel USB 3.10") && l.Contains("OK"));
        Assert.Contains(lines, l => l.Contains("USB Root Hub") && l.Contains("OK"));
    }

    // BashText-bearing objects must continue to stream as plain text and must
    // NOT be re-rendered through the formatter (which would add headers like
    // "BashText" and a separator). This is the regression bar for the existing
    // Invoke-Bash* pipeline path while we add native-PSObject formatting.
    [Fact]
    public async Task ExecuteAsync_BashTextObjects_StreamWithoutFormatterHeader()
    {
        var lines = new List<string>();
        _worker!.OutputCallback = lines.Add;

        var exitCode = await _worker.ExecuteAsync("Invoke-BashEcho 'one'; Invoke-BashEcho 'two'");
        Assert.Equal(0, exitCode);

        var joined = string.Join("\n", lines);
        Assert.Contains("one", joined);
        Assert.Contains("two", joined);

        // No formatter table for plain text/BashText output.
        Assert.DoesNotContain("BashText", joined);
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("--------"));
    }

    // Mixed stream: a BashText emitter followed by native PSObjects must keep
    // the BashText line raw and render the native objects as a table afterwards.
    [Fact]
    public async Task ExecuteAsync_MixedBashTextAndPSObjects_BothRenderCorrectly()
    {
        var lines = new List<string>();
        _worker!.OutputCallback = lines.Add;

        var script = @"
            Invoke-BashEcho 'before-table'
            [PSCustomObject]@{ Col1 = 'a'; Col2 = 'b' }
        ";
        var exitCode = await _worker.ExecuteAsync(script);
        Assert.Equal(0, exitCode);

        Assert.Contains(lines, l => l.Contains("before-table"));
        Assert.Contains(lines, l => l.Contains("Col1") && l.Contains("Col2"));
        Assert.Contains(lines, l => l.Contains(" a ") || (l.Contains("a") && l.Contains("b")));
        Assert.DoesNotContain(lines, l => l.StartsWith("@{"));
    }
}
