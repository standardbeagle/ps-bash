using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// awk <c>print | "cmd"</c>: <c>close()</c> (explicit, or the one at the end of the program) waits for the command as
/// long as it runs - bash's awk has no time limit there - but a STOP (Ctrl+C, a pipeline stop) ends the wait and the
/// command's whole process tree (<c>docs/specs/stdio-messaging.md</c> §4.6). The bug: close() took the sink out of the
/// open outputs before its unbounded wait, so <c>StopProcessing</c> (which walked only the open ones) never reached
/// the command, and the stop hung until the command ended on its own.
/// <para>
/// Hand-written asserts: stopping is ps-bash's own mechanism (no bash oracle for "Stop() returns"). The command runs
/// through the nested-shell override (<c>PSBASH_NESTED_EXE</c> = powershell.exe / /bin/sh) so the held child is a
/// plain process whose pid the test can watch, not a command inside a ps-bash host. The class runs alone
/// (<see cref="WallClockBoundCollection"/>), so the process-wide override reaches no other test.
/// </para>
/// </summary>
[Collection(WallClockBoundCollection.Name)]
public class AwkPipeStopTests : IClassFixture<SharedPwshFixture>
{
    /// <summary>How long the held command lives unless stopped: at least twice <see cref="StopBound"/> (testing.md).</summary>
    private const int HoldSeconds = 120;

    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(40);

    private readonly SharedPwshFixture _fixture;

    public AwkPipeStopTests(SharedPwshFixture fixture) => _fixture = fixture;

    private static string ShellExe() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
        : "/bin/sh";

    /// <summary>A command (for <see cref="ShellExe"/> <c>-c</c>) that publishes its pid to <paramref name="pidFile"/> and holds.</summary>
    private static string HoldCommand(string pidFile) => OperatingSystem.IsWindows()
        ? $"[IO.File]::WriteAllText('{pidFile}.tmp', [string]$PID); Move-Item -LiteralPath '{pidFile}.tmp' -Destination '{pidFile}'; Start-Sleep -Seconds {HoldSeconds}"
        : $"echo $$ > '{pidFile}.tmp'; mv '{pidFile}.tmp' '{pidFile}'; exec sleep {HoldSeconds}";

    private static string AwkString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static void AssertGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            Assert.True(p.WaitForExit(StopBound), $"the pipe command (pid {pid}) is still running after the stop");
        }
        catch (ArgumentException) { /* not running */ }
        catch (InvalidOperationException) { /* exited between the lookup and the wait */ }
    }

    [Theory]
    [InlineData("BEGIN { c = @C@; print \"x\" | c; close(c); print \"after\" }")] // explicit close()
    [InlineData("BEGIN { c = @C@; print \"x\" | c }")]                             // the close at program end
    public void Awk_PipeCloseWaitingForCommand_Stop_EndsTheCommandAndReturns(string program)
    {
        Assert.True(TimeSpan.FromSeconds(HoldSeconds) >= 2 * StopBound, "the hold must outlast twice the bound");
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "awk-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pidFile = Path.Combine(dir, "cmd.pid");
        using var env = PsBash.Testing.ProcessEnv.Set("PSBASH_NESTED_EXE", ShellExe());
        int pid = 0;
        try
        {
            var script = "Invoke-BashAwk " + PsQuote(program.Replace("@C@", AwkString(HoldCommand(pidFile))));
            var took = BoundedPipeline.StopWhenReady(_fixture.AcquireFresh(), script, () =>
            {
                string? text = PsBash.Testing.PublishedFile.ReadWhenReady(pidFile, StartLimit);
                Assert.True(text is not null, $"the pipe command never started within {StartLimit.TotalSeconds:0}s");
                pid = int.Parse(text!.Trim());
            }, StopBound, "awk close() waiting for a pipe command");

            AssertGone(pid);
            Assert.True(took < StopBound, $"the stop took {took.TotalMilliseconds:0}ms");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
