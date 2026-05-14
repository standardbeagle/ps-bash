using PsBash.Testing;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Thin per-suite shim over the shared <see cref="PsBashRunner"/> /
/// <see cref="ProcessSpawn"/> helpers (REFACTOR-3).
///
/// The escalation/fault-injection suite hard-fails on a missing launcher
/// binary (build PsBash.Shell first) and uses a 30 s default timeout. Those
/// two suite-specific choices are the only thing left here — the actual
/// Process.Start + pipe-drain + timeout + kill-tree loop now lives once in
/// PsBash.Testing and a reliability fix lands O(1) across every suite.
///
/// RELIABILITY CONTRACT: inherited verbatim from <see cref="ProcessSpawn"/> —
/// every spawn uses a timeout + Kill(entireProcessTree: true) in finally so a
/// hung command never orphans the process tree.
/// </summary>
internal static class ProcessRunHelper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Hard-fail on a missing binary: the escalation suite treats an unbuilt
    // launcher as a setup error, not a skip.
    private static readonly string LauncherPath =
        PsBashLocator.ResolveRequired();

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] arguments,
        TimeSpan? timeout = null)
    {
        var result = await ProcessSpawn.RunAsync(
            LauncherPath, arguments, timeout ?? DefaultTimeout);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithStdinAsync(
        string stdinContent,
        string[] arguments,
        TimeSpan? timeout = null)
    {
        var result = await ProcessSpawn.RunAsync(
            LauncherPath, arguments, timeout ?? DefaultTimeout, stdinContent: stdinContent);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }
}
