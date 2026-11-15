using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// A ps-bash started by a host's command joins that host's session (base endpoint and its executor slots) instead of
/// anchoring a new session on the host, which started one more full daemon per host (19-21 hosts, 3.5 GB). Launcher
/// endpoint resolution (no bash equivalent); hand-written asserts on synthetic ancestry chains and sidecars.
/// </summary>
public class EnclosingHostTests
{
    private static ProcessIdentity? Select(int launcherPid, Dictionary<int, (string Name, long Start, int Parent)> chain)
        => ProcessAncestry.SelectEnclosingHost(
            launcherPid,
            pid => chain.TryGetValue(pid, out var n) && n.Parent > 0 ? n.Parent : null,
            pid => chain.TryGetValue(pid, out var n) ? (n.Name, n.Start) : null);

    [Fact]
    public void SelectEnclosingHost_HostIsTheParent()
    {
        var chain = new Dictionary<int, (string, long, int)> { [5] = ("ps-bash", 1, 10), [10] = ("ps-bash-host", 77, 0) };
        Assert.Equal(new ProcessIdentity(10, 77), Select(5, chain));
    }

    [Fact]
    public void SelectEnclosingHost_ClimbsPlainShellsBetween()
    {
        // awk system() / a .cmd wrapper puts a shell between the host and the nested launcher.
        var chain = new Dictionary<int, (string, long, int)>
        {
            [5] = ("ps-bash", 1, 6), [6] = ("cmd.exe", 2, 7), [7] = ("sh", 3, 10), [10] = ("ps-bash-host.exe", 77, 0),
        };
        Assert.Equal(new ProcessIdentity(10, 77), Select(5, chain));
    }

    [Theory]
    [InlineData("node")]     // an agent started by a command owns its own session
    [InlineData("claude")]
    [InlineData("python")]
    [InlineData("dotnet")]
    public void SelectEnclosingHost_DoesNotClimbPastAnAgentOrService(string between)
    {
        var chain = new Dictionary<int, (string, long, int)>
        {
            [5] = ("ps-bash", 1, 6), [6] = (between, 2, 10), [10] = ("ps-bash-host", 77, 0),
        };
        Assert.Null(Select(5, chain));
    }

    [Fact]
    public void SelectEnclosingHost_TooManyShells_IsNotEnclosed()
    {
        var chain = new Dictionary<int, (string, long, int)>
        {
            [5] = ("ps-bash", 1, 6), [6] = ("sh", 2, 7), [7] = ("sh", 3, 8), [8] = ("sh", 4, 9), [9] = ("sh", 5, 10),
            [10] = ("ps-bash-host", 77, 0),
        };
        Assert.Null(Select(5, chain));
    }

    [Fact]
    public void SelectEnclosingHost_TopLevelLauncher_IsNotEnclosed()
    {
        var chain = new Dictionary<int, (string, long, int)> { [5] = ("ps-bash", 1, 6), [6] = ("bash", 2, 7), [7] = ("node", 3, 0) };
        Assert.Null(Select(5, chain));
    }

    private static HostMetadata Sidecar(int pid, string endpoint, DateTimeOffset started)
        => new(pid, "ps-bash-host", 1, "b", "unix", endpoint, started, "u");

    private static readonly DateTimeOffset HostStart = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity Host = new(10, HostStart.UtcTicks);

    [Fact]
    public void EnclosingHostBase_MainHost_IsItsOwnEndpoint()
        => Assert.Equal("/r/host-u-s1-2.sock",
            IpcTransportFactory.EnclosingHostBase(Host, [Sidecar(9, "/r/other.sock", HostStart), Sidecar(10, "/r/host-u-s1-2.sock", HostStart.AddSeconds(2))]));

    [Fact]
    public void EnclosingHostBase_SlotHost_MapsToTheSessionBase()
        => Assert.Equal("/r/host-u-s1-2.sock",
            IpcTransportFactory.EnclosingHostBase(Host, [Sidecar(10, "/r/host-u-s1-2.sock-x3", HostStart.AddSeconds(2))]));

    [Fact]
    public void EnclosingHostBase_StaleSidecarFromAnEarlierHostWithThatPid_IsIgnored()
        => Assert.Null(IpcTransportFactory.EnclosingHostBase(Host, [Sidecar(10, "/r/old.sock", HostStart.AddMinutes(-5))]));

    [Fact]
    public void EnclosingHostBase_UnregisteredHost_IsNull()
        => Assert.Null(IpcTransportFactory.EnclosingHostBase(Host, [Sidecar(9, "/r/other.sock", HostStart)]));

    [Fact]
    public void EnclosingHostOwnEndpoint_SlotHost_IsTheSlotItServes()
        => Assert.Equal("/r/host-u-s1-2.sock-x3",
            IpcTransportFactory.EnclosingHostOwnEndpoint(Host, [Sidecar(9, "/r/other.sock", HostStart), Sidecar(10, "/r/host-u-s1-2.sock-x3", HostStart.AddSeconds(2))]));

    [Fact]
    public void EnclosingHostOwnEndpoint_StaleSidecar_IsNull()
        => Assert.Null(IpcTransportFactory.EnclosingHostOwnEndpoint(Host, [Sidecar(10, "/r/old.sock-x1", HostStart.AddMinutes(-5))]));

    [Theory]
    [InlineData("/r/host.sock", 0)]
    [InlineData("/r/host.sock-x1", 1)]
    [InlineData("/r/host.sock-x6", 6)]
    [InlineData("/r/psbash-slot-0123456789ABCDEF-x2", 0)]  // hashed: its own base
    public void SlotIndexOf(string endpoint, int expected)
        => Assert.Equal(expected, IpcTransportFactory.SlotIndexOf(endpoint));

    [Theory]
    [InlineData("/r/host.sock", "/r/host.sock")]
    [InlineData("/r/host.sock-x1", "/r/host.sock")]
    [InlineData("/r/host.sock-x6", "/r/host.sock")]
    [InlineData("/r/psbash-slot-0123456789ABCDEF-x2", "/r/psbash-slot-0123456789ABCDEF-x2")]  // hashed: no way back
    public void BaseOfSlotEndpoint(string endpoint, string expected)
        => Assert.Equal(expected, IpcTransportFactory.BaseOfSlotEndpoint(endpoint));
}
