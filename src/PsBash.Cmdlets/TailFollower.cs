using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The polling core of <c>tail -f / -F / --follow=name / --retry</c> (GNU coreutils 9.4 semantics, messages
/// oracle-checked), separated from the cmdlet so it can be driven one deterministic <see cref="Poll"/> at a
/// time by tests.
/// <list type="bullet">
/// <item><b>descriptor</b> mode (<c>-f</c>, <c>--follow=descriptor</c>) follows the file that was opened: a
/// later removal is silent, a file that was MISSING at the start is retried only with <c>--retry</c>.</item>
/// <item><b>name</b> mode (<c>-F</c>, <c>--follow=name</c>) follows whatever the NAME refers to: it reports
/// <c>'f' has become inaccessible</c> when the name vanishes, <c>'f' has appeared;  following new file</c>
/// when it comes back, and <c>'f' has been replaced;  following new file</c> when the file under the name
/// is a different one (a new inode). Without <c>--retry</c> a name that becomes inaccessible is dropped.</item>
/// <item>A file that shrinks reports <c>f: file truncated</c> and is re-read from the start.</item>
/// <item>Only COMPLETE lines are emitted; a trailing fragment waits for its newline
/// (<see cref="InvokeBashTailCommand.SplitFollowChunk"/>).</item>
/// </list>
/// <b>Intentional difference:</b> the replacement check runs on every poll, not only after
/// <c>--max-unchanged-stats</c> quiet polls (it is accepted and validated, but a superset of GNU's
/// reopen cadence), and descriptor mode follows by path (a Windows file cannot be renamed while open).
/// </summary>
internal sealed class TailFollower
{
    internal sealed class Target
    {
        public string Display = "";
        public string Path = "";
        /// <summary>Read position (bytes already emitted).</summary>
        public long Pos;
        /// <summary>The file is currently being followed.</summary>
        public bool Open;
        /// <summary>No further polling (could not be opened without retry, or became inaccessible in name mode without retry).</summary>
        public bool Dead;
        public ulong Ident;
        public bool HasIdent;
    }

    private readonly List<Target> _targets;
    private readonly bool _byName;
    private readonly bool _retry;
    private readonly Action<string> _warn;
    private const long ReadCap = 64L << 20;

    internal TailFollower(IEnumerable<Target> targets, bool byName, bool retry, Action<string> warn)
    {
        _targets = targets.ToList();
        _byName = byName;
        _retry = retry;
        _warn = warn;
    }

    /// <summary>True while at least one target can still produce output.</summary>
    internal bool AnyLeft => _targets.Any(t => !t.Dead);

    /// <summary>
    /// Marks the initial state of a target the cmdlet already printed (<paramref name="openedAtLength"/>
    /// = the length it was read up to) or could not open (<paramref name="openedAtLength"/> null).
    /// Without retry an unopened target is dropped (GNU: "no files remaining" when none is left).
    /// </summary>
    internal void Start(Target t, long? openedAtLength)
    {
        if (openedAtLength is { } len)
        {
            t.Open = true;
            t.Pos = len;
            RememberIdent(t);
        }
        else if (!_retry)
        {
            // Unopenable and not retrying (-F is --follow=name --retry, so it never lands here).
            t.Dead = true;
        }
    }

    private static string Quote(string s) => "'" + s + "'";

    private void RememberIdent(Target t)
    {
        t.HasIdent = FileIdentity.TryGetInode(t.Path, out var id);
        t.Ident = id;
    }

    /// <summary>
    /// One pass over every live target. <paramref name="emit"/> receives each target that produced complete
    /// new lines together with those lines (already CR-stripped, no terminators).
    /// </summary>
    internal void Poll(Action<Target, List<string>> emit)
    {
        foreach (var t in _targets)
        {
            if (t.Dead) continue;

            bool exists = File.Exists(t.Path);
            if (!exists)
            {
                if (t.Open)
                {
                    t.Open = false;
                    if (_byName)
                    {
                        _warn($"tail: {Quote(t.Display)} has become inaccessible: No such file or directory");
                        if (!_retry) t.Dead = true;
                    }
                    // descriptor mode: the open file just keeps its (now unlinked) data — nothing to read.
                }
                continue;
            }

            if (!t.Open)
            {
                // Appeared (initially missing with retry, or came back in name mode): follow from the start.
                if (!_byName && t.Pos > 0) continue; // descriptor mode never re-attaches
                _warn($"tail: {Quote(t.Display)} has appeared;  following new file");
                t.Open = true;
                t.Pos = 0;
                RememberIdent(t);
            }
            else if (_byName && t.HasIdent && FileIdentity.TryGetInode(t.Path, out var now) && now != t.Ident)
            {
                _warn($"tail: {Quote(t.Display)} has been replaced;  following new file");
                t.Pos = 0;
                t.Ident = now;
            }

            long length;
            try { length = new FileInfo(t.Path).Length; }
            catch { continue; }

            if (length < t.Pos)
            {
                _warn($"tail: {t.Display}: file truncated");
                t.Pos = 0;
            }
            if (length == t.Pos) continue;

            try
            {
                using var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(t.Pos, SeekOrigin.Begin);
                long avail = length - t.Pos;
                int toRead = (int)Math.Min(avail, ReadCap);
                var buffer = new byte[toRead];
                int read = fs.Read(buffer, 0, toRead);
                var (advance, lines) = InvokeBashTailCommand.SplitFollowChunk(buffer, read, avail, ReadCap);
                if (advance == 0) continue; // an incomplete last line: wait for its newline
                t.Pos += advance;
                if (lines.Count > 0) emit(t, lines);
            }
            catch (IOException)
            {
                // The file is busy or just rotated: try again on the next poll.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Whether the process <paramref name="pid"/> is still running (<c>tail --pid</c>). A PID that cannot be
    /// found is treated as dead, which ends the follow after one final poll.
    /// </summary>
    internal static bool IsAlive(long pid)
    {
        if (pid <= 0) return true;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Exception) { return true; }
    }
}
