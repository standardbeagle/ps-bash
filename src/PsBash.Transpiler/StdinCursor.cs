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
    private readonly LinkedList<object> _buffer = new();
    private bool _sourceDone;

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

    /// <summary>Number of records that can be read right now: 0 only at end of input, else the buffered count (at least 1).</summary>
    public int Count
    {
        get
        {
            FillOne();
            return _buffer.Count;
        }
    }

    /// <summary>Take the next record. Throws <see cref="InvalidOperationException"/> at end of input (as <see cref="Queue{T}"/> does).</summary>
    public object Dequeue()
    {
        FillOne();
        if (_buffer.First is null) throw new InvalidOperationException("The stdin cursor is empty.");
        var item = _buffer.First.Value;
        _buffer.RemoveFirst();
        return item;
    }

    /// <summary>Append a record after everything buffered (eager fill; a lazy source still follows).</summary>
    public void Enqueue(object item) => _buffer.AddLast(item);

    /// <summary>Put records back at the FRONT (the unread rest of a record <c>read</c> took one line from), in order.</summary>
    public void PushFront(IEnumerable<object> items)
    {
        var list = items as IList<object> ?? items.ToList();
        for (int i = list.Count - 1; i >= 0; i--)
            _buffer.AddFirst(list[i]);
    }

    /// <summary>Run when the owning scope ends (<see cref="Close"/>): stops a background producer feeding the source.</summary>
    public Action? OnClose { get; set; }

    /// <summary>End the scope: release the source and stop whatever produces it. Idempotent.</summary>
    public void Close()
    {
        _sourceDone = true;
        _buffer.Clear();
        var close = OnClose;
        OnClose = null;
        try { close?.Invoke(); } catch { /* best effort */ }
        try { _source?.Dispose(); } catch { /* best effort */ }
    }

    private void FillOne()
    {
        if (_buffer.Count > 0 || _sourceDone || _source is null) return;
        if (_source.MoveNext())
        {
            _buffer.AddLast(_source.Current);
            return;
        }
        _sourceDone = true;
        _source.Dispose();
    }
}
