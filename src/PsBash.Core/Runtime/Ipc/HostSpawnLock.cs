using System.Security.Cryptography;
using System.Text;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Cross-process single-flight lock guarding host <b>spawn</b> on a shared
/// (<see cref="Lifetime.Daemon"/>) endpoint. When N launchers in the same session
/// race to start that session's daemon from cold, all N would otherwise reach the spawn path and —
/// because <see cref="UnixSocketTransport"/> unlinks-before-bind and the Windows
/// pipe allows 16 server instances — leave N-1 orphan runspaces (the
/// concurrent-cold-start thundering herd). This lock lets exactly one launcher
/// run the spawn path; the others wait for its host and connect.
///
/// <para>Backed by an exclusively-opened file under <c>{TEMP}/ps-bash/</c>.
/// <see cref="FileShare.None"/> is the deliberate exception to
/// <c>temp-files.md</c>'s "shared temp files use FileShare.ReadWrite" rule — a
/// lock's entire purpose is mutual exclusion. The OS drops the handle on
/// <see cref="Dispose"/> OR on process death, so a crashed lock-holder never
/// deadlocks the herd (the next launcher re-acquires). Unlike
/// <see cref="System.Threading.Mutex"/>, a file handle is not thread-affine, so
/// it is safe to acquire before an <c>await</c> and release after the
/// continuation resumes on another thread — which the async spawn path does.</para>
///
/// <para>The lock is scoped to <c>scheme:endpoint</c> — and the endpoint is now
/// per-session — so each session arbitrates its own spawn independently, and an
/// isolated test endpoint never serializes against a real session daemon (or another
/// test's endpoint). The <see cref="Lifetime.PerInvocation"/> path never
/// acquires it: those endpoints are process-local and uncontended.</para>
/// </summary>
internal sealed class HostSpawnLock : IDisposable
{
    private FileStream? _stream;

    private HostSpawnLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Try to acquire the spawn lock for <paramref name="scheme"/>:<paramref name="endpoint"/>
    /// without blocking. Returns the held lock, or <c>null</c> if another launcher
    /// holds it (the caller should wait for that launcher's host instead of
    /// spawning), or if the lock file cannot be created at all — in which case the
    /// caller degrades to spawning unguarded, which is never worse than the
    /// pre-lock behavior.
    /// </summary>
    public static HostSpawnLock? TryAcquire(string scheme, string endpoint)
    {
        string path;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ps-bash");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"spawn-{scheme}-{ShortHash(endpoint)}.lock");
        }
        catch
        {
            return null; // cannot establish a lock file — degrade to unguarded spawn
        }

        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new HostSpawnLock(fs);
        }
        catch (IOException) { return null; }                 // held by a concurrent launcher
        catch (UnauthorizedAccessException) { return null; } // transient ACL/permission race
    }

    /// <summary>
    /// Stable short hex digest of the endpoint so the lock filename is bounded and
    /// filesystem-safe regardless of the endpoint's characters (socket paths
    /// contain separators; pipe names do not).
    /// </summary>
    private static string ShortHash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(24);
        for (int i = 0; i < 12; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        var s = _stream;
        _stream = null;
        try { s?.Dispose(); } catch { /* best effort — OS releases the handle regardless */ }
    }
}
