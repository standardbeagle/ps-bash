// CA1416: TerminalMode.EnterRawForFd is [UnsupportedOSPlatform("windows")];
// each test call site is gated by Skip.If(IsWindows) so the analyzer warning
// is a false positive in this assembly. Suppressed file-wide because all
// EnterRawForFd usages here are POSIX-only tests.
#pragma warning disable CA1416

using System.Runtime.InteropServices;
using PsBash.Shell.Pty;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// PTY-3 acceptance tests for <see cref="TerminalMode"/>: the launcher-side
/// terminal mode toggle that runs around <c>RunHostUnderPtyAsync</c>.
///
/// <para><b>Surface under test:</b> <c>TerminalMode.EnterRawIfTty()</c>
/// returns an <c>IDisposable</c> that on POSIX clears canonical mode + echo
/// via <c>tcsetattr</c>/<c>cfmakeraw</c>, and on Windows clears
/// <c>ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT</c> on
/// stdin while enabling <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> there and
/// <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> on stdout. Disposing restores
/// the original state.</para>
///
/// <para><b>Why this is the launcher's job (not the host's REPL):</b> with
/// PSBASH_PTY=1, the launcher spawns the host under a real pseudo-terminal
/// and pumps bytes between its own stdio and the PTY master. If the
/// launcher's own stdin sits in cooked mode, the kernel line-buffers
/// keystrokes until Enter — so single keys never reach the host until the
/// user hits return. Raw mode on the launcher side is therefore the price
/// of admission for TUI apps (vim, less, fzf) to see their input.</para>
///
/// <para><b>Test invocation</b> (Linux): <c>./scripts/test.sh --filter
/// "FullyQualifiedName~TerminalModeTests"</c>. POSIX tests run; Windows
/// test is <c>Skip.IfNot</c>-gated and runs only on Windows CI.</para>
/// </summary>
public class TerminalModeTests
{
    // ------------------------------------------------------------------
    // POSIX
    // ------------------------------------------------------------------

    /// <summary>
    /// POSIX: when stdin is not a tty (the typical xunit testhost case —
    /// stdin is closed or a pipe), <see cref="TerminalMode.EnterRawIfTty"/>
    /// must return a non-null disposable whose state reports
    /// <c>IsActive=false</c>, take no syscalls, and dispose cleanly. This
    /// is QA rubric Directive 7: the no-tty negative path is the common
    /// case in CI / under <c>ps-bash &lt; /dev/null</c>.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public void Posix_EnterRawIfTty_NoTty_ReturnsInactiveScope()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        using var scope = TerminalMode.EnterRawIfTty();

