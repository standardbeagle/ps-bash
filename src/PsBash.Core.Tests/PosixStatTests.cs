using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// <see cref="PosixStat"/> decodes each OS/architecture's <c>struct stat</c>. The buffers here are built from the C
/// headers' offsets (Linux x86_64 <c>asm/stat.h</c>, <c>asm-generic/stat.h</c> for arm64, Darwin
/// <c>__DARWIN_STRUCT_STAT64</c>), not from the decoder, so a layout that drifts from its header fails on any OS.
/// Oracle note (qa-rubric Directive 1): a libc ABI fact with no bash equivalent; the x86_64 offsets were checked
/// against glibc's own <c>lstat</c> in WSL.
/// </summary>
public class PosixStatTests
{
    private const uint Dir0755 = 0x41ED;   // S_IFDIR | 0755
    private const uint Fifo0600 = 0x1180;  // S_IFIFO | 0600

    private static byte[] LinuxX64(ulong dev, ulong ino, ulong nlink, uint mode, uint uid, uint gid, long size)
    {
        var b = new byte[144];
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0), dev);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), ino);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), nlink);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), gid);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48), size);
        return b;
    }

    private static byte[] LinuxGeneric64(ulong dev, ulong ino, uint mode, uint nlink, uint uid, uint gid, long size)
    {
        var b = new byte[128];
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0), dev);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), ino);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), nlink);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), gid);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48), size);
        return b;
    }

    private static byte[] Darwin64(uint dev, ushort mode, ushort nlink, ulong ino, uint uid, uint gid, long size)
    {
        var b = new byte[144];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), dev);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), mode);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), nlink);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), ino);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), gid);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(96), size);
        return b;
    }

    [Fact]
    public void Decode_LinuxX64_ReadsEveryFieldAtItsHeaderOffset()
    {
        var buf = LinuxX64(dev: 2096, ino: 73730, nlink: 16, mode: Dir0755, uid: 1000, gid: 1001, size: 4096);
        Assert.True(PosixStat.TryDecode(buf, StatLayout.LinuxX64, out var st));
        Assert.Equal(new PosixStatInfo(2096, 73730, Dir0755, 16, 1000, 1001, 4096), st);
        Assert.True(st.IsDirectory);
        Assert.Equal((UnixFileMode)0x1ED, st.Permissions);
    }

    [Fact]
    public void Decode_LinuxArm64_ReadsModeAtOffset16()
    {
        var buf = LinuxGeneric64(dev: 66306, ino: 12, mode: Fifo0600, nlink: 1, uid: 501, gid: 20, size: 0);
        Assert.True(PosixStat.TryDecode(buf, StatLayout.LinuxGeneric64, out var st));
        Assert.Equal(new PosixStatInfo(66306, 12, Fifo0600, 1, 501, 20, 0), st);
        Assert.True(st.IsFifo);
        Assert.False(st.IsDirectory);
    }

    /// <summary>REGRESSION: the x86_64 struct was used on arm64. Its st_mode offset (24) holds arm64's st_uid, so a
    /// uid-1000 directory read as mode 01750 (not a directory, not owned-check-able).</summary>
    [Fact]
    public void Decode_Arm64BufferWithTheX64Layout_IsNotTheFile()
    {
        var buf = LinuxGeneric64(dev: 1, ino: 2, mode: Dir0755, nlink: 3, uid: 1000, gid: 20, size: 4096);
        var arm64Buffer = new byte[144];
        buf.CopyTo(arm64Buffer, 0);
        Assert.True(PosixStat.TryDecode(arm64Buffer, StatLayout.LinuxX64, out var wrong));
        Assert.False(wrong.IsDirectory);
        Assert.NotEqual(1000u, wrong.Uid);

        Assert.True(PosixStat.TryDecode(buf, StatLayout.LinuxGeneric64, out var right));
        Assert.True(right.IsDirectory);
        Assert.Equal(1000u, right.Uid);
    }

    [Fact]
    public void Decode_Darwin64_ReadsSixteenBitModeAndLinks()
    {
        var buf = Darwin64(dev: 16777234, mode: 0xA1ED, nlink: 1, ino: 9_000_000_001, uid: 501, gid: 20, size: 11);
        Assert.True(PosixStat.TryDecode(buf, StatLayout.Darwin64, out var st));
        Assert.Equal(new PosixStatInfo(16777234, 9_000_000_001, 0xA1ED, 1, 501, 20, 11), st);
        Assert.True(st.IsSymbolicLink);
    }

    [Theory]
    [InlineData(StatLayout.LinuxX64, 143)]
    [InlineData(StatLayout.LinuxGeneric64, 127)]
    [InlineData(StatLayout.Darwin64, 143)]
    [InlineData(StatLayout.Unsupported, 256)]
    public void Decode_ShortBufferOrUnsupportedLayout_IsFalse(StatLayout layout, int length)
    {
        Assert.False(PosixStat.TryDecode(new byte[length], layout, out var st));
        Assert.Equal(default(PosixStatInfo), st);
    }

    [Theory]
    [InlineData(true, false, Architecture.X64, true, StatLayout.LinuxX64)]
    [InlineData(true, false, Architecture.Arm64, true, StatLayout.LinuxGeneric64)]
    [InlineData(true, false, Architecture.X86, true, StatLayout.Unsupported)]
    [InlineData(true, false, Architecture.Arm, true, StatLayout.Unsupported)]
    [InlineData(false, true, Architecture.Arm64, true, StatLayout.Darwin64)]
    [InlineData(false, true, Architecture.X64, true, StatLayout.Darwin64)]
    [InlineData(false, false, Architecture.X64, true, StatLayout.Unsupported)]
    [InlineData(true, false, Architecture.X64, false, StatLayout.Unsupported)]
    public void LayoutFor_EachShippedTarget(bool linux, bool macOS, Architecture arch, bool littleEndian, StatLayout expected)
        => Assert.Equal(expected, PosixStat.LayoutFor(linux, macOS, arch, littleEndian));

    [Fact]
    public void XStatVersion_IsGlibcsStatVer()
    {
        Assert.Equal(1, PosixStat.XStatVersion(StatLayout.LinuxX64));
        Assert.Equal(0, PosixStat.XStatVersion(StatLayout.LinuxGeneric64));
    }

    // ---- the live calls (this OS's layout) ----

    private static string TempRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps-bash", "posixstat-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void LStat_DirectoryAndSymlink_AgreeWithTheRuntime()
    {
        Skip.If(OperatingSystem.IsWindows(), "libc stat is Unix-only");
        if (OperatingSystem.IsWindows()) return; // unreachable: tells the platform analyzer the Unix-mode calls are Unix-only
        var root = TempRoot();
        try
        {
            var file = Path.Combine(root, "f");
            File.WriteAllText(file, "abc");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            var link = Path.Combine(root, "l");
            File.CreateSymbolicLink(link, file);

            Assert.True(PosixStat.TryLStat(root, out var dir));
            Assert.True(dir.IsDirectory);
            Assert.Equal(File.GetUnixFileMode(root), dir.Permissions);

            Assert.True(PosixStat.TryLStat(file, out var reg));
            Assert.True(reg.IsRegularFile);
            Assert.Equal(3L, reg.Size);
            Assert.Equal(1UL, reg.LinkCount);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, reg.Permissions);

            Assert.True(PosixStat.TryLStat(link, out var lnk));
            Assert.True(lnk.IsSymbolicLink);
            Assert.True(PosixStat.TryStat(link, out var target));
            Assert.Equal(reg.Inode, target.Inode);
            Assert.Equal(reg.Device, target.Device);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void LStat_MissingPath_IsFalse()
    {
        Skip.If(OperatingSystem.IsWindows(), "libc stat is Unix-only");
        Assert.False(PosixStat.TryLStat(Path.Combine(Path.GetTempPath(), "ps-bash", "no-such-" + Guid.NewGuid().ToString("N")), out _));
    }

    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void FStat_PipeIsFifo_ClosedFdIsFalse()
    {
        Skip.If(OperatingSystem.IsWindows(), "libc stat is Unix-only");
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        int fd = (int)pipe.SafePipeHandle.DangerousGetHandle();
        Assert.True(PosixStat.TryFStat(fd, out var st));
        Assert.True(st.IsFifo);
        Assert.False(PosixStat.TryFStat(99999, out _));
    }

    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Windows_EveryCallAnswersCannotTell()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the live Unix answers are pinned above");
        Assert.Equal(StatLayout.Unsupported, PosixStat.Current);
        Assert.False(PosixStat.TryLStat(Path.GetTempPath(), out _));
        Assert.False(PosixStat.TryStat(Path.GetTempPath(), out _));
        Assert.False(PosixStat.TryFStat(0, out _));
    }
}
