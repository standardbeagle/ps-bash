using System.IO.Pipes;
using System.Runtime.InteropServices;
using PsBash.Host;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Tests for the inherited-stdio fd-detach pipe detection (RC-5).
///
/// The core question — "is this fd a pipe/FIFO?" — is answerable on every
/// POSIX platform. On Linux the host uses /proc/self/fd readlink; on macOS
/// (no /proc) it uses fstat + S_ISFIFO. fstat exists on Linux too, so the
/// macOS code path's CORE LOGIC is exercised here even though the runtime
/// dispatch (DetachInheritedStdioIfRequested) only selects it on Darwin.
///
/// The end-to-end "spawn host on macOS, exec echo, assert no SIGABRT" check
/// is gated on the CI 3-OS matrix — this box is Linux/WSL2 with no macOS.
///
/// Runs alone: it asserts on the process-wide fd table, which parallel classes change all the time (pipes, dup2,
/// child spawns), and a number is reused the moment it is freed. CI (publish v0.15.0 attempt 1, ubuntu) saw the
/// pipe it had just opened without FD_CLOEXEC report FD_CLOEXEC; the same commit passed everywhere else.
/// </summary>
[Collection(nameof(InheritedFdDetachTests))]
[CollectionDefinition(nameof(InheritedFdDetachTests), DisableParallelization = true)]
public sealed class InheritedFdDetachTests
{
    [Fact]
    public void IsFdPipeViaFstat_ReturnsTrue_ForAnonymousPipe()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // POSIX-only: no fstat on Windows.

        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var fd = (int)pipe.SafePipeHandle.DangerousGetHandle();

        Assert.True(InheritedFdDetach.IsFdPipeViaFstat(fd),
            "fstat on an anonymous pipe fd should report S_ISFIFO");
    }

    [Fact]
    public void IsFdPipeViaFstat_ReturnsFalse_ForRegularFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // POSIX-only: no fstat on Windows.

        var path = Path.GetTempFileName();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var fd = (int)fs.SafeFileHandle.DangerousGetHandle();

            Assert.False(InheritedFdDetach.IsFdPipeViaFstat(fd),
                "fstat on a regular-file fd should not report S_ISFIFO");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsFdPipeViaFstat_ReturnsFalse_ForClosedOrInvalidFd()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // POSIX-only: no fstat on Windows.

        // A very high fd number that is virtually certain to be unopened.
        // fstat returns -1/EBADF — must be treated as "not a pipe", never close.
        Assert.False(InheritedFdDetach.IsFdPipeViaFstat(9999),
            "fstat on an unopened fd should return false, not throw");
    }

    /// <summary>
    /// REGRESSION (g30, every Linux Differential test that started a program): the detach closed EVERY pipe at fd 3+,
    /// and once the host registered POSIX signal handlers before it (ShellSignalSender.CatchPosixSignals) that
    /// included the runtime's signal pipe. The first child exit (SIGCHLD) then aborted the host with EBADF. A pipe
    /// opened in this process (FD_CLOEXEC, as the runtime opens all of its own) is never selected; a pipe that
    /// survived an exec (no FD_CLOEXEC) is. The runtime's signal pipe is open here because of the registration.
    /// </summary>
    [SkippableFact]
    public void InheritedPipeFds_SelectsOnlyPipesThatCameThroughExec_NeverTheRuntimesOwn()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX fd inheritance; Windows hosts never run the detach");
        using var signals = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGCHLD, _ => { });
        using var own = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var inheritable = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        int ownFd = (int)own.SafePipeHandle.DangerousGetHandle();
        int inheritableFd = (int)inheritable.SafePipeHandle.DangerousGetHandle();
        Assert.False(InheritedFdDetach.IsInherited(ownFd));
        Assert.True(InheritedFdDetach.IsInherited(inheritableFd));

        var selected = InheritedFdDetach.InheritedPipeFds();

        Assert.DoesNotContain(ownFd, selected);
        Assert.Contains(inheritableFd, selected);
        Assert.All(selected, fd => Assert.True(InheritedFdDetach.IsInherited(fd), $"fd {fd} has FD_CLOEXEC: opened here, not inherited"));
    }

    [SkippableFact]
    public void IsInherited_IsFalse_ForAnUnopenedFd()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX fd inheritance");
        Assert.False(InheritedFdDetach.IsInherited(9999));
    }

    [Fact]
    public void IsFdPipe_AgreesWithFstat_OnLinuxForPipe()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return; // /proc readlink path is Linux-only.

        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var fd = (int)pipe.SafePipeHandle.DangerousGetHandle();

        // The platform dispatcher must classify a pipe as a pipe on Linux.
        Assert.True(InheritedFdDetach.IsFdPipe(fd));
    }
}
