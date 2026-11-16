namespace PsBash.Core;

/// <summary>
/// The shared standard input of a command (<c>$global:__BashStdIn</c>): a cursor over a LAZY record
/// source plus a pushback buffer. Every stdin-reading command inside the scope pulls records through
/// the same cursor, which is what makes <c>{ read x; sort; }</c> sort the REST.
/// <para>
/// The surface is deliberately a <see cref="Queue{T}"/> look-alike (<see cref="Count"/>,
/// <see cref="Dequeue"/>, <see cref="Enqueue"/>) because the emitted PowerShell
/// (<c>PsBuild.StdinFeed</c>: <c>while ($q.Count -gt 0) { $q.Dequeue() }</c>) was written against a queue.
/// <see cref="Count"/> is the one non-queue member: it PULLS one record from the source when nothing is
/// buffered (blocking until one arrives or the source ends) and reports 0 only at end of input, so
/// <c>Count -gt 0</c> means "another record exists" without ever draining the source ahead of its consumer.
/// That laziness is what lets a reader that stops early (<c>head -n1</c>) leave the rest unread, and what
/// lets the launcher's own stdin (an unbounded, possibly never-ending pipe) back a command.
/// </para>
/// Lives in the Transpiler assembly (a leaf every layer references, like <see cref="RawBytes"/>) so the
/// host that builds the cursor and the cmdlets that consume it share one type.
/// </summary>
public sealed class StdinCursor
{
    private readonly IEnumerator<object>? _source;
    private readonly IStdinRecordSource? _waitable;
    private readonly LinkedList<object> _buffer = new();
    // A background job (`cmd &`) inherits the stdin of the compound command that started it, so the job's runspace
    // and the shell's may read one cursor at once (bash: two processes on one pipe). _gate guards the buffer; _fillGate
    // serialises pulls from the source, and is never held while the buffer is changed by a thread that is not pulling,
    // so a put-back or an enqueue cannot wait behind a reader blocked on a silent source.
    private readonly object _gate = new();
    private readonly object _fillGate = new();
    private volatile bool _sourceDone;

    /// <summary>
    /// True for the SHELL's own stdin (the launcher's forwarded stdin), false for the stdin of a compound command
    /// (a pipe, <c>&lt; file</c>, a here-doc). bash gives a background job the latter but not the former: with job
    /// control off, <c>cmd &amp;</c> at the top of a script reads <c>/dev/null</c>.
    /// </summary>
    public bool IsShellStdin { get; init; }

    /// <summary>An empty cursor, filled by <see cref="Enqueue"/> (the eager compound-scope form).</summary>
    public StdinCursor()
    {
        _sourceDone = true;
    }

    /// <summary>A cursor over <paramref name="source"/>, enumerated one record at a time on demand.</summary>
    public StdinCursor(IEnumerator<object> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>A cursor over <paramref name="source"/>, enumerated one record at a time on demand.</summary>
    public StdinCursor(IEnumerable<object> source) : this((source ?? throw new ArgumentNullException(nameof(source))).GetEnumerator())
    {
    }

    /// <summary>
    /// A cursor over a source whose wait for the next record can be CANCELLED (<see cref="TryDequeue"/>), so a
    /// background reader — the pump feeding a native program's stdin — can be stopped while the source is
    /// silent without losing or half-taking a record.
    /// </summary>
    public StdinCursor(IStdinRecordSource source)
    {
        _waitable = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// True when a wait for the next record can be cancelled (<see cref="TryDequeue"/> honours its token
    /// while blocked) — a cancellable source, or none left to wait on. An enumerator-backed cursor blocks in
    /// <c>MoveNext</c> regardless of the token.
    /// </summary>
    public bool CanCancelWait => _waitable is not null || _sourceDone || _source is null;

    /// <summary>
    /// Take the next record, waiting for one until <paramref name="ct"/> is cancelled. False at end of input.
    /// Cancellation throws <see cref="OperationCanceledException"/> and consumes nothing.
    /// </summary>
    public bool TryDequeue(CancellationToken ct, out object? record)
    {
        record = null;
        if (IsBufferEmpty())
        {
            ct.ThrowIfCancellationRequested();
            FillOne(ct);
        }
        lock (_gate)
        {
            if (_buffer.First is null) return false;
            record = _buffer.First.Value;
            _buffer.RemoveFirst();
            return true;
        }
    }

    /// <summary>Number of records that can be read right now: 0 only at end of input, else the buffered count (at least 1).</summary>
    public int Count
    {
        get
        {
            FillOne();
            lock (_gate) return _buffer.Count;
        }
    }

    private bool IsBufferEmpty()
    {
        lock (_gate) return _buffer.First is null;
    }

    /// <summary>Take the next record. Throws <see cref="InvalidOperationException"/> at end of input (as <see cref="Queue{T}"/> does).</summary>
    public object Dequeue()
    {
        FillOne();
        lock (_gate)
        {
            if (_buffer.First is null) throw new InvalidOperationException("The stdin cursor is empty.");
            var item = _buffer.First.Value;
            _buffer.RemoveFirst();
            return item;
        }
    }

    /// <summary>Append a record after everything buffered (eager fill; a lazy source still follows).</summary>
    public void Enqueue(object item)
    {
        lock (_gate) _buffer.AddLast(item);
    }

    /// <summary>Put records back at the FRONT (the unread rest of a record <c>read</c> took one line from), in order.</summary>
    public void PushFront(IEnumerable<object> items)
    {
        var list = items as IList<object> ?? items.ToList();
        lock (_gate)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                _buffer.AddFirst(list[i]);
        }
    }

    /// <summary>Run when the owning scope ends (<see cref="Close"/>): stops a background producer feeding the source.</summary>
    public Action? OnClose { get; set; }

    /// <summary>End the scope: release the source and stop whatever produces it. Idempotent.</summary>
    public void Close()
    {
        _sourceDone = true;
        lock (_gate) _buffer.Clear();
        var close = OnClose;
        OnClose = null;
        try { close?.Invoke(); } catch { /* best effort */ }
        try { _source?.Dispose(); } catch { /* best effort */ }
        try { _waitable?.Dispose(); } catch { /* best effort */ }
    }

    private void FillOne(CancellationToken ct = default)
    {
        if (_sourceDone || !IsBufferEmpty()) return;
        lock (_fillGate)
        {
            if (_sourceDone || !IsBufferEmpty()) return;
            if (_waitable is not null)
            {
                // Cancellation propagates out before anything is taken.
                if (_waitable.TryReadNext(ct, out var record))
                {
                    Enqueue(record ?? "");
                    return;
                }
                _sourceDone = true;
                _waitable.Dispose();
                return;
            }
            if (_source is null) return;
            if (_source.MoveNext())
            {
                Enqueue(_source.Current);
                return;
            }
            _sourceDone = true;
            _source.Dispose();
        }
    }
}

/// <summary>
/// A record source whose wait for the next record honours a <see cref="CancellationToken"/>: the backing of a
/// <see cref="StdinCursor"/> that a background reader must be able to abandon while the input is silent (the
/// launcher's forwarded stdin, a lazy producer).
/// </summary>
public interface IStdinRecordSource : IDisposable
{
    /// <summary>
    /// Block until the next record (true), end of input (false), or cancellation of <paramref name="ct"/>
    /// (<see cref="OperationCanceledException"/>, nothing consumed).
    /// </summary>
    bool TryReadNext(CancellationToken ct, out object? record);
}
