using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// A reset after the host acknowledged execution start used to print one undifferentiated
/// "host connection reset" — a host crash, an external kill and a dropped connection all read the
/// same, so no reset could be root-caused. The launcher now names which one happened.
/// </summary>
public class HostLossDiagnosticTests
{
    [Fact]
    public void Describe_HostExitedWithCode_NamesPidAndCode()
    {
        var obs = new IpcWorker.HostObservation { Pid = 4242 };
        obs.RecordExit(ExitedProcess(7));
        var msg = IpcWorker.DescribeStartedReset(obs, aliveNow: false);
        Assert.StartsWith("host process (pid 4242) exited with code 7 after the command started executing", msg);
        Assert.Contains("not retrying because the command may already have run", msg);
        Assert.Contains("~/.psbash/host.log", msg);
    }

    [Fact]
    public void Describe_HostGoneCodeUnknown_SaysExited()
    {
        var obs = new IpcWorker.HostObservation { Pid = 4242 };
        Assert.StartsWith("host process (pid 4242) exited after", IpcWorker.DescribeStartedReset(obs, aliveNow: false));
    }

    [Fact]
    public void Describe_HostStillAlive_SaysDroppedConnection()
    {
        var obs = new IpcWorker.HostObservation { Pid = 4242 };
        Assert.StartsWith("host (pid 4242) is still running but dropped the connection",
            IpcWorker.DescribeStartedReset(obs, aliveNow: true));
    }

    [Fact]
    public void Describe_NoPid_SaysUnknown()
    {
        Assert.StartsWith("host connection reset (host pid unknown)", IpcWorker.DescribeStartedReset(null, aliveNow: false));
        Assert.StartsWith("host connection reset (host pid unknown)",
            IpcWorker.DescribeStartedReset(new IpcWorker.HostObservation { Pid = 0 }, aliveNow: true));
    }

    /// <summary>A real exited process, so RecordExit reads a genuine exit code.</summary>
    private static System.Diagnostics.Process ExitedProcess(int code)
    {
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c exit {code}")
            : new System.Diagnostics.ProcessStartInfo("/bin/sh", $"-c \"exit {code}\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        return p;
    }
}
