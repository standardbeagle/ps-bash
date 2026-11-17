using PsBash.Spec;
using PsBash.Testing;

namespace PsBash.Spec.Tests;

/// <summary>
/// One real ps-bash launcher process per case (<c>ps-bash -c script</c>). <c>PSBASH_SPEC_MODE</c> picks how the host
/// behind the launcher is provided: <c>perinv</c> (a fresh host per case, the Phase 1 behaviour), or <c>warm</c> (default:
/// one daemon on a private endpoint shared by every case of the run, so host start-up is paid once).
/// </summary>
internal sealed class PsBashExecutor : IScriptExecutor
{
    public static string Mode { get; } = Environment.GetEnvironmentVariable("PSBASH_SPEC_MODE") is { Length: > 0 } m ? m : "inhost";

    // One endpoint per test process; the daemon idles out shortly after the run.
    private static readonly string Endpoint =
        "unix:" + Path.Combine(Path.GetTempPath(), "ps-bash", "spec-" + Guid.NewGuid().ToString("N") + ".sock");

    /// <summary>How long the shared daemon (and its spill slots) stay up with nothing to run.</summary>
    internal const int HostIdleSecs = 20;

    private static readonly Dictionary<string, string> Env = Mode == "perinv"
        ? new() { ["PSBASH_PER_INVOCATION"] = "1" }
        : new()
        {
            ["PSBASH_PER_INVOCATION"] = "0",
            ["PSBASH_IPC_ENDPOINT"] = Endpoint,
            ["PSBASH_HOST_IDLE_SECS"] = HostIdleSecs.ToString(),
            ["PSBASH_HOST_MAX_MB"] = "1536",
            ["PSBASH_EXEC_SPILL_MS"] = Environment.GetEnvironmentVariable("PSBASH_SPEC_SPILL_MS") ?? "250",
        };

    /// <summary>
    /// The launcher process for one case. It STARTS in the case directory (and has it as OLDPWD, like the in-host path):
    /// a case's files land there whatever its script does first, never in the directory the test run started in. A
    /// `cd` written in front of the script only held once that command had run.
    /// </summary>
    internal static System.Diagnostics.ProcessStartInfo StartInfo(CaseDir caseDir, string scriptFile)
    {
        var psi = ProcessSpawn.BuildPsi(PsBashLocator.ResolveRequired(BuildConfig.Auto), scriptFile);
        psi.WorkingDirectory = caseDir.Path;
        psi.Environment["OLDPWD"] = caseDir.Path;
        psi.Environment.Remove("PWD"); // the runner's own; the shell derives it from the directory it starts in
        return psi;
    }

    public async Task<ExecResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancel)
    {
        using var caseDir = new CaseDir(script);
        // Oils cases run "the shell under test" as $SH. The oracle sets SH=bash, and so does this: cases guard bash-inapplicable
        // parts with `case $SH in bash) exit ;; esac`, which a launcher PATH would never match (ps-bash then ran OSH-only
        // tests bash itself skips). `bash` inside ps-bash is the running launcher, so `$SH -c` still exercises this build.
        string shell = SpecImporter.UsesShellVariable(script) ? "SH=bash; " : "";
        // Same line as the case's first line, so `$LINENO` in a case counts from 1 like it does in the in-host path.
        // Run as a script FILE, like the oracle (`bash case.sh`) and the in-host path: a `-c` command ends with a
        // different status for some errors (`${x?}` is 127 under -c, 1 in a file), so `-c` judged those cases wrongly.
        string file = Path.Combine(LaunchDir, Guid.NewGuid().ToString("N") + ".sh");
        Directory.CreateDirectory(LaunchDir);
        await File.WriteAllTextAsync(file, shell + script, cancel);
        await WarmHostsAsync(cancel);
        Interlocked.Increment(ref _running);
        try
        {
            var r = await ProcessSpawn.RunAsync(StartInfo(caseDir, file), timeout, stdinContent: null, env: Env);
            return new ExecResult(r.ExitCode, r.Stdout, r.Stderr, r.WallMs);
        }
        finally
        {
            Volatile.Write(ref _lastEndTicks, DateTime.UtcNow.Ticks);
            Interlocked.Decrement(ref _running);
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly string LaunchDir = Path.Combine(Path.GetTempPath(), "ps-bash", "spec-launch");
    private static readonly SemaphoreSlim WarmGate = new(1, 1);
    private static int _running;
    private static long _lastEndTicks; // 0 = no case has run yet: the hosts were never started
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// A case's time limit ends a hang; it must never include starting the shared hosts. The first launcher case of a run
    /// cold-starts the daemon, and its first nested <c>$SH</c> cold-starts a spill slot too (slot 0 is busy running the
    /// parent): on a loaded machine that took 12 s on its own, so sh-usage "sh +c is accepted" (the file's first case
    /// with a nested shell, limit 14 s) timed out in most full runs. The daemon also idles out after
    /// <see cref="HostIdleSecs"/>, so a launcher case after a stretch of in-host cases paid it again. Before such a case,
    /// <c>ps-bash -c 'bash -c :'</c> starts the daemon and its first spill slot, outside any case's clock.
    /// </summary>
    private static async Task WarmHostsAsync(CancellationToken cancel)
    {
        if (Mode == "perinv") return; // a private host per case is the mode's point: its start is part of every case
        if (!HostsMayBeCold(Volatile.Read(ref _running), Volatile.Read(ref _lastEndTicks), DateTime.UtcNow.Ticks)) return;
        await WarmGate.WaitAsync(cancel);
        try
        {
            if (!HostsMayBeCold(Volatile.Read(ref _running), Volatile.Read(ref _lastEndTicks), DateTime.UtcNow.Ticks)) return;
            var psi = ProcessSpawn.BuildPsi(PsBashLocator.ResolveRequired(BuildConfig.Auto), "-c", "bash -c :");
            psi.WorkingDirectory = LaunchDir;
            // A warm-up that fails leaves the case to report what is wrong; it is not itself a result.
            try { await ProcessSpawn.RunAsync(psi, WarmupTimeout, stdinContent: null, env: Env); }
            catch (Exception) when (!cancel.IsCancellationRequested) { }
            Volatile.Write(ref _lastEndTicks, DateTime.UtcNow.Ticks);
        }
        finally { WarmGate.Release(); }
    }

    /// <summary>
    /// Whether the shared hosts may be down: none started yet, or nothing running and the last case ended more than half
    /// the idle timeout ago (half, so a host is never caught mid-shutdown).
    /// </summary>
    internal static bool HostsMayBeCold(int running, long lastEndTicks, long nowTicks)
        => lastEndTicks == 0
           || running == 0 && nowTicks - lastEndTicks > TimeSpan.FromSeconds(HostIdleSecs / 2.0).Ticks;
}

/// <summary>Picks the executor for the run: <c>inhost</c> (default, shared slots), else a launcher per case.</summary>
internal static class Executors
{
    private static readonly Lazy<InHostExecutor> Shared = new(() => new InHostExecutor(Slots));

    public static int Slots
        => int.TryParse(Environment.GetEnvironmentVariable("PSBASH_SPEC_PAR"), out var n) && n > 0 ? n : 4;

    public static IScriptExecutor Get() => PsBashExecutor.Mode == "inhost" ? Shared.Value : new PsBashExecutor();

    /// <summary>The shared in-host executor, whatever the run's mode (the isolation self-test drives both paths).</summary>
    public static IScriptExecutor InHost => Shared.Value;
}
