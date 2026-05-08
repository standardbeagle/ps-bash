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

    [Fact]
    public async Task ExecuteAsync_SetLocationThenInvokeBashLs_UsesCurrentLocation()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-sdkworker-cwd-ls-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "cwd-marker.txt"), "content");
            var lines = new List<string>();
            _worker!.OutputCallback = lines.Add;

            await _worker.ExecuteAsync($"Set-Location '{dir.FullName.Replace("'", "''")}'");
            await _worker.ExecuteAsync("Invoke-BashLs");

            Assert.Contains(lines, l => l.Contains("cwd-marker.txt"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_CdThenInvokeBashLsInSameCommand_UsesChangedLocation()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-sdkworker-cd-ls-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "same-command-marker.txt"), "content");
            var lines = new List<string>();
            _worker!.OutputCallback = lines.Add;

            await _worker.ExecuteAsync($"cd '{dir.FullName.Replace("'", "''")}' && Invoke-BashLs");

            Assert.Contains(lines, l => l.Contains("same-command-marker.txt"));
        }
        finally { dir.Delete(recursive: true); }
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

        var result = await _worker.QueryAsync("'query-result'");

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

    // ── Regression: ls / Invoke-BashLs output format ─────────────────────────

    /// <summary>
    /// Regression: SdkWorker.RunCommand was calling PSObject.ToString() on pipeline
    /// output, which returns "@{Name=...; BashText=...}" for PSCustomObject types like
    /// PsBash.LsEntry instead of the BashText value. Fix: extract BashText explicitly.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InvokeBashLs_OutputsBashTextNotRawObjectFormat()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-sdkworker-ls-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "testfile.txt"), "content");
            var lines = new List<string>();
            _worker!.OutputCallback = lines.Add;

            await _worker.ExecuteAsync($"Invoke-BashLs '{dir.FullName.Replace("'", "''")}'");

            // No line should be a raw PSCustomObject hash dump
            Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("@{"));
            // At least one line must contain the filename
            Assert.Contains(lines, l => l.Contains("testfile.txt"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_InvokeBashLsLongFormat_OutputsPermissionsColumn()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-sdkworker-lsl-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "readme.txt"), "hello");
            var lines = new List<string>();
            _worker!.OutputCallback = lines.Add;

            await _worker.ExecuteAsync($"Invoke-BashLs -l '{dir.FullName.Replace("'", "''")}'");

            // No raw object dump
            Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("@{"));
            // Long listing: at least one line starts with a Unix permissions character
            Assert.Contains(lines, l =>
            {
                var t = l.TrimStart();
                return t.Length > 0 && (t[0] == '-' || t[0] == 'd' || t[0] == 'l');
            });
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>
    /// Verifies that the ls alias (which maps to Invoke-BashLs) also produces
    /// clean output through the SdkWorker — not raw "@{...}" PSCustomObject dumps.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LsAlias_OutputsCleanLines()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-sdkworker-ls-alias-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "check.txt"), "ok");
            var lines = new List<string>();
            _worker!.OutputCallback = lines.Add;

            // ls is aliased to Invoke-BashLs in the PsBash module
            await _worker.ExecuteAsync($"ls '{dir.FullName.Replace("'", "''")}'");

            Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("@{"));
            Assert.Contains(lines, l => l.Contains("check.txt"));
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── Regression: native PS cmdlet output via Out-String -Stream ───────────

    /// <summary>
    /// Regression: native PS cmdlets that return typed PSCustomObjects (like New-Object /
    /// [pscustomobject]) must not produce "@{...}" dumps. SdkWorker defers non-BashText
    /// typed objects to Out-String -Stream after Invoke() — mirroring the pwsh worker's
    /// fallback path.
    /// Oracle note (Directive 1): behavior is ps-bash-specific (SDK/pwsh parity gap,
    /// no bash equivalent for formatting engine output).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NativePSCustomObject_OutputsFormattedNotAtBraceDump()
    {
        var lines = new List<string>();
        _worker!.OutputCallback = lines.Add;

        // Emit a typed PSCustomObject directly — this is what Select-Object and
        // many native cmdlets return. BaseObject is PSCustomObject (not string).
        var exitCode = await _worker.ExecuteAsync("[pscustomobject]@{Name='test123'; Value=42}");

        Assert.Equal(0, exitCode);
        // Must not produce raw hash dump
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("@{"));
        // Out-String -Stream formats as a table with column headers
        Assert.Contains(lines, l => l.Contains("Name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("test123"));
    }

    /// <summary>
    /// Regression: PSCustomObject arrays (common return value from many PS cmdlets) must not
    /// produce "@{...}" dumps. SdkWorker's property-enumeration fallback emits
    /// "Property : Value" lines (Format-List style) for each PSCustomObject.
    /// Oracle note (Directive 1): ps-bash-specific (PowerShell pipeline object formatting).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MultipleNativePSCustomObjects_OutputsFormattedNotAtBraceDump()
    {
        var lines = new List<string>();
        _worker!.OutputCallback = lines.Add;

        // Emit multiple PSCustomObjects in a pipeline — [pscustomobject] is SMA-native
        // and does not require Select-Object / Microsoft.PowerShell.Utility module.
        var exitCode = await _worker.ExecuteAsync(
            "@([pscustomobject]@{Num=1},[pscustomobject]@{Num=2},[pscustomobject]@{Num=3})");

        Assert.Equal(0, exitCode);
        // Must not produce raw hash dumps
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("@{"));
        // Property-enumeration fallback emits "Num : 1" etc.
        Assert.Contains(lines, l => l.Contains("Num", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("1"));
        Assert.Contains(lines, l => l.Contains("2"));
        Assert.Contains(lines, l => l.Contains("3"));
    }
}
