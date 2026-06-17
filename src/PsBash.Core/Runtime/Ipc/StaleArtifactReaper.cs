using System.Diagnostics;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// One-shot janitor for the <c>%TEMP%/ps-bash</c> (or <c>$TMPDIR/ps-bash</c>)
/// directory. Over time this dir accumulates per-session endpoint sockets
/// (<c>host-*.sock</c>), their metadata sidecars (<c>*.host.json</c>), and
/// spawn locks (<c>spawn-*.lock</c>) from hosts/launchers that were killed
/// (OOM, SIGKILL, machine reboot) before they could clean up after themselves —
/// observed in the wild as ~100+ stale sockets and ~250+ stale locks.
///
/// None of this leakage causes the wedge directly, but it is unbounded growth
/// and it muddies diagnostics. The reaper deletes ONLY artifacts whose owning
/// process is provably gone, so it can never disturb a live host:
/// <list type="bullet">
///   <item>a <c>*.host.json</c> sidecar whose recorded PID is not a running
///   <c>ps-bash-host</c> ⇒ delete the sidecar and its companion socket;</item>
///   <item>a <c>spawn-*.lock</c> we can open exclusively ⇒ its owner released
///   it (dead or done) ⇒ delete; a lock still held by a live owner fails the
///   exclusive open and is left untouched.</item>
/// </list>
/// Best-effort throughout: every operation is wrapped so a permission or race
/// failure can never break host startup. Cheap (a flat-dir scan) and run once
/// per host launch, off the hot path.
/// </summary>
public static class StaleArtifactReaper
{
    private const string HostProcessName = "ps-bash-host";

    /// <summary>Reap stale artifacts under the canonical ps-bash temp dir.</summary>
    public static void Reap() => Reap(Path.Combine(Path.GetTempPath(), "ps-bash"));

    /// <summary>Reap stale artifacts under <paramref name="dir"/> (test seam).</summary>
    public static int Reap(string dir)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
        }
        catch { return 0; }

        removed += ReapDeadSidecars(dir);
        removed += ReapReleasedLocks(dir);
        return removed;
    }

    private static int ReapDeadSidecars(string dir)
    {
        int removed = 0;
        string[] sidecars;
        try { sidecars = Directory.GetFiles(dir, "*.host.json"); }
        catch { return 0; }

        foreach (var sidecar in sidecars)
        {
            try
            {
                var meta = ReadSidecar(sidecar);
                // A sidecar we cannot parse carries no trustworthy owner — leave
                // it; a live host may be mid-rewrite. Only act on a clearly-dead
                // owner.
                if (meta is null) continue;
                if (IsHostAlive(meta.Pid)) continue;

                // Owner is gone. Delete the sidecar and, for a unix endpoint, the
                // companion socket file (sidecar path == endpoint + ".host.json").
                TryDelete(sidecar);
                removed++;
                if (sidecar.EndsWith(".host.json", StringComparison.Ordinal))
                {
                    var socket = sidecar[..^".host.json".Length];
                    if (File.Exists(socket)) { TryDelete(socket); removed++; }
                }
            }
            catch { /* best effort per file */ }
        }
        return removed;
    }

    private static int ReapReleasedLocks(string dir)
    {
        int removed = 0;
        string[] locks;
        try { locks = Directory.GetFiles(dir, "spawn-*.lock"); }
        catch { return 0; }

        foreach (var lockFile in locks)
        {
            // The owner holds the lock with FileShare.None for its whole life. If
            // we can open it exclusively, no live owner holds it ⇒ safe to delete.
            // A still-held lock throws IOException (sharing violation) and is left.
            try
            {
                using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // opened exclusively — owner is gone
                }
                TryDelete(lockFile);
                removed++;
            }
            catch (IOException) { /* still held by a live owner — leave it */ }
            catch { /* permission/race — leave it */ }
        }
        return removed;
    }

    private static HostMetadata? ReadSidecar(string sidecarPath)
    {
        // HostMetadata.TryRead derives the path from (scheme, endpoint); here we
        // already have the concrete sidecar path, so read it directly. Unix
        // sidecars are "<endpoint>.host.json"; pipe sidecars are
        // "<pipe-name>.host.json" under the temp dir. Either way the scheme only
        // affects path derivation, which we bypass — the JSON body carries the
        // authoritative scheme/endpoint. Reuse TryRead by passing the matching
        // (scheme, endpoint) is awkward, so parse via the unix convention which
        // PathFor("unix", endpoint) reproduces exactly for any sidecar path.
        var endpoint = sidecarPath.EndsWith(".host.json", StringComparison.Ordinal)
            ? sidecarPath[..^".host.json".Length]
            : sidecarPath;
        return HostMetadata.TryRead("unix", endpoint);
    }

    private static bool IsHostAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            // A recycled PID could belong to an unrelated process; only treat it
            // as alive when it is actually a ps-bash-host. Anything else ⇒ the
            // host is gone and its artifacts are reapable.
            return string.Equals(p.ProcessName, HostProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }   // no such process
        catch (InvalidOperationException) { return false; }
        catch { return true; }                          // unsure ⇒ don't reap
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
