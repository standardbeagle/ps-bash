using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PsBash.Core.Platform;

/// <summary>
/// Hands the read end of a pipe this process holds to ANOTHER process that is already running, as that process's own
/// handle on the SAME pipe: one open pipe, two readers, as bash's fork gives a job the shell's stdin. A background job
/// that runs in a host process of its own (a warm spare, started long before the job) reads the compound's stdin this
/// way (docs/specs/stdio-messaging.md §4.7). The sender offers (<see cref="Lease.OfferTo"/>) and keeps holding its end
/// until the receiver is done; the receiver turns the token into its handle (<see cref="Accept"/>).
/// <list type="bullet">
/// <item>Windows: <c>DuplicateHandle</c> into the receiver (the sender may: the receiver is its own child). The token is
/// the handle's value THERE; it is inheritable, so the receiver can hand it on to a program through a handle list.</item>
/// <item>Linux: the receiver opens <c>/proc/&lt;sender&gt;/fd/&lt;fd&gt;</c>, which opens the same pipe. A pipe is a FIFO
/// to <c>open</c>: a read-only open BLOCKS while the pipe has no writer, and the sender's pump has closed its writer once
/// the input ended (<c>echo in | …</c>), so the open is non-blocking and the flag is cleared after. Close-on-exec: a
/// program gets it only by a <c>dup2</c> onto its fd 0.</item>
/// <item>macOS: no way short of <c>SCM_RIGHTS</c> over a socket; <see cref="Lease.OfferTo"/> is null there and the job
/// reads end of input (a known gap).</item>
/// </list>
/// </summary>
public static class PipeHandoff
{
    private const string WindowsPrefix = "w:";
    private const string ProcPrefix = "p:";

    /// <summary>
    /// The read end of a pipe the caller HOLDS for a receiver (a program, a job's host): <see cref="Dispose"/> once the
    /// receiver is done lets go of it, once.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private Action? _release;

        public Lease(SafeHandle readEnd, Action release)
        {
            ArgumentNullException.ThrowIfNull(readEnd);
            ArgumentNullException.ThrowIfNull(release);
            ReadEnd = readEnd;
            _release = release;
        }

        /// <summary>The read end (inheritable on Windows, close-on-exec on POSIX).</summary>
        public SafeHandle ReadEnd { get; }

        /// <summary>
        /// Make <see cref="ReadEnd"/> openable by the process <paramref name="targetPid"/>: the token it gives
        /// <see cref="Accept"/>, or null when this OS has no way or the OS refused. The lease must outlive the receiver's use.
        /// </summary>
        public string? OfferTo(int targetPid) => Offer(ReadEnd, targetPid);

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>See <see cref="Lease.OfferTo"/>.</summary>
    public static string? Offer(SafeHandle readEnd, int targetPid)
    {
        ArgumentNullException.ThrowIfNull(readEnd);
        if (targetPid <= 0 || readEnd.IsInvalid || readEnd.IsClosed) return null;
        bool added = false;
        try
        {
            readEnd.DangerousAddRef(ref added);
            var handle = readEnd.DangerousGetHandle();
            if (OperatingSystem.IsWindows()) return OfferWindows(handle, targetPid);
            if (OperatingSystem.IsLinux())
                return ProcPrefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ":"
                    + handle.ToInt64().ToString(CultureInfo.InvariantCulture);
            return null;
        }
        catch (ObjectDisposedException) { return null; }
        finally
        {
            if (added) readEnd.DangerousRelease();
        }
    }

    /// <summary>
    /// The pipe <paramref name="token"/> names, as this process's own handle (the caller owns it), or null when the token
    /// is not for this OS or the pipe cannot be opened (the sender is gone or let go).
    /// </summary>
    public static SafePipeHandle? Accept(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (token.StartsWith(WindowsPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()
                || !long.TryParse(token.AsSpan(WindowsPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value == 0) return null;
            return new SafePipeHandle(new IntPtr(value), ownsHandle: true);
        }
        if (token.StartsWith(ProcPrefix, StringComparison.Ordinal) && OperatingSystem.IsLinux())
        {
            var parts = token.Substring(ProcPrefix.Length).Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var fd)) return null;
            return AcceptLinux(pid, fd);
        }
        return null;
    }

    // ---- Windows ----

    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
        uint access, bool inheritHandle, uint options);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string? OfferWindows(IntPtr handle, int targetPid)
    {
        var target = OpenProcess(ProcessDupHandle, false, targetPid);
        if (target == IntPtr.Zero) return null;
        try
        {
            // Inheritable in the receiver: it hands the pipe on to its programs through their handle lists.
            if (!DuplicateHandle(GetCurrentProcess(), handle, target, out var there, 0, true, DuplicateSameAccess)) return null;
            return WindowsPrefix + there.ToInt64().ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            CloseHandle(target);
        }
    }

    // ---- Linux (x86_64 and arm64 share these values) ----

    private const int ORdOnly = 0, ONonBlock = 0x800, OCloExec = 0x80000;
    private const int FGetFl = 3, FSetFl = 4;

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int OpenPath(string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int Fcntl(int fd, int command, int argument);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int ClosePosix(int fd);

    private static SafePipeHandle? AcceptLinux(int pid, int fd)
    {
        // Non-blocking: the sender's writer may be closed already (the input ended), and a FIFO open waits for one.
        int mine = OpenPath($"/proc/{pid}/fd/{fd}", ORdOnly | ONonBlock | OCloExec);
        if (mine < 0) return null;
        int flags = Fcntl(mine, FGetFl, 0);
        if (flags < 0 || Fcntl(mine, FSetFl, flags & ~ONonBlock) < 0)
        {
            ClosePosix(mine);
            return null;
        }
        return new SafePipeHandle(new IntPtr(mine), ownsHandle: true);
    }
}
