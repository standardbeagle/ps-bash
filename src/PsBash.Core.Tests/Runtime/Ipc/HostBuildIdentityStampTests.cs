using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// Regression tests for <see cref="HostProtocol.BuildIdentityFor"/> — the
/// binary-stamp obsolete-detection fix.
///
/// Known-bad: <see cref="HostProtocol.BuildIdentity"/> is the assembly
/// <c>InformationalVersion</c>, which does NOT change between dev rebuilds of
/// the same <c>&lt;Version&gt;</c>. A launcher therefore reused a daemon running
/// OLD code after a recompile (the "stale host" trap that silently ran tests and
/// interactive sessions against pre-fix binaries). <c>BuildIdentityFor</c> folds
/// the host binary's file stamp (mtime + size) into the identity so any rebuild
/// makes the running daemon's recorded identity diverge and the launcher
/// replaces it. The launcher and host stat the SAME physical host binary, so
/// they agree on an unchanged build and disagree the instant it is rebuilt.
/// </summary>
public class HostBuildIdentityStampTests : IDisposable
{
    private readonly string _dir;

    public HostBuildIdentityStampTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "psbash-buildid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteBinary(string name, byte[] content, DateTime mtimeUtc)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, content);
        File.SetLastWriteTimeUtc(p, mtimeUtc);
        return p;
    }

    [Fact]
    public void BuildIdentityFor_NullOrMissingPath_FallsBackToBareVersion()
    {
        Assert.Equal(HostProtocol.BuildIdentity, HostProtocol.BuildIdentityFor(null));
        Assert.Equal(HostProtocol.BuildIdentity, HostProtocol.BuildIdentityFor(""));
        Assert.Equal(HostProtocol.BuildIdentity,
            HostProtocol.BuildIdentityFor(Path.Combine(_dir, "does-not-exist.exe")));
    }

    [Fact]
    public void BuildIdentityFor_StampsExistingFile_AndStartsWithBareVersion()
    {
        var f = WriteBinary("host.exe", new byte[] { 1, 2, 3 }, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var id = HostProtocol.BuildIdentityFor(f);

        Assert.NotEqual(HostProtocol.BuildIdentity, id);
        Assert.StartsWith(HostProtocol.BuildIdentity + "#", id);
    }

    [Fact]
    public void BuildIdentityFor_SameUnchangedFile_IsStable()
    {
        var f = WriteBinary("host.exe", new byte[] { 9, 9, 9, 9 }, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(HostProtocol.BuildIdentityFor(f), HostProtocol.BuildIdentityFor(f));
    }

    [Fact]
    public void BuildIdentityFor_DifferentMtime_ProducesDifferentIdentity()
    {
        // Same content + size, different last-write-time = a rebuild that wrote
        // identical bytes at a new time. Must still be detected as drift.
        var a = WriteBinary("a.exe", new byte[] { 1, 1, 1 }, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = WriteBinary("b.exe", new byte[] { 1, 1, 1 }, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(HostProtocol.BuildIdentityFor(a), HostProtocol.BuildIdentityFor(b));
    }

    [Fact]
    public void BuildIdentityFor_DifferentSize_ProducesDifferentIdentity()
    {
        var when = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        var a = WriteBinary("a.exe", new byte[] { 1, 2, 3 }, when);
        var b = WriteBinary("b.exe", new byte[] { 1, 2, 3, 4, 5 }, when);

        Assert.NotEqual(HostProtocol.BuildIdentityFor(a), HostProtocol.BuildIdentityFor(b));
    }

    [Fact]
    public void BuildIdentityFor_RebuiltInPlace_ChangesIdentity()
    {
        // Simulate the actual stale-host scenario: a daemon recorded identity for
        // a binary, then the binary is rebuilt in place with new content.
        var f = WriteBinary("host.exe", new byte[] { 0xAA }, new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
        var before = HostProtocol.BuildIdentityFor(f);

        File.WriteAllBytes(f, new byte[] { 0xBB, 0xCC });
        File.SetLastWriteTimeUtc(f, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        var after = HostProtocol.BuildIdentityFor(f);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void MetadataMatchesLauncher_StaleStamp_IsObsolete_FreshStamp_Matches()
    {
        // The daemon recorded an OLD-build stamp; the launcher now expects the
        // NEW-build stamp for the same logical host path → must NOT match (replace).
        var staleStamp = HostProtocol.BuildIdentityFor(
            WriteBinary("old.exe", new byte[] { 1 }, new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc)));
        var hostPath = WriteBinary("ps-bash-host.exe", new byte[] { 2, 2 },
            new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        var freshStamp = HostProtocol.BuildIdentityFor(hostPath);

        var staleMeta = new HostMetadata(
            Pid: 4242,
            ExecutablePath: hostPath,
            ProtocolVersion: HostProtocol.ProtocolVersion,
            BuildIdentity: staleStamp,
            TransportScheme: "unix",
            Endpoint: "/tmp/x.sock",
            StartedAt: DateTimeOffset.UtcNow,
            Owner: Environment.UserName);

        // Stale daemon (old stamp) vs launcher expecting the fresh stamp → obsolete.
        Assert.False(HostOwnership.MetadataMatchesLauncher(staleMeta, hostPath, freshStamp));

        // Same daemon re-stamped to the fresh build → reusable.
        var freshMeta = staleMeta with { BuildIdentity = freshStamp };
        Assert.True(HostOwnership.MetadataMatchesLauncher(freshMeta, hostPath, freshStamp));
    }
}
