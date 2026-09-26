using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// R06 (01M3F77T4SM4M9T6B9DF8CCE0K): <see cref="SdkWorker"/> must reset its
/// process environment to the launcher's forwarded block before each command,
/// so a shared daemon cannot leak env state between invocations. The
/// environment is process-global and the pool shares it; the reset is only safe
/// because execution is serialized by the worker's process-wide gate.
///
/// Oracle note (qa-rubric Directive 1): ps-bash-specific host behavior (no bash
/// oracle in-process); hand-written asserts are justified per the exception list.
/// </summary>
[Collection("SdkHost")]
public class SdkWorkerEnvironmentTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Build a realistic launcher environment block: the current process
    /// environment (so essential vars such as TEMP/PSModulePath survive — a real
    /// launcher always forwards its whole block) with <paramref name="set"/>
    /// applied and <paramref name="remove"/> deleted. This mirrors the wire
    /// semantics without poisoning later tests by wiping TEMP.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> Block(
        IEnumerable<KeyValuePair<string, string>> set,
        params string[] remove)
    {
        var removed = new HashSet<string>(remove, StringComparer.Ordinal);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && !removed.Contains(k))
                map[k] = e.Value as string ?? string.Empty;
        }
        foreach (var entry in set)
            map[entry.Key] = entry.Value;
        return map.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    }

    private static async Task<(int ExitCode, List<string> Lines)> RunAsync(
        SdkWorker worker,
        string command,
        IReadOnlyList<KeyValuePair<string, string>>? environment)
    {
        var lines = new List<string>();
        var exit = await worker.ExecuteWithOutputAsync(command, lines.Add, null, default, environment);
        return (exit, lines);
    }

    // A var CHANGED between two invocations must be observed by the second.
    // Pre-fix the worker reuses whatever the process environment held from the
    // previous run (or from host spawn), so the second prints the first value.
    [Fact]
    public async Task Environment_ChangedBetweenInvocations_SecondRunSeesNewValue()
    {
        var worker = _fixture.CreateWorker();

        var first = await RunAsync(worker, "Invoke-BashEcho $env:REVIEW_X",
            Block(new[] { new KeyValuePair<string, string>("REVIEW_X", "first") }));
        var second = await RunAsync(worker, "Invoke-BashEcho $env:REVIEW_X",
            Block(new[] { new KeyValuePair<string, string>("REVIEW_X", "second") }));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains(first.Lines, l => l.Contains("first"));
        Assert.Contains(second.Lines, l => l.Contains("second"));
    }

    // A var UNSET in the launcher block must read as unset: the reset replaces
    // the environment, it does not merge on top of the daemon's current one.
    [Fact]
    public async Task Environment_RemovedBetweenInvocations_SecondRunSeesUnset()
    {
        var worker = _fixture.CreateWorker();

        var withVar = await RunAsync(worker, "Invoke-BashEcho \"V=[$env:REMOVED_ENV_PROBE]\"",
            Block(new[] { new KeyValuePair<string, string>("REMOVED_ENV_PROBE", "present") }));
        var withoutVar = await RunAsync(worker, "Invoke-BashEcho \"V=[$env:REMOVED_ENV_PROBE]\"",
            Block(Array.Empty<KeyValuePair<string, string>>(), "REMOVED_ENV_PROBE"));

        Assert.Equal(0, withVar.ExitCode);
        Assert.Equal(0, withoutVar.ExitCode);
        Assert.Contains(withVar.Lines, l => l.Contains("V=[present]"));
        Assert.Contains(withoutVar.Lines, l => l.Contains("V=[]"));
    }

    // `export` (a bash var written to $env:) in one invocation must not survive
    // into the next: the next launcher's block simply does not contain it, so the
    // reset removes it.
    [Fact]
    public async Task Environment_ExportInOneInvocation_NotVisibleToNext()
    {
        var worker = _fixture.CreateWorker();
        var name = "PSBASH_LEAK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var clean = Block(Array.Empty<KeyValuePair<string, string>>());

        var exported = await RunAsync(worker, $"export {name}=leak; Invoke-BashEcho exported", clean);
        var read = await RunAsync(worker, $"Invoke-BashEcho \"leaked=[$env:{name}]\"", clean);

        Assert.Equal(0, exported.ExitCode);
        Assert.Equal(0, read.ExitCode);
        Assert.Contains(exported.Lines, l => l.Contains("exported"));
        Assert.Contains(read.Lines, l => l.Contains("leaked=[]"));
    }

    // On Windows variable names are case-insensitive. A launcher block whose
    // entry differs from the daemon's existing instance only in casing must be
    // retained (set to the launcher's casing/value), NOT set-then-deleted by the
    // keep-set comparison. Regression: an Ordinal keep-set treated the two
    // casings as distinct, so the just-set entry was immediately removed.
    [Fact]
    public async Task Environment_DivergentCasingFromDaemon_RetainedOnWindows()
    {
        var worker = _fixture.CreateWorker();
        var upper = "PSBASH_CASING_PROBE";
        var lower = upper.ToLowerInvariant();

        var seeded = await RunAsync(worker, $"$env:{upper} = 'seed'; Invoke-BashEcho seeded", null);
        // Minimal block: ONLY the lower-cased entry, so the reset must not keep the
        // daemon's pre-existing upper-cased instance. A launcher that has renamed
        // its casing sends its whole block, just this one entry here.
        var onlyLower = new List<KeyValuePair<string, string>>
        {
            new(lower, "fromlauncher"),
        };
        var reset = await RunAsync(worker, $"Invoke-BashEcho \"seen=[$env:{upper}]\"", onlyLower);

        Assert.Equal(0, seeded.ExitCode);
        Assert.Equal(0, reset.ExitCode);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(reset.Lines, l => l.Contains("seen=[fromlauncher]"));
        }

        // Restore so later tests in the shared collection are not polluted.
        await RunAsync(worker, $"Remove-Item Env:{upper} -ErrorAction SilentlyContinue", null);
    }

    // The legacy null block must NOT wipe the environment: in-process
    // interactive callers (InteractiveShell) pass null and rely on the host
    // process environment (their own forwarded config) staying intact.
    [Fact]
    public async Task Environment_NullBlock_LeavesEnvironmentUntouched()
    {
        var worker = _fixture.CreateWorker();
        var name = "PSBASH_NULLKEEP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var set = await RunAsync(worker, $"$env:{name} = 'kept'; Invoke-BashEcho set", null);
        var read = await RunAsync(worker, $"Invoke-BashEcho \"kept=[$env:{name}]\"", null);

        Assert.Equal(0, set.ExitCode);
        Assert.Equal(0, read.ExitCode);
        Assert.Contains(read.Lines, l => l.Contains("kept=[kept]"));
    }}
