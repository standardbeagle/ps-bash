using System;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="IpcWorker.ResolveWatchdogPid"/> — the seam that
/// fixes the stale-PID liveness-watchdog race: <c>_cachedHostPid</c> is
/// resolved BEFORE <c>ConnectAsync</c>, so a concurrent launcher that replaces
/// the daemon (kills the cached PID, spawns a new host) between that
/// resolution and the connect completing must not leave the watchdog watching
/// the now-dead old PID — that would abort the in-flight read on the
/// perfectly healthy new host as a false <c>HostProcessExitedException</c>.
/// </summary>
public class IpcWorkerWatchdogPidTests
{
    private const string HostBinary = "/opt/ps-bash/ps-bash-host";

    private static HostMetadata Meta(int pid, string exe = HostBinary) =>
        new(
            Pid: pid,
            ExecutablePath: exe,
            ProtocolVersion: HostProtocol.ProtocolVersion,
            BuildIdentity: "build-abc",
            TransportScheme: "unix",
            Endpoint: "/tmp/x.sock",
            StartedAt: DateTimeOffset.UtcNow,
            Owner: "tester");

    [Fact]
    public void PerInvocation_ReturnsOwnedHostId_IgnoringCacheAndSidecar()
    {
        var pid = IpcWorker.ResolveWatchdogPid(
            Lifetime.PerInvocation,
            ownedHostId: 4242,
            cachedHostPid: 999, // must be ignored entirely for PerInvocation
            cachedPidIsAlive: () => throw new InvalidOperationException("must not be consulted"),
            readFreshMetadata: () => throw new InvalidOperationException("must not be consulted"),
            hostBinaryPath: HostBinary,
            probeProcess: _ => throw new InvalidOperationException("must not be consulted"));

        Assert.Equal(4242, pid);
    }

    [Fact]
    public void Daemon_CachedPidStillAlive_ReusesCache_NoSidecarReRead()
    {
        bool sidecarRead = false;
        var pid = IpcWorker.ResolveWatchdogPid(
            Lifetime.Daemon,
            ownedHostId: 0,
            cachedHostPid: 111,
            cachedPidIsAlive: () => true,
            readFreshMetadata: () => { sidecarRead = true; return Meta(999); },
            hostBinaryPath: HostBinary,
            probeProcess: _ => throw new InvalidOperationException("must not be consulted"));

        Assert.Equal(111, pid);
        Assert.False(sidecarRead, "the common (no-race) path must not pay for a sidecar re-read");
    }

    [Fact]
    public void Daemon_CachedPidDead_ReResolvesFromFreshSidecar_WhenExecutableMatches()
    {
        // THE RACE: cached PID 111 (the old host) has already exited — a
        // concurrent launcher killed it and spawned a replacement — but our
        // connect just succeeded against whatever is answering NOW. The fresh
        // sidecar read (post-connect) names the new PID 222 running the same
        // host binary; it must be trusted.
        var pid = IpcWorker.ResolveWatchdogPid(
            Lifetime.Daemon,
            ownedHostId: 0,
            cachedHostPid: 111,
            cachedPidIsAlive: () => false,
            readFreshMetadata: () => Meta(222),
            hostBinaryPath: HostBinary,
            probeProcess: p => (p == 222, HostBinary));

        Assert.Equal(222, pid);
    }

    [Fact]
    public void Daemon_CachedPidDead_FreshPidRecycledToUnrelatedExecutable_SkipsWatchdog()
    {
        // The fresh sidecar names PID 222, but that PID is now running a
        // DIFFERENT executable (recycled by the OS to an unrelated process,
        // or the sidecar was read mid-write). Trusting it would let the
        // watchdog watch garbage. Must return 0 (no watchdog; fall back to
        // the idle-timeout backstop) rather than risk a false abort later.
        var pid = IpcWorker.ResolveWatchdogPid(
            Lifetime.Daemon,
            ownedHostId: 0,
            cachedHostPid: 111,
            cachedPidIsAlive: () => false,
            readFreshMetadata: () => Meta(222),
            hostBinaryPath: HostBinary,
            probeProcess: p => (p == 222, "/opt/other/unrelated-process"));

        Assert.Equal(0, pid);
    }

    [Fact]
    public void Daemon_CachedPidDead_FreshPidAlsoDead_SkipsWatchdog()
    {
        var pid = IpcWorker.ResolveWatchdogPid(
            Lifetime.Daemon,
            ownedHostId: 0,
            cachedHostPid: 111,
            cachedPidIsAlive: () => false,
            readFreshMetadata: () => Meta(222),
            hostBinaryPath: HostBinary,
            probeProcess: p => (false, null));

        Assert.Equal(0, pid);
    }

    [Fact]
    public void Daemon_CachedPidDead_NoFreshSidecar_SkipsWatchdog()
    {
        var pid = IpcWorker.ResolveWatchdogPid(
            Lifetime.Daemon,
            ownedHostId: 0,
            cachedHostPid: 111,
            cachedPidIsAlive: () => false,
            readFreshMetadata: () => null,
            hostBinaryPath: HostBinary,
            probeProcess: _ => throw new InvalidOperationException("must not be consulted"));

        Assert.Equal(0, pid);
    }

    [Fact]
    public void Daemon_NeverCached_ReadsFreshSidecarDirectly()
    {
        var pid = IpcWorker.ResolveWatchdogPid(
            Lifetime.Daemon,
            ownedHostId: 0,
            cachedHostPid: 0, // never cached (rare health-reuse path)
            cachedPidIsAlive: () => throw new InvalidOperationException("must not probe a non-positive PID"),
            readFreshMetadata: () => Meta(333),
            hostBinaryPath: HostBinary,
            probeProcess: p => (p == 333, HostBinary));

        Assert.Equal(333, pid);
    }
}
