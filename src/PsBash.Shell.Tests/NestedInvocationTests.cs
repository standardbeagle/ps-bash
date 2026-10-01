using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// A ps-bash started BY a command that a ps-bash host is executing (nested <c>bash -c</c>, awk
/// <c>"cmd" | getline</c>) must never reuse the host that is running its parent: that host holds
/// its process-wide exec gate for the parent's command, so the child would queue behind the very
/// command that is waiting for it (deadlock / reset connection). Covers an explicit
/// <c>PSBASH_IPC_ENDPOINT</c> and the default per-session endpoint, one and two levels deep.
/// </summary>
[Trait("Category", "Integration")]
public class NestedInvocationTests
{
    private static readonly string? PsBashExe = InteractiveShellHarness.FindPsBashBinary();

    private static async Task<(int Exit, string Out, string Err)> RunAsync(
        ProcessStartInfo psi, TimeSpan timeout)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new Xunit.Sdk.XunitException(
                $"nested invocation did not finish within {timeout.TotalSeconds}s (deadlock). stdout={await so} stderr={await se}");
        }
        return (p.ExitCode, await so, await se);
    }

    private static ProcessStartInfo ExplicitEndpoint(string script)
    {
        var psi = PsBashTestProcess.Create(new[] { "-c", script }, ipcEndpoint: PsBashTestProcess.CreateEndpoint());
        psi.Environment["PSBASH_HOST_IDLE_SECS"] = "3";
        return psi;
    }

    [SkippableFact]
    public async Task NestedBashC_ExplicitEndpoint_OneLevel_Completes()
    {
        Skip.If(PsBashExe is null, "built ps-bash required");
        var r = await RunAsync(ExplicitEndpoint("bash -c 'echo hi'"), TimeSpan.FromSeconds(90));
        Assert.Equal("hi", r.Out.Trim());
        Assert.Equal(0, r.Exit);
    }

    [SkippableFact]
    public async Task NestedBashC_ExplicitEndpoint_TwoLevels_Completes()
    {
        Skip.If(PsBashExe is null, "built ps-bash required");
        var r = await RunAsync(ExplicitEndpoint("bash -c \"bash -c 'echo deep'\""), TimeSpan.FromSeconds(120));
        Assert.Equal("deep", r.Out.Trim());
        Assert.Equal(0, r.Exit);
    }

    [SkippableFact]
    public async Task NestedBashC_RepeatedCalls_ReuseOneWarmNestedHost()
    {
        Skip.If(PsBashExe is null, "built ps-bash required");
        // $$ is the PID of the host process running the command. Both nested calls must land on
        // the SAME nested daemon (not a fresh private host each), distinct from the outer host;
        // a doubly nested call lands on a third (depth 2) host.
        var r = await RunAsync(
            ExplicitEndpoint(
                "a=$(bash -c 'echo $$'); b=$(bash -c 'echo $$'); " +
                "c=$(bash -c \"bash -c 'echo \\$\\$'\"); echo \"$a $b $c $$\""),
            TimeSpan.FromSeconds(120));
        Assert.Equal(0, r.Exit);
        var pids = r.Out.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, pids.Length);
        Assert.Equal(pids[0], pids[1]);                 // reused nested host
        Assert.NotEqual(pids[0], pids[3]);              // not the outer host
        Assert.NotEqual(pids[2], pids[0]);              // depth 2 is its own host
        Assert.NotEqual(pids[2], pids[3]);
    }

    [SkippableFact]
    public async Task AwkGetlineFromCommand_ExplicitEndpoint_Completes()
    {
        Skip.If(PsBashExe is null, "built ps-bash required");
        var r = await RunAsync(
            ExplicitEndpoint("awk 'BEGIN { \"echo hi\" | getline x; print x }'"), TimeSpan.FromSeconds(90));
        Assert.Equal("hi", r.Out.Trim());
        Assert.Equal(0, r.Exit);
    }

    [SkippableFact]
    public async Task NestedBashC_DefaultSessionEndpoint_TwoLevels_Completes()
    {
        Skip.If(PsBashExe is null, "built ps-bash required");
        // Default (per-session) endpoint in an isolated runtime dir; no explicit endpoint.
        var tempRoot = Path.Combine(Path.GetTempPath(), "psb-nest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            var psi = PsBashTestProcess.Create(
                new[] { "-c", "bash -c \"bash -c 'echo deep'\"" }, isolatedIpc: false);
            psi.Environment["TEMP"] = tempRoot;
            psi.Environment["TMP"] = tempRoot;
            psi.Environment["TMPDIR"] = tempRoot;
            psi.Environment[IpcTransportFactory.EndpointEnvVar] = null;
            psi.Environment["PSBASH_SESSION"] = null;
            psi.Environment["PSBASH_PER_INVOCATION"] = "0";
            psi.Environment["PSBASH_HOST_IDLE_SECS"] = "3";
                var r = await RunAsync(psi, TimeSpan.FromSeconds(120));
            Assert.Equal("deep", r.Out.Trim());
            Assert.Equal(0, r.Exit);
        }
        finally
        {
            KillHostsUnder(Path.Combine(tempRoot, "ps-bash"));
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void KillHostsUnder(string runtimeDir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(runtimeDir, "*.host.json"))
            {
                var pid = HostMetadata.TryRead("unix", f[..^".host.json".Length])?.Pid;
                if (pid is > 0)
                {
                    try { using var h = Process.GetProcessById(pid.Value); h.Kill(entireProcessTree: true); }
                    catch { }
                }
            }
        }
        catch { }
    }
}
