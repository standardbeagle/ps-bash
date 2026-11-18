using System.Runtime.InteropServices;

namespace PsBash.Core.Platform;

/// <summary>
/// The one table of signal numbers (docs/specs/platform-dependencies.md §Signals). A script sees bash's Linux numbering
/// on every platform (<c>kill -l</c>, trap numbers, a 128+N status), and every constant here is that number. The OS
/// numbers differ on macOS and the BSDs (USR1 is 10 on Linux, 30 on macOS, and 10 there is SIGBUS). Every number handed
/// to <c>kill</c>/<c>killpg</c>/<c>signal</c>/<see cref="PosixSignalRegistration"/> goes through <see cref="ToNative(int)"/>
/// (or <see cref="AsPosixSignal"/>), and every signal read back from a wait status through <see cref="ExitStatusOf(int)"/>.
/// Guard: <c>Core.Tests/OsSignalsGuardTests</c> (no signal constant or raw-number signal call outside this file).
/// </summary>
public static class OsSignals
{
    public const int SIGHUP = 1, SIGINT = 2, SIGQUIT = 3, SIGABRT = 6, SIGBUS = 7, SIGKILL = 9, SIGUSR1 = 10,
        SIGSEGV = 11, SIGUSR2 = 12, SIGPIPE = 13, SIGALRM = 14, SIGTERM = 15, SIGCHLD = 17, SIGCONT = 18,
        SIGSTOP = 19, SIGTSTP = 20, SIGTTIN = 21, SIGTTOU = 22, SIGURG = 23, SIGWINCH = 28, SIGSYS = 31;

    // Index = Linux number, value = the BSD/macOS number (XNU sys/signal.h); 0 = that OS has no such signal
    // (STKFLT 16, PWR 30). The numbers XSI fixes (HUP INT QUIT ABRT KILL ALRM TERM ...) are the same in both.
    private static readonly int[] LinuxToBsd =
    [
        0, 1, 2, 3, 4, 5, 6, 10, 8, 9, 30, 11, 31, 13, 14, 15,
        0, 20, 19, 17, 18, 21, 22, 16, 24, 25, 26, 27, 28, 23, 0, 12,
    ];

    /// <summary>True on the systems that number signals the BSD way (macOS, FreeBSD).</summary>
    public static bool BsdNumbering { get; } = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsFreeBSD();

    /// <summary>The number this OS uses for bash signal <paramref name="bashSignal"/>; 0 when it has no such signal.</summary>
    public static int ToNative(int bashSignal) => ToNative(bashSignal, BsdNumbering);

    internal static int ToNative(int bashSignal, bool bsd)
    {
        if (bashSignal <= 0) return 0;
        if (!bsd) return bashSignal;
        return bashSignal < LinuxToBsd.Length ? LinuxToBsd[bashSignal] : 0;   // the BSDs have no real-time signals
    }

    /// <summary>The bash number of OS signal <paramref name="nativeSignal"/>; the native number itself when bash has none (macOS EMT 7, INFO 29).</summary>
    public static int FromNative(int nativeSignal) => FromNative(nativeSignal, BsdNumbering);

    internal static int FromNative(int nativeSignal, bool bsd)
    {
        if (!bsd || nativeSignal <= 0) return nativeSignal;
        int linux = Array.IndexOf(LinuxToBsd, nativeSignal, 1);
        return linux > 0 ? linux : nativeSignal;
    }

    /// <summary>
    /// The status bash reports for a child that a raw <c>waitpid</c> status describes: the exit code, or 128 + the
    /// bash number of the signal that ended it (a macOS child killed by SIGUSR1 is 138, as on Linux, not 158).
    /// </summary>
    public static int ExitStatusOf(int waitStatus) => ExitStatusOf(waitStatus, BsdNumbering);

    internal static int ExitStatusOf(int waitStatus, bool bsd)
        => (waitStatus & 0x7f) == 0 ? (waitStatus >> 8) & 0xff : 128 + FromNative(waitStatus & 0x7f, bsd);

