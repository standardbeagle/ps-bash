using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Core.Tests.Platform;

/// <summary>
/// Terminal layout knowledge in <see cref="TermiosLayout"/> and the ABI switch in <see cref="TerminalIoctl"/>, checked
/// from hand-built buffers on every OS. The live round trip (set on a PTY master, read back on the slave) is
/// <c>PtyAllocationTests.Posix_WindowSize_SetOnMaster_ReadsBackOnSlave</c> in Shell.Tests.
/// Oracle note (qa-rubric Directive 1): C struct layouts have no bash equivalent; the values are the headers'.
/// </summary>
public class TerminalPlatformTests
{
    [Fact]
    public void LflagOffset_IsTheFourthTcflag_FourBytesOnLinux_EightOnMacOS()
    {
        Assert.Equal(12, TermiosLayout.LflagOffset(macOS: false));
        Assert.Equal(24, TermiosLayout.LflagOffset(macOS: true));
    }

    [Fact]
    public void BufferSize_HoldsEitherLayout()
    {
        Assert.True(TermiosLayout.BufferSize >= 60, "glibc/musl struct termios is 60 bytes");
        Assert.True(TermiosLayout.BufferSize >= 72, "macOS struct termios is 72 bytes");
    }

    [Fact]
    public void ReadLflag_LinuxLayout_ReadsFourBytesAtTwelve()
    {
        var buf = new byte[TermiosLayout.BufferSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), 0xAAAA_AAAA);  // c_cflag
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), 0x8A3B);      // c_lflag: ICANON|ECHO|...
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 0xFFFF_FFFF); // c_line + c_cc
        ulong lflag = TermiosLayout.ReadLflag(buf, macOS: false);
        Assert.Equal(0x8A3BUL, lflag);
        Assert.NotEqual(0UL, lflag & TermiosLayout.Icanon(macOS: false));
        Assert.NotEqual(0UL, lflag & TermiosLayout.Echo);
    }

    /// <summary>REGRESSION: the test probe read macOS <c>c_lflag</c> as 4 bytes at offset 12 — the high half of
    /// <c>c_oflag</c> — because <c>tcflag_t</c> is an 8-byte <c>unsigned long</c> there.</summary>
    [Fact]
    public void ReadLflag_MacLayout_ReadsEightBytesAtTwentyFour_NotOffsetTwelve()
    {
        var buf = new byte[TermiosLayout.BufferSize];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8), 0x3);          // c_oflag (bytes 8..15)
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(16), 0x4B00);      // c_cflag
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(24), 0x5CB);       // c_lflag: ICANON(0x100)|ECHO(0x8)|...
        ulong lflag = TermiosLayout.ReadLflag(buf, macOS: true);
        Assert.Equal(0x5CBUL, lflag);
        Assert.NotEqual(0UL, lflag & TermiosLayout.Icanon(macOS: true));
        Assert.NotEqual(0UL, lflag & TermiosLayout.Echo);
        Assert.Equal(0UL, TermiosLayout.ReadLflag(buf, macOS: false) & TermiosLayout.Icanon(macOS: true)); // the old read
    }

    [Fact]
    public void Icanon_DiffersByOs()
    {
        Assert.Equal(0x2UL, TermiosLayout.Icanon(macOS: false));
        Assert.Equal(0x100UL, TermiosLayout.Icanon(macOS: true));
    }

    [Fact]
    public void VariadicArgsOnStack_OnlyOnAppleArm64()
        => Assert.Equal(OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
            TerminalIoctl.VariadicArgsOnStack);

    [Fact]
    public void WinsizeRequests_AreTheHeaderValues()
    {
        // Linux asm-generic/ioctls.h; macOS sys/ttycom.h _IOR('t', 104, struct winsize) / _IOW('t', 103, ...).
        Assert.Equal(OperatingSystem.IsLinux() ? 0x5413UL : 0x40087468UL, TerminalIoctl.TIOCGWINSZ);
        Assert.Equal(OperatingSystem.IsLinux() ? 0x5414UL : 0x80087467UL, TerminalIoctl.TIOCSWINSZ);
        Assert.Equal(8, Marshal.SizeOf<TerminalIoctl.Winsize>());
    }
}
