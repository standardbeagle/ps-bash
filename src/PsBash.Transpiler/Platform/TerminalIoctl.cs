using System.Runtime.InteropServices;

namespace PsBash.Core.Platform;

/// <summary>
/// The ONLY declaration of <c>ioctl(2)</c> in ps-bash (guard <c>PlatformProbeGuardTests</c>): read and set a terminal's
/// window size (<c>TIOCGWINSZ</c> / <c>TIOCSWINSZ</c>). POSIX only.
/// <para>
/// <b>Why one helper.</b> <c>ioctl</c> is variadic — <c>int ioctl(int fd, unsigned long request, ...)</c> — and a
/// P/Invoke declaration is always a fixed-argument call. On Linux (x86-64 SysV and standard AAPCS64) and on macOS
/// x86-64, a variadic integer/pointer argument travels in the same register as a named one would, so the fixed
/// three-argument form reaches the callee correctly. Apple arm64 differs: every variadic argument is passed on the
/// STACK (8-byte slots from the caller's <c>sp</c>), while the fixed form puts the pointer in <c>x2</c>, which the
/// callee's <c>va_arg</c> never reads — it reads whatever is at <c>[sp]</c>. So on osx-arm64 the call is declared
/// with six dummy <c>nint</c> arguments: the named <c>fd</c> and <c>request</c> plus six dummies fill <c>x0</c>–<c>x7</c>,
/// and the pointer, as the ninth integer argument of a fixed call, goes to <c>[sp+0]</c> — exactly the slot the
/// callee's first <c>va_arg</c> reads. The dummies are never looked at. (The same padding on Linux arm64 would be
/// WRONG — its callee reads <c>x2</c> — hence the runtime switch.)
/// </para>
/// </summary>
public static partial class TerminalIoctl
{
    /// <summary><c>TIOCGWINSZ</c>: Linux 0x5413; macOS/BSD 0x40087468 (<c>_IOR('t', 104, struct winsize)</c>).</summary>
    public static ulong TIOCGWINSZ => OperatingSystem.IsLinux() ? 0x5413UL : 0x40087468UL;

    /// <summary><c>TIOCSWINSZ</c>: Linux 0x5414; macOS/BSD 0x80087467 (<c>_IOW('t', 103, struct winsize)</c>).</summary>
    public static ulong TIOCSWINSZ => OperatingSystem.IsLinux() ? 0x5414UL : 0x80087467UL;

    /// <summary>True when variadic arguments must be passed on the stack (Apple arm64).</summary>
    public static bool VariadicArgsOnStack =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary><c>struct winsize</c>: four <c>unsigned short</c>s, identical on Linux and macOS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Winsize
    {
        /// <summary>Rows.</summary>
        public ushort ws_row;
        /// <summary>Columns.</summary>
        public ushort ws_col;
        /// <summary>Width in pixels (unused).</summary>
        public ushort ws_xpixel;
        /// <summary>Height in pixels (unused).</summary>
        public ushort ws_ypixel;
    }

    /// <summary><c>ioctl(fd, TIOCGWINSZ)</c>. False (zeroed outputs, <paramref name="errno"/> set) when fd is not a tty.</summary>
    public static bool TryGetWindowSize(int fd, out ushort cols, out ushort rows, out int errno)
    {
        var ws = default(Winsize);
        if (Call(fd, TIOCGWINSZ, ref ws, out errno) != 0)
        {
            cols = 0; rows = 0;
            return false;
        }
        cols = ws.ws_col;
        rows = ws.ws_row;
        return true;
    }

    /// <summary><c>ioctl(fd, TIOCSWINSZ)</c>. False with <paramref name="errno"/> set on failure.</summary>
    public static bool TrySetWindowSize(int fd, ushort cols, ushort rows, out int errno)
    {
        var ws = new Winsize { ws_row = rows, ws_col = cols };
        return Call(fd, TIOCSWINSZ, ref ws, out errno) == 0;
    }

    private static int Call(int fd, ulong request, ref Winsize ws, out int errno)
    {
        int rc = VariadicArgsOnStack
            ? ioctl_variadic_on_stack(fd, request, 0, 0, 0, 0, 0, 0, ref ws)
            : ioctl_variadic_in_registers(fd, request, ref ws);
        errno = rc == 0 ? 0 : Marshal.GetLastPInvokeError();
        return rc;
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl_variadic_in_registers(int fd, ulong request, ref Winsize ws);

    // Apple arm64: x0 = fd, x1 = request, x2..x7 = padding, ninth argument (the pointer) at [sp+0] = first va_arg.
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl_variadic_on_stack(int fd, ulong request,
        nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7, ref Winsize ws);
}
