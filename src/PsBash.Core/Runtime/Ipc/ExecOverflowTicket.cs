using System.Security.Cryptography;
using System.Text;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// The memory bound on overflow hosts (docs/specs/executor-slots.md §Overflow). When every executor slot of a session
/// is busy, an unrelated launcher may run its command on an overflow host instead of waiting — but each host costs
/// ~70-150 MB private memory, so at most <see cref="Max"/> run per session. A ticket is one of <see cref="Max"/>
/// exclusively-opened files under <see cref="PsBashRuntimeDirectory"/>: held for the command's life, dropped by the OS
/// on <see cref="Dispose"/> or process death (a crashed holder never leaks one). Ticket <see cref="Index"/> i runs its
/// command on overflow slot <see cref="IpcTransportFactory.OverflowSlot"/>(i), a slot daemon that stays warm between
/// overflow commands (idle window as any slot) instead of a host started and killed for each one.
/// <see cref="FileShare.None"/> is the deliberate lock exception to <c>temp-files.md</c>, as in <see cref="HostSpawnLock"/>.
/// </summary>
internal sealed class ExecOverflowTicket : IDisposable
{
    /// <summary>Default overflow hosts per session (<c>PSBASH_EXEC_OVERFLOW_MAX</c>; 0 = never overflow, always wait).</summary>
    public const int DefaultMax = 2;

    /// <summary>Default wait for a slot before overflowing (<c>PSBASH_EXEC_OVERFLOW_MS</c>): a burst of short commands frees a slot well inside it, so it starts no host.</summary>
    public const int DefaultGraceMs = 2000;

    /// <summary>Most overflow slots a session may have, whatever <c>PSBASH_EXEC_OVERFLOW_MAX</c> says: a slot name carries at most two digits.</summary>
    public const int Ceiling = 16;

    private FileStream? _stream;

    private ExecOverflowTicket(FileStream? stream, int index) { _stream = stream; Index = index; }

    /// <summary>Which of the session's tickets this is (0-based); it names the overflow slot the command runs on.</summary>
    public int Index { get; }

    /// <summary>False for the unguarded ticket a runtime directory without lock files gives: another launcher may hold the same one.</summary>
    public bool Exclusive => Volatile.Read(ref _stream) is not null;

    public static int Max()
        => int.TryParse(Environment.GetEnvironmentVariable("PSBASH_EXEC_OVERFLOW_MAX"), out var n) && n >= 0
            ? Math.Min(n, Ceiling) : DefaultMax;

    public static int GraceMs()
        => int.TryParse(Environment.GetEnvironmentVariable("PSBASH_EXEC_OVERFLOW_MS"), out var ms) && ms >= 0 ? ms : DefaultGraceMs;

    /// <summary>
    /// Take a free ticket of the session whose base endpoint is <paramref name="baseEndpoint"/>, or null when all
    /// <paramref name="max"/> are held. A runtime directory that cannot hold lock files gives an unguarded ticket:
    /// waiting there would be the unbounded stall this exists to end.
    /// </summary>
    public static ExecOverflowTicket? TryAcquire(string scheme, string baseEndpoint, int max)
    {
        if (max <= 0) return null;
        string dir;
        try { dir = PsBashRuntimeDirectory.EnsureDirectory(); }
        catch { return new ExecOverflowTicket(null, 0); }

        string stem = "overflow-" + scheme + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseEndpoint)))[..16];
        for (int i = 0; i < max; i++)
        {
            try
            {
                return new ExecOverflowTicket(new FileStream(
                    Path.Combine(dir, $"{stem}-{i}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), i);
            }
            catch (IOException) { }                 // held by another launcher's overflow host
            catch (UnauthorizedAccessException) { } // same, seen as access denied on Windows
        }
        return null;
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
