using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PsBash.Core.Platform;

/// <summary>The C <c>struct stat</c> shape of one OS/architecture: the libc ABI <see cref="PosixStat"/> decodes.</summary>
public enum StatLayout
{
    /// <summary>No layout is known for this OS/architecture: every stat call answers "cannot tell".</summary>
    Unsupported,

    /// <summary>Linux x86_64 (glibc and musl, <c>asm/stat.h</c>): <c>st_nlink</c> is a 64-bit word ahead of
    /// <c>st_mode</c>. 144 bytes.</summary>
    LinuxX64,

    /// <summary>Linux on an <c>asm-generic/stat.h</c> architecture: arm64 (glibc and musl). Also riscv64 and
    /// loongarch64, which ps-bash does not ship. <c>st_mode</c> and a 32-bit <c>st_nlink</c> right after <c>st_ino</c>. 128 bytes.</summary>
    LinuxGeneric64,

    /// <summary>64-bit Darwin, the 64-bit-inode <c>struct stat</c> (<c>__DARWIN_STRUCT_STAT64</c>): a 32-bit
    /// <c>st_dev</c>, 16-bit <c>st_mode</c> and <c>st_nlink</c>, then <c>st_ino</c>. 144 bytes.</summary>
    Darwin64,
}

/// <summary>The fields of a <c>struct stat</c> ps-bash reads. <see cref="Mode"/> is the raw <c>st_mode</c>
/// (file-type bits included).</summary>
public readonly record struct PosixStatInfo(ulong Device, ulong Inode, uint Mode, ulong LinkCount, uint Uid, uint Gid, long Size)
{
    private const uint S_IFMT = 0xF000;

    /// <summary>The low 12 mode bits (permissions, setuid/setgid, sticky).</summary>
    public UnixFileMode Permissions => (UnixFileMode)(Mode & 0xFFF);

    /// <summary><c>S_ISDIR</c>.</summary>
    public bool IsDirectory => (Mode & S_IFMT) == 0x4000;

    /// <summary><c>S_ISREG</c>.</summary>
    public bool IsRegularFile => (Mode & S_IFMT) == 0x8000;

    /// <summary><c>S_ISLNK</c> (only an lstat can see one).</summary>
    public bool IsSymbolicLink => (Mode & S_IFMT) == 0xA000;

    /// <summary><c>S_ISFIFO</c>: a pipe or named FIFO.</summary>
    public bool IsFifo => (Mode & S_IFMT) == 0x1000;
}

/// <summary>
/// The ONE place ps-bash calls libc <c>stat</c>/<c>lstat</c>/<c>fstat</c> and knows the <c>struct stat</c> layout of
/// each OS/architecture (spec <c>docs/specs/README.md</c> §Platform dependencies; guard
/// <c>PlatformDependencyGuardTests</c>). The kernel writes into a byte buffer that <see cref="TryDecode"/> reads at
/// the offsets of <see cref="Current"/>, so every layout is testable on any OS with a hand-built buffer.
/// <para>
/// REGRESSION: two copies of a Linux <c>struct stat</c> declared the x86_64 layout and called it "x86_64 / arm64".
/// On arm64 <c>st_mode</c> sits at offset 16, where x86_64 has <c>st_nlink</c>: the mode read was the uid, so a
/// runtime directory's owner check and the host's inherited-pipe detection decided on garbage.
/// </para>
/// </summary>
public static unsafe class PosixStat
{
    /// <summary>Bytes handed to the kernel: more than the largest layout (144).</summary>
    private const int BufferSize = 256;

    /// <summary>The layout of the running process.</summary>
    public static StatLayout Current { get; } = LayoutFor(
        OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture, BitConverter.IsLittleEndian);

    /// <summary>Which <c>struct stat</c> an OS/architecture uses. Every layout is little-endian as decoded; a
    /// big-endian process gets <see cref="StatLayout.Unsupported"/> rather than byte-swapped fields.</summary>
    internal static StatLayout LayoutFor(bool linux, bool macOS, Architecture arch, bool littleEndian)
    {
        if (!littleEndian) return StatLayout.Unsupported;
        if (macOS) return arch is Architecture.Arm64 or Architecture.X64 ? StatLayout.Darwin64 : StatLayout.Unsupported;
        if (!linux) return StatLayout.Unsupported;
        return arch switch
        {
            Architecture.X64 => StatLayout.LinuxX64,
            Architecture.Arm64 => StatLayout.LinuxGeneric64,
            _ => StatLayout.Unsupported,
        };
    }

    /// <summary>The size of the <c>struct stat</c> <paramref name="layout"/> describes; 0 when unsupported.</summary>
    public static int SizeOf(StatLayout layout) => layout switch
    {
        StatLayout.LinuxX64 => 144,
        StatLayout.LinuxGeneric64 => 128,
        StatLayout.Darwin64 => 144,
        _ => 0,
    };

