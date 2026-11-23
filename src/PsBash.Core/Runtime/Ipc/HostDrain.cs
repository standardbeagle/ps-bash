using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Retires a set of hosts as each becomes idle (<see cref="HostRetire"/>), bounded by a deadline: what an install
/// runs after swapping the binaries, so the old build's daemons go away without interrupting a command in flight
/// (`ps-bash host drain`, install-local.ps1). A host whose endpoint now answers for another process (its endpoint
/// was retired and a new host bound it) is no longer the one listed, and is left alone.
/// </summary>
public static class HostDrain
{
    public sealed record Target(int Pid, string Scheme, string Endpoint);

    /// <summary>
    /// Retired: claimed while idle and told to shut down. Gone: the process had exited. Replaced: another host owns the
    /// endpoint now. Busy: still running a command at the deadline. Unresponsive: alive but never answered a claim.
    /// </summary>
    public enum State { Retired, Gone, Replaced, Busy, Unresponsive }

    public sealed record Result(Target Target, State State);

    /// <param name="ownerPid">The pid the endpoint's sidecar names now, or null when there is none (test seam).</param>
    public static async Task<IReadOnlyList<Result>> DrainAsync(
        IReadOnlyList<Target> targets, TimeSpan deadline, TimeSpan poll, Func<int, bool> isAlive,
        Func<Target, int?> ownerPid, Action<Result>? report, CancellationToken ct)
    {
        var results = new List<Result>();
        var pending = new List<Target>(targets);
        var unanswered = new HashSet<Target>();
        var stopAt = DateTime.UtcNow + deadline;
        while (true)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var t = pending[i];
                State? done = null;
                if (!isAlive(t.Pid)) done = State.Gone;
                else if (ownerPid(t) is { } owner && owner != t.Pid) done = State.Replaced;
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
                    var r = new Result(t, state);
                    results.Add(r);
                    report?.Invoke(r);
                    pending.RemoveAt(i);
                }
            }
            if (pending.Count == 0 || DateTime.UtcNow >= stopAt) break;
            await Task.Delay(poll, ct).ConfigureAwait(false);
        }
        foreach (var t in pending)
        {
            var r = new Result(t, unanswered.Contains(t) ? State.Unresponsive : State.Busy);
            results.Add(r);
            report?.Invoke(r);
        }
        return results;
    }

    /// <summary>The pid the endpoint's sidecar names (null when it has none).</summary>
    public static int? SidecarPid(Target t) => HostMetadata.TryRead(t.Scheme, t.Endpoint)?.Pid;
}
