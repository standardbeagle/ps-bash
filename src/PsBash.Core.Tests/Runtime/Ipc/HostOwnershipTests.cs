using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// HostOwnership classification — the safety gate that prevents killing an
/// unrelated process after PID reuse. Covers the "verify PID still refers to
/// expected ps-bash host before killing" acceptance bullet of SNlQPegASmvs.
/// </summary>
public class HostOwnershipTests
{
    private static HostMetadata Meta(int pid, string exe, string owner = "tester") =>
        new(
            Pid: pid,
            ExecutablePath: exe,
            ProtocolVersion: 2,
            BuildIdentity: "test",
            TransportScheme: "unix",
            Endpoint: "/tmp/x.sock",
            StartedAt: DateTimeOffset.UtcNow,
            Owner: owner);

    [Fact]
    public void Classify_NullMetadata_SafeArtifactCleanup()
    {
        var d = HostOwnership.Classify(null, "anyone", out var reason);
        Assert.Equal(HostOwnership.CleanupDecision.SafeArtifactCleanup, d);
        Assert.Equal("", reason);
    }

    [Fact]
    public void Classify_OwnerMismatch_UnsafeToTouch()
    {
        var meta = Meta(pid: Environment.ProcessId, exe: "x", owner: "alice");
        var d = HostOwnership.Classify(meta, "bob", out var reason);
        Assert.Equal(HostOwnership.CleanupDecision.UnsafeToTouch, d);
        Assert.Contains("alice", reason);
        Assert.Contains("bob", reason);
    }

    [Fact]
    public void Classify_OwnerMatchCaseInsensitive()
    {
        // Windows usernames are case-insensitive; the classifier must match.
        var meta = Meta(pid: int.MaxValue, exe: "x", owner: "Tester");
        // Dead PID + matching owner => SafeArtifactCleanup, NOT UnsafeToTouch.
        var d = HostOwnership.Classify(meta, "TESTER", out _);
        Assert.Equal(HostOwnership.CleanupDecision.SafeArtifactCleanup, d);
    }

    [Fact]
    public void Classify_DeadPid_SafeArtifactCleanup()
    {
        // int.MaxValue is virtually guaranteed to not be a live process.
        var meta = Meta(pid: int.MaxValue, exe: "x", owner: "tester");
        var d = HostOwnership.Classify(meta, "tester", out _);
        Assert.Equal(HostOwnership.CleanupDecision.SafeArtifactCleanup, d);
    }

    [Fact]
    public void Classify_AlivePidWithMatchingExe_SafeProcessShutdown()
    {
        // Use the test process itself as the "live host". Its own MainModule
        // path matches what we record, so the classifier must say it's safe
        // to send shutdown signals.
        var ownPid = Environment.ProcessId;
        var ownExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        Skip.If(string.IsNullOrEmpty(ownExe), "cannot read own executable path");

        var meta = Meta(pid: ownPid, exe: ownExe!, owner: Environment.UserName);
        var d = HostOwnership.Classify(meta, Environment.UserName, out _);
        Assert.Equal(HostOwnership.CleanupDecision.SafeProcessShutdown, d);
    }

    [Fact]
    public void Classify_AlivePidWithMismatchedExe_UnsafeToTouch()
    {
        // Same live PID (us) but recorded as a totally different executable —
        // classic PID reuse scenario. Must refuse.
        var ownPid = Environment.ProcessId;
        var ownExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        Skip.If(string.IsNullOrEmpty(ownExe), "cannot read own executable path");

        var fakeRecordedExe = OperatingSystem.IsWindows()
            ? @"C:\definitely\not\our\binary.exe"
            : "/definitely/not/our/binary";
        var meta = Meta(pid: ownPid, exe: fakeRecordedExe, owner: Environment.UserName);

        var d = HostOwnership.Classify(meta, Environment.UserName, out var reason);
        Assert.Equal(HostOwnership.CleanupDecision.UnsafeToTouch, d);
        Assert.Contains("PID reuse", reason);
        Assert.Contains(fakeRecordedExe, reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ProbeProcess_NonPositivePid_NotAlive(int pid)
    {
        var (alive, exe) = HostOwnership.ProbeProcess(pid);
        Assert.False(alive);
        Assert.Null(exe);
    }

    [Fact]
    public void ProbeProcess_OwnPid_AliveWithExe()
    {
        var (alive, exe) = HostOwnership.ProbeProcess(Environment.ProcessId);
        Assert.True(alive);
        // exe may be null on platforms where MainModule is restricted; the
        // classifier handles that branch separately.
        Assert.True(exe is null || File.Exists(exe), "running exe path must be readable when present");
    }
}
