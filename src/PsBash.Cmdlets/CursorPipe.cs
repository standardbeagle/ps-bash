using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The ONE pipe a <see cref="StdinCursor"/>'s input is in while a program reads it: bash's single open file description
/// of a stdin that the shell, its programs and its background jobs all read (docs/specs/stdio-messaging.md §4.7).
/// <para>
/// <b>Holders.</b> Every program that reads the cursor holds the pipe (<see cref="Hold"/>, then <see cref="Release"/>
/// once it exited): the foreground's through the process stdin (<see cref="NativeStdinBridge.Enter"/>), any other's as
/// its fd 0 (<see cref="NativeStdinBridge.ForChild"/>). A pump thread moves the cursor's records into the pipe as fast
/// as the pipe takes them.
/// </para>
/// <para>
/// <b>The shell's own reads</b> (<c>read</c>, <c>cat</c>, a <c>while read</c> loop: <see cref="StdinCursor.TryDequeue(out object?)"/>)
/// read the pipe too while it is up, one byte at a time up to a newline, as bash's <c>read</c> does on a pipe, so a
/// program that reads nothing (<c>sleep 1 &amp; read a</c>) leaves the input where the shell finds it. Reading the
/// records past the pipe instead found them already pumped away.
/// </para>
/// <para>
/// <b>Teardown.</b> The last holder or reader out (never while another still reads) stops the pump, drains what nobody
/// read and puts it back in front of the cursor, THEN detaches; a program or reader that arrives meanwhile waits for
/// that and starts afresh. So the input keeps its order (a second pipe opened during a put-back took the records
/// behind it first).
/// </para>
/// <para>
/// <b>Adopted.</b> A background job in a host process of its own gets the shell's pipe itself (<see cref="Adopt"/>, the
/// read end handed over by <c>PipeHandoff</c>): its cursor's pipe has no pump and no records behind it, its reads and its
/// programs take the bytes the shell's pipe holds, and it never tears down (what the job did not read is the shell's).
/// </para>
/// </summary>
internal sealed class CursorPipe : IStdinTap
{
    private const int PipeBufferSize = 64 * 1024;

    private enum State { Open, Closing, Closed }

    private readonly StdinCursor _cursor;
    // Null for an adopted pipe (another process writes it).
    private readonly AnonymousPipeServerStream? _writer;
    private readonly SafePipeHandle _readEnd;
    private readonly AnonymousPipeClientStream _reader;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread? _pump;
    private readonly object _lock = new();
    // One shell reader at a time takes a whole line (two `read`s, the job's and the shell's, never interleave bytes).
    private readonly object _readLock = new();
    // Records the shell put back (or Count looked at) ahead of the pipe's bytes.
    private readonly LinkedList<object> _front = new();
    private int _holders = 1;
    private int _readers;
    private State _state;

    private CursorPipe(StdinCursor cursor, AnonymousPipeServerStream writer)
    {
        _cursor = cursor;
        _writer = writer;
        _readEnd = writer.ClientSafePipeHandle;
        // The shell's reads and the final drain; the handle stays the read end programs get (ReadEnd).
        _reader = new AnonymousPipeClientStream(PipeDirection.In, _readEnd);
        _pump = new Thread(Pump) { IsBackground = true, Name = "ps-bash stdin pipe pump" };
    }

    // Adopted: the hold this constructor counts (_holders = 1) is the adoption's own and is never released.
    private CursorPipe(StdinCursor cursor, SafePipeHandle adopted)
    {
        _cursor = cursor;
        _readEnd = adopted;
        _reader = new AnonymousPipeClientStream(PipeDirection.In, adopted);
    }

    /// <summary>
    /// The read end a program gets as its stdin. Inheritable on Windows (an explicit handle list or the process stdin);
    /// POSIX <c>dup2</c>s it onto fd 0, which clears close-on-exec there only.
    /// </summary>
    public SafePipeHandle ReadEnd => _readEnd;

    /// <summary>
    /// A cursor whose input is <paramref name="readEnd"/>, a pipe another process fills and holds (the shell that started
    /// this job host, through <c>PipeHandoff</c>): every read and every program takes from that pipe, at the position the
    /// sender's own readers share. Closing the cursor lets go of the handle.
    /// </summary>
    internal static StdinCursor Adopt(SafePipeHandle readEnd)
    {
        ArgumentNullException.ThrowIfNull(readEnd);
        var cursor = new StdinCursor(); // nothing behind the pipe: its bytes are the whole input
        var pipe = new CursorPipe(cursor, readEnd);
        cursor.TryAttachTap(pipe);
        cursor.OnClose = readEnd.Dispose;
        return cursor;
    }

    /// <summary>
    /// Hold the cursor's pipe for one program, opening it when there is none. Null when the cursor's wait cannot be
    /// cancelled (the pump could never be stopped) or the OS refuses the pipe.
    /// </summary>
    public static CursorPipe? Hold(StdinCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (!cursor.CanCancelWait) return null;
        while (true)
        {
            switch (cursor.Tap)
            {
                case CursorPipe pipe:
                    if (pipe.TryHold()) return pipe;
                    continue; // it ended (and detached): open the next one
                case null:
                    var fresh = Open(cursor);
                    if (fresh is null) return null;
                    if (cursor.TryAttachTap(fresh))
                    {
                        fresh._pump!.Start();
                        return fresh;
                    }
                    fresh.DisposeUnstarted();
                    continue;
                default:
                    return null;
            }
        }
    }