    /// <summary>errno values of <c>waitpid</c>, the same on Linux and macOS.</summary>
    public const int EINTR = 4, ECHILD = 10;

    /// <summary>
    /// The status bash reports for a child whose wait status is gone: <c>waitpid</c> failed with ECHILD because another
    /// waiter reaped it first (the .NET runtime reaps every child of a process that inherited SIGCHLD ignored) or the
    /// kernel discarded it. bash records 0 then (jobs.c <c>wait_for</c>: "can't find true status").
    /// </summary>
    public const int LostChildStatus = 0;

    /// <summary>
    /// What a blocking <c>waitpid(pid, &amp;status, 0)</c> that returned <paramref name="rc"/> (errno
    /// <paramref name="errno"/>) says about child <paramref name="pid"/>: its status (<see cref="ExitStatusOf(int)"/>),
    /// <see cref="LostChildStatus"/> when the status is gone (ECHILD, or any other failure: nothing is left to wait for),
    /// or null when the wait must be repeated (EINTR). The one decoder of a wait's outcome; never a status invented at
    /// the call site (it was 255, which no bash reports for such a child).
    /// </summary>
    public static int? WaitOutcome(int rc, int pid, int waitStatus, int errno) => WaitOutcome(rc, pid, waitStatus, errno, BsdNumbering);

    internal static int? WaitOutcome(int rc, int pid, int waitStatus, int errno, bool bsd)
    {
        if (rc == pid && pid > 0) return ExitStatusOf(waitStatus, bsd);
        if (rc < 0 && errno == EINTR) return null;
        return LostChildStatus;
    }

    /// <summary>
    /// True when the .NET runtime itself uses <paramref name="bashSignal"/> here, so ps-bash must never register it. The
    /// BSDs have no SIGRTMIN, so CoreCLR (pal signal.cpp) and NativeAOT (UnixSignals.h) use SIGUSR1 as
    /// INJECT_ACTIVATION_SIGNAL and send it to their own threads on every GC suspension. A registration chains after the
    /// runtime's handler, so each GC looked like <c>kill -USR1</c>: scripts on macOS died with 138 and no output
    /// (publish v0.15.0). Linux uses SIGRTMIN for this.
    /// </summary>
    public static bool RuntimeOwned(int bashSignal) => RuntimeOwned(bashSignal, BsdNumbering);

    internal static bool RuntimeOwned(int bashSignal, bool bsd) => bsd && bashSignal == SIGUSR1;

    /// <summary>
    /// The real signals a ps-bash process catches for the script it runs (USR1 and USR2, whose default action would end
    /// the process), as bash numbers, minus those the runtime owns here (<see cref="RuntimeOwned(int)"/>).
    /// </summary>
    public static IReadOnlyList<int> ScriptSignals() => ScriptSignals(BsdNumbering);

    internal static IReadOnlyList<int> ScriptSignals(bool bsd)
    {
        var list = new List<int>(2);
        foreach (var sig in new[] { SIGUSR1, SIGUSR2 })
            if (!RuntimeOwned(sig, bsd)) list.Add(sig);
        return list;
    }

    /// <summary>
    /// <paramref name="bashSignal"/> as the raw <see cref="PosixSignal"/> value <see cref="PosixSignalRegistration.Create"/>
    /// takes (a positive value is the OS number). Throws for a signal the runtime owns or this OS lacks: registering it
    /// breaks the runtime, and there is nothing to register.
    /// </summary>
    public static PosixSignal AsPosixSignal(int bashSignal)
    {
        if (RuntimeOwned(bashSignal))
            throw new InvalidOperationException($"signal {bashSignal} is the .NET runtime's own on this OS; it cannot be caught");
        int native = ToNative(bashSignal);
        if (native == 0) throw new PlatformNotSupportedException($"signal {bashSignal} does not exist on this OS");
        return (PosixSignal)native;
    }
}
