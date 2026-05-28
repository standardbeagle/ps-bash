using PsBash.Core.Runtime;
using PsBash.Host.Runtime;
using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

/// <summary>
/// Tests for <see cref="CompletionEngine"/> — the async completion seam that composes the
/// static TabCompleter base set with live, runspace-backed command-name completion.
///
/// Oracle note (qa-rubric Directive 1): completion is a ps-bash-specific interactive surface
/// with no bash oracle; hand-written asserts are justified per the exception list. A fake
/// <see cref="IWorker"/> stands in for the runspace so the merge / fallback / cancellation
/// paths are exercised deterministically (no sleeps — Directive 6).
/// </summary>
public class CompletionEngineTests
{
    private sealed class FakeWorker : IWorker, ICompletionWorker
    {
        public Func<string, CancellationToken, Task<string>>? OnQuery;
        public Func<string, int, CancellationToken, Task<IReadOnlyList<string>>>? OnComplete;
        public int QueryCount;
        public int CompleteCount;

        public Action<string>? OutputCallback { get; set; }
        public bool HasExited { get; set; }

        public Task<int> ExecuteAsync(string command, CancellationToken ct = default) => Task.FromResult(0);

        public Task<string> QueryAsync(string expression, CancellationToken ct = default)
        {
            QueryCount++;
            return OnQuery is null ? Task.FromResult(string.Empty) : OnQuery(expression, ct);
        }

        Task<IReadOnlyList<string>> ICompletionWorker.CompleteInputAsync(string input, int cursorIndex, CancellationToken ct)
        {
            CompleteCount++;
            return OnComplete is null
                ? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>())
                : OnComplete(input, cursorIndex, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CompletionEngine Engine(IWorker? worker) => new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        cwd: () => Environment.CurrentDirectory,
        lastCommand: () => null,
        history: null,
        worker: worker);

    [Fact]
    public async Task CommandPosition_MergesLiveRunspaceCommands_FilteredByPrefix()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("Get-Foo\nGet-Bar\n") };
        var result = (await Engine(fake).CompleteAsync("Get-F", cursor: 5, default)).Texts();