        Assert.NotNull(scope);
        Assert.False(scope.IsActive,
            "Under xunit/testhost stdin is not a tty; EnterRawIfTty must no-op.");
    }

    /// <summary>
    /// POSIX: opening a fresh PTY pair and pointing <see cref="TerminalMode"/>
    /// at the slave fd via <see cref="TerminalMode.EnterRawForFd"/> must
    /// clear the canonical-mode and echo bits. We then re-read the termios
    /// state via <c>tcgetattr</c> and assert ICANON / ECHO are off.
    ///
    /// <para>This is the load-bearing assertion: pump latency for TUI apps
    /// depends entirely on these two flags being off. If ICANON stays set,
    /// the launcher's kernel line-buffers keystrokes and vim never sees an
    /// 'h' until Enter.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_EnterRawForFd_OnPtySlave_ClearsCanonicalAndEcho()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        // Allocate a PTY and use its slave fd as the target. We probe the fd
        // directly rather than the standard input handle so the test does
        // not depend on testhost stdio.
        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        int slaveFd = pty.SlaveFileDescriptor;
        Assert.True(slaveFd > 0, "PTY allocation should populate the slave fd on POSIX");

        // Sanity: ICANON should be set before EnterRawForFd flips it.
        Assert.True(TerminalModeProbe.IsCanonical(slaveFd),
            "Fresh PTY slaves are in canonical mode by default; if this trips, the kernel changed semantics or the slave fd is wrong.");

        using (var scope = TerminalMode.EnterRawForFd(slaveFd))
        {
            Assert.True(scope.IsActive,
                "EnterRawForFd against a real tty fd must flip IsActive=true");

            Assert.False(TerminalModeProbe.IsCanonical(slaveFd),
                "EnterRaw must clear ICANON via cfmakeraw");
            Assert.False(TerminalModeProbe.HasEcho(slaveFd),
                "EnterRaw must clear ECHO via cfmakeraw");
        }

        // After scope dispose, original (cooked + echo) state is restored.
        Assert.True(TerminalModeProbe.IsCanonical(slaveFd),
            "Dispose must restore canonical mode (ICANON=on)");
        Assert.True(TerminalModeProbe.HasEcho(slaveFd),
            "Dispose must restore echo (ECHO=on)");
    }

    /// <summary>
    /// POSIX: double-dispose is a no-op and never throws. The launcher
    /// pumps live inside try/finally and a ProcessExit handler also
    /// disposes the scope as a belt-and-suspenders against crash paths;
    /// both calls must be safe.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_EnterRawForFd_DoubleDispose_IsSafe()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        var scope = TerminalMode.EnterRawForFd(pty.SlaveFileDescriptor);
        Assert.True(scope.IsActive);
        scope.Dispose();
        scope.Dispose(); // must not throw
    }

    /// <summary>
    /// POSIX: handing a bogus fd (closed / nonsense) to
    /// <see cref="TerminalMode.EnterRawForFd"/> must return an inactive
    /// scope rather than throwing — the launcher's mode toggle should be
    /// best-effort and fall back silently when the kernel says "not a
    /// terminal". QA rubric Directive 7 (negative cases) + Directive 14
    /// (missing target).
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public void Posix_EnterRawForFd_OnNonTtyFd_ReturnsInactive()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        // fd 9999 is virtually guaranteed to be closed in xunit testhost.
        using var scope = TerminalMode.EnterRawForFd(9999);
        Assert.False(scope.IsActive,
            "Closed / non-tty fd must yield an inactive scope, not an exception");
    }

    // ------------------------------------------------------------------
    // Windows
    // ------------------------------------------------------------------

    /// <summary>
    /// Windows: EnterRawIfTty must clear the cooked-input bits
    /// (ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT)
    /// from stdin and set ENABLE_VIRTUAL_TERMINAL_INPUT, then restore
    /// the original mode on dispose. The asserted invariant is checked
    /// against the modes the scope captured before/after, not against
    /// the live console (testhost may not have a real console).
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Windows_ComputeRawInputMode_ClearsCookedBitsAndSetsVtInput()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only");

        // Simulate a typical Windows console-input mode coming in cooked
        // with VT-input disabled (the state after EnsureConsoleInputRestored
        // in the host's InteractiveShell).
        const uint ENABLE_PROCESSED_INPUT = 0x0001;
        const uint ENABLE_LINE_INPUT = 0x0002;
        const uint ENABLE_ECHO_INPUT = 0x0004;
        const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
        const uint ENABLE_WINDOW_INPUT = 0x0008;
        const uint ENABLE_MOUSE_INPUT = 0x0010;

        uint cooked = ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT;
        uint raw = TerminalMode.ComputeRawInputMode(cooked);

        Assert.Equal(0u, raw & ENABLE_LINE_INPUT);
        Assert.Equal(0u, raw & ENABLE_ECHO_INPUT);
        Assert.Equal(0u, raw & ENABLE_PROCESSED_INPUT);
        Assert.NotEqual(0u, raw & ENABLE_VIRTUAL_TERMINAL_INPUT);
        // Preserve unrelated bits: a TUI app may still want window/mouse events.
        Assert.NotEqual(0u, raw & ENABLE_WINDOW_INPUT);
        Assert.NotEqual(0u, raw & ENABLE_MOUSE_INPUT);
    }

    /// <summary>
    /// Windows: ComputeRawOutputMode must set ENABLE_VIRTUAL_TERMINAL_PROCESSING
    /// and DISABLE_NEWLINE_AUTO_RETURN (so the host's VT writes pass through
    /// without auto-CR injection that flickers TUI cursor moves).
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Windows_ComputeRawOutputMode_EnablesVtAndDisablesAutoReturn()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only");

        const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
        const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;
        const uint ENABLE_PROCESSED_OUTPUT = 0x0001;

        uint cooked = ENABLE_PROCESSED_OUTPUT;
        uint raw = TerminalMode.ComputeRawOutputMode(cooked);

        Assert.NotEqual(0u, raw & ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        Assert.NotEqual(0u, raw & DISABLE_NEWLINE_AUTO_RETURN);
        // Preserve ENABLE_PROCESSED_OUTPUT — turning that off would break
        // simple line-oriented programs that don't emit VT.
        Assert.NotEqual(0u, raw & ENABLE_PROCESSED_OUTPUT);
    }

    // ------------------------------------------------------------------
    // Launcher integration — assert that Program.cs wires TerminalMode in
    // around RunHostUnderPtyAsync. Source-text check; cheap and reliable.
    // ------------------------------------------------------------------

    /// <summary>
    /// Launcher integration regression (QA rubric Directive 13: known-bad
    /// memory). The launcher's <c>RunHostUnderPtyAsync</c> must call
    /// <c>TerminalMode.EnterRawIfTty()</c> before starting the bidirectional
    /// pump and dispose it after the host exits. If a later refactor moves
    /// the call out, vim / less / fzf go back to line-buffered input — the
    /// regression we are guarding against.
    /// </summary>
    [Fact]
    public void Launcher_RunHostUnderPtyAsync_WrapsPumpsInTerminalModeScope()
    {
        var asmDir = Path.GetDirectoryName(typeof(TerminalMode).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && dir.Name != "src" && dir.Parent is not null)
            dir = dir.Parent;
        Assert.NotNull(dir);

        var programPath = Path.Combine(dir!.FullName, "PsBash.Shell", "Program.cs");
        Assert.True(File.Exists(programPath), $"Could not find {programPath}");
        var src = File.ReadAllText(programPath);

        int runIdx = src.IndexOf("RunHostUnderPtyAsync", StringComparison.Ordinal);
        Assert.True(runIdx > 0);

        // The TerminalMode call must appear inside the same method body. We
        // find the method declaration and assert the call appears between
        // the declaration and the closing brace.
        int methodSig = src.IndexOf("static async Task<int> RunHostUnderPtyAsync", StringComparison.Ordinal);
        Assert.True(methodSig > 0, "RunHostUnderPtyAsync method signature missing");

        int terminalModeIdx = src.IndexOf("TerminalMode.EnterRawIfTty", methodSig, StringComparison.Ordinal);
        Assert.True(terminalModeIdx > 0,
            "Launcher's RunHostUnderPtyAsync must call TerminalMode.EnterRawIfTty() before starting the pumps");

        // And the pump invocations must follow. ("CopyToAsync" is how PTY-2
        // wires the bidirectional pump; the mode toggle must precede it so
        // bytes arrive raw.)
        int copyIdx = src.IndexOf("CopyToAsync", terminalModeIdx, StringComparison.Ordinal);
        Assert.True(copyIdx > terminalModeIdx,
            "Bidirectional pump (CopyToAsync) must run inside the TerminalMode scope, not before it");
    }

    /// <summary>
    /// Launcher integration: pre-Win10-1809 fallback (Directive 5
    /// platform-locked risk + Directive 7 negative case). The launcher
    /// must wrap the PtyAllocator call so PlatformNotSupportedException
    /// (thrown by ConPtyAdapter on build &lt; 17763) falls back to the
    /// legacy inherited-stdio path with a warning. Source-text regression.
    /// </summary>
    [Fact]
    public void Launcher_RunHostUnderPtyAsync_HasPlatformNotSupportedFallback()
    {
        var asmDir = Path.GetDirectoryName(typeof(TerminalMode).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && dir.Name != "src" && dir.Parent is not null)
            dir = dir.Parent;
        var programPath = Path.Combine(dir!.FullName, "PsBash.Shell", "Program.cs");
        var src = File.ReadAllText(programPath);

        // Either an explicit catch on PlatformNotSupportedException or a
        // documented opt-out path. We require the explicit catch — silent
        // fall-through would hide a misconfigured user environment.
        Assert.Contains("PlatformNotSupportedException", src);
    }
}

