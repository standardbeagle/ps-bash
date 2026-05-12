using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PsBash.Shell.Pty;

/// <summary>
/// PTY-3: launcher-side tty mode toggle.
///
/// <para>The launcher (ps-bash) is the user's terminal. When the launcher
/// spawns the host under a real pseudo-terminal (PTY-2's
/// <c>RunHostUnderPtyAsync</c>), it then pumps bytes between its own stdio
/// and the PTY master. For TUI apps (vim, less, fzf) to see keystrokes as
/// they happen, the launcher's stdin must be in <b>raw mode</b> — otherwise
/// the kernel line-buffers each byte until Enter is pressed.</para>
///
/// <para>This class implements the save-set-restore lifecycle as an
/// <see cref="IDisposable"/> scope. The launcher enters raw mode for the
/// duration of the host child's lifetime, then restores the user's
/// original terminal state on exit. A best-effort
/// <see cref="AppDomain.ProcessExit"/> hook guarantees restore even if the
/// launcher crashes between enter and dispose — a stuck terminal is the
/// canonical "ps-bash ate my shell" complaint and must not happen.</para>
///
/// <para><b>POSIX path:</b> <c>tcgetattr</c> snapshots the current termios,
/// <c>cfmakeraw</c> on a working copy, <c>tcsetattr(TCSANOW)</c> applies
/// it, and dispose calls <c>tcsetattr(TCSANOW)</c> with the saved snapshot.
/// Non-tty fds yield an <see cref="Scope.IsActive"/>=<c>false</c> scope
/// (no syscalls, no state to restore).</para>
///
/// <para><b>Windows path:</b> <c>GetConsoleMode</c> snapshots stdin/stdout
/// modes, <see cref="ComputeRawInputMode"/> and
/// <see cref="ComputeRawOutputMode"/> derive the raw bits, and
/// <c>SetConsoleMode</c> applies them. Dispose restores. The Windows
/// minimum build (10.0.17763 / 1809) gate lives in
/// <see cref="ConPtyAdapter"/> — <see cref="TerminalMode"/> assumes the
/// PTY allocation already succeeded and the host is running.</para>
/// </summary>
internal static partial class TerminalMode
{
    /// <summary>
    /// Enter raw mode against the launcher's standard input handle (and
    /// stdout on Windows, where the VT-output bits also matter). Returns
    /// a disposable scope that restores the saved state on
    /// <see cref="IDisposable.Dispose"/>. If stdin is not a terminal (e.g.
    /// the launcher is itself driven by a pipe), the scope reports
    /// <see cref="Scope.IsActive"/>=<c>false</c> and dispose is a no-op.
    /// </summary>
    public static Scope EnterRawIfTty()
    {
        if (OperatingSystem.IsWindows())
            return EnterRawWindows();

        // POSIX: stdin is fd 0. If it isn't a tty (e.g. xunit testhost,
        // <c>ps-bash &lt; /dev/null</c>), EnterRawForFd returns an inactive
        // scope without complaining.
        return EnterRawForFd(0);
    }

    /// <summary>
    /// POSIX-only entry point used by tests and by
    /// <see cref="EnterRawIfTty"/>. Caller provides the fd to put in raw
    /// mode (e.g. stdin=0, or a PTY slave fd in a test harness).
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static Scope EnterRawForFd(int fd)
    {
        var saved = new byte[PosixNative.TermiosBufSize];
        unsafe
        {
            fixed (byte* p = saved)
            {
                // tcgetattr returns -1 / errno=ENOTTY if fd is not a tty.
                // Treat any non-zero return as "not a tty, no-op".
                if (PosixNative.tcgetattr(fd, (IntPtr)p) != 0)
                    return Scope.Inactive;
            }
        }

        var working = new byte[PosixNative.TermiosBufSize];
        Array.Copy(saved, working, PosixNative.TermiosBufSize);
        unsafe
        {
            fixed (byte* p = working)
            {
                PosixNative.cfmakeraw((IntPtr)p);
                if (PosixNative.tcsetattr(fd, PosixNative.TCSANOW, (IntPtr)p) != 0)
                {
                    // Couldn't apply — surface as inactive rather than throw.
                    // The launcher will still function; TUI apps will be
                    // line-buffered but ps-bash isn't broken.
                    return Scope.Inactive;
                }
            }
        }

        return Scope.Active(new PosixRestorer(fd, saved));
    }

    /// <summary>
    /// Windows: derive the "raw stdin" console-input mode from a current
    /// mode value. Clears <c>ENABLE_LINE_INPUT</c>, <c>ENABLE_ECHO_INPUT</c>,
    /// <c>ENABLE_PROCESSED_INPUT</c>, and sets
    /// <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> so keystrokes arrive as VT
    /// sequences. Other bits (window / mouse) are preserved.
    /// </summary>
    public static uint ComputeRawInputMode(uint current)
    {
        const uint ENABLE_PROCESSED_INPUT = 0x0001;
        const uint ENABLE_LINE_INPUT = 0x0002;
        const uint ENABLE_ECHO_INPUT = 0x0004;
        const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

        uint raw = current & ~(ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT);
        raw |= ENABLE_VIRTUAL_TERMINAL_INPUT;
        return raw;
    }

    /// <summary>
    /// Windows: derive the "raw stdout" console-output mode from a current
    /// mode value. Adds <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> (so the
    /// host can write VT escape sequences) and
    /// <c>DISABLE_NEWLINE_AUTO_RETURN</c> (so LF stays LF without an
    /// auto-CR that flickers TUI cursor movement).
    /// <c>ENABLE_PROCESSED_OUTPUT</c> is preserved if already set.
    /// </summary>
    public static uint ComputeRawOutputMode(uint current)
    {
        const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
        const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;
        return current | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN;
    }

