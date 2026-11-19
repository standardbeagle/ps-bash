using System.Runtime.InteropServices;

namespace PsBash.Core.Platform;

/// <summary>
/// The host kernel's <c>uname(2)</c> answer on Linux and macOS: what coreutils <c>uname</c> prints. Windows has no
/// such call; there ps-bash keeps its MSYS-style identity (<c>MINGW64_NT-…</c>), which is what scripts written for Git
/// Bash test for. REGRESSION: <c>uname</c> printed <c>MINGW64_NT-6.8.0</c> on a Linux host, so <c>case $(uname) in
/// Linux)</c> took the Windows branch.
/// <para>
/// <c>struct utsname</c> is five or six fixed char fields: Linux (glibc, musl) <c>sysname nodename release version
/// machine domainname</c> of 65 bytes each, Darwin <c>sysname nodename release version machine</c> of 256 bytes
/// (<c>_SYS_NAMELEN</c>). <see cref="Decode"/> is pure so both layouts are tested on any OS.
/// Spec: <c>docs/specs/platform-dependencies.md</c>.
/// </para>
/// </summary>
public static unsafe class KernelIdentity
{
    /// <summary>The <c>uname(2)</c> fields coreutils prints (<c>-s -n -r -v -m</c>).</summary>
    public readonly record struct Uts(string SysName, string NodeName, string Release, string Version, string Machine);

    /// <summary>Bytes per <c>struct utsname</c> field on Linux (<c>_UTSNAME_LENGTH</c>).</summary>
    public const int LinuxFieldLength = 65;

    /// <summary>Bytes per <c>struct utsname</c> field on Darwin (<c>_SYS_NAMELEN</c>).</summary>
    public const int DarwinFieldLength = 256;

    /// <summary>The field length of this OS's <c>struct utsname</c>.</summary>
    public static int FieldLength => OperatingSystem.IsMacOS() ? DarwinFieldLength : LinuxFieldLength;

    /// <summary>
    /// The five fields from a <c>struct utsname</c> buffer whose fields are <paramref name="fieldLength"/> bytes,
    /// each NUL-terminated (bytes kept exactly: <see cref="RawBytes"/>). Null when the buffer is shorter than five fields.
    /// </summary>
    public static Uts? Decode(ReadOnlySpan<byte> buffer, int fieldLength)
    {
        if (fieldLength <= 0 || buffer.Length < 5 * fieldLength) return null;
        return new Uts(Field(buffer, 0, fieldLength), Field(buffer, 1, fieldLength), Field(buffer, 2, fieldLength),
            Field(buffer, 3, fieldLength), Field(buffer, 4, fieldLength));
    }

    private static string Field(ReadOnlySpan<byte> buffer, int k, int fieldLength)
    {
        var f = buffer.Slice(k * fieldLength, fieldLength);
        int nul = f.IndexOf((byte)0);
        return RawBytes.GetString(nul < 0 ? f : f[..nul]);
    }

    /// <summary>This host's kernel identity, or null on Windows (or when the call fails).</summary>
    public static Uts? Host()
    {
        if (OperatingSystem.IsWindows()) return null;
        // Large enough for either layout (6 x 65 = 390, 5 x 256 = 1280).
        Span<byte> buf = stackalloc byte[6 * DarwinFieldLength];
        buf.Clear();
        fixed (byte* p = buf)
        {
            if (uname(p) != 0) return null;
        }
        return Decode(buf, FieldLength);
    }

    [DllImport("libc", EntryPoint = "uname")]
    private static extern int uname(byte* buf);
}
