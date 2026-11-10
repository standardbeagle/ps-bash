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
        // Run as a script FILE, like the oracle (`bash case.sh`) and the in-host path: a `-c` command ends with a
        // different status for some errors (`${x?}` is 127 under -c, 1 in a file), so `-c` judged those cases wrongly.
        string file = Path.Combine(Path.GetTempPath(), "ps-bash", "spec-launch", Guid.NewGuid().ToString("N") + ".sh");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, $"cd '{caseDir.Path}'; " + shell + script, cancel);
        try
        {
            var r = await runner.RunArgsAsync([file]);
            return new ExecResult(r.ExitCode, r.Stdout, r.Stderr, r.WallMs);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
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
