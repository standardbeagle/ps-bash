using System.Buffers.Binary;

namespace PsBash.Core.Platform;

/// <summary>
/// Where the fields of <c>struct termios</c> sit, for code that reads one filled by <c>tcgetattr</c>. Product code
/// treats termios as opaque (it only saves and restores the bytes) and needs just <see cref="BufferSize"/>; tests read
/// <c>c_lflag</c> to check canonical mode / echo.
/// <list type="bullet">
/// <item>Linux (glibc, musl): <c>tcflag_t</c> = <c>unsigned int</c> (4 bytes). <c>c_iflag</c>@0, <c>c_oflag</c>@4,
/// <c>c_cflag</c>@8, <c>c_lflag</c>@12, <c>c_line</c>@16, <c>c_cc[32]</c>@17, speeds @52/@56 — 60 bytes.</item>
/// <item>macOS: <c>tcflag_t</c> = <c>unsigned long</c> (8 bytes on 64-bit). <c>c_iflag</c>@0, <c>c_oflag</c>@8,
/// <c>c_cflag</c>@16, <c>c_lflag</c>@24, <c>c_cc[20]</c>@32, <c>speed_t</c> (unsigned long) speeds @56/@64 — 72 bytes.</item>
/// </list>
/// </summary>
public static class TermiosLayout
{
    /// <summary>A buffer comfortably larger than any supported <c>struct termios</c> (Linux 60, macOS 72 bytes).</summary>
    public const int BufferSize = 256;

    /// <summary>Byte width of <c>tcflag_t</c>: 8 on macOS (<c>unsigned long</c>), 4 on Linux (<c>unsigned int</c>).</summary>
    public static int FlagWidth(bool macOS) => macOS ? 8 : 4;

    /// <summary>Byte offset of <c>c_lflag</c>: the fourth <c>tcflag_t</c>.</summary>
    public static int LflagOffset(bool macOS) => 3 * FlagWidth(macOS);

    /// <summary><c>ICANON</c>: Linux 0x2, macOS 0x100.</summary>
    public static ulong Icanon(bool macOS) => macOS ? 0x100UL : 0x2UL;

    /// <summary><c>ECHO</c>: 0x8 on both.</summary>
    public const ulong Echo = 0x8;

    /// <summary>Reads <c>c_lflag</c> from a termios buffer laid out for Linux or macOS (native little-endian).</summary>
    public static ulong ReadLflag(ReadOnlySpan<byte> termios, bool macOS)
    {
        int at = LflagOffset(macOS);
        return macOS
            ? BinaryPrimitives.ReadUInt64LittleEndian(termios[at..])
            : BinaryPrimitives.ReadUInt32LittleEndian(termios[at..]);
    }

    /// <summary>Whether the running OS uses the macOS layout.</summary>
    public static bool CurrentIsMacOS => OperatingSystem.IsMacOS();
}
