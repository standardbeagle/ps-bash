using PsBash.Testing;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Thin per-suite shim over the shared <see cref="PsBashRunner"/> /
/// <see cref="ProcessSpawn"/> helpers (REFACTOR-3).
///
/// The escalation/fault-injection suite hard-fails on a missing launcher
/// binary (build PsBash.Shell first), uses a 30 s default timeout, and pins the
/// host lifetime (below). Those suite-specific choices are all that is left here — the actual
/// Process.Start + pipe-drain + timeout + kill-tree loop now lives once in
/// PsBash.Testing and a reliability fix lands O(1) across every suite.
///
/// RELIABILITY CONTRACT: inherited verbatim from <see cref="ProcessSpawn"/> —
/// every spawn uses a timeout + Kill(entireProcessTree: true) in finally so a
/// hung command never orphans the process tree.
///
/// HOST LIFETIME: spawns run on ONE shared warm daemon per suite by default
/// (the same decision the differential/oracle suites made for
/// 01M3GWY9AX7BY4MDB7YFVWP4BZ). A ps-bash cold start is ~3 s idle and exceeds a
/// test's spawn budget under full-suite load, which is how the added-criteria
/// cases (`MissingCommand_Exits127` 30 s SpawnTimeout,
/// `Scale_LargePipe_WcCount`/`Regression_LastExitcodeNotPollutedBetweenCommands`
/// wrong exit value) failed. Warming once removes that cost. A test that NEEDS
/// isolation (concurrent-daemon corruption, per-invocation host behavior) passes
/// its own <see cref="IsolatedDaemon.Env"/> or
/// <see cref="PerInvocationEnv"/> explicitly, which wins over the warm default.
/// </summary>
internal static class ProcessRunHelper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Cold-host env: a private host killed with its launcher.</summary>
    public static readonly IReadOnlyDictionary<string, string> PerInvocationEnv =
        new Dictionary<string, string> { ["PSBASH_PER_INVOCATION"] = "1" };

    // Hard-fail on a missing binary: the escalation suite treats an unbuilt
    // launcher as a setup error, not a skip.
    private static readonly string LauncherPath =
        PsBashLocator.ResolveRequired();

    // One shared warm daemon for the whole suite. Created lazily on first spawn
    // so a run that never spawns pays nothing; the host idles out at process
    // end (its idle window is short) so it does not lock the build's DLLs.
    private static readonly Lazy<IsolatedDaemon> _sharedDaemon = new(() => new IsolatedDaemon());

    /// <summary>Env for the suite's shared warm daemon (endpoint + no per-invocation).</summary>
    public static IReadOnlyDictionary<string, string> SharedDaemonEnv => _sharedDaemon.Value.Env;

    /// <summary>
    /// Builds the env for a default spawn: on the shared warm daemon, layering
    /// the caller's <paramref name="env"/> on top (caller wins) so an explicit
    /// per-invocation or isolated-daemon request overrides the warm default.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ResolveEnv(
        IReadOnlyDictionary<string, string>? env)
    {
        var merged = new Dictionary<string, string>(SharedDaemonEnv);
        if (env is not null)
            foreach (var (k, v) in env) merged[k] = v;
        return merged;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var result = await ProcessSpawn.RunAsync(
            LauncherPath, arguments, timeout ?? DefaultTimeout, env: ResolveEnv(env));
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithStdinAsync(
        string stdinContent,
        string[] arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var result = await ProcessSpawn.RunAsync(
            LauncherPath, arguments, timeout ?? DefaultTimeout,
            stdinContent: stdinContent, env: ResolveEnv(env));
        return (result.ExitCode, result.Stdout, result.Stderr);
    }
}

/// <summary>
/// A shared daemon host on an endpoint private to one test, killed on dispose, so a
/// test that exercises daemon behavior never leaves a host running after the suite.
/// </summary>
internal sealed class IsolatedDaemon : IAsyncDisposable
{
    private int _pid;

    public IReadOnlyDictionary<string, string> Env { get; }

    public IsolatedDaemon()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        // Short on purpose: a unix socket path is capped at 108 bytes.
        var spec = OperatingSystem.IsWindows()
            ? $"pipe:psbash-esc-{id}"
            : $"unix:{Path.Combine(Path.GetTempPath(), "ps-bash", $"esc-{id}.sock")}";
        Env = new Dictionary<string, string>
        {
            ["PSBASH_IPC_ENDPOINT"] = spec,
            // Idle out shortly after the suite rather than the 600 s default, so
            // the warm daemon cannot outlive the run and lock src/PsBash.Shell/bin
            // DLLs against the next build (the original reason per-invocation was
            // forced). Same value as the differential fixture's warm host.
            ["PSBASH_HOST_IDLE_SECS"] = "20",
        };
    }

    /// <summary>Starts the daemon and records its PID (<c>$$</c> is the host's PID).</summary>
    public async Task WarmAsync()
    {
        var (exit, stdout, stderr) = await ProcessRunHelper.RunAsync(new[] { "-c", "echo $$" }, env: Env);
        if (exit != 0 || !int.TryParse(stdout.Trim(), out _pid))
            throw new InvalidOperationException(
                $"isolated daemon did not start: exit={exit} stdout='{stdout}' stderr='{stderr}'");
    }

    public ValueTask DisposeAsync()
    {
        if (_pid != 0)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(_pid); p.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
        return ValueTask.CompletedTask;
    }
}
