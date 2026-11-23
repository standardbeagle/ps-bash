using PsBash.Core.Platform;

namespace PsBash.Cmdlets;

/// <summary>
/// A <c>nohup CMD</c> child as a job: <c>nohup CMD &amp;</c> enters the shell's job table through
/// <see cref="BashBgJobTable.AddProcessJob"/>, as bash's does, so <c>wait</c> (bare, <c>$!</c>, <c>%N</c>, <c>-n</c>),
/// <c>jobs</c> and <c>kill %N</c> see it; a foreground <c>nohup CMD</c> waits on <see cref="AsyncResult"/>.
/// <para>
/// The shape is the one <c>Complete-BashBgJob</c> reads from a job in a host of its own (<c>BashJobProcess</c>):
/// <see cref="Pid"/>, <see cref="Code"/>, <see cref="AsyncResult"/>, <see cref="EndInvoke"/>, <see cref="Streams"/>,
/// <see cref="Stop()"/>, <see cref="Dispose"/>; its output went to its files, so there is none to replay. Unlike that job,
/// the process was started to outlive the shell: neither <see cref="Dispose"/> nor the shell's teardown ends it, only a
/// <c>kill</c> (<see cref="Send"/>). It holds the shell's stdin pipe it was given (<see cref="PipeHandoff.Lease"/>) until it
/// exits, then lets go of it BEFORE it reports done, so the shell's next read finds what CMD left.
/// </para>
/// </summary>
public sealed class DetachedJob : IDisposable
{
    private readonly DetachedProcess.DetachedChild _child;
    private readonly ManualResetEvent _done = new(false);
    private readonly Handle _handle;
    private PipeHandoff.Lease? _stdin;
    private volatile int _sent;

    /// <summary>Owns <paramref name="child"/> and <paramref name="stdin"/> from here: both are let go of when the child exits.</summary>
    internal DetachedJob(DetachedProcess.DetachedChild child, PipeHandoff.Lease? stdin)
    {
        _child = child;
        _stdin = stdin;
        _handle = new Handle(_done);
        Pid = child.Pid;
        child.OnExit(Exited);
    }

    /// <summary>The OS process id (<c>$!</c>, <c>jobs -p</c>).</summary>
    public int Pid { get; }

    /// <summary>The exit status, once <see cref="AsyncResult"/> completed: 128+N when signal N ended it.</summary>
    public int Code { get; private set; }

    /// <summary>The signal a <c>kill</c> sent that ended the process, or null: <c>wait</c> reports it (Write-BashBgSignalReport).</summary>
    public int? Signal => _sent != 0 && Code == 128 + _sent && _done.WaitOne(0) ? _sent : null;

    /// <summary>Completes when the process has exited and the stdin pipe is let go of.</summary>
    public IAsyncResult AsyncResult => _handle;

    /// <summary>Mirrors <c>PowerShell.Streams</c>: the child's stderr went to its file, so there is none here.</summary>
    public StreamSet Streams { get; } = new();

    /// <summary>The job's stdout to replay: none, it went to its file.</summary>
    public string[] EndInvoke(IAsyncResult _) => [];

    /// <summary><c>kill -N</c> (<see cref="DetachedProcess.DetachedChild.Signal"/>); <c>wait</c> then collects the real status.</summary>
    public bool Send(int signal)
    {
        _sent = signal;
        return _child.Signal(signal);
    }

    /// <summary>SIGTERM, as a job record's <c>Stop()</c>.</summary>
    public void Stop() => Send(OsSignals.SIGTERM);

    /// <summary>Forget the job: the process runs on (it was started to outlive the shell) and is let go of when it exits.</summary>
    public void Dispose() { }

    private void Exited(int code)
    {
        Code = code;
        try { Interlocked.Exchange(ref _stdin, null)?.Dispose(); }
        finally
        {
            _child.Dispose();
            _done.Set();
        }
    }

    public sealed class StreamSet
    {
        /// <summary>No stderr frames: the child wrote its own.</summary>
        public string[] Error => [];
    }

    private sealed class Handle(ManualResetEvent done) : IAsyncResult
    {
        public bool IsCompleted => done.WaitOne(0);
        public WaitHandle AsyncWaitHandle => done;
        public object? AsyncState => null;
        public bool CompletedSynchronously => false;
    }
}