    [SupportedOSPlatform("windows")]
    private static Scope EnterRawWindows()
    {
        const int STD_INPUT_HANDLE = -10;
        const int STD_OUTPUT_HANDLE = -11;

        IntPtr hIn = WindowsNative.GetStdHandle(STD_INPUT_HANDLE);
        IntPtr hOut = WindowsNative.GetStdHandle(STD_OUTPUT_HANDLE);

        if (!WindowsNative.GetConsoleMode(hIn, out uint savedIn))
            return Scope.Inactive;

        uint savedOut = 0;
        bool hasOut = WindowsNative.GetConsoleMode(hOut, out savedOut);

        uint rawIn = ComputeRawInputMode(savedIn);
        if (!WindowsNative.SetConsoleMode(hIn, rawIn))
            return Scope.Inactive;

        if (hasOut)
        {
            uint rawOut = ComputeRawOutputMode(savedOut);
            // Best-effort: if stdout doesn't accept the new mode, leave it
            // alone but still apply the stdin change. Lots of redirected-
            // stdout scenarios will fail here cleanly.
            WindowsNative.SetConsoleMode(hOut, rawOut);
        }

        return Scope.Active(new WindowsRestorer(hIn, savedIn, hOut, savedOut, hasOut));
    }

    /// <summary>
    /// Disposable scope returned by <see cref="EnterRawIfTty"/> and
    /// <see cref="EnterRawForFd"/>. <see cref="IsActive"/> reports whether
    /// the underlying call actually toggled any state; an inactive scope
    /// has no syscalls to make on dispose.
    /// </summary>
    public sealed class Scope : IDisposable
    {
        private IModeRestorer? _restorer;

        public static Scope Inactive { get; } = new Scope(null);
        public static Scope Active(IModeRestorer restorer) => new Scope(restorer);

        private Scope(IModeRestorer? restorer)
        {
            _restorer = restorer;
            if (restorer is not null)
            {
                // Belt-and-suspenders: if the launcher crashes between
                // EnterRawIfTty() and Dispose(), the ProcessExit hook
                // restores the terminal so the user does not lose echo /
                // line input in their parent shell.
                ProcessExitGuard.Register(this);
            }
        }

        public bool IsActive => _restorer is not null;

        public void Dispose()
        {
            // Interlocked so the ProcessExit hook + a normal dispose can
            // both run safely.
            var r = System.Threading.Interlocked.Exchange(ref _restorer, null);
            if (r is null) return;
            try { r.Restore(); }
            catch { /* best-effort */ }
            ProcessExitGuard.Unregister(this);
        }
    }

    internal interface IModeRestorer
    {
        void Restore();
    }

    private sealed class PosixRestorer : IModeRestorer
    {
        private readonly int _fd;
        private readonly byte[] _saved;

        public PosixRestorer(int fd, byte[] saved)
        {
            _fd = fd;
            _saved = saved;
        }

        public void Restore()
        {
            unsafe
            {
                fixed (byte* p = _saved)
                {
                    PosixNative.tcsetattr(_fd, PosixNative.TCSANOW, (IntPtr)p);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsRestorer : IModeRestorer
    {
        private readonly IntPtr _hIn;
        private readonly uint _savedIn;
        private readonly IntPtr _hOut;
        private readonly uint _savedOut;
        private readonly bool _hasOut;

        public WindowsRestorer(IntPtr hIn, uint savedIn, IntPtr hOut, uint savedOut, bool hasOut)
        {
            _hIn = hIn; _savedIn = savedIn;
            _hOut = hOut; _savedOut = savedOut; _hasOut = hasOut;
        }

        public void Restore()
        {
            WindowsNative.SetConsoleMode(_hIn, _savedIn);
            if (_hasOut)
                WindowsNative.SetConsoleMode(_hOut, _savedOut);
        }
    }

    /// <summary>
    /// AppDomain.ProcessExit hook that restores any scope still alive at
    /// process termination. Without this, a launcher crash mid-host leaves
    /// the user's terminal in raw mode and they have to <c>stty sane</c>
    /// to recover.
    /// </summary>
    private static class ProcessExitGuard
    {
        private static readonly object _gate = new();
        private static readonly HashSet<Scope> _live = new();
        private static int _hooked;

        public static void Register(Scope s)
        {
            lock (_gate) _live.Add(s);
            if (System.Threading.Interlocked.Exchange(ref _hooked, 1) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
                AppDomain.CurrentDomain.UnhandledException += (_, _) => DisposeAll();
            }
        }

        public static void Unregister(Scope s)
        {
            lock (_gate) _live.Remove(s);
        }

        private static void DisposeAll()
        {
            Scope[] snapshot;
            lock (_gate)
            {
                snapshot = new Scope[_live.Count];
                _live.CopyTo(snapshot);
            }
            foreach (var s in snapshot)
            {
                try { s.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // POSIX libc
    // ------------------------------------------------------------------

    private static partial class PosixNative
    {
        // termios is opaque to us; the 256-byte buffer is comfortably larger
        // than glibc's 60 bytes, musl's 60 bytes, and macOS's 44 bytes.
        public const int TermiosBufSize = 256;

        // tcsetattr "when" arg: apply changes immediately.
        public const int TCSANOW = 0;

        [LibraryImport("libc", SetLastError = true)]
        public static partial int tcgetattr(int fd, IntPtr termios);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int tcsetattr(int fd, int optionalActions, IntPtr termios);

        [LibraryImport("libc")]
        public static partial void cfmakeraw(IntPtr termios);
    }

    // ------------------------------------------------------------------
    // Windows kernel32
    // ------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static partial class WindowsNative
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial IntPtr GetStdHandle(int nStdHandle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}
