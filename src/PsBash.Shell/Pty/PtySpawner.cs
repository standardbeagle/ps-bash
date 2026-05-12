using System.Runtime.InteropServices;

namespace PsBash.Shell.Pty;

/// <summary>
/// Spawns a child process with an <see cref="IPty"/>'s slave side attached as
/// the child's stdin, stdout, and stderr (PTY-2). The launcher uses this in
/// interactive mode so the host's <c>System.Console</c> is wired to a real
/// terminal fd. <c>-c</c> / script mode does <i>not</i> route through this
/// type — it keeps the redirected-pipe IPC path so daemon reuse and framing
/// remain intact.
///
/// <para><b>POSIX contract:</b> the child calls <c>setsid()</c> and then
/// <c>ioctl(TIOCSCTTY)</c> on the slave fd BEFORE <c>execve</c> so the slave
/// becomes the controlling terminal of the new session. Without this step,
/// reading <c>$TTY</c> / running interactive subprocesses like <c>vim</c>
/// inside the host would silently misbehave. The launcher itself must not be
/// a session leader holding a controlling tty when this happens — we run from
/// a process group that does not own a tty (the launcher's stdio is the real
/// terminal, not a slave we just opened).</para>
///
/// <para><b>POSIX async-signal safety:</b> between <c>fork()</c> and
/// <c>execve()</c> only async-signal-safe libc calls are made
/// (<c>setsid</c>, <c>ioctl</c>, <c>dup2</c>, <c>close</c>, <c>execve</c>,
/// <c>_exit</c>). Argv and envp are pre-built as native UTF-8 buffers in the
/// parent BEFORE the fork; no managed allocation occurs in the child path.</para>
///
/// <para><b>Windows contract:</b> the launcher passes the
/// <see cref="IPty.SlaveHandle"/> (HPCON) via
/// <c>STARTUPINFOEX</c>'s attribute list using
/// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE</c>, and
/// <c>CreateProcessW</c> is called with <c>EXTENDED_STARTUPINFO_PRESENT</c>.
/// The HPCON owns the slave-side pipe handles; the launcher must keep the
/// <see cref="IPty"/> alive until the child exits.</para>
/// </summary>
internal abstract class PtySpawner : IAsyncDisposable
{
    /// <summary>
    /// PID of the spawned child. On Windows this is the
    /// <c>PROCESS_INFORMATION.dwProcessId</c>; on POSIX, the return value of
    /// <c>fork()</c> in the parent.
    /// </summary>
    public abstract int Pid { get; }

    /// <summary>Wait for the spawned child to exit. Returns the exit code.</summary>
    public abstract Task<int> WaitForExitAsync(CancellationToken ct);

    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Spawn <paramref name="executablePath"/> with <paramref name="arguments"/>
    /// and the slave of <paramref name="pty"/> wired as stdin/stdout/stderr.
    /// <paramref name="environment"/> is merged onto the launcher's current
    /// environment (existing names overwritten, new names added). Pass
    /// <c>null</c> for no overrides.
    /// </summary>
    /// <remarks>
    /// The returned spawner owns the OS-level handles to the child process
    /// (Windows: process handle; POSIX: pid). Disposing it does <i>not</i>
    /// kill the child — callers should <see cref="WaitForExitAsync"/> first,
    /// or send a signal externally.
    /// </remarks>
    public static PtySpawner Spawn(
        string executablePath,
        IReadOnlyList<string> arguments,
        IPty pty,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(pty);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
#pragma warning disable CA1416 // platform-guarded above
            return WindowsPtySpawner.SpawnInternal(executablePath, arguments, pty, environment);
#pragma warning restore CA1416
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
#pragma warning disable CA1416 // POSIX-guarded above
            return PosixPtySpawner.SpawnInternal(executablePath, arguments, pty, environment);
#pragma warning restore CA1416
        }

        throw new PlatformNotSupportedException(
            $"PTY-attached spawn not supported on this platform: {RuntimeInformation.OSDescription}");
    }
}
