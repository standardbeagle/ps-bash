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
/// HOST LIFETIME: every launch runs per-invocation (a private host killed with its
/// launcher), as Differential does via CanonicalEnv. The launcher default is a
/// shared daemon that idles for 600 s, so each test run left a dev-build host alive
/// that locked the bin DLLs for the next build, and a cold shared daemon under
/// full-suite load blew a test's spawn budget. A test that NEEDS a shared daemon
/// passes <see cref="IsolatedDaemon.Env"/>, which it owns and kills.
/// </summary>
internal static class ProcessRunHelper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<string, string> PerInvocationEnv =
        new Dictionary<string, string> { ["PSBASH_PER_INVOCATION"] = "1" };

    // Hard-fail on a missing binary: the escalation suite treats an unbuilt
    // launcher as a setup error, not a skip.
    private static readonly string LauncherPath =
        PsBashLocator.ResolveRequired();

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var result = await ProcessSpawn.RunAsync(
            LauncherPath, arguments, timeout ?? DefaultTimeout, env: env ?? PerInvocationEnv);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithStdinAsync(
        string stdinContent,
        string[] arguments,
        TimeSpan? timeout = null)
    {
        var result = await ProcessSpawn.RunAsync(
            LauncherPath, arguments, timeout ?? DefaultTimeout,
            stdinContent: stdinContent, env: PerInvocationEnv);
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
        Env = new Dictionary<string, string> { ["PSBASH_IPC_ENDPOINT"] = spec };
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
