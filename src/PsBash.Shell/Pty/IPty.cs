using System.Runtime.InteropServices;

namespace PsBash.Shell.Pty;

/// <summary>
/// Cross-platform pseudo-terminal handle. The launcher allocates one of these
/// before spawning the host in interactive mode (PTY-2 owns the spawn side).
///
/// <para><b>Platform support:</b></para>
/// <list type="bullet">
///   <item><description><b>Windows:</b> ConPTY (CreatePseudoConsole). Requires
///     Windows 10 1809+ (build 17763). Older builds throw
///     <see cref="PlatformNotSupportedException"/> from <see cref="PtyAllocator.AllocateAsync"/>.</description></item>
///   <item><description><b>POSIX:</b> posix_openpt + grantpt + unlockpt + ptsname.</description></item>
/// </list>
///
/// <para><b>Dispose contract:</b> Disposal closes the <i>slave</i> side
/// first, then the master side. On POSIX, closing the master before the
/// slave would SIGHUP any child whose controlling terminal is the slave.
/// On Windows, <c>ClosePseudoConsole</c> is invoked before the outward-facing
/// pipe handles are disposed, because closing those handles first can
/// deadlock the console thread. Callers spawning a child against
/// <see cref="SlaveHandle"/> / <see cref="SlaveFileDescriptor"/> must dup or
/// detach the slave before disposing this PTY. After
/// <see cref="IAsyncDisposable.DisposeAsync"/>, all handle accessors return
/// invalid values, <see cref="Input"/> / <see cref="Output"/> are closed,
/// and <see cref="Resize"/> becomes a no-op.</para>
/// </summary>
internal interface IPty : IAsyncDisposable
{
    /// <summary>
    /// Stream that writes bytes <i>into</i> the PTY (toward the child process's
    /// stdin / terminal). On Windows this wraps the input pipe handed to the
    /// ConPTY; on POSIX it writes to the master fd.
    /// </summary>
    Stream Input { get; }

    /// <summary>
    /// Stream that reads bytes <i>from</i> the PTY (from the child process's
    /// stdout/stderr). On Windows this wraps the output pipe from the ConPTY;
    /// on POSIX it reads from the master fd.
    /// </summary>
    Stream Output { get; }

    /// <summary>
    /// Opaque handle to the slave side of the PTY, used by the spawn code
    /// (PTY-2) to attach the child process.
    ///
    /// <para>Windows: the <c>HPCON</c> returned by <c>CreatePseudoConsole</c>.
    /// Pass this to <c>UpdateProcThreadAttribute</c> with
    /// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> when calling
    /// <c>CreateProcess</c>.</para>
    ///
    /// <para>POSIX: <see cref="IntPtr.Zero"/>; use <see cref="SlaveFileDescriptor"/> instead.</para>
    /// </summary>
    IntPtr SlaveHandle { get; }

    /// <summary>
    /// POSIX slave file descriptor. The child <c>fork+exec</c> path
    /// (PTY-2) dup2s this onto stdin/stdout/stderr and then <c>ioctl(TIOCSCTTY)</c>s
    /// to make the slave the controlling terminal of the child.
    ///
    /// <para>Windows: <c>-1</c>; use <see cref="SlaveHandle"/> instead.</para>
    /// </summary>
    int SlaveFileDescriptor { get; }

    /// <summary>
    /// POSIX slave device path (e.g. <c>/dev/pts/3</c> on Linux,
    /// <c>/dev/ttysNNN</c> on macOS). The <see cref="PtySpawner"/> POSIX path
    /// uses this to <c>open()</c> the slave inside the child process (after
    /// <c>posix_spawn</c> has made the child a session leader), so the slave
    /// becomes the child's controlling terminal without needing the launcher
    /// to hold an open slave fd.
    ///
    /// <para>Windows: <c>null</c>; use <see cref="SlaveHandle"/> (HPCON) instead.</para>
    /// </summary>
    string? SlaveName { get; }

    /// <summary>
    /// Resize the PTY's window dimensions. Safe to call concurrently with
    /// I/O on <see cref="Input"/> / <see cref="Output"/>. After
    /// <see cref="IAsyncDisposable.DisposeAsync"/>, calls become a no-op.
    /// </summary>
    /// <param name="cols">Visible columns (width in characters).</param>
    /// <param name="rows">Visible rows (height in characters).</param>
    void Resize(short cols, short rows);
}

/// <summary>
/// Factory that selects the correct <see cref="IPty"/> adapter for the
/// current platform.
/// </summary>
internal static class PtyAllocator
{
    /// <summary>
    /// Allocate a pseudo-terminal sized to (<paramref name="cols"/>, <paramref name="rows"/>).
    ///
    /// <para>Returns a fully-constructed <see cref="IPty"/> ready for the
    /// PTY-2 spawn step. Caller owns disposal.</para>
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// Windows builds older than 10.0.17763 (1809), or unsupported platforms.
    /// </exception>
    public static ValueTask<IPty> AllocateAsync(short cols, short rows)
    {
        if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols), "cols must be positive");
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows), "rows must be positive");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ConPtyAdapter.AllocateAsync(cols, rows);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            return UnixPtyAdapter.AllocateAsync(cols, rows);
        }

        throw new PlatformNotSupportedException(
            $"PTY allocation not supported on this platform: {RuntimeInformation.OSDescription}");
    }
}