    /// <summary>Reads the fields of a <c>struct stat</c> laid out as <paramref name="layout"/>. False when the layout
    /// is unsupported or the buffer is shorter than the struct.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, StatLayout layout, out PosixStatInfo info)
    {
        info = default;
        var size = SizeOf(layout);
        if (size == 0 || buffer.Length < size) return false;
        switch (layout)
        {
            case StatLayout.LinuxX64:
                // st_dev u64 @0, st_ino u64 @8, st_nlink u64 @16, st_mode u32 @24, st_uid @28, st_gid @32,
                // __pad0 @36, st_rdev @40, st_size i64 @48.
                info = new PosixStatInfo(
                    Device: U64(buffer, 0), Inode: U64(buffer, 8), Mode: U32(buffer, 24), LinkCount: U64(buffer, 16),
                    Uid: U32(buffer, 28), Gid: U32(buffer, 32), Size: I64(buffer, 48));
                return true;
            case StatLayout.LinuxGeneric64:
                // st_dev u64 @0, st_ino u64 @8, st_mode u32 @16, st_nlink u32 @20, st_uid @24, st_gid @28,
                // st_rdev @32, __pad1 @40, st_size i64 @48.
                info = new PosixStatInfo(
                    Device: U64(buffer, 0), Inode: U64(buffer, 8), Mode: U32(buffer, 16), LinkCount: U32(buffer, 20),
                    Uid: U32(buffer, 24), Gid: U32(buffer, 28), Size: I64(buffer, 48));
                return true;
            case StatLayout.Darwin64:
                // st_dev i32 @0, st_mode u16 @4, st_nlink u16 @6, st_ino u64 @8, st_uid @16, st_gid @20,
                // st_rdev @24, four timespecs @32..95, st_size i64 @96.
                info = new PosixStatInfo(
                    Device: U32(buffer, 0), Inode: U64(buffer, 8), Mode: U16(buffer, 4), LinkCount: U16(buffer, 6),
                    Uid: U32(buffer, 16), Gid: U32(buffer, 20), Size: I64(buffer, 96));
                return true;
            default:
                return false;
        }
    }

    /// <summary><c>stat(2)</c>: follows a symbolic link. False when the path cannot be inspected (errno via
    /// <see cref="Marshal.GetLastPInvokeError"/>), on Windows, and on an unsupported layout.</summary>
    public static bool TryStat(string path, out PosixStatInfo info) => Call(Op.Stat, path, 0, out info);

    /// <summary><c>lstat(2)</c>: a symbolic link is judged by itself, never by its target.</summary>
    public static bool TryLStat(string path, out PosixStatInfo info) => Call(Op.LStat, path, 0, out info);

    /// <summary><c>fstat(2)</c> of an open descriptor. False for a closed or invalid fd (EBADF).</summary>
    public static bool TryFStat(int fd, out PosixStatInfo info) => Call(Op.FStat, null, fd, out info);

    private enum Op { Stat, LStat, FStat }

    private static bool Call(Op op, string? path, int fd, out PosixStatInfo info)
    {
        info = default;
        var layout = Current;
        if (layout == StatLayout.Unsupported) return false;
        byte* buf = stackalloc byte[BufferSize];
        new Span<byte>(buf, BufferSize).Clear();
        int rc;
        try
        {
            // macOS x86_64 exports the 64-bit-inode struct under the $INODE64 symbols (plain `stat` there is the
            // legacy 32-bit-inode struct, another layout); arm64 has only the 64-bit-inode one, under the plain name.
            bool inode64 = layout == StatLayout.Darwin64 && RuntimeInformation.ProcessArchitecture == Architecture.X64;
            rc = (op, inode64) switch
            {
                (Op.Stat, false) => stat(path!, buf),
                (Op.Stat, true) => stat_inode64(path!, buf),
                (Op.LStat, false) => lstat(path!, buf),
                (Op.LStat, true) => lstat_inode64(path!, buf),
                (_, false) => fstat(fd, buf),
                (_, true) => fstat_inode64(fd, buf),
            };
        }
        catch (EntryPointNotFoundException) when (layout is StatLayout.LinuxX64 or StatLayout.LinuxGeneric64)
        {
            // glibc before 2.33 (Ubuntu 20.04, Debian 11, RHEL 8) has no stat/lstat/fstat symbols: they were inline
            // wrappers over __xstat(version, ...). Every glibc since still exports those.
            try { rc = XStat(op, path, fd, XStatVersion(layout), buf); }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return false; }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // An unforeseen libc: answer "cannot tell", never throw.
            return false;
        }
        return rc == 0 && TryDecode(new ReadOnlySpan<byte>(buf, BufferSize), layout, out info);
    }

    /// <summary>The <c>_STAT_VER</c> glibc's own headers passed to <c>__xstat</c> for <paramref name="layout"/>:
    /// <c>_STAT_VER_LINUX</c> = 1 on x86_64, <c>_STAT_VER_KERNEL</c> = 0 on the asm-generic architectures (any other
    /// value is EINVAL there).</summary>
    internal static int XStatVersion(StatLayout layout) => layout == StatLayout.LinuxX64 ? 1 : 0;

    private static int XStat(Op op, string? path, int fd, int version, byte* buf) => op switch
    {
        Op.Stat => xstat(version, path!, buf),
        Op.LStat => lxstat(version, path!, buf),
        _ => fxstat(version, fd, buf),
    };

    private static uint U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    private static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);
    private static long I64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt64LittleEndian(b[at..]);

    [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
    private static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "stat$INODE64")]
    private static extern int stat_inode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat$INODE64")]
    private static extern int lstat_inode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
    private static extern int fstat(int fd, byte* buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "fstat$INODE64")]
    private static extern int fstat_inode64(int fd, byte* buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "__xstat")]
    private static extern int xstat(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "__lxstat")]
    private static extern int lxstat(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "__fxstat")]
    private static extern int fxstat(int version, int fd, byte* buf);
}
