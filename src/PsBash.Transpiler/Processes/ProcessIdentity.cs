using System.Diagnostics;
using System.Globalization;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// A process's pid paired with its start time (UTC ticks). The start time makes the
/// identity unique over time: two different processes that reuse one PID still get
/// distinct identities. Used as the automatic session anchor
/// (<c>ProcessAncestry.FindSessionAnchor</c>) and handed to the daemon it keys
/// as <c>--session-anchor=&lt;pid&gt;:&lt;startTicks&gt;</c>, so the daemon can exit once its
/// session is gone (see <c>docs/specs/host-lifecycle-contract.md</c> §Session anchor lifetime).
/// </summary>
public readonly record struct ProcessIdentity(int Pid, long StartTimeUtcTicks)
{
    /// <summary>The host argument name that carries a session anchor.</summary>
    public const string SessionAnchorArg = "--session-anchor";

    /// <summary>Start time of <paramref name="process"/> in UTC ticks — the one formula every identity uses.</summary>
    public static long StartTicksOf(Process process) => process.StartTime.ToUniversalTime().Ticks;

    /// <summary>
    /// The identity of <paramref name="pid"/>, a child this process just started. Never throws: an unreadable start
    /// time gives ticks 0. The child already runs, so this is bookkeeping, and the OS refuses it for a child that runs
    /// as another user (a setuid program such as macOS <c>/bin/ps</c> or <c>sudo</c>: EPERM) or that already exited.
    /// Every spawn takes its identity here, so a refused read can never turn a started child into a failed spawn
    /// (and leave it unreaped).
    /// </summary>
    public static ProcessIdentity Of(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessIdentity(pid, StartTicksOf(process));
        }
        catch (Exception)
        {
            return new ProcessIdentity(pid, 0);
        }
    }

    /// <summary><c>&lt;pid&gt;:&lt;startTicks&gt;</c>, the <see cref="SessionAnchorArg"/> value.</summary>
    public string ToArgumentValue()
        => Pid.ToString(CultureInfo.InvariantCulture) + ":" + StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parse a <see cref="ToArgumentValue"/> string; false for anything malformed.</summary>
    public static bool TryParse(string? value, out ProcessIdentity identity)
    {
        identity = default;
        if (string.IsNullOrEmpty(value)) return false;
        var colon = value.IndexOf(':');
        if (colon <= 0) return false;
        if (!int.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            return false;
        if (!long.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            return false;
        identity = new ProcessIdentity(pid, ticks);
        return true;
    }

    /// <summary>
    /// True while THIS process is running: the pid exists, has not exited, and still has
    /// the recorded start time. A recycled pid (different start time) is not this process.
    /// Returns true when the start time cannot be read for a live pid (access denied):
    /// an unreadable process must not shut a daemon down.
    /// </summary>
    public bool IsRunning()
    {
        Process process;
        try { process = Process.GetProcessById(Pid); }
        catch (ArgumentException) { return false; }      // no such pid
        catch (InvalidOperationException) { return false; }
        using (process)
        {
            try
            {
                if (process.HasExited) return false;
                return StartTicksOf(process) == StartTimeUtcTicks;
            }
            catch (InvalidOperationException) { return false; } // exited between calls
            catch (System.ComponentModel.Win32Exception) { return true; } // unreadable, not proven gone
            catch (NotSupportedException) { return true; }
        }
    }
}
