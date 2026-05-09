using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// HostMetadata sidecar I/O: path derivation per scheme, atomic write, JSON
/// round-trip, malformed-input tolerance, and idempotent removal. Covers the
/// "stale endpoint without process" and "named-pipe path has a real
/// replacement strategy" acceptance bullets of SNlQPegASmvs.
/// </summary>
public class HostMetadataTests : IDisposable
{
    private readonly string _tempRoot;

    public HostMetadataTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ps-bash", "metatest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private HostMetadata Sample(int pid = 12345, string exe = @"C:\bin\ps-bash-host.exe", string endpoint = "/tmp/x.sock") =>
        new(
            Pid: pid,
            ExecutablePath: exe,
            ProtocolVersion: 2,
            BuildIdentity: "test-build",
            TransportScheme: "unix",
            Endpoint: endpoint,
            StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Owner: "tester");

    [Fact]
    public void PathFor_Unix_PlacesSidecarNextToSocket()
    {
        var path = HostMetadata.PathFor("unix", "/tmp/host.sock");
        Assert.Equal("/tmp/host.sock.host.json", path);
    }

    [Fact]
    public void PathFor_Pipe_PlacesSidecarUnderPsBashTemp()
    {
        var path = HostMetadata.PathFor("pipe", "psbash-host-andy");
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "ps-bash", "psbash-host-andy.host.json"),
            path);
    }

    [Fact]
    public void PathFor_UnknownScheme_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => HostMetadata.PathFor("tcp", "x"));
        Assert.Contains("tcp", ex.Message);
    }

    [Fact]
    public void Write_Then_TryRead_RoundTripsAllFields()
    {
        var endpoint = Path.Combine(_tempRoot, "rt.sock");
        var meta = Sample(endpoint: endpoint);
        meta.Write("unix", endpoint);

        var read = HostMetadata.TryRead("unix", endpoint);
        Assert.NotNull(read);
        Assert.Equal(meta, read);
    }

    [Fact]
    public void TryRead_Absent_ReturnsNull()
    {
        var endpoint = Path.Combine(_tempRoot, "missing.sock");
        Assert.Null(HostMetadata.TryRead("unix", endpoint));
    }

    [Fact]
    public void TryRead_Malformed_ReturnsNullInsteadOfThrowing()
    {
        var endpoint = Path.Combine(_tempRoot, "bad.sock");
        File.WriteAllText(HostMetadata.PathFor("unix", endpoint), "{not valid json");
        Assert.Null(HostMetadata.TryRead("unix", endpoint));
    }

    [Fact]
    public void Remove_RemovesExistingSidecar()
    {
        var endpoint = Path.Combine(_tempRoot, "rm.sock");
        Sample(endpoint: endpoint).Write("unix", endpoint);
        Assert.True(File.Exists(HostMetadata.PathFor("unix", endpoint)));

        HostMetadata.Remove("unix", endpoint);
        Assert.False(File.Exists(HostMetadata.PathFor("unix", endpoint)));
    }

    [Fact]
    public void Remove_AbsentSidecar_NoThrow()
    {
        var endpoint = Path.Combine(_tempRoot, "noop.sock");
        // Idempotency: caller should not need to check first.
        HostMetadata.Remove("unix", endpoint);
        HostMetadata.Remove("unix", endpoint);
    }

    [Fact]
    public void Write_OverwritesExistingSidecar()
    {
        var endpoint = Path.Combine(_tempRoot, "ow.sock");
        Sample(pid: 1, endpoint: endpoint).Write("unix", endpoint);
        Sample(pid: 2, endpoint: endpoint).Write("unix", endpoint);

        var read = HostMetadata.TryRead("unix", endpoint);
        Assert.NotNull(read);
        Assert.Equal(2, read!.Pid);
    }
}
