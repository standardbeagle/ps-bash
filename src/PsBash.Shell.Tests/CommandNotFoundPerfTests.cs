using System.Diagnostics;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// An unknown command must cost about the same as a known one on a warm host. The
/// CommandNotFoundAction handler used to rebuild a `Get-Module -ListAvailable` index (~8 s on a
/// typical PSModulePath) in EVERY pooled runspace, so each miss paid it. The index is now cached
/// process-wide and on disk. The bound is RELATIVE to `echo` on the same host (machine-load safe).
/// </summary>
[Trait("Category", "Integration")]
public class CommandNotFoundPerfTests
{
    private static readonly string? PsBashExe = InteractiveShellHarness.FindPsBashBinary();

    private static long TimeMs(string endpoint, string script)
    {
        var psi = PsBashTestProcess.Create(new[] { "-c", script }, ipcEndpoint: endpoint);
        psi.Environment["PSBASH_HOST_IDLE_SECS"] = "20";
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new Xunit.Sdk.XunitException("command did not finish in 120s: " + script);
        }
        so.Wait(); se.Wait();
        return sw.ElapsedMilliseconds;
    }

    private static long Median(string endpoint, string script)
    {
        var t = Enumerable.Range(0, 3).Select(_ => TimeMs(endpoint, script)).OrderBy(x => x).ToArray();
        return t[1];
    }

    [SkippableFact]
    public void CommandNotFound_OnWarmHost_CostsAboutAsMuchAsEcho()
    {
        Skip.If(PsBashExe is null, "built ps-bash required");
        var ep = PsBashTestProcess.CreateEndpoint();
        TimeMs(ep, "echo warm");              // spawn + warm the host
        TimeMs(ep, "__no_such_cmd__; echo a"); // may build the on-disk index once (untimed)

        var echo = Median(ep, "echo a");
        var miss = Median(ep, "__no_such_cmd__; echo a");

        Assert.True(miss <= 3 * echo + 500,
            $"command-not-found median {miss} ms vs echo median {echo} ms (bound 3x + 500 ms)");
    }
}
