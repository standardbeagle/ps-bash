using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Core.Tests.Platform;

/// <summary>
/// The AF_UNIX path budget per OS. Oracle note: sizeof(sun_path) from the Linux (sys/un.h, 108) and XNU (sys/un.h, 104)
/// headers, minus the NUL; no bash equivalent.
/// </summary>
public class UnixSocketPathTests
{
    [Fact]
    public void MaxChars_IsSunPathMinusTheNul()
    {
        bool bsd = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsFreeBSD();
        Assert.Equal(bsd ? 103 : 107, UnixSocketPath.MaxChars);
        Assert.Equal(UnixSocketPath.MaxChars, PsBash.Core.Runtime.Ipc.IpcTransportFactory.UnixSocketPathBudget());
    }
}
