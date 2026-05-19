using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using PsBash.Shell;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Windows-only process lifecycle tests for the reliability watchdog:
///  - ps-bash.exe must exit when its launching parent dies (Job Object + poller).
///  - ps-bash.exe -c must exit immediately when stdin is a closed pipe and no
///    command was passed (EOF fast-path), rather than hanging.
/// </summary>
[Trait("Category", "Integration")]
public class ProcessLifecycleTests
{
    private static readonly string PsBashExe = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PsBash.Shell", "bin", "Debug", "net10.0", "ps-bash.exe"));

    private static bool CanRun => OperatingSystem.IsWindows() && File.Exists(PsBashExe);

    [SkippableFact]
    public async Task ClosedStdinPipeWithEmptyCommand_ExitsImmediately()
    {
        Skip.IfNot(CanRun, "Windows + built ps-bash.exe required");

        // Launch ps-bash with stdin redirected then close it immediately.
        // With no -c arg and closed stdin, the process must exit 0 without hanging.
        var psi = new ProcessStartInfo
        {
            FileName = PsBashExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        try
        {
            // Close the stdin pipe immediately — simulates a dead parent whose
            // handle was inherited but never written to.
            process.StandardInput.Close();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(cts.Token);

            Assert.True(process.HasExited, "ps-bash did not exit within 15s on closed-stdin EOF");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task ShortCommand_ExitsPromptly_WithNoLingeringWorker()
    {
        Skip.IfNot(CanRun, "Windows + built ps-bash.exe required");

        // Acceptance: `ps-bash -c "exit 0"` must exit within a couple seconds
        // and leave no orphaned host process whose command-line references our
        // ps-bash PID. The Job Object ensures the host dies with us.
        var psi = new ProcessStartInfo
        {
            FileName = PsBashExe,
            Arguments = "-c \"exit 0\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment[IpcTransportFactory.EndpointEnvVar] = PsBashTestProcess.CreateEndpoint();
        psi.Environment["PSBASH_HOST_IDLE_SECS"] = "1";
        using var process = Process.Start(psi)!;
        int ourPid = process.Id;
        try
        {
            // 10s cap: cold-start includes pwsh JIT, module load from disk, and
            // the runspace parent-watcher setup (~3s warm, up to ~8s cold). The
            // test's purpose is "no orphan workers leak," not a perf gate.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(cts.Token);
            Assert.True(process.HasExited, "ps-bash -c 'exit 0' did not exit within 20s");
            Assert.Equal(0, process.ExitCode);

            // Poll until no host process remains whose parent was our
            // (now-dead) ps-bash. Replaces a 500ms Task.Delay — usually
            // completes in <50 ms once the launcher exits.
            var reapDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (true)
            {
                var orphan = false;
                foreach (var p in Process.GetProcessesByName("ps-bash-host"))
                {
                    try
                    {
                        var pp = JobObjectWatchdog.GetParentProcessId(p.Handle);
                        if (pp == ourPid) orphan = true;
                    }
                    catch { /* process may have exited during enumeration */ }
                    finally { p.Dispose(); }
                }
                if (!orphan || DateTime.UtcNow >= reapDeadline) break;
                await Task.Delay(50);
            }

            // Final assertion: no orphan whose parent is ours.
            foreach (var p in Process.GetProcessesByName("ps-bash-host"))
            {
                try
                {
                    var pp = JobObjectWatchdog.GetParentProcessId(p.Handle);
                    Assert.NotEqual(ourPid, pp);
                }
                catch { /* process may have exited during enumeration */ }
                finally { p.Dispose(); }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task ParentProcessDies_ChildPsBashExitsPromptly()
    {
        Skip.IfNot(CanRun, "Windows + built ps-bash.exe required");

        // Spawn an intermediate cmd.exe which in turn spawns ps-bash -c "Start-Sleep 30".
        // Kill cmd.exe (the parent). The Job Object chain + parent-death watcher in
        // ps-bash must cause it to exit within a few seconds even though its
        // own command (Start-Sleep 30) would otherwise keep it alive.
        // Use cmd.exe as an intermediate "parent" that we can force-kill. cmd.exe
        // has quirky argument parsing, so build the /c line manually.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{PsBashExe}\" -c \"Start-Sleep 30\"\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var parent = Process.Start(psi)!;
        int? psBashPid = null;
        try
        {
            // Poll briefly to find the ps-bash.exe child whose parent is our cmd.exe.
            using (var discover = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (!discover.IsCancellationRequested && psBashPid is null)
                {
                    foreach (var p in Process.GetProcessesByName("ps-bash"))
                    {
                        try
                        {
                            // Filter to the ps-bash whose real parent is our cmd.exe.
                            var parentPid = JobObjectWatchdog.GetParentProcessId(p.Handle);
                            if (parentPid == parent.Id)
                            {
                                psBashPid = p.Id;
                                break;
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                    if (psBashPid is null) await Task.Delay(100, discover.Token);
                }
            }

            if (psBashPid is null)
            {
                var stderr = await parent.StandardError.ReadToEndAsync();
                var stdout = await parent.StandardOutput.ReadToEndAsync();
                Assert.Fail($"Did not discover ps-bash child of cmd.exe pid {parent.Id}. parent.HasExited={parent.HasExited} exitCode={(parent.HasExited ? parent.ExitCode.ToString() : "-")} stdout={stdout} stderr={stderr}");
            }

            // Confirm ps-bash is actually running Start-Sleep (give it a moment to
            // enter the long-lived state), so we know the subsequent exit is
            // attributable to the parent-death watchdog, not a fast natural exit.
            await Task.Delay(500);
            using (var alive = Process.GetProcessById(psBashPid.Value))
            {
                Assert.False(alive.HasExited, "ps-bash exited before parent was killed");
            }

            // Kill the intermediate cmd.exe parent forcibly.
            parent.Kill(entireProcessTree: false);
            await parent.WaitForExitAsync();

            // Assert: the ps-bash child exits within 8s of its parent dying.
            // Poll by PID lookup — ArgumentException (or HasExited) signals exit.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            bool exited = false;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var child = Process.GetProcessById(psBashPid.Value);
                    if (child.HasExited) { exited = true; break; }
                }
                catch (ArgumentException) { exited = true; break; }
                await Task.Delay(100);
            }
            Assert.True(exited, $"ps-bash (pid {psBashPid}) did not exit within 8s after parent died");
        }
        finally
        {
            if (!parent.HasExited)
            {
                try { parent.Kill(entireProcessTree: true); } catch { }
            }
            if (psBashPid is not null)
            {
                try
                {
                    using var leftover = Process.GetProcessById(psBashPid.Value);
                    leftover.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }
    }
}