    private static CursorPipe? Open(StdinCursor cursor)
    {
        AnonymousPipeServerStream? writer = null;
        try
        {
            var readEnd = OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None;
            // The server end is the WRITE end and is never inheritable, or a program would never see end of input.
            writer = new AnonymousPipeServerStream(PipeDirection.Out, readEnd, PipeBufferSize);
            return new CursorPipe(cursor, writer);
        }
        catch (Exception)
        {
            writer?.Dispose();
            return null;
        }
    }

    private void DisposeUnstarted()
    {
        _reader.Dispose();
        _writer!.Dispose();
        _cancel.Dispose();
    }

    private bool TryHold()
    {
        lock (_lock)
        {
            while (_state == State.Closing) Monitor.Wait(_lock);
            if (_state == State.Closed) return false;
            _holders++;
            return true;
        }
    }

    /// <summary>A program that held the pipe is gone (once per <see cref="Hold"/>).</summary>
    public void Release()
    {
        bool last;
        lock (_lock)
        {
            _holders--;
            last = TakeTeardown();
        }
        if (last) Teardown();
    }

    // Under _lock: the last one out starts the teardown (the pipe is Open only while someone holds or reads it).
    private bool TakeTeardown()
    {
        if (_holders > 0 || _readers > 0 || _state != State.Open) return false;
        _state = State.Closing;
        return true;
    }

    // A shell read starts: false when the pipe is gone (read the cursor). With no program left the read ends the pipe
    // first and reads the cursor, so the shell never reads a pipe one byte at a time for nothing.
    private bool EnterRead()
    {
        lock (_lock)
        {
            while (_state == State.Closing) Monitor.Wait(_lock);
            if (_state == State.Closed) return false;
            if (_holders > 0 || _readers > 0)
            {
                _readers++;
                return true;
            }
            _state = State.Closing;
        }
        Teardown();
        return false;
    }

    private void ExitRead()
    {
        bool last;
        lock (_lock)
        {
            _readers--;
            last = TakeTeardown();
        }
        if (last) Teardown();
    }

    bool? IStdinTap.TryTake(out object? record)
    {
        record = null;
        if (!EnterRead()) return null;
        try
        {
            lock (_readLock)
            {
                lock (_lock)
                {
                    if (_front.First is { } first)
                    {
                        record = first.Value;
                        _front.RemoveFirst();
                        return true;
                    }
                }
                return ReadLine(out record);
            }
        }
        finally { ExitRead(); }
    }

    bool? IStdinTap.HasNext()
    {
        if (!EnterRead()) return null;
        try
        {
            lock (_readLock)
            {
                lock (_lock)
                    if (_front.First is not null) return true;
                if (!ReadLine(out var record)) return false;
                lock (_lock) _front.AddLast(record!);
                return true;
            }
        }
        finally { ExitRead(); }
    }

    bool IStdinTap.TryPushFront(IList<object> items)
    {
        lock (_lock)
        {
            while (_state == State.Closing) Monitor.Wait(_lock);
            if (_state == State.Closed) return false;
            for (int i = items.Count - 1; i >= 0; i--) _front.AddFirst(items[i]);
            return true;
        }
    }

    // One line of the pipe, byte by byte (a program may read the next byte): false at end of input. End of input is
    // real here: only the teardown stops the pump early, and it never runs while a shell read is in progress.
    private bool ReadLine(out object? record)
    {
        var line = new MemoryStream();
        var one = new byte[1];
        try
        {
            while (_reader.Read(one, 0, 1) == 1)
            {
                if (one[0] == (byte)'\n')
                {
                    record = RawBytes.GetString(line.GetBuffer().AsSpan(0, (int)line.Length));
                    return true;
                }
                line.WriteByte(one[0]);
            }
        }
        catch (IOException)
        {
            // Broken pipe: end of input.
        }
        catch (ObjectDisposedException)
        {
            // An adopted pipe whose cursor was closed: end of input.
        }
        record = line.Length > 0
            ? BashRuntime.TextRecord(RawBytes.GetString(line.GetBuffer().AsSpan(0, (int)line.Length)), unterminated: true)
            : null;
        return record is not null;
    }

    private void Pump()
    {
        var ct = _cancel.Token;
        try
        {
            while (_cursor.TryTakeUpstream(ct, out var record))
            {
                // A record taken is always written: the teardown's drain gets it back.
                var bytes = NativeStdinBridge.RecordBytes(record);
                if (bytes.Length > 0)
                {
                    _writer!.Write(bytes, 0, bytes.Length);
                    _writer.Flush();
                }
                if (ct.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Teardown while the input was silent.
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            // End of input (or teardown): close the write end, so a reader sees end of input and the drain completes.
            try { _writer!.Dispose(); } catch { /* already closed */ }
        }
    }

    // Stop the pump, take back what nobody read, put it in front of the cursor, detach, and let waiters go on.
    private void Teardown()
    {
        _cancel.Cancel();
        // Drain until the pump closes the write end: it may be blocked writing into a full pipe, and only reading
        // unblocks it. Every other holder of the pipe only has the read end.
        var unread = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = _reader.Read(buffer, 0, buffer.Length)) > 0) unread.Write(buffer, 0, n);
        }
        catch (IOException)
        {
            // Broken pipe: nothing more to read.
        }
        _pump!.Join();
        _reader.Dispose();
        _writer!.Dispose();
        _cancel.Dispose();

        var back = new List<object>();
        lock (_lock)
        {
            back.AddRange(_front);
            _front.Clear();
        }
        if (unread.Length > 0)
            back.AddRange(NativeStdinBridge.Records(RawBytes.GetString(unread.GetBuffer().AsSpan(0, (int)unread.Length))));
        if (back.Count > 0) _cursor.PushFrontUpstream(back);
        _cursor.DetachTap(this);
        lock (_lock)
        {
            _state = State.Closed;
            Monitor.PulseAll(_lock);
        }
    }
}
