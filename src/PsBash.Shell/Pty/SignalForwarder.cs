using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PsBash.Shell.Pty;

/// <summary>
/// PTY-5: launcher-side terminal-signal forwarding. The follow-on PTY-3
/// explicitly deferred.
///
/// <para>In raw passthrough mode the launcher is the process attached to the
/// user's real tty, so the kernel delivers terminal signals
/// (<c>SIGINT</c> on Ctrl-C, <c>SIGTSTP</c> on Ctrl-Z, <c>SIGWINCH</c> on
/// window resize) to the <i>launcher</i> — never to the host. Without
/// explicit forwarding, Ctrl-C is dead inside ps-bash and a window resize is
/// silent (vim / htop never repaint). This type installs launcher-side
/// handlers for the lifetime of the raw-mode host child and re-delivers:</para>
///
/// <list type="bullet">
///   <item><description><b>POSIX</b> <c>SIGINT</c> / <c>SIGTSTP</c> →
///     <c>killpg(hostPgid, sig)</c>. The host is a session / process-group
///     leader (PTY-2's <c>posix_spawn</c> + <c>POSIX_SPAWN_SETSID</c>), so its
///     pgid equals its pid and the whole foreground job receives the
///     signal.</description></item>
///   <item><description><b>POSIX</b> <c>SIGWINCH</c> → read the launcher tty's
///     window size via <c>ioctl(TIOCGWINSZ)</c> and push it onto the PTY
///     master via <see cref="IPty.Resize"/> (which issues
///     <c>ioctl(TIOCSWINSZ)</c>). The kernel then sends <c>SIGWINCH</c> to the
///     host through the slave so TUI apps repaint.</description></item>
///   <item><description><b>Windows</b> Ctrl-C: ConPTY auto-translates the
///     console Ctrl-C into a <c>CTRL_C_EVENT</c> delivered to the attached
///     host, which surfaces as <c>Console.CancelKeyPress</c> there — the
///     launcher does <i>not</i> re-inject it. The launcher only installs a
///     console-control handler that <b>suppresses</b> its own default
///     terminate-on-Ctrl-C so the launcher process survives long enough to
///     keep pumping bytes. For resize, a lightweight poll watches
///     <c>Console.WindowWidth/Height</c> and calls <see cref="IPty.Resize"/>
///     (ConPTY's <c>ResizePseudoConsole</c>). See the Ctrl-C vs Ctrl-Break
///     matrix in <c>docs/specs/pty.md</c>.</description></item>
/// </list>
///
/// <para><b>Lifecycle:</b> like <see cref="TerminalMode"/>, this is a
/// save-set-restore <see cref="IDisposable"/> scope. The launcher enters it
/// inside the same <c>using</c> region as the raw-mode scope and disposes it
/// in the outer <c>finally</c> so a crashed pump still removes the handlers
/// and the user's parent shell regains normal Ctrl-C behaviour. An inactive
/// scope (non-tty launcher stdin) installs nothing and disposes as a no-op.</para>
///
/// <para><b>AOT:</b> the launcher publishes with <c>PublishAot=true</c>. POSIX
/// signal delivery uses <see cref="PosixSignalRegistration"/> where it covers
/// the signal (<c>SIGINT</c>) and raw <c>sigaction</c> P/Invoke via
/// <c>[LibraryImport]</c> source-generated marshalling for <c>SIGTSTP</c> /
/// <c>SIGWINCH</c> which <see cref="PosixSignal"/> does not enumerate. No
/// runtime codegen, no reflection — AOT-safe.</para>
/// </summary>
internal sealed partial class SignalForwarder : IDisposable, IAsyncDisposable
{
    private readonly List<IDisposable> _registrations = new();
    private readonly Action _detach;
    private readonly Func<ValueTask> _detachAsync;
    private int _disposed;

    /// <summary>
    /// Count of <c>SIGINT</c> signals forwarded to the host process group.
    /// Read by the launcher's prompt renderer to know the host was
    /// interrupted (see <c>HostProtocol.SignalDeliveredPrefix</c>).
    /// </summary>
    public int SigintDeliveredCount => Volatile.Read(ref _sigintCount);
    private int _sigintCount;

    /// <summary>
    /// Count of <c>SIGTSTP</c> (Ctrl-Z) signals forwarded to the host process
    /// group. POSIX only; always 0 on Windows.
    /// </summary>
    public int SigtstpDeliveredCount => Volatile.Read(ref _sigtstpCount);
    private int _sigtstpCount;

    /// <summary>
    /// Count of <c>SIGWINCH</c> resize events propagated to the PTY master.
    /// </summary>
    public int WinchForwardedCount => Volatile.Read(ref _winchCount);
    private int _winchCount;

