using PsBash.Core.Runtime.Ipc;

namespace PsBash.Host.Server;

/// <summary>
/// Ties a shared daemon's lifetime to the session that keyed its endpoint. The launcher
/// passes the session anchor (<c>--session-anchor=&lt;pid&gt;:&lt;startTicks&gt;</c>); this
/// watcher polls it and, once that process is gone (or its pid now belongs to a different
/// process), shortens the idle timeout to <see cref="Grace"/>. A command still running
/// <see cref="HangupGrace"/> after that is hung up: the host shuts down, which stops it.
/// </summary>
/// <remarks>
/// <para>Why this is safe: the endpoint name embeds the anchor's pid AND start time, so once
/// the anchor is gone no new launcher can ever resolve this endpoint. The only remaining work
/// is what is already connected. Contrast <see cref="ParentDeathWatcher"/>, which cancels
/// outright — right for a private host whose one launcher died, wrong for a daemon that may
/// still be serving a command.</para>
/// <para>Why a running command is hung up rather than awaited: a launcher already exits when its
/// own parent dies, and a dead launcher's command is stopped when the host sees the disconnect.
/// A command still running after the session is gone therefore has nobody waiting for it: its
/// launcher hangs off a process that outlived the session, or the disconnect went unseen. Awaiting
/// it kept the daemon alive for good: after a Claude Code restart, daemons logged "exiting 5s after
/// the last command" and never exited; seven orphaned hosts on that desktop ran for 25+ minutes at
/// about half a core each.
/// bash does the same to a session's jobs when the session leader dies (SIGHUP).</para>
/// <para>GH #7: an agent whose per-call shell became the anchor (its own process name is not a
/// recognized session host) got one daemon per call, each idling 600 s.</para>
/// </remarks>
public sealed class SessionAnchorWatcher : IAsyncDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Idle time allowed after the anchor is gone, before the daemon exits.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>How long a command may keep running after the anchor is gone before it is hung up.</summary>
    public static readonly TimeSpan HangupGrace = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _watcherCts = new();
    private readonly Task _watchTask;

    private SessionAnchorWatcher(Func<bool> isAnchorRunning, IdleShutdown idle, CancellationTokenSource hostShutdown,
        string anchorText, TimeSpan pollInterval, TimeSpan grace, TimeSpan hangupGrace, Action<string> recordExitReason)
    {
        _watchTask = WatchAsync(isAnchorRunning, idle, hostShutdown, anchorText, pollInterval, grace, hangupGrace,
            recordExitReason, _watcherCts.Token);
    }

    /// <summary>
    /// Watch <paramref name="anchor"/>, or return null when there is none (explicit endpoint,
    /// <c>PSBASH_SESSION</c>, per-user or nested daemon: idle timeout only).
    /// </summary>
    /// <param name="hostShutdown">Cancelled to hang up a command still running after the session is gone.</param>
    /// <param name="isAnchorRunning">Test seam replacing <see cref="ProcessIdentity.IsRunning"/>.</param>
    /// <param name="recordExitReason">Where the hang-up's exit reason goes (default <see cref="HostLog.SetExitReason"/>).</param>
    public static SessionAnchorWatcher? TryCreate(
        ProcessIdentity? anchor,
        IdleShutdown idle,
        CancellationTokenSource hostShutdown,
        TimeSpan? pollInterval = null,
        TimeSpan? grace = null,
        TimeSpan? hangupGrace = null,
        Func<bool>? isAnchorRunning = null,
        Action<string>? recordExitReason = null)
    {
        if (anchor is not { } id) return null;
        return new SessionAnchorWatcher(
            isAnchorRunning ?? id.IsRunning, idle, hostShutdown, id.ToArgumentValue(),
            pollInterval ?? PollInterval, grace ?? Grace, hangupGrace ?? HangupGrace,
            recordExitReason ?? HostLog.SetExitReason);
    }

    private static async Task WatchAsync(
        Func<bool> isAnchorRunning, IdleShutdown idle, CancellationTokenSource hostShutdown, string anchorText,
        TimeSpan interval, TimeSpan grace, TimeSpan hangupGrace, Action<string> recordExitReason, CancellationToken ct)
    {
        try
        {
            do await Task.Delay(interval, ct).ConfigureAwait(false);
            while (isAnchorRunning());

            HostLog.Write($"session anchor {anchorText} gone; exiting {grace.TotalSeconds:0}s after the last command");
            idle.ShortenTimeout(grace, $"session anchor {anchorText} exited");

            await Task.Delay(hangupGrace, ct).ConfigureAwait(false);
            if (hostShutdown.IsCancellationRequested || idle.InFlight == 0) return; // the idle timer ends it
            var reason = $"session anchor {anchorText} exited; hung up {idle.InFlight} command(s) still running "
                         + $"{hangupGrace.TotalSeconds:0}s later";
            HostLog.Write(reason);
            recordExitReason(reason);
            hostShutdown.TryCancel();
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _watcherCts.Cancel();
        try { await _watchTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        _watcherCts.Dispose();
    }
}
