using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PsBash.Shell.Pty;

/// <summary>
/// POSIX pseudo-terminal adapter using the explicit
/// <c>posix_openpt</c> -&gt; <c>grantpt</c> -&gt; <c>unlockpt</c> -&gt; <c>ptsname</c>
/// pathway. Chosen over <c>forkpty</c> because the launcher does not fork
/// for the host process — the child is spawned by PTY-2 via
/// <c>posix_spawn</c> / <c>fork+exec</c>, and the slave fd is dup2'd in the
/// child.
///
/// <para><b>Controlling terminal:</b> This adapter does <i>not</i> call
/// <c>ioctl(TIOCSCTTY)</c>. That call belongs in the child after
/// <c>setsid()</c> and is PTY-2's responsibility — the slave must become
/// the controlling tty of the child, not the launcher.</para>
///
/// <para><b>Dispose order:</b> The slave fd is closed first, then the
/// master. Closing the master first would SIGHUP any process whose
/// controlling terminal is the slave.</para>
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed partial class UnixPtyAdapter : IPty
{
    private readonly FileStream _masterStream;
    private readonly int _masterFd;
    private int _slaveFd;
    private bool _disposed;

    private UnixPtyAdapter(int masterFd, int slaveFd, FileStream masterStream)
    {
        _masterFd = masterFd;
        _slaveFd = slaveFd;
        _masterStream = masterStream;
    }

    public Stream Input => _masterStream;
    public Stream Output => _masterStream;
    public IntPtr SlaveHandle => IntPtr.Zero;
    public int SlaveFileDescriptor => _slaveFd;

    public static ValueTask<IPty> AllocateAsync(short cols, short rows)
    {
        int masterFd = NativeMethods.posix_openpt(NativeMethods.PosixOpenPtFlags);
        if (masterFd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"posix_openpt failed (errno={err}): {Marshal.GetPInvokeErrorMessage(err)}");
        }

        int slaveFd = -1;
        try
        {
            if (NativeMethods.grantpt(masterFd) != 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"grantpt failed (errno={err}): {Marshal.GetPInvokeErrorMessage(err)}");
            }

            if (NativeMethods.unlockpt(masterFd) != 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"unlockpt failed (errno={err}): {Marshal.GetPInvokeErrorMessage(err)}");
            }

            // ptsname_r is the thread-safe variant. Allocate 256 bytes — POSIX
            // does not specify a max but real implementations stay well under
            // 64 bytes ("/dev/pts/<n>").
            var nameBuf = new byte[256];
            int rc;
            unsafe
            {
                fixed (byte* p = nameBuf)
                {
                    rc = NativeMethods.ptsname_r(masterFd, p, (nuint)nameBuf.Length);
                }
            }
            if (rc != 0)
            {
                throw new InvalidOperationException(
                    $"ptsname_r failed (rc={rc}): {Marshal.GetPInvokeErrorMessage(rc)}");
            }

            int nameLen = Array.IndexOf(nameBuf, (byte)0);
            if (nameLen <= 0)
            {
                throw new InvalidOperationException("ptsname_r returned an empty slave name");
            }
            string slaveName = System.Text.Encoding.UTF8.GetString(nameBuf, 0, nameLen);

            slaveFd = NativeMethods.open(slaveName, NativeMethods.PosixOpenSlaveFlags, 0);
            if (slaveFd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"open({slaveName}) failed (errno={err}): {Marshal.GetPInvokeErrorMessage(err)}");
            }

            // Wrap the master fd in a FileStream. ownsHandle=false so we
            // control the close order in DisposeAsync (slave before master).
            var masterHandle = new SafeFileHandle((IntPtr)masterFd, ownsHandle: false);
            var masterStream = new FileStream(masterHandle, FileAccess.ReadWrite);

            // Set the initial window size via the master fd before the adapter
            // takes ownership. If this fails we have not yet handed ownership
            // to the adapter, so the outer catch can clean up safely.
            SetWinsize(masterFd, cols, rows);

            return ValueTask.FromResult<IPty>(new UnixPtyAdapter(masterFd, slaveFd, masterStream));
        }
        catch
        {
            // Single ownership boundary: the adapter has not been constructed
            // yet on any failure path, so we own both fds here. Close slave
            // first, then master, per the documented dispose order.
            if (slaveFd >= 0) NativeMethods.close(slaveFd);
            NativeMethods.close(masterFd);
            throw;
        }
    }

    private static void SetWinsize(int masterFd, short cols, short rows)
    {
        var ws = new NativeMethods.Winsize { ws_row = (ushort)rows, ws_col = (ushort)cols };
        if (NativeMethods.ioctl(masterFd, NativeMethods.TIOCSWINSZ, ref ws) != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"ioctl(TIOCSWINSZ) failed (errno={err}): {Marshal.GetPInvokeErrorMessage(err)}");
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed) return;
        SetWinsize(_masterFd, cols, rows);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;

        // Order: slave first, master last. Closing master before slave can
        // SIGHUP a child whose controlling tty is the slave.
        int slaveFd = System.Threading.Interlocked.Exchange(ref _slaveFd, -1);
        if (slaveFd >= 0)
        {
            NativeMethods.close(slaveFd);
        }

        try { _masterStream.Dispose(); } catch { /* swallow — fd already closed */ }
        NativeMethods.close(_masterFd);
    }

    // libc PInvoke. libSystem.B.dylib on macOS, libc.so.6 / libc on Linux.
    // .NET's NativeLibrary resolver handles the soname differences; we use the
    // generic "libc" alias.
    private static partial class NativeMethods
    {
        private const string LibC = "libc";

        // Linux: O_RDWR=2, O_NOCTTY=0x100, O_NONBLOCK=0x800.
        // macOS:  O_RDWR=2, O_NOCTTY=0x20000, O_NONBLOCK=0x4.
        // FreeBSD same as macOS for these flags.
        public static int PosixOpenPtFlags =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? 0x002 | 0x100               // O_RDWR | O_NOCTTY
                : 0x002 | 0x20000;            // macOS / FreeBSD

        public static int PosixOpenSlaveFlags => PosixOpenPtFlags;

        // TIOCSWINSZ differs by OS:
        //   Linux:   0x5414
        //   macOS:   0x80087467 (_IOW('t', 103, struct winsize))
        public static ulong TIOCSWINSZ =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? 0x5414UL
                : 0x80087467UL;

        [StructLayout(LayoutKind.Sequential)]
        public struct Winsize
        {
            public ushort ws_row;
            public ushort ws_col;
            public ushort ws_xpixel;
            public ushort ws_ypixel;
        }

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_openpt(int flags);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int grantpt(int fd);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int unlockpt(int fd);

        [LibraryImport(LibC, SetLastError = true)]
        public static unsafe partial int ptsname_r(int fd, byte* buf, nuint buflen);

        [LibraryImport(LibC, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int open(string path, int flags, uint mode);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int close(int fd);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int ioctl(int fd, ulong request, ref Winsize ws);
    }
}
