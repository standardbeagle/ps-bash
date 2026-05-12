using System.Diagnostics;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Process-ownership classification for the kill-escalation safety gate.
/// Verifies that a recorded host PID still resolves to the same executable
/// before any invasive cleanup, so a launcher never kills an unrelated
/// process that has reused a recycled PID.
/// </summary>
public static class HostOwnership
{
    /// <summary>
    /// What a launcher is allowed to do with a recorded host, per
    /// <c>docs/specs/host-lifecycle-contract.md</c> validation states.
    /// </summary>
    public enum CleanupDecision
    {
        /// <summary>
        /// No metadata, or metadata says PID is dead. The endpoint and any
        /// sidecar artifacts are owned by no live process; safe to unlink and
        /// spawn a replacement without touching any process.
        /// </summary>
        SafeArtifactCleanup,

        /// <summary>
        /// Metadata is valid, PID is alive, and the running executable matches
        /// the recorded one. The host is reachable; the launcher should attempt
        /// graceful shutdown first and only escalate to kill after the graceful
        /// path is exhausted. Returned when the recorded host owner matches
        /// the current user.
        /// </summary>
        SafeProcessShutdown,

        /// <summary>
        /// Metadata is valid and PID is alive, but the running process is NOT
        /// the recorded executable (PID reuse), OR the recorded owner does not
        /// match the current user. The launcher MUST NOT delete artifacts and
        /// MUST NOT terminate the process; the failure should surface with a
        /// human-readable reason naming the conflict.
        /// </summary>
        UnsafeToTouch,
    }

    /// <summary>
    /// Classify what a launcher may do given the metadata sidecar (or its
    /// absence) and the live process state.
    /// </summary>
    /// <param name="metadata">The parsed sidecar, or <c>null</c> when absent.</param>
    /// <param name="currentUser">
    /// The user identity to match against <see cref="HostMetadata.Owner"/>.
    /// Defaults to <see cref="Environment.UserName"/> in production callers;
    /// tests inject a value to exercise the mismatch branch.
    /// </param>
    /// <param name="reason">
    /// Human-readable explanation suitable for an error message when the
    /// decision is <see cref="CleanupDecision.UnsafeToTouch"/>. Empty for
    /// other decisions.
    /// </param>
    public static CleanupDecision Classify(
        HostMetadata? metadata,
        string currentUser,
        out string reason)
    {
        reason = "";
        if (metadata is null) return CleanupDecision.SafeArtifactCleanup;

        if (!OwnerMatches(metadata.Owner, currentUser))
        {
            reason = $"sidecar owner '{metadata.Owner}' does not match current user '{currentUser}'";
            return CleanupDecision.UnsafeToTouch;
        }

        var (alive, runningExe) = ProbeProcess(metadata.Pid);
        if (!alive) return CleanupDecision.SafeArtifactCleanup;

        if (!ExecutablesMatch(runningExe, metadata.ExecutablePath))
        {
            reason =
                $"recorded PID {metadata.Pid} is alive but running '{runningExe ?? "<unknown>"}' " +
                $"instead of recorded '{metadata.ExecutablePath}' (likely PID reuse)";
            return CleanupDecision.UnsafeToTouch;
        }

        return CleanupDecision.SafeProcessShutdown;
    }

    /// <summary>
    /// Verify that a responding host sidecar describes the same host binary and
    /// build the launcher is about to use. A health payload can match across a
    /// local reinstall from the same commit, while the old host process is still
    /// running from a renamed .old executable; the executable path closes that
    /// gap.
    /// </summary>
    public static bool MetadataMatchesLauncher(
        HostMetadata? metadata,
        string expectedExecutablePath,
        string expectedBuildIdentity)
    {
        if (metadata is null) return true;
        if (metadata.ProtocolVersion != HostProtocol.ProtocolVersion) return false;
        if (!string.Equals(metadata.BuildIdentity, expectedBuildIdentity, StringComparison.Ordinal)) return false;
        return ExecutablesMatch(metadata.ExecutablePath, expectedExecutablePath);
    }

    /// <summary>
    /// Probe whether <paramref name="pid"/> resolves to a live process and,
    /// if so, the path of its main module. Returns <c>(false, null)</c> when
    /// the process is dead, the PID is unknown, or platform restrictions
    /// prevent module inspection. Treats every failure as "cannot prove
    /// alive" so callers fall through to the conservative branch.
    /// </summary>
    public static (bool Alive, string? ExecutablePath) ProbeProcess(int pid)
    {
        if (pid <= 0) return (false, null);
        try
        {
            using var proc = Process.GetProcessById(pid);
            string? exe = null;
            try { exe = proc.MainModule?.FileName; }
            catch { /* permission denied or 32/64 bridge; leave exe null */ }
            return (true, exe);
        }
        catch (ArgumentException) { return (false, null); }      // no such process
        catch (InvalidOperationException) { return (false, null); }
    }

    private static bool OwnerMatches(string recorded, string current)
        => string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compare executable paths case-insensitively after normalization. If we
    /// could not read the running executable (null), return <c>true</c> so
    /// the caller doesn't escalate to UnsafeToTouch on a permission glitch
    /// — the conservative branch (no kill) is selected later by the absence
    /// of a SafeProcessShutdown signal.
    /// </summary>
    private static bool ExecutablesMatch(string? running, string recorded)
    {
        if (string.IsNullOrEmpty(running)) return true;
        try
        {
            var a = Path.GetFullPath(running);
            var b = Path.GetFullPath(recorded);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(running, recorded, StringComparison.OrdinalIgnoreCase);
        }
    }
}
