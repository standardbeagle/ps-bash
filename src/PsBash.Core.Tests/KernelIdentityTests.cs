using System;
using System.Text;
using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// <see cref="KernelIdentity"/>: the <c>struct utsname</c> decoder for both layouts (hand-built buffers, any OS) and
/// the live call on Linux / macOS. Oracle note (qa-rubric Directive 1): the layouts are the C headers' (glibc
/// <c>sys/utsname.h</c> <c>_UTSNAME_LENGTH</c> 65, XNU <c>sys/utsname.h</c> <c>_SYS_NAMELEN</c> 256); the live values
/// are compared with the system's own <c>uname</c>.
/// </summary>
public class KernelIdentityTests
{
    private static byte[] Build(int fieldLength, int fields, params string[] values)
    {
        var buf = new byte[fieldLength * fields];
        for (int k = 0; k < Math.Min(values.Length, fields); k++)   // Darwin has no domainname field
            Encoding.ASCII.GetBytes(values[k]).CopyTo(buf, k * fieldLength);
        return buf;
    }

    [Theory]
    [InlineData(KernelIdentity.LinuxFieldLength, 6)]
    [InlineData(KernelIdentity.DarwinFieldLength, 5)]
    public void Decode_ReadsTheFiveFieldsOfEitherLayout(int fieldLength, int fields)
    {
        var buf = Build(fieldLength, fields, "Linux", "box", "6.8.0-45-generic", "#45-Ubuntu SMP", "x86_64", "(none)");
        var uts = KernelIdentity.Decode(buf, fieldLength);
        Assert.Equal(new KernelIdentity.Uts("Linux", "box", "6.8.0-45-generic", "#45-Ubuntu SMP", "x86_64"), uts);
    }

    [Fact]
    public void Decode_ShortBuffer_IsNull()
        => Assert.Null(KernelIdentity.Decode(new byte[4 * KernelIdentity.LinuxFieldLength], KernelIdentity.LinuxFieldLength));

    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Host_OnWindows_IsNull()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows has no uname(2)");
        Assert.Null(KernelIdentity.Host());
    }

    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void Host_OnUnix_IsTheSystemUname()
    {
        Skip.If(OperatingSystem.IsWindows(), "uname(2) is Linux / macOS");
        var uts = KernelIdentity.Host();
        Assert.NotNull(uts);
        Assert.Equal(OperatingSystem.IsMacOS() ? "Darwin" : "Linux", uts!.Value.SysName);
        Assert.Equal(Environment.MachineName, uts.Value.NodeName.Split('.')[0], ignoreCase: true);
        // .NET reports the kernel release on Linux (on macOS it is the product version, not Darwin's).
        if (OperatingSystem.IsLinux())
            Assert.Equal(Environment.OSVersion.Version.Major.ToString(), uts.Value.Release.Split('.')[0]);
    }
}
