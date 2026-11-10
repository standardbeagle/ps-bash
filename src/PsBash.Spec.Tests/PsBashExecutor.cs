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

    private static readonly Dictionary<string, string> Env = Mode == "perinv"
        ? new() { ["PSBASH_PER_INVOCATION"] = "1" }
        : new()
        {
            ["PSBASH_PER_INVOCATION"] = "0",
            ["PSBASH_IPC_ENDPOINT"] = Endpoint,
            ["PSBASH_HOST_IDLE_SECS"] = "20",
            ["PSBASH_HOST_MAX_MB"] = "1536",
            ["PSBASH_EXEC_SPILL_MS"] = Environment.GetEnvironmentVariable("PSBASH_SPEC_SPILL_MS") ?? "250",
        };

    public async Task<ExecResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancel)
    {
        using var caseDir = new CaseDir(script);
        var runner = PsBashRunner.Create().WithTimeout(timeout).WithEnv(Env);
        // Oils cases run "the shell under test" as $SH. The oracle sets SH=bash, and so does this: cases guard bash-inapplicable
        // parts with `case $SH in bash) exit ;; esac`, which a launcher PATH would never match (ps-bash then ran OSH-only
        // tests bash itself skips). `bash` inside ps-bash is the running launcher, so `$SH -c` still exercises this build.
        string shell = SpecImporter.UsesShellVariable(script) ? "SH=bash; " : "";
        // Same line as the case's first line, so `$LINENO` in a case counts from 1 like it does in the in-host path.
        var r = await runner.RunScriptAsync($"cd '{caseDir.Path}'; " + shell + script);
        return new ExecResult(r.ExitCode, r.Stdout, r.Stderr, r.WallMs);
    }
}

/// <summary>Picks the executor for the run: <c>inhost</c> (default, shared slots), else a launcher per case.</summary>
internal static class Executors
{
    private static readonly Lazy<InHostExecutor> Shared = new(() => new InHostExecutor(Slots));

    public static int Slots
        => int.TryParse(Environment.GetEnvironmentVariable("PSBASH_SPEC_PAR"), out var n) && n > 0 ? n : 4;

    public static IScriptExecutor Get() => PsBashExecutor.Mode == "inhost" ? Shared.Value : new PsBashExecutor();
}
