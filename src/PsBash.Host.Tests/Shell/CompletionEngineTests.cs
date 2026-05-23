using PsBash.Core.Runtime;
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
    private sealed class FakeWorker : IWorker
    {
        public Func<string, CancellationToken, Task<string>>? OnQuery;
        public int QueryCount;

        public Action<string>? OutputCallback { get; set; }
        public bool HasExited { get; set; }

        public Task<int> ExecuteAsync(string command, CancellationToken ct = default) => Task.FromResult(0);

        public Task<string> QueryAsync(string expression, CancellationToken ct = default)
        {
            QueryCount++;
            return OnQuery is null ? Task.FromResult(string.Empty) : OnQuery(expression, ct);
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
        var result = await Engine(fake).CompleteAsync("Get-F", cursor: 5, default);

        Assert.Equal(1, fake.QueryCount);
        Assert.Contains("Get-Foo", result);          // live command, matches the typed prefix
        Assert.DoesNotContain("Get-Bar", result);    // re-filtered out (does not start with "Get-F")
    }

    [Fact]
    public async Task WorkerThrows_FallsBackToStaticBaseSet()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => throw new InvalidOperationException("busy") };
        var result = await Engine(fake).CompleteAsync("l", cursor: 1, default);

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

        var result = await Engine(fake).CompleteAsync("l", cursor: 1, cts.Token);

        Assert.Contains("ls", result);
    }

    [Fact]
    public async Task NotCommandPosition_DoesNotQueryRunspace()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("ShouldNotAppear\n") };
        // Cursor is on an argument (path), not the command word.
        var result = await Engine(fake).CompleteAsync("ls /tmp/x", cursor: 9, default);

        Assert.Equal(0, fake.QueryCount);
        Assert.DoesNotContain("ShouldNotAppear", result);
    }

    [Fact]
    public async Task EmptyToken_DoesNotQueryRunspace()
    {
        // An empty command token would make Get-Command -Name '*' enumerate the whole session.
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("Everything\n") };
        var result = await Engine(fake).CompleteAsync(string.Empty, cursor: 0, default);

        Assert.Equal(0, fake.QueryCount);
        Assert.DoesNotContain("Everything", result);
    }

    [Fact]
    public async Task ExitedWorker_SkipsLiveQuery()
    {
        var fake = new FakeWorker { HasExited = true, OnQuery = (_, _) => Task.FromResult("Get-Foo\n") };
        var result = await Engine(fake).CompleteAsync("Get-F", cursor: 5, default);

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
        Assert.Contains("-Path", result);
        Assert.Contains("-PathType", result);
        Assert.DoesNotContain("-Force", result); // does not match the typed "-Pa" prefix
    }

    [Fact]
    public async Task ParameterValue_AfterFlag_CompletesValidateSetOrEnumValues()
    {
        var fake = new FakeWorker { OnQuery = (_, _) => Task.FromResult("quick\nquiet\nfull\n") };
        const string line = "Set-Thing -Mode q";
        var result = await Engine(fake).CompleteAsync(line, line.Length, default);

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
        var result = await Engine(fake).CompleteAsync(line, line.Length, default);

        Assert.Equal(0, fake.QueryCount);
        Assert.DoesNotContain("-ShouldNotAppear", result);
    }
}
