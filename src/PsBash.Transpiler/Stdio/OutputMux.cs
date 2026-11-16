namespace PsBash.Core.Stdio;

/// <summary>
/// One shell session's output path for producers that are not the foreground command: today the background jobs
/// (<c>cmd &amp;</c>), later nested shells and the foreground command itself (docs/specs/stdio-messaging.md).
/// <para>
/// Each producer opens an <see cref="OutputChannel"/> and writes <c>(fd, text)</c> chunks to it. While a sink is
/// attached (a command of the session is running, so its stdout/stderr exist) a chunk goes straight to the sink, in
/// arrival order across every channel, on the producer's thread: whatever back-pressure the sink applies (the IPC
/// output queue, a slow terminal) blocks the producer, as a full pipe blocks a writer. While no sink is attached the
/// chunk is queued, and each channel may hold at most <see cref="WindowChars"/> queued characters (its credit); a
/// producer out of credit blocks until a sink is attached (the queue drains in order) or its
/// <c>abandoned</c> check says it is being stopped. Memory therefore stays bounded without dropping output.
/// </para>
/// <para>
/// Lives in the Transpiler assembly because the host (which attaches the sink) and the cmdlets / module (which open
/// the channels) both load it.
/// </para>
/// </summary>
public sealed class OutputMux
{
    /// <summary>Queued characters one channel may hold while no sink is attached: a pipe buffer's worth.</summary>
    public const int DefaultWindowChars = 64 * 1024;

    /// <summary>How often a blocked producer re-checks whether it was abandoned (stopped, killed).</summary>
    internal const int AbandonPollMs = 50;

    private readonly object _gate = new();
    private readonly Queue<Chunk> _pending = new();
    private Action<int, string>? _sink;
    private long _pendingChars;
    private int _nextId;
    private int _open;

    public OutputMux(int windowChars = DefaultWindowChars)
    {
        WindowChars = windowChars > 0 ? windowChars : DefaultWindowChars;
    }

    /// <summary>Credit of one channel, in characters, while no sink is attached.</summary>
    public int WindowChars { get; }

    /// <summary>The line terminator producers should use for record boundaries (LF on the framed IPC stream).</summary>
    public string NewLine { get; private set; } = "\n";

    /// <summary>Channels opened and not yet closed: producers that may still write.</summary>
    public int OpenChannels => Volatile.Read(ref _open);

    /// <summary>Characters queued while detached, all channels together.</summary>
    public long PendingChars { get { lock (_gate) return _pendingChars; } }

    public bool IsAttached { get { lock (_gate) return _sink is not null; } }

    /// <summary>Open a producer channel. <paramref name="name"/> is diagnostic only.</summary>
    public OutputChannel Open(string name)
    {
        Interlocked.Increment(ref _open);
        return new OutputChannel(this, Interlocked.Increment(ref _nextId), name);
    }

    internal void OnClosed()
    {
        Interlocked.Decrement(ref _open);
        lock (_gate) Monitor.PulseAll(_gate);
    }

    /// <summary>
    /// Route every channel to <paramref name="sink"/> (called with fd 1, 2, ... and the exact text). Chunks queued
    /// while detached are delivered first, in order. The sink is called under this mux's lock, so it must not call
    /// back into the mux; it may block (back-pressure).
    /// </summary>
    public void Attach(Action<int, string> sink, string newLine = "\n")
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sink = sink;
            NewLine = newLine;
            while (_pending.Count > 0)
            {
                var c = _pending.Dequeue();
                Release(c);
                Deliver(sink, c.Fd, c.Text);
            }
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Stop delivering; later writes queue (bounded per channel). Waits for an in-flight delivery to end.</summary>
    public void Detach()
    {
        lock (_gate) _sink = null;
    }

    /// <summary>
    /// Remove and return everything queued while detached, in order, returning the credit to the producers. For a
    /// consumer that is not a sink (the interactive prompt prints job output between commands).
    /// </summary>
    public IReadOnlyList<KeyValuePair<int, string>> TakePending()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return Array.Empty<KeyValuePair<int, string>>();
            var list = new List<KeyValuePair<int, string>>(_pending.Count);
            while (_pending.Count > 0)
            {
                var c = _pending.Dequeue();
                Release(c);
                list.Add(new KeyValuePair<int, string>(c.Fd, c.Text));
            }
            Monitor.PulseAll(_gate);
            return list;
        }
    }

    /// <summary>Block up to <paramref name="timeoutMs"/> for something to be queued. True when something is.</summary>
    public bool WaitForPending(int timeoutMs)
    {
        lock (_gate)
        {
            if (_pending.Count > 0) return true;
            Monitor.Wait(_gate, timeoutMs);
            return _pending.Count > 0;
        }
    }

    internal bool Write(OutputChannel channel, int fd, string text, Func<bool>? abandoned)
    {
        if (string.IsNullOrEmpty(text)) return true;
        lock (_gate)
        {
            while (true)
            {
                if (_sink is { } sink)
                {
                    Deliver(sink, fd, text);
                    return true;
                }
                // Out of credit unless this chunk fits the window; an empty channel always takes one chunk, so a
                // single chunk larger than the window cannot wedge its producer forever.
                if (channel.PendingChars == 0 || channel.PendingChars + text.Length <= WindowChars)
                {
                    _pending.Enqueue(new Chunk(channel, fd, text));
                    channel.PendingChars += text.Length;
                    _pendingChars += text.Length;
                    Monitor.PulseAll(_gate);
                    return true;
                }
                if (abandoned?.Invoke() == true) return false;
                Monitor.Wait(_gate, AbandonPollMs);
            }
        }
    }

    private void Release(Chunk c)
    {
        c.Channel.PendingChars -= c.Text.Length;
        _pendingChars -= c.Text.Length;
    }

    private static void Deliver(Action<int, string> sink, int fd, string text)
    {
        // A broken sink (the launcher went away) is the sink owner's to handle; a producer must not die of it.
        try { sink(fd, text); }
        catch (Exception) { }
    }

    private readonly record struct Chunk(OutputChannel Channel, int Fd, string Text);
}

/// <summary>One producer's channel into an <see cref="OutputMux"/>: writes <c>(fd, text)</c>; closed when the producer ends.</summary>
public sealed class OutputChannel : IDisposable
{
    private readonly OutputMux _mux;
    private int _closed;

    internal OutputChannel(OutputMux mux, int id, string name)
    {
        _mux = mux;
        Id = id;
        Name = name;
    }

    public int Id { get; }
    public string Name { get; }

    /// <summary>Characters of this channel queued in the mux (guarded by the mux lock).</summary>
    internal long PendingChars { get; set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Write <paramref name="text"/> to <paramref name="fd"/> (1 stdout, 2 stderr). Blocks while the channel is out of
    /// credit; returns false (the text is dropped) only when <paramref name="abandoned"/> reports the producer is being
    /// stopped while it waited.
    /// </summary>
    public bool Write(int fd, string text, Func<bool>? abandoned = null) => _mux.Write(this, fd, text, abandoned);

    /// <summary>The producer is done; text it queued is still delivered.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _mux.OnClosed();
    }

    public void Dispose() => Close();
}