        Assert.Equal(1, fake.QueryCount);
        Assert.Contains("Get-Foo", result);          // live command, matches the typed prefix
        Assert.DoesNotContain("Get-Bar", result);    // re-filtered out (does not start with "Get-F")
    }

    [Fact]
    public async Task WorkerThrows_FallsBackToStaticBaseSet()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => throw new InvalidOperationException("busy") };
        var result = (await Engine(fake).CompleteAsync("l", cursor: 1, default)).Texts();

        // No exception surfaces, and the static base set (KnownCommands) still completes.
        Assert.Contains("ls", result);
    }

    [Fact]
    public async Task QueryCancelled_FallsBackToBase_WithoutHanging()
    {
        // The query never completes on its own; it resolves only when the token cancels.
        var fake = new FakeWorker
        {
            OnQuery = (_, ct) =>
            {
                var tcs = new TaskCompletionSource<string>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            },
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled → deterministic, no timer/sleep

        var result = (await Engine(fake).CompleteAsync("l", cursor: 1, cts.Token)).Texts();

        Assert.Contains("ls", result);
    }

    [Fact]
    public async Task NotCommandPosition_DoesNotQueryRunspace()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("ShouldNotAppear\n") };
        // Cursor is on an argument (path), not the command word.
        var result = (await Engine(fake).CompleteAsync("ls /tmp/x", cursor: 9, default)).Texts();

        Assert.Equal(0, fake.QueryCount);
        Assert.DoesNotContain("ShouldNotAppear", result);
    }

    [Fact]
    public async Task EmptyToken_DoesNotQueryRunspace()
    {
        // An empty command token would make Get-Command -Name '*' enumerate the whole session.
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("Everything\n") };
        var result = (await Engine(fake).CompleteAsync(string.Empty, cursor: 0, default)).Texts();

        Assert.Equal(0, fake.QueryCount);
        Assert.DoesNotContain("Everything", result);
    }

    [Fact]
    public async Task ExitedWorker_SkipsLiveQuery()
    {
        var fake = new FakeWorker { HasExited = true, OnQuery = (_, _) => Task.FromResult("Get-Foo\n") };
        var result = (await Engine(fake).CompleteAsync("Get-F", cursor: 5, default)).Texts();

        Assert.Equal(0, fake.QueryCount);
        Assert.DoesNotContain("Get-Foo", result);
    }

    [Fact]
    public async Task ParameterName_PsCmdlet_CompletesRealParametersFilteredByPrefix()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("-Path\n-PathType\n-Force\n") };
        const string line = "Get-ChildItem -Pa";
        var result = await Engine(fake).CompleteAsync(line, line.Length, default);

        Assert.Equal(1, fake.QueryCount);
        Assert.Contains("-Path", result.Texts());
        Assert.Contains("-PathT", result.Texts());
        Assert.DoesNotContain("-Force", result.Texts()); // does not match the typed "-Pa" prefix
        Assert.Contains("-PathType", result.Labels()); // display remains canonical
    }

    [Fact]
    public async Task ParameterName_DisplaysCanonicalNameAndType_ButInsertsSafePrefix()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("Path|String|\nPathType|String|\n") };
        const string line = "Get-ChildItem -Pa";
        var result = await Engine(fake).CompleteAsync(line, line.Length, default);

        Assert.Contains(result, item => item.InsertText == "-PathT" && item.DisplayText == "-PathType <String>");
    }

    [Fact]
    public void BuildParameterNameItems_AmbiguousPrefixFallsBackToCanonicalFullName()
    {
        var candidates = new[]
        {
            new PowerShellParameterCandidate("Path", "String", []),
            new PowerShellParameterCandidate("PathType", "String", []),
        };

        var result = CompletionEngine.BuildParameterNameItems(candidates, "-Pa");

        Assert.Contains(result, item => item.InsertText == "-Path" && item.DisplayText == "-Path <String>");
        Assert.Contains(result, item => item.InsertText == "-PathT" && item.DisplayText == "-PathType <String>");
    }

    [Fact]
    public void BuildParameterNameItems_AvoidsPowerShellCommonParameterCollisions()
    {
        var candidates = new[]
        {
            new PowerShellParameterCandidate("ErrorActionPreference", "ActionPreference", []),
        };

        var result = CompletionEngine.BuildParameterNameItems(candidates, "-E");

        var item = Assert.Single(result);
        Assert.Equal("-ErrorActionP", item.InsertText);
        Assert.Equal("-ErrorActionPreference <ActionPreference>", item.DisplayText);
    }

    [Fact]
    public void BuildParameterNameItems_PrefersSafeAliases()
    {
        var candidates = new[]
        {
            new PowerShellParameterCandidate("LiteralPath", "String", ["LP"]),
            new PowerShellParameterCandidate("Path", "String", []),
        };

        var result = CompletionEngine.BuildParameterNameItems(candidates, "-L");

        var item = Assert.Single(result);
        Assert.Equal("-LP", item.InsertText);
        Assert.Equal("-LiteralPath <String>", item.DisplayText);
    }

    [Fact]
    public void BuildParameterNameItems_MatchesCaseInsensitively()
    {
        var candidates = new[]
        {
            new PowerShellParameterCandidate("Destination", "String", []),
        };

        var result = CompletionEngine.BuildParameterNameItems(candidates, "-d");

        var item = Assert.Single(result);
        Assert.Equal("-Des", item.InsertText);
        Assert.Equal("-Destination <String>", item.DisplayText);
    }

    [Fact]
    public void BuildParameterNameItems_CanBeConfiguredToInsertCanonicalNames()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_PS_PARAMETER_INSERT");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_PS_PARAMETER_INSERT", "full");
            var candidates = new[]
            {
                new PowerShellParameterCandidate("PathType", "String", []),
            };

            var result = CompletionEngine.BuildParameterNameItems(candidates, "-Pa");

            var item = Assert.Single(result);
            Assert.Equal("-PathType", item.InsertText);
            Assert.Equal("-PathType <String>", item.DisplayText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_PS_PARAMETER_INSERT", prior);
        }
    }

    [Fact]
    public async Task ParameterValue_AfterFlag_CompletesValidateSetOrEnumValues()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("quick\nquiet\nfull\n") };
        const string line = "Set-Thing -Mode q";
        var result = (await Engine(fake).CompleteAsync(line, line.Length, default)).Texts();

        Assert.Equal(1, fake.QueryCount);
        Assert.Contains("quick", result);
        Assert.Contains("quiet", result);
        Assert.DoesNotContain("full", result); // filtered by the typed "q" prefix
    }

    [Fact]
    public async Task BashCommandFlag_DoesNotQueryPsParameters()
    {
        // grep has bash flag specs, so flag completion stays on the static path — no PS query.
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("-ShouldNotAppear\n") };
        const string line = "grep -i";
        var result = (await Engine(fake).CompleteAsync(line, line.Length, default)).Texts();

        Assert.Equal(0, fake.QueryCount);
        Assert.DoesNotContain("-ShouldNotAppear", result);
    }

    [Fact]
    public async Task ParameterValue_PrefersPowerShellEngine_OverIntrospection()
    {
        // The PS engine (CompleteInput) returns values, so the introspection fallback is skipped.
        var fake = new FakeWorker
        {
            OnComplete = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["fast", "slow"]),
            OnQuery = (_, _) => Task.FromResult("SHOULD-NOT-FALL-BACK\n"),
        };
        const string line = "Set-Thing -Mode f";
        var result = (await Engine(fake).CompleteAsync(line, line.Length, default)).Texts();

        Assert.Equal(1, fake.CompleteCount);
        Assert.Equal(0, fake.QueryCount); // PS engine returned values → no introspection fallback
        Assert.Contains("fast", result);
        Assert.Contains("slow", result);
    }

    // ── Floating panel: PowerShell-cmdlet parameter hints (GetFlagHintsAsync) ──

    [Fact]
    public async Task FlagHints_PsCmdlet_ReturnsParamWithTypeAndValueSet()
    {
        // Worker returns "name|type|values" rows (the expression does the -like prefix filter).
        var fake = new FakeWorker
        {
            OnQuery = (_, _) => Task.FromResult("CommonTCPPort|String|HTTP,RDP,SMB,WINRM\n"),
        };
        const string line = "tnc -C";
        var hints = await Engine(fake).GetFlagHintsAsync(line, line.Length, default);

        var h = Assert.Single(hints);
        Assert.Equal("-CommonTCPPort <String>", h.Head);
        Assert.Equal("HTTP, RDP, SMB, WINRM", h.Desc); // value-set expanded + spaced
    }

    [Fact]
    public async Task FlagHints_PsParamWithoutValueSet_ShowsTypeOnly()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("Port|Int32|\n") };
        const string line = "tnc -P";
        var hints = await Engine(fake).GetFlagHintsAsync(line, line.Length, default);

        var h = Assert.Single(hints);
        Assert.Equal("-Port <Int32>", h.Head);
        Assert.Equal("", h.Desc);
    }

    [Fact]
    public async Task FlagHints_BashCommand_ReturnsEmpty_NoQuery()
    {
        // grep has bash flag specs → the sync panel handles it; no runspace query.
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("ShouldNotAppear|String|\n") };
        const string line = "grep -i";
        var hints = await Engine(fake).GetFlagHintsAsync(line, line.Length, default);

        Assert.Empty(hints);
        Assert.Equal(0, fake.QueryCount);
    }

    [Fact]
    public async Task FlagHints_NonFlagToken_ReturnsEmpty_NoQuery()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("X|String|\n") };
        const string line = "tnc foo";
        var hints = await Engine(fake).GetFlagHintsAsync(line, line.Length, default);

        Assert.Empty(hints);
        Assert.Equal(0, fake.QueryCount);
    }

    [Fact]
    public async Task FlagHints_CommandPosition_ReturnsEmpty()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("X|String|\n") };
        var hints = await Engine(fake).GetFlagHintsAsync("tn", cursor: 2, default);

        Assert.Empty(hints);
    }

    [Fact]
    public async Task FlagHints_NoWorker_ReturnsEmpty()
    {
        const string line = "tnc -C";
        var hints = await Engine(null).GetFlagHintsAsync(line, line.Length, default);
        Assert.Empty(hints);
    }

    [Fact]
    public async Task ParameterValue_FallsBackToIntrospection_WhenPsEngineEmpty()
    {
        // CompleteInput yields nothing → fall back to ValidateSet/enum introspection, filtered.
        var fake = new FakeWorker
        {
            OnComplete = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            OnQuery = (_, _) => Task.FromResult("fast\nslow\nfull\n"),
        };
        const string line = "Set-Thing -Mode f";
        var result = (await Engine(fake).CompleteAsync(line, line.Length, default)).Texts();

        Assert.Equal(1, fake.CompleteCount);
        Assert.Equal(1, fake.QueryCount);
        Assert.Contains("fast", result);
        Assert.DoesNotContain("slow", result); // does not match the typed "f"
    }
}