    /// <summary>Whether this scope installed any handlers.</summary>
    public bool IsActive { get; }

    private SignalForwarder(bool active, Action detach, Func<ValueTask>? detachAsync = null)
    {
        IsActive = active;
        _detach = detach;
        _detachAsync = detachAsync ?? (() =>
        {
            detach();
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Inactive scope: installs nothing, disposes as a no-op. Returned when
    /// the launcher's stdin is not a tty (driven by a pipe / test harness) so
    /// there are no terminal signals to forward.
    /// </summary>
    public static SignalForwarder Inactive { get; } = new(active: false, detach: static () => { });

    /// <summary>
    /// Install launcher-side signal forwarding for the lifetime of a raw-mode
    /// host child.
    /// </summary>
    /// <param name="hostPid">
    /// PID of the host spawned under the PTY. On POSIX this is also the host's
    /// process-group id because PTY-2 spawns it as a session leader
    /// (<c>POSIX_SPAWN_SETSID</c>); <c>killpg(hostPid, sig)</c> therefore
    /// targets the whole foreground job.
    /// </param>
    /// <param name="pty">
    /// The PTY whose master receives resize updates on <c>SIGWINCH</c>.
    /// </param>
    /// <param name="isLauncherStdinTty">
    /// Whether the launcher's own stdin is a real terminal. When
    /// <c>false</c> (pipe-driven launcher, test host) an <see cref="Inactive"/>
    /// scope is returned — there is no controlling tty to receive signals.
    /// </param>
    public static SignalForwarder Install(int hostPid, IPty pty, bool isLauncherStdinTty)
    {
        ArgumentNullException.ThrowIfNull(pty);

        if (!isLauncherStdinTty)
            return Inactive;

        if (OperatingSystem.IsWindows())
            return InstallWindows(pty);

        return InstallPosix(hostPid, pty);
    }

    // ------------------------------------------------------------------
    // POSIX
    // ------------------------------------------------------------------

    [UnsupportedOSPlatform("windows")]
    private static SignalForwarder InstallPosix(int hostPid, IPty pty)
    {
        SignalForwarder? self = null;

        // SIGINT: PosixSignalRegistration covers it. Cancel=true so the
        // launcher's own process does NOT terminate — we only re-deliver to
        // the host's process group. The host's foreground job handles the
        // SIGINT (cancels the running pipeline); the launcher keeps pumping.
        var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            PosixNative.killpg(hostPid, PosixNative.SIGINT);
            Interlocked.Increment(ref self!._sigintCount);
        });

        // SIGTSTP and SIGWINCH are not enumerated by PosixSignal, so they need
        // raw sigaction. We install a single native trampoline per signal that
        // forwards and (for SIGWINCH) resizes. The handler runs on the signal
        // stack — it only does async-signal-safe work: a libc killpg / ioctl
        // and a relaxed counter increment via a static field.
        PosixNative.InstallRawHandler(PosixNative.SIGTSTP, OnSigtstp);
        PosixNative.InstallRawHandler(PosixNative.SIGWINCH, OnSigwinch);

        // Static bridge: the raw C handler is a static function pointer, so it
        // reaches instance state through these statics. Only one raw forwarder
        // is alive at a time (one host child under PTY), guarded by the
        // launcher's single interactive path.
        s_hostPid = hostPid;
        s_pty = pty;

        void Detach()
        {
            sigint.Dispose();
            PosixNative.RestoreDefault(PosixNative.SIGTSTP);
            PosixNative.RestoreDefault(PosixNative.SIGWINCH);
            s_pty = null;
            s_self = null;
        }

        self = new SignalForwarder(active: true, Detach);
        s_self = self;
        self._registrations.Add(sigint);

        // Prime the PTY with the launcher's current window size: a resize that
        // happened between PTY allocation and handler install would otherwise
        // be missed until the next SIGWINCH.
        ForwardWinch();

        return self;
    }

    // Static bridge for the raw (sigaction) handlers. A POSIX signal handler
    // is a bare function pointer with no closure; these statics carry the
    // per-launch context. The launcher runs exactly one interactive PTY child
    // at a time, so a single set of statics is sufficient and correct.
    private static int s_hostPid;
    private static IPty? s_pty;
    private static SignalForwarder? s_self;

    [UnsupportedOSPlatform("windows")]
    private static void OnSigtstp(int sig)
    {
        // Re-deliver Ctrl-Z to the host's foreground process group so the
        // running job is suspended (POSIX job control). The launcher itself
        // is NOT stopped — if it were, byte pumping would freeze and the user
        // could never resume.
        PosixNative.killpg(s_hostPid, PosixNative.SIGTSTP);
        var s = s_self;
        if (s is not null) Interlocked.Increment(ref s._sigtstpCount);
    }

    [UnsupportedOSPlatform("windows")]
    private static void OnSigwinch(int sig)
    {
        ForwardWinch();
    }

    /// <summary>
    /// Read the launcher tty's current window size (<c>TIOCGWINSZ</c> on
    /// stdin) and push it onto the PTY master (<c>TIOCSWINSZ</c> via
    /// <see cref="IPty.Resize"/>). Best-effort: a non-tty stdin or a closed
    /// PTY yields a silent no-op.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void ForwardWinch()
    {
        var pty = s_pty;
        if (pty is null) return;

        if (!PosixNative.TryGetWinsize(0, out short cols, out short rows))
            return;
        if (cols <= 0 || rows <= 0) return;

        try
        {
            pty.Resize(cols, rows);
            var s = s_self;
            if (s is not null) Interlocked.Increment(ref s._winchCount);
        }
        catch
        {
            // PTY disposed mid-resize, or master fd closed. The host child is
            // on its way out; dropping the resize is the correct fallback.
        }
    }

    // ------------------------------------------------------------------
    // Windows
    // ------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static SignalForwarder InstallWindows(IPty pty)
    {
        // Ctrl-C: ConPTY already routes the console CTRL_C_EVENT to the
        // attached host (Console.CancelKeyPress fires there). The launcher
        // must only stop ITS OWN default behaviour of terminating on Ctrl-C,
        // otherwise the launcher dies and byte pumping stops. We install a
        // console control handler that returns TRUE for CTRL_C_EVENT and
        // CTRL_BREAK_EVENT (handled = "don't run the default terminator") and
        // FALSE for CTRL_CLOSE/LOGOFF/SHUTDOWN (let the system tear us down).
        //
        // Ctrl-C vs Ctrl-Break matrix (see docs/specs/pty.md):
        //   CTRL_C_EVENT      → ConPTY delivers to host; launcher suppresses
        //                       its own terminate. Host cancels the pipeline.
        //   CTRL_BREAK_EVENT  → also crosses the ConPTY boundary cleanly; some
        //                       console hosts only deliver Ctrl-Break, so we
        //                       suppress the launcher default for it too.
        //   CTRL_CLOSE_EVENT  → window closing; do NOT suppress — the launcher
        //                       and host should both exit.
        var handler = new WindowsNative.HandlerRoutine(ctrlType =>
        {
            return ctrlType is WindowsNative.CTRL_C_EVENT or WindowsNative.CTRL_BREAK_EVENT;
        });

        if (!WindowsNative.SetConsoleCtrlHandler(handler, add: true))
        {
            // No console (redirected) — nothing to forward. Inactive scope.
            return Inactive;
        }

        // Resize: Windows has no SIGWINCH. Poll Console.WindowWidth/Height on a
        // background loop and push deltas onto the ConPTY via IPty.Resize
        // (ResizePseudoConsole). The poll is cancelled on dispose.
        var cts = new CancellationTokenSource();
        SignalForwarder? self = null;

        var pollTask = Task.Run(async () =>
        {
            short lastCols = 0, lastRows = 0;
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        int w = Console.WindowWidth;
                        int h = Console.WindowHeight;
                        if (w > 0 && w <= short.MaxValue && h > 0 && h <= short.MaxValue)
                        {
                            short cols = (short)w, rows = (short)h;
                            if (cols != lastCols || rows != lastRows)
                            {
                                lastCols = cols;
                                lastRows = rows;
                                pty.Resize(cols, rows);
                                if (self is not null)
                                    Interlocked.Increment(ref self._winchCount);
                            }
                        }
                    }
                    catch
                    {
                        // No console / PTY disposed — stop polling.
                        break;
                    }

                    // 150 ms cadence: resize is a low-frequency, human-driven
                    // event; this is responsive without busy-spinning.
                    await Task.Delay(150, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* disposed */ }
        });

        void Detach()
        {
            WindowsNative.SetConsoleCtrlHandler(handler, add: false);
            cts.Cancel();
            _ = pollTask.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                cts,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            // Keep the delegate rooted until detach so the GC cannot collect
            // it while the OS still holds the native function pointer.
            GC.KeepAlive(handler);
        }

        async ValueTask DetachAsync()
        {
            WindowsNative.SetConsoleCtrlHandler(handler, add: false);
            cts.Cancel();
            try { await pollTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch { /* best-effort */ }
            cts.Dispose();
            GC.KeepAlive(handler);
        }

        self = new SignalForwarder(active: true, Detach, DetachAsync);
        return self;
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _detach(); }
        catch { /* best-effort: a launcher tearing down must not throw here */ }
        DisposeRegistrations();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _detachAsync().ConfigureAwait(false); }
        catch { /* best-effort: a launcher tearing down must not throw here */ }
        DisposeRegistrations();
    }

    private void DisposeRegistrations()
    {
        foreach (var r in _registrations)
        {
            try { r.Dispose(); } catch { /* best-effort */ }
        }
        _registrations.Clear();
    }

    // ------------------------------------------------------------------
    // POSIX libc
    // ------------------------------------------------------------------

    [UnsupportedOSPlatform("windows")]
    private static partial class PosixNative
    {
        private const string LibC = "libc";

        // Signal numbers. SIGINT/SIGWINCH are stable across Linux/macOS/BSD.
        // SIGTSTP differs: 20 on Linux and on the BSD family (macOS/FreeBSD)
        // alike — it is one of the historically-aligned numbers. SIGWINCH is
        // 28 on Linux and on macOS/FreeBSD.
        public const int SIGINT = 2;
        public const int SIGTSTP = 20;
        public const int SIGWINCH = 28;

        // Raw POSIX handler trampoline. The C signature is void(*)(int) with
        // the platform default (Cdecl) calling convention. [UnmanagedFunctionPointer]
        // pins the convention so the AOT compiler emits a correct reverse
        // P/Invoke stub; the delegate type is statically known so no runtime
        // codegen is needed.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SignalHandler(int sig);

        // Keep managed delegates rooted for the lifetime of the registration —
        // the OS holds a native pointer into them.
        private static readonly Dictionary<int, SignalHandler> s_rooted = new();
        private static readonly object s_gate = new();

        public static void InstallRawHandler(int sig, SignalHandler handler)
        {
            lock (s_gate)
            {
                s_rooted[sig] = handler;
                IntPtr fn = Marshal.GetFunctionPointerForDelegate(handler);
                // signal() is the portable, async-signal-safe-to-install
                // primitive. sigaction() is richer but signal() is sufficient
                // for "forward this signal" and has identical numbers across
                // Linux/macOS/BSD. SIG_ERR == (void*)-1 indicates failure.
                IntPtr prev = signal(sig, fn);
                if (prev == (IntPtr)(-1))
                {
                    // Could not install — drop the root; the signal keeps its
                    // default disposition. Forwarding for this signal is then
                    // a no-op, which is a safe degradation.
                    s_rooted.Remove(sig);
                }
            }
        }

        public static void RestoreDefault(int sig)
        {
            lock (s_gate)
            {
                // SIG_DFL == (void*)0.
                signal(sig, IntPtr.Zero);
                s_rooted.Remove(sig);
            }
        }

        /// <summary>
        /// <c>ioctl(fd, TIOCGWINSZ)</c> — read a tty's window size. Returns
        /// <c>false</c> (and zeroed out-params) if the fd is not a tty.
        /// </summary>
        public static bool TryGetWinsize(int fd, out short cols, out short rows)
        {
            var ws = default(Winsize);
            if (ioctl(fd, TIOCGWINSZ, ref ws) != 0)
            {
                cols = 0; rows = 0;
                return false;
            }
            cols = (short)ws.ws_col;
            rows = (short)ws.ws_row;
            return true;
        }

        // TIOCGWINSZ: Linux 0x5413; macOS/FreeBSD 0x40087468 (_IOR('t',104,winsize)).
        public static ulong TIOCGWINSZ =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? 0x5413UL
                : 0x40087468UL;

        [StructLayout(LayoutKind.Sequential)]
        public struct Winsize
        {
            public ushort ws_row;
            public ushort ws_col;
            public ushort ws_xpixel;
            public ushort ws_ypixel;
        }

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int killpg(int pgrp, int sig);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int ioctl(int fd, ulong request, ref Winsize ws);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial IntPtr signal(int signum, IntPtr handler);
    }

    // ------------------------------------------------------------------
    // Windows kernel32
    // ------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static class WindowsNative
    {
        public const uint CTRL_C_EVENT = 0;
        public const uint CTRL_BREAK_EVENT = 1;
        public const uint CTRL_CLOSE_EVENT = 2;

        // The handler is marshalled as a native function pointer. [DllImport]
        // (not [LibraryImport]) is used here because the source generator does
        // not marshal delegate parameters; classic [DllImport] delegate
        // marshalling is AOT-safe (the delegate type is statically known, no
        // runtime codegen) and the ILC compiler handles the reverse P/Invoke
        // stub at publish time.
        [return: MarshalAs(UnmanagedType.Bool)]
        public delegate bool HandlerRoutine(uint dwCtrlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetConsoleCtrlHandler(
            HandlerRoutine handlerRoutine,
            [MarshalAs(UnmanagedType.Bool)] bool add);
    }
}
