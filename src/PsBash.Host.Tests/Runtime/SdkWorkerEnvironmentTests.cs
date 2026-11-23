using PsBash.Host.Runtime;
using PsBash.Testing;
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
/// <para>A command given a block REPLACES this process's environment, and the scripts set <c>$env:</c> names they
/// build: the class runs alone (which also keeps it off the SdkHost lane) and puts the whole environment back.</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class SdkWorkerEnvironmentTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();
    private readonly ProcessEnv _env = ProcessEnv.PreserveAll();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        _env.Dispose();
    }

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

    // REGRESSION: $SECONDS counted from the warm runspace's load, not the shell's start: a host's runspace is warmed
    // long before a command arrives, so `[ $SECONDS -ge 10 ]` was true ~2 s into a script. Each launcher command is a
    // new shell; $SECONDS starts at 0 there.
    [Fact]
    public async Task Seconds_CountsFromTheShellsStart_NotTheRunspacesWarmUp()
    {
        var worker = _fixture.CreateWorker();
        await RunAsync(worker, "$null", Block([]));   // warm, as a host's pool is
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var (exit, lines) = await RunAsync(worker, PsBash.Core.Transpiler.BashTranspiler.Transpile("echo $SECONDS")!, Block([]));

        Assert.Equal(0, exit);
        Assert.Equal("0", string.Concat(lines).Trim());
    }

    // `SECONDS=n` restarts the count from n, and assigning the same value again restarts it again.
    [Fact]
    public async Task Seconds_AssignmentRestartsTheCount()
    {
        var worker = _fixture.CreateWorker();
        string script = "SECONDS=0; sleep 1.2; SECONDS=0; echo \"again=$SECONDS\"; SECONDS=10; sleep 1.2; echo \"ten=$SECONDS\"";

        var (exit, lines) = await RunAsync(worker, PsBash.Core.Transpiler.BashTranspiler.Transpile(script)!, Block([]));

        Assert.Equal(0, exit);
        string text = string.Concat(lines);
        Assert.Contains("again=0", text);
        Assert.Matches("ten=1[12]", text);   // 1.2 s after SECONDS=10 (12 only on a stall past 0.8 s)
    }

    // Executor slots replaced the "inside a host" marker: a ps-bash a command spawns is told the host is
    // busy and moves to another slot, so nothing marks the nesting in the environment.
    [Fact]
    public async Task LauncherFramedCommand_DoesNotSetTheRetiredNestingMarkers()
    {
        var worker = _fixture.CreateWorker();

        var run = await RunAsync(worker, "Invoke-BashEcho \"[$env:PSBASH_INSIDE_HOST][$env:PSBASH_NEST_DEPTH]\"",
            Block(Array.Empty<KeyValuePair<string, string>>()));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains(run.Lines, l => l.Trim() == "[][]");
    }

    // bash `$HOME` / `~` emit PowerShell `$HOME` and the provider `~`, which a runspace captures
    // when it is CREATED — from the daemon's environment. Pre-fix a launcher with its own HOME
    // read the daemon's (`cd ~/x` went to the wrong tree), and switching back must work too.
    [Fact]
    public async Task Environment_LauncherHome_DrivesHomeVariableAndTilde()
    {
        var worker = _fixture.CreateWorker();
        var homeVar = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        var daemonHome = Environment.GetEnvironmentVariable(homeVar)!;
        var launcherHome = Directory.CreateTempSubdirectory("psbash-home-").FullName;
        const string probe = "Invoke-BashEcho \"var=$HOME\"; Invoke-BashEcho \"tilde=$((Resolve-Path ~).ProviderPath)\"";
        try
        {
            var asLauncher = await RunAsync(worker, probe,
                Block(new[] { new KeyValuePair<string, string>(homeVar, launcherHome) }));
            var asDaemon = await RunAsync(worker, probe,
                Block(new[] { new KeyValuePair<string, string>(homeVar, daemonHome) }));

            // Output is coalesced into multi-line frames: compare whole lines.
            static string[] Lines(List<string> frames)
                => string.Concat(frames).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // The runspace $HOME holds the script-visible shell form C:/x (docs/specs/paths.md); the provider's
            // `~` stays native.
            static string Shell(string p) => OperatingSystem.IsWindows() ? p.Replace('\\', '/') : p;
            Assert.Equal(0, asLauncher.ExitCode);
            Assert.Equal(new[] { $"var={Shell(launcherHome)}", $"tilde={launcherHome}" }, Lines(asLauncher.Lines));
            Assert.Equal(new[] { $"var={Shell(daemonHome)}", $"tilde={daemonHome}" }, Lines(asDaemon.Lines));
        }
        finally
        {
            Directory.Delete(launcherHome, recursive: true);
        }
    }

    // WSL with Windows dirs on PATH: each command-not-found pays PowerShell's wildcard listing of
    // every /mnt dir (~9 s for System32 alone). The first miss in a host says why and how to fix it;
    // later misses and non-WSL hosts keep the plain bash message.
    [SkippableFact]
    public async Task CommandNotFound_WslWindowsPath_HintsOncePerHost()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "WSL hint is Linux-only");
        const string flag = "PsBash.WslPathHintShown";
        AppContext.SetData(flag, null); // the hint is once per process; other tests may have tripped it
        var worker = _fixture.CreateWorker();
        var path = "/mnt/z/psbash-no-such-dir:" + Environment.GetEnvironmentVariable("PATH");
        var miss = "pbnope" + Guid.NewGuid().ToString("N")[..8];

        async Task<string> MissAsync(params KeyValuePair<string, string>[] set)
        {
            var err = new List<string>();
            var exit = await worker.ExecuteWithOutputAsync(miss, _ => { }, err.Add, default,
                Block(set.Append(new("PATH", path)), "WSL_DISTRO_NAME"));
            Assert.Equal(127, exit);
            return string.Concat(err);
        }

        try
        {
            var notWsl = await MissAsync();
            var first = await MissAsync(new KeyValuePair<string, string>("WSL_DISTRO_NAME", "test"));
            var second = await MissAsync(new KeyValuePair<string, string>("WSL_DISTRO_NAME", "test"));

            Assert.DoesNotContain("appendWindowsPath", notWsl);
            Assert.Contains($"{miss}: command not found", first);
            Assert.Matches(@"PATH has [1-9]\d* Windows directories under /mnt", first);
            Assert.Contains("appendWindowsPath=false", first);
            Assert.Contains($"{miss}: command not found", second);
            Assert.DoesNotContain("appendWindowsPath", second);
        }
        finally
        {
            AppContext.SetData(flag, null);
        }
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
        // Realistic launcher block (essential vars such as TEMP survive) with
        // BOTH casings of the probe removed, then ONLY the lower-cased entry
        // added — the reset must not keep the daemon's pre-existing upper-cased
        // instance. A launcher that has renamed its casing sends its whole
        // block, just with the new casing.
        var onlyLower = Block(
            new[] { new KeyValuePair<string, string>(lower, "fromlauncher") },
            upper, lower);
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
