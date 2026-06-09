using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Tests for SdkWorker: in-process PowerShell SDK worker that implements IWorker.
/// Oracle note (Directive 1): SdkWorker behavior is ps-bash-specific (no bash
/// oracle available in-process); hand-written asserts justified per exception list.
///
/// Per-test setup/teardown is delegated to <see cref="HostWorkerFixture"/>:
/// each test calls <c>_fixture.CreateWorker()</c> for a fresh worker that the
/// fixture disposes when the test finishes (DisposeAsync on the test class
/// drains the fixture's tracked-worker list). Tests that probe Dispose
/// semantics may dispose explicitly; duplicate disposes are swallowed by
/// the fixture.
/// </summary>
[Collection("SdkHost")]
public class SdkWorkerTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    // AC1: Invoke-BashEcho hello emits "hello", exit 0
    [Fact]
    public async Task ExecuteAsync_InvokeBashEchoHello_EmitsHelloExitZero()
    {
        var run = await _fixture.ExecuteCapturedAsync("Invoke-BashEcho 'hello'");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains(run.Lines, l => l.Contains("hello"));
    }

    [Fact]
    public async Task ExecuteAsync_InvokeBashLsLong_EmitsBashTextNotPsObjectSerialization()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"psbash-ls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "sample.txt"), "content");

        try
        {
            var psPath = tempDir.Replace("'", "''");
            var run = await _fixture.ExecuteCapturedAsync(
                $"Set-Location '{psPath}'; Invoke-BashLs -la");

            Assert.Equal(0, run.ExitCode);
            Assert.Contains(run.Lines, l => l.Contains("sample.txt", StringComparison.Ordinal));
            Assert.DoesNotContain(run.Lines, l => l.StartsWith("@{", StringComparison.Ordinal));
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

        var worker = _fixture.CreateWorker();
        await worker.ExecuteAsync($"Set-Location '{cdPath}'");
        var pwd = await worker.QueryAsync("(Get-Location).Path");

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
        var freshWorker = _fixture.CreateWorker();
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
        var worker = _fixture.CreateWorker();
        await worker.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await worker.ExecuteAsync("Write-Output 'should not run'"));
    }

    // HasExited is true after dispose
    [Fact]
    public async Task HasExited_TrueAfterDispose()
    {
        var worker = _fixture.CreateWorker();
        await worker.DisposeAsync();
        Assert.True(worker.HasExited);
    }

    // QueryAsync saves/restores OutputCallback
    [Fact]
    public async Task QueryAsync_PreservesCallerOutputCallback()
    {
        var worker = _fixture.CreateWorker();
        var callerLines = new List<string>();
        Action<string> callerCallback = l => callerLines.Add(l);
        worker.OutputCallback = callerCallback;

        var result = await worker.QueryAsync("Write-Output 'query-result'");

        Assert.Same(callerCallback, worker.OutputCallback);
        Assert.Contains("query-result", result);
    }

    [Fact]
    public async Task QueryAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var worker = _fixture.CreateWorker();
        await worker.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await worker.QueryAsync("'unreachable'"));
    }

    [Fact]
    public async Task QueryAsync_RestoresNullCallbackForCallerWhoNeverSetOne()
    {
        var worker = _fixture.CreateWorker();
        Assert.Null(worker.OutputCallback);

        await worker.QueryAsync("'probe'");

        Assert.Null(worker.OutputCallback);
    }

    // Diagnostic: exit 7 must return exit code 7
    [Fact]
    public async Task ExecuteAsync_Exit7_ReturnsExitCode7()
    {
        var worker = _fixture.CreateWorker();
        var exitCode = await worker.ExecuteAsync("exit 7");
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

        var run = await _fixture.ExecuteCapturedAsync(script);
        Assert.Equal(0, run.ExitCode);
        Assert.NotEmpty(run.Lines);

        var joined = string.Join("\n", run.Lines);

        // Hashtable-style ToString() means the formatter was bypassed — that's the bug.
        Assert.DoesNotContain("@{FriendlyName=", joined);

        // Header row must be present.
        Assert.Contains("FriendlyName", joined);
        Assert.Contains("Status", joined);

        // Separator line under the header (PowerShell formatter signature).
        Assert.Contains(run.Lines, l => l.Contains("------------") && l.Contains("------"));

        // Both data rows must be rendered as formatted columns, not @{...}.
        Assert.Contains(run.Lines, l => l.Contains("Intel USB 3.10") && l.Contains("OK"));
        Assert.Contains(run.Lines, l => l.Contains("USB Root Hub") && l.Contains("OK"));
    }

    // Opt-in styled default output: with PSBASH_DEFAULT_FORMAT=styled, the same native
    // PSObject pipeline is rendered by Format-Styled (Strata/Spectre) instead of Out-String,
    // so the output carries ANSI SGR escapes and NOT the stock formatter's dashed separator.
    // Oracle note (Directive 1): ps-bash-specific (no bash equivalent) — asserts on the
    // cmdlet surface. Env-gated and restored so no other test sees the flag.
    [Fact]
    public async Task ExecuteAsync_StyledDefaultFlag_RendersNativePSObjectsWithAnsi()
    {
        var priorFormat = Environment.GetEnvironmentVariable("PSBASH_DEFAULT_FORMAT");
        var priorNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_DEFAULT_FORMAT", "styled");
            Environment.SetEnvironmentVariable("NO_COLOR", null); // ensure ANSI is emitted

            var script = @"
                [PSCustomObject]@{ FriendlyName = 'Intel USB 3.10'; Status = 'OK' }
                [PSCustomObject]@{ FriendlyName = 'USB Root Hub';   Status = 'OK' }
            ";
            var run = await _fixture.ExecuteCapturedAsync(script);
            Assert.Equal(0, run.ExitCode);
            Assert.NotEmpty(run.Lines);

            var joined = string.Join("\n", run.Lines);
            // Strata/Spectre emitted ANSI styling — the styled path, not Out-String.
            Assert.Matches("\\x1b\\[[0-9;]*m", joined);
            // The data is still present (styled, not lost).
            Assert.Contains("Intel USB 3.10", joined);
            Assert.Contains("USB Root Hub", joined);
            // The stock Out-String formatter's dashed header separator must be ABSENT.
            Assert.DoesNotContain(run.Lines, l => l.Contains("------------") && l.Contains("------"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_DEFAULT_FORMAT", priorFormat);
            Environment.SetEnvironmentVariable("NO_COLOR", priorNoColor);
        }
    }

    // BashText-bearing objects must continue to stream as plain text and must
    // NOT be re-rendered through the formatter (which would add headers like
    // "BashText" and a separator). This is the regression bar for the existing
    // Invoke-Bash* pipeline path while we add native-PSObject formatting.
    [Fact]
    public async Task ExecuteAsync_BashTextObjects_StreamWithoutFormatterHeader()
    {
        var run = await _fixture.ExecuteCapturedAsync(
            "Invoke-BashEcho 'one'; Invoke-BashEcho 'two'");
        Assert.Equal(0, run.ExitCode);

        var joined = string.Join("\n", run.Lines);
        Assert.Contains("one", joined);
        Assert.Contains("two", joined);

        // No formatter table for plain text/BashText output.
        Assert.DoesNotContain("BashText", joined);
        Assert.DoesNotContain(run.Lines, l => l.TrimStart().StartsWith("--------"));
    }

    // A many-property object must render through PowerShell's REAL formatter
    // (Out-String), which picks Format-List (one "Name : Value" line per
    // property) for >4 properties — not the hand-rolled fallback's single wide
    // all-columns table row. This is the regression bar for native cmdlet output
    // like `Test-NetConnection` rendering with its registered view + line breaks
    // (the `tnc` formatting bug). Distinguishes the real formatter from fallback.
    [Fact]
    public async Task ExecuteAsync_ManyPropertyObject_RendersAsListViaRealFormatter()
    {
        var script = "[PSCustomObject]@{ Alpha='a'; Bravo='b'; Charlie='c'; Delta='d'; Echo='e'; Foxtrot='f' }";
        var run = await _fixture.ExecuteCapturedAsync(script);
        Assert.Equal(0, run.ExitCode);

        var joined = string.Join("\n", run.Lines);
        Assert.DoesNotContain("@{Alpha=", joined);

        // List view: each property on its own line as "Name : Value" — proves the
        // real formatter ran (the fallback emits one wide table row) and that the
        // line breaks survived (the WriteLine newline fix).
        Assert.Contains(run.Lines, l => l.TrimStart().StartsWith("Alpha") && l.Contains(": a"));
        Assert.Contains(run.Lines, l => l.TrimStart().StartsWith("Foxtrot") && l.Contains(": f"));
    }

    // Mixed stream: a BashText emitter followed by native PSObjects must keep
    // the BashText line raw and render the native objects as a table afterwards.
    [Fact]
    public async Task ExecuteAsync_MixedBashTextAndPSObjects_BothRenderCorrectly()
    {
        var script = @"
            Invoke-BashEcho 'before-table'
            [PSCustomObject]@{ Col1 = 'a'; Col2 = 'b' }
        ";
        var run = await _fixture.ExecuteCapturedAsync(script);
        Assert.Equal(0, run.ExitCode);

        Assert.Contains(run.Lines, l => l.Contains("before-table"));
        Assert.Contains(run.Lines, l => l.Contains("Col1") && l.Contains("Col2"));
        Assert.Contains(run.Lines, l => l.Contains(" a ") || (l.Contains("a") && l.Contains("b")));
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("@{"));
    }

    // Regression: an auto-loadable *alias* must resolve via the CommandNotFoundAction
    // discovery fallback. The SDK runspace bypasses the normal module auto-loader (to
    // dodge the v5 PSSnapIn TypeLoadException), which left alias-triggered auto-load
    // broken — `tnc` failed even though `Test-NetConnection` worked and plain pwsh
    // resolved both. The handler now indexes Get-Module -ListAvailable exports and
    // imports the owning module on a miss. NetTCPIP is Windows-only, so this uses a
    // self-contained temp module to stay cross-platform (Directive 5).
    // Oracle note (Directive 1): ps-bash-specific runspace behavior, no bash oracle.
    [Fact]
    public async Task ExecuteAsync_AutoloadableAlias_ResolvesViaDiscoveryFallback()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var modName = "PbAutoload" + token;
        var funcName = "Get-PbProbe" + token;
        var aliasName = "pbalias" + token;
        var root = Path.Combine(Path.GetTempPath(), "psbash-autoload-" + token);
        var modDir = Path.Combine(root, modName);
        Directory.CreateDirectory(modDir);
        try
        {
            File.WriteAllText(Path.Combine(modDir, modName + ".psm1"),
                $"function {funcName} {{ 'pbprobe-ran' }}\n" +
                $"Set-Alias -Name {aliasName} -Value {funcName}\n" +
                $"Export-ModuleMember -Function {funcName} -Alias {aliasName}\n");
            File.WriteAllText(Path.Combine(modDir, modName + ".psd1"),
                "@{\n" +
                $"  RootModule = '{modName}.psm1'\n" +
                "  ModuleVersion = '1.0.0'\n" +
                $"  GUID = '{Guid.NewGuid()}'\n" +
                $"  FunctionsToExport = @('{funcName}')\n" +
                $"  AliasesToExport = @('{aliasName}')\n" +
                "  CmdletsToExport = @()\n" +
                "  VariablesToExport = @()\n" +
                "}\n");

            var rootEscaped = root.Replace("'", "''");
            // Fresh runspace (ExecuteCapturedAsync creates one): the index is cold, so it
            // is built AFTER PSModulePath is extended, picking up the temp module. Invoke
            // the ALIAS (not the function) — the case the auto-loader misses.
            var script =
                $"$env:PSModulePath = '{rootEscaped}' + [IO.Path]::PathSeparator + $env:PSModulePath; " +
                $"{aliasName}";

            var run = await _fixture.ExecuteCapturedAsync(script);

            Assert.Equal(0, run.ExitCode);
            Assert.Contains(run.Lines, l => l.Contains("pbprobe-ran", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // Regression guard: the discovery fallback must NOT swallow genuine unknowns —
    // a command no module exports still exits 127 (bash "command not found").
    [Fact]
    public async Task ExecuteAsync_GenuineUnknownCommand_StillExits127()
    {
        var missing = "pbnope" + Guid.NewGuid().ToString("N")[..8];
        var run = await _fixture.ExecuteCapturedAsync(missing);
        Assert.Equal(127, run.ExitCode);
    }

    // P3: the live CompleteInput path must run PowerShell's own completion engine against the
    // runspace and honor Register-ArgumentCompleter — the parameter-value mechanism the
    // interactive completion engine uses for dynamic values.
    [Fact]
    public async Task CompleteInput_HonorsRegisterArgumentCompleter()
    {
        var worker = _fixture.CreateWorker();
        await worker.ExecuteAsync(
            "function Test-Paint { param([string]$Color) }; " +
            "Register-ArgumentCompleter -CommandName Test-Paint -ParameterName Color -ScriptBlock { " +
            "  param($commandName,$parameterName,$wordToComplete,$commandAst,$fakeBound) " +
            "  'crimson','cobalt','coral' | Where-Object { $_ -like \"$wordToComplete*\" } | " +
            "  ForEach-Object { [System.Management.Automation.CompletionResult]::new($_) } }");

        var completer = (ICompletionWorker)worker;
        const string fragment = "Test-Paint -Color co";
        var matches = await completer.CompleteInputAsync(fragment, fragment.Length);

        Assert.Contains("cobalt", matches);
        Assert.Contains("coral", matches);
        Assert.DoesNotContain("crimson", matches); // does not match the "co" word
    }

    // Regression guard for the concurrent env/cwd corruption (fb6bf72): a bash
    // command's variables are process-global ($env:NAME) and the warm pool does
    // NOT isolate them across runspaces. Two commands executing concurrently in
    // the same host process therefore race on the shared env unless SdkWorker's
    // process-wide _globalExecGate serializes execution.
    //
    // Deterministic by construction: each worker writes its own marker to the
    // SHARED env var, sleeps (forcing temporal overlap), then reads it back.
    // WITH the gate each command runs to completion atomically and reads its own
    // marker. WITHOUT the gate every worker writes (last writer wins), all sleep
    // concurrently, then all read the same final value — so at least one worker
    // reads a marker that is not its own and the assertion fails.
    [Fact]
    public async Task ConcurrentExecute_SharedEnvVar_EachCommandReadsItsOwnValue()
    {
        const int workerCount = 5;
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => _fixture.CreateWorker())
            .ToArray();

        async Task<(int Id, List<string> Lines)> RunAsync(int id, SdkWorker worker)
        {
            var lines = new List<string>();
            worker.OutputCallback = lines.Add;
            // Set the shared env var, force overlap with a sleep, then echo it.
            await worker.ExecuteAsync(
                $"$env:__psbash_racevar = '{id}'; " +
                "Start-Sleep -Milliseconds 150; " +
                "Invoke-BashEcho $env:__psbash_racevar");
            return (id, lines);
        }

        var results = await Task.WhenAll(
            workers.Select((w, id) => RunAsync(id, w)));

        foreach (var (id, lines) in results)
        {
            var emitted = string.Concat(lines).Replace("\n", string.Empty).Trim();
            Assert.Equal(id.ToString(), emitted);
        }
    }
}
