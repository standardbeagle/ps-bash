using System;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="IpcWorker.ShouldReuseLocally"/>, the local-only
/// (no-socket) decision behind the optimistic-reuse fast path. It must reuse a
/// daemon ONLY when the sidecar exists, names OUR build, and its PID is alive —
/// every other case must fall through (return false) to the full probe/spawn/
/// replace path so no ownership-safety gate is bypassed.
/// </summary>
public class IpcWorkerReuseTests
{
    private const string Exe = "/opt/ps-bash/ps-bash-host";
    private const string BuildId = "build-abc";

    private static HostMetadata Meta(int pid = 4321, string exe = Exe, string buildId = BuildId, int? protocol = null) =>
        new(
            Pid: pid,
            ExecutablePath: exe,
            ProtocolVersion: protocol ?? HostProtocol.ProtocolVersion,
            BuildIdentity: buildId,
            TransportScheme: "unix",
            Endpoint: "/tmp/x.sock",
            StartedAt: DateTimeOffset.UtcNow,
            Owner: "tester");

    [Fact]
    public void NullSidecar_DoesNotReuse()
        => Assert.False(IpcWorker.ShouldReuseLocally(null, Exe, BuildId, _ => true));

    [Fact]
    public void MatchingBuildAndAlivePid_Reuses()
        => Assert.True(IpcWorker.ShouldReuseLocally(Meta(), Exe, BuildId, _ => true));

    [Fact]
    public void MatchingBuildButDeadPid_DoesNotReuse()
        => Assert.False(IpcWorker.ShouldReuseLocally(Meta(), Exe, BuildId, _ => false));

    [Fact]
    public void MismatchedBuildIdentity_DoesNotReuse()
        => Assert.False(IpcWorker.ShouldReuseLocally(Meta(buildId: "build-OLD"), Exe, BuildId, _ => true));

    [Fact]
    public void MismatchedProtocol_DoesNotReuse()
        => Assert.False(IpcWorker.ShouldReuseLocally(
            Meta(protocol: HostProtocol.ProtocolVersion + 1), Exe, BuildId, _ => true));

    [Fact]
    public void MismatchedExecutable_DoesNotReuse()
        => Assert.False(IpcWorker.ShouldReuseLocally(
            Meta(exe: "/opt/other/ps-bash-host"), Exe, BuildId, _ => true));

    [Fact]
    public void NonPositivePid_DoesNotReuse()
        => Assert.False(IpcWorker.ShouldReuseLocally(Meta(pid: 0), Exe, BuildId, _ => true));

    [Fact]
    public void AlivePredicate_IsConsultedWithTheRecordedPid()
    {
        int seen = -1;
        IpcWorker.ShouldReuseLocally(Meta(pid: 9988), Exe, BuildId, p => { seen = p; return true; });
        Assert.Equal(9988, seen);
    }
}
