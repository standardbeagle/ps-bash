using System.Diagnostics;

namespace PsBash.Cmdlets;

/// <summary>
/// The file behind a <c>&gt; f</c> / <c>&gt;&gt; f</c> / <c>2&gt; f</c> redirect (<see cref="InvokeBashRedirectCommand"/>):
/// buffered for throughput, but never holding output back the way a plain 64 KB buffer did, where
/// <c>{ step1; long_step; } &gt; log</c> left <c>log</c> empty until the group ended. bash writes each
/// command's output as it is produced; the redirect cannot see where one command ends (in PowerShell's
/// pipeline the producer runs on, on the same thread, as soon as a record has been written), so it
/// approximates that with three rules:
/// <list type="number">
/// <item><b>Trickle: write at once.</b> A record that arrives <see cref="SyncFlushGap"/> or more after the
/// previous one (or is the first) is written through to the file before control returns to the producer:
/// <c>{ echo a; cat f; } &gt; f</c> sees <c>a</c>, and a slow producer's log is current for <c>tail -f</c>.
/// The cost is one write per record only when records are at least that far apart, so at most a few
/// percent of the producer's own time.</item>
/// <item><b>Burst: buffer, flush on idle.</b> Records closer together than that (<c>seq 1000000 &gt; f</c>)
/// stay in the buffer, and a timer writes them out once they have waited <see cref="IdleFlushDelay"/>,
/// so the tail of a burst reaches the file within that delay even while the producer goes quiet.</item>
/// <item><b>In-process readers flush first.</b> Every ps-bash file read opens through
/// <see cref="BashFileSystem.OpenRead"/>, which calls <see cref="FlushPending"/>: <c>{ echo a; echo b; cat f; } &gt; f</c>
/// (the burst <c>a b</c> then a reader on the same thread) sees both lines, as in bash.</item>
/// </list>
/// What remains (docs/specs/intentional-differences.md): a reader OUTSIDE ps-bash's file reads (a native
/// program, or a size check like <c>test -s</c> / <c>stat</c>) that runs right after a burst in the same
/// redirected group can miss up to <see cref="IdleFlushDelay"/> of the burst's tail.
/// <para>Every touch of the stream is under <see cref="_gate"/>: the timer and a reader's
/// <see cref="FlushPending"/> run on other threads.</para>
/// </summary>
internal sealed class RedirectFileWriter : IDisposable
{
    /// <summary>A record this long after the previous one is written through at once (1 ms).</summary>
    internal static readonly long SyncFlushGap = Stopwatch.Frequency / 1000;

    /// <summary>The longest buffered output waits for the idle timer (ms).</summary>
    internal const int IdleFlushDelay = 50;

    private static readonly object RegistryGate = new();
    private static readonly List<RedirectFileWriter> Open = [];
    // Writers holding unwritten bytes: lets FlushPending (on every file read) return at once in the usual case.
    private static int s_dirtyWriters;

    private readonly object _gate = new();
    private FileStream? _stream;
    private Timer? _timer;
    private bool _timerArmed;
    private bool _dirty;
    private long _lastWrite = long.MinValue;

    internal RedirectFileWriter(FileStream stream)
    {
        _stream = stream;
        lock (RegistryGate) Open.Add(this);
    }

    /// <summary>Write one record's bytes: through to the file when the producer is slow, else into the buffer.</summary>
    internal void Write(byte[] bytes)
    {
        lock (_gate)
        {
            if (_stream is null) return;
            _stream.Write(bytes, 0, bytes.Length);
            long now = Stopwatch.GetTimestamp();
            bool trickle = _lastWrite == long.MinValue || now - _lastWrite >= SyncFlushGap;
            _lastWrite = now;
            if (trickle) { FlushLocked(); return; }
            MarkDirtyLocked();
        }
    }

    /// <summary>Write every redirect's buffered output to its file: a ps-bash command is about to read a file.</summary>
    internal static void FlushPending()
    {
        if (Volatile.Read(ref s_dirtyWriters) == 0) return;
        RedirectFileWriter[] writers;
        lock (RegistryGate) writers = [.. Open];
        foreach (var w in writers)
        {
            lock (w._gate)
            {
                // Best-effort: the writer's own next write or close reports a failing disk.
                try { w.FlushLocked(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }
    }

    private void MarkDirtyLocked()
    {
        if (!_dirty)
        {
            _dirty = true;
            Interlocked.Increment(ref s_dirtyWriters);
        }
        if (_timerArmed) return;
        _timerArmed = true;
        _timer ??= new Timer(static state => ((RedirectFileWriter)state!).OnIdle(), this, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(IdleFlushDelay, Timeout.Infinite);
    }

    private void FlushLocked()
    {
        if (_dirty)
        {
            _dirty = false;
            Interlocked.Decrement(ref s_dirtyWriters);
        }
        _stream?.Flush();
    }

    private void OnIdle()
    {
        lock (_gate)
        {
            _timerArmed = false;
            if (_stream is null) return;
            // A timer thread must never throw (it would end the host); a full disk surfaces on the next write or close.
            try { FlushLocked(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>Write what is buffered and close the file.</summary>
    public void Dispose()
    {
        lock (RegistryGate) Open.Remove(this);
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _timerArmed = false;
            if (_dirty)
            {
                _dirty = false;
                Interlocked.Decrement(ref s_dirtyWriters);
            }
            var stream = _stream;
            _stream = null;
            stream?.Dispose();
        }
    }
}