/// <summary>
/// Internal helpers that probe POSIX termios state via <c>tcgetattr</c>.
/// Kept in the test assembly so production code does not ship a probe
/// surface for callers who shouldn't have it.
/// </summary>
internal static partial class TerminalModeProbe
{
    // termios buffer must be large enough for any glibc/musl/BSD layout.
    // Linux glibc: 60 bytes. macOS: 44 bytes. 256 is comfortably bounded.
    private const int TermiosBufSize = 256;

    // Index offsets into the termios struct. These match the layout used
    // by both glibc and musl on Linux — the only platforms where this
    // probe is exercised (POSIX-only test path).
    //
    //   struct termios {
    //     tcflag_t c_iflag;
    //     tcflag_t c_oflag;
    //     tcflag_t c_cflag;
    //     tcflag_t c_lflag;   // <-- ICANON / ECHO live here
    //     ...
    //   };
    //
    // tcflag_t = unsigned int (4 bytes) on Linux. c_lflag is therefore
    // at offset 12 (3 * 4).
    //
    // ICANON = 0x00000002 (Linux); ECHO = 0x00000008 (Linux).
    private const int CLflagOffsetLinux = 12;
    // macOS layout differs (c_iflag/c_oflag/c_cflag/c_lflag are 4 bytes
    // each but c_lflag bit values match); we don't run this probe on macOS
    // by default — Posix tests run on the WSL2 Linux side per qa-rubric.
    private const uint ICANON = 0x00000002;
    private const uint ECHO = 0x00000008;

    public static bool IsCanonical(int fd)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return true; // probe disabled
        Span<byte> buf = stackalloc byte[TermiosBufSize];
        unsafe
        {
            fixed (byte* p = buf)
            {
                if (NativeMethods.tcgetattr(fd, (IntPtr)p) != 0) return true;
                uint lflag = ReadUInt32(buf, CLflagOffsetLinux);
                return (lflag & ICANON) != 0;
            }
        }
    }

    public static bool HasEcho(int fd)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return true;
        Span<byte> buf = stackalloc byte[TermiosBufSize];
        unsafe
        {
            fixed (byte* p = buf)
            {
                if (NativeMethods.tcgetattr(fd, (IntPtr)p) != 0) return true;
                uint lflag = ReadUInt32(buf, CLflagOffsetLinux);
                return (lflag & ECHO) != 0;
            }
        }
    }

    private static uint ReadUInt32(Span<byte> buf, int offset)
    {
        return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", SetLastError = true)]
        public static partial int tcgetattr(int fd, IntPtr termios);
    }
}
