using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Retires a set of hosts as each becomes idle (<see cref="HostRetire"/>), bounded by a deadline: what an install
/// runs after swapping the binaries, so the old build's daemons go away without interrupting a command in flight
/// (`ps-bash host drain`, install-local.ps1). A host whose endpoint now answers for another process (its endpoint
/// was retired and a new host bound it) is no longer the one listed, and is left alone. A host another listed host is
/// anchored on is claimed while that one lives, and retired after it, since its exit would hang up that one's command.
/// </summary>
public static class HostDrain
{
    /// <summary>
    /// A host to retire. <c>AnchorPid</c> is the listed host this one is anchored on (<c>--session-anchor</c>), if any:
    /// this host exits, hanging up its command, once that one is gone, so that one is held and retired only after it.
    /// </summary>
    public sealed record Target(int Pid, string Scheme, string Endpoint, int? AnchorPid = null);

    /// <summary>
    /// Retired: claimed while idle and told to shut down. Gone: the process had exited. Replaced: another host owns the
    /// endpoint now. Busy: still running a command at the deadline. Unresponsive: alive but never answered a claim.
    /// </summary>
    public enum State { Retired, Gone, Replaced, Busy, Unresponsive }

    public sealed record Result(Target Target, State State);

    /// <summary>
    /// Drains <paramref name="targets"/> until each is settled or <paramref name="deadline"/> passes, polling every
    /// <paramref name="poll"/>. <paramref name="isAlive"/> and <paramref name="ownerPid"/> (the pid the endpoint's
    /// sidecar names now, or null when there is none) are test seams; <paramref name="report"/> sees each result as it
    /// is settled.
    /// </summary>
    public static async Task<IReadOnlyList<Result>> DrainAsync(
        IReadOnlyList<Target> targets, TimeSpan deadline, TimeSpan poll, Func<int, bool> isAlive,
        Func<Target, int?> ownerPid, Action<Result>? report, CancellationToken ct)
    {
        var results = new List<Result>();
        var pending = new List<Target>(targets);
        var unanswered = new HashSet<Target>();
        // Hosts held for their dependents: claimed, so no launcher's command starts on them and no launcher retires them.
        var held = new Dictionary<Target, HostRetire.Claim>();
        var stopAt = DateTime.UtcNow + deadline;
        try
        {
            while (true)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var t = pending[i];
                    State? done = null;
                    if (!isAlive(t.Pid)) done = State.Gone;
                    else if (ownerPid(t) is { } owner && owner != t.Pid) done = State.Replaced;
                    else if (HasLiveDependent(t))
                    {
                        if (!held.ContainsKey(t))
                        {
                            var (claim, refused) = await HostRetire.TryClaimIdleAsync(() => new UnixSocketTransport(t.Endpoint), ct).ConfigureAwait(false);
                            if (claim is not null) { held[t] = claim; unanswered.Remove(t); }
                            else if (refused == HostRetire.Outcome.Unreachable && !isAlive(t.Pid)) done = State.Gone;
                            else if (refused == HostRetire.Outcome.Unreachable) unanswered.Add(t);
                            else unanswered.Remove(t);
                        }
                    }
                    else if (held.Remove(t, out var claim))
                    {
                        await claim.RetireAsync(HostProtocol.DefaultShutdownDeadlineMs, ct).ConfigureAwait(false);
                        done = State.Retired;
                    }
                    else
                    {
                        var outcome = await HostRetire.TryRetireIdleAsync(
                            () => new UnixSocketTransport(t.Endpoint), HostProtocol.DefaultShutdownDeadlineMs, ct).ConfigureAwait(false);
                        if (outcome == HostRetire.Outcome.Retired) done = State.Retired;
                        else if (outcome == HostRetire.Outcome.Unreachable && !isAlive(t.Pid)) done = State.Gone;
                        else if (outcome == HostRetire.Outcome.Unreachable) unanswered.Add(t);
                        else unanswered.Remove(t);
                    }
                    if (done is { } state)
                    {
                        if (held.Remove(t, out var stale)) await stale.DisposeAsync().ConfigureAwait(false);
                        var r = new Result(t, state);
                        results.Add(r);
                        report?.Invoke(r);
                        pending.RemoveAt(i);
                    }
                }
                if (pending.Count == 0 || DateTime.UtcNow >= stopAt) break;
                await Task.Delay(poll, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // At the deadline a held host is released untouched: its dependent is still running a command.
            foreach (var claim in held.Values) await claim.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var t in pending)
        {
            var r = new Result(t, unanswered.Contains(t) ? State.Unresponsive : State.Busy);
            results.Add(r);
            report?.Invoke(r);
        }
        return results;

        bool HasLiveDependent(Target t)
        {
            foreach (var d in pending)
                if (d.AnchorPid == t.Pid && d.Pid != t.Pid && isAlive(d.Pid)) return true;
            return false;
        }
    }

    /// <summary>The pid the endpoint's sidecar names (null when it has none).</summary>
    public static int? SidecarPid(Target t) => HostMetadata.TryRead(t.Scheme, t.Endpoint)?.Pid;
}
