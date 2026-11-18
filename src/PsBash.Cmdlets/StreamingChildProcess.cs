using System.Diagnostics;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// A child process whose output streams to the cmdlet as it runs, with no time budget: a nested shell
/// (<c>bash build.sh | tee log</c>, <c>sh -c make</c>) must show progress and may run for hours. Start it with
/// <see cref="BashRuntime.StartChildProcessStreaming"/>.
/// <para>
/// <b>Threads.</b> Reader threads move stdout / stderr text into ONE queue in arrival order; everything that
/// touches the pipeline (<c>WriteObject</c>, the stderr writer) runs on the cmdlet's pipeline thread in
/// <see cref="PumpAvailable"/>, <see cref="WriteStdin"/> and <see cref="Complete"/>. <see cref="Kill"/> is the
/// one member safe from another thread (<c>StopProcessing</c>).
/// </para>
/// <para>
/// <b>Merged stderr.</b> Two pipes have no order between them: lines the child wrote to fd 1 and fd 2 in turn arrive
/// in whatever order the two reader threads happen to run. When the caller's stderr is merged into its stdout the
/// child is spawned with ONE pipe for both (<see cref="ChildStdioMode.MergeStderr"/>) and there is one reader: what it
/// reads is stdout, in the order written.
/// </para>
/// <para>
/// <b>Records.</b> stdout is emitted line by line exactly as <see cref="BashRuntime.EmitBashLines"/> would split the
/// whole output (CRLF→LF, one record per <c>\n</c>, an unterminated last line flagged); stderr is emitted as it
/// arrives (a prompt without a newline shows at once), CRLF→LF.
/// </para>
/// <para>
/// <b>Back-pressure.</b> At most <see cref="BufferChars"/> of output wait between the reader threads and the pipeline
/// thread, about one pipe buffer. A reader that would go past it waits until the pipeline thread drains, so the child
/// blocks on its own write, as it would on a full pipe: a fast child under a slow (or busy) pipeline holds bounded
/// memory, not all of its output.
/// </para>
/// <para>
/// <b>Exit.</b> Pipe EOF ends the reading, not the wait: a child that closed its stdout and stderr and keeps running
/// (<c>exec &gt;&amp;- 2&gt;&amp;-; sleep 1; exit 3</c>) is waited for, and its own status is returned, as bash does.
/// The wait ends at the child's exit or at a stop (<see cref="Kill"/>).
/// </para>
/// </summary>
public sealed class StreamingChildProcess : IDisposable
{
    /// <summary>After the child exits, how long its pipes may stay open (a backgrounded grandchild holding them).</summary>
    private static readonly TimeSpan ExitDrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>The most output text (chars) held for the pipeline thread before the readers wait: about a pipe buffer.</summary>
    internal const int BufferChars = 64 * 1024;

    /// <summary>The most one read hands over (chars); the buffer can exceed <see cref="BufferChars"/> by less than this.</summary>
    internal const int ReadChars = 4096;

    private readonly SpawnedChild _child;
    private readonly Thread _exitWatcher;
    // Guarded by its own lock: the readers wait on it for room, the pipeline thread pulses it when it drains.
    private readonly Queue<(bool IsStderr, string Text)> _chunks = new();
    private readonly List<(bool IsStderr, string Text)> _taken = new();
    private int _bufferedChars;
    private bool _released;
    // Cancelled by Kill: ends Complete's wait for the child's exit.
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly ChildOutputAssembler _assembler = new();
    private readonly List<string> _lines = new();
    private Stream? _stdin;
    private int _openStreams;
    private int _killed;
    private bool _completed;

    internal StreamingChildProcess(SpawnedChild child, bool keepStdinOpen)
    {
        _child = child;
        if (keepStdinOpen && child.StandardInput is { } stdin)
            _stdin = stdin.BaseStream;
        else
            try { child.StandardInput?.Close(); } catch { /* the child may not have opened it */ }
        // Wakes the pump loops at exit (they poll HasExited as well, so a late wake only costs one poll interval).
        _exitWatcher = new Thread(() => { try { child.WaitForExit(); } catch { /* disposed */ } Signal(); })
        {
            IsBackground = true,
            Name = "ps-bash streaming child exit",
        };
        _exitWatcher.Start();
        _openStreams = child.StandardError is null ? 1 : 2;
        StartReader(child.StandardOutput, isStderr: false);
        if (child.StandardError is { } stderr) StartReader(stderr, isStderr: true);
    }

    /// <summary>True while the child's stdin is open for <see cref="WriteStdin"/>.</summary>
    public bool StdinOpen => _stdin is not null;

    /// <summary>
    /// Write <paramref name="bytes"/> to the child's stdin, emitting the child's output while the write waits on a
    /// full pipe (the child may print before it reads). False — and stdin is closed — when the child no longer
    /// reads it (exited, or closed fd 0): bash's broken pipe. Pipeline thread only.
    /// </summary>
    public bool WriteStdin(Cmdlet cmdlet, byte[] bytes) => WriteStdin(SinkFor(cmdlet), bytes);

    internal bool WriteStdin(IChildOutputSink sink, byte[] bytes)
    {
        var stdin = _stdin;
        if (stdin is null) return false;
        if (bytes.Length == 0) return true;
        if (HasExited()) { CloseStdin(); return false; }
        var write = Task.Run(() => { stdin.Write(bytes, 0, bytes.Length); stdin.Flush(); });
        var handles = new WaitHandle[] { _signal, ((IAsyncResult)write).AsyncWaitHandle };
        while (!write.IsCompleted)
        {
            Drain(sink, final: false);
            WaitHandle.WaitAny(handles, 200);
        }
        if (write.IsCompletedSuccessfully) return true;
        _ = write.Exception; // observed: a broken pipe is an expected outcome
        CloseStdin();
        return false;
    }

    /// <summary>End of input: the child sees EOF on its stdin. Idempotent.</summary>
    public void CloseStdin()
    {
        if (Interlocked.Exchange(ref _stdin, null) is null) return;
        // Through the writer that owns the stream, so Process.Dispose does not flush a closed one.
        try { _child.StandardInput?.Close(); } catch { /* broken pipe on the final flush; the handle still closes */ }
    }

    /// <summary>Emit whatever output has arrived, without waiting. Pipeline thread only.</summary>
    public void PumpAvailable(Cmdlet cmdlet) => Drain(SinkFor(cmdlet), final: false);

    /// <summary>
    /// Close stdin, emit the child's output until its pipes close, wait for it to exit, and return its exit code. When
    /// the cmdlet is stopping the child is killed and nothing more is emitted. Pipeline thread only; once.
    /// </summary>
    public int Complete(Cmdlet cmdlet) => Complete(SinkFor(cmdlet));

    internal int Complete(IChildOutputSink sink)
    {
        if (_completed) return SafeExitCode();
        _completed = true;
        CloseStdin();
        var exitedAt = (DateTime?)null;
        while (true)
        {
            if (sink.Stopping) { Kill(); return SafeExitCode(); }
            bool streamsDone = Volatile.Read(ref _openStreams) == 0;
            Drain(sink, final: false);
            if (streamsDone) break;
            if (HasExited())
            {
                exitedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - exitedAt > ExitDrainGrace) break;
            }
            _signal.WaitOne(200);
        }
        Drain(sink, final: true);
        // EOF on the pipes ends the reading, not the wait: the child may have closed them and still be running. No time
        // limit (bash waits as long as it runs); a stop ends the wait (Kill cancels the token; Stopping is polled too).
        while (!_child.WaitForExit(200, _stop.Token))
        {
            if (sink.Stopping || Volatile.Read(ref _killed) != 0) { Kill(); break; }
        }
        return SafeExitCode();
    }

    /// <summary>
    /// Kill the child's whole process tree (<see cref="SpawnedChild.KillTree"/>: the Job Object / process group, so a
    /// grandchild whose parent already exited goes too). Safe from any thread; idempotent.
    /// </summary>
    public void Kill()
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0) return;
        try { _child.KillTree(); }
        catch { /* already gone / race */ }
        Release();
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        Signal();
    }

    /// <summary>Pids of the child's tree alive now. Diagnostics and tests.</summary>
    internal IReadOnlyList<int> TreePids() => _child.TreePids();

    /// <summary>Output chars waiting for the pipeline thread now. Tests.</summary>
    internal int BufferedChars { get { lock (_chunks) return _bufferedChars; } }

    /// <summary>The child has exited (what it started may still run). Tests.</summary>
    internal bool ChildHasExited => HasExited();

    /// <summary>Every output pipe reached EOF. Tests.</summary>
    internal bool OutputClosed => Volatile.Read(ref _openStreams) == 0;

    public void Dispose()
    {
        if (!HasExited()) Kill();
        Release();
        CloseStdin();
        try { _child.Dispose(); } catch { }
        _signal.Dispose();
        _stop.Dispose();
    }

    /// <summary>Nobody will drain any more (kill / dispose): a reader waiting for room drops its chunk and ends.</summary>
    private void Release()
    {
        lock (_chunks)
        {
            _released = true;
            Monitor.PulseAll(_chunks);
        }
    }

    private bool HasExited()
    {
        try { return _child.HasExited; } catch { return true; }
    }

    private int SafeExitCode()
    {
        try
        {
            // Bounded: only reached after the child exited or was killed, when the exit is at most a moment away.
            if (!_child.WaitForExit(2_000)) return 124;
            return _child.ExitCode;
        }
        catch { return 1; }
    }

    private void Signal()
    {
        try { _signal.Set(); } catch (ObjectDisposedException) { }
    }

    private void StartReader(StreamReader reader, bool isStderr)
    {
        var thread = new Thread(() =>
        {
            var buffer = new char[ReadChars];
            try
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (!Put(isStderr, new string(buffer, 0, read))) break;
                    Signal();
                }
            }
            catch
            {
                // Teardown (kill / dispose) closes the pipe under the read; what arrived is already queued.
            }
            finally
            {
                Interlocked.Decrement(ref _openStreams);
                Signal();
            }
        })
        {
            IsBackground = true,
            Name = isStderr ? "ps-bash streaming child stderr" : "ps-bash streaming child stdout",
        };
        thread.Start();
    }

    /// <summary>
    /// Queue one chunk for the pipeline thread, first waiting while the buffer is full (an empty buffer always takes a
    /// chunk). False when nobody will drain any more (<see cref="Release"/>): the reader stops. Reader threads only.
    /// </summary>
    private bool Put(bool isStderr, string text)
    {
        lock (_chunks)
        {
            while (!_released && _bufferedChars > 0 && _bufferedChars + text.Length > BufferChars)
                Monitor.Wait(_chunks);
            if (_released) return false;
            _chunks.Enqueue((isStderr, text));
            _bufferedChars += text.Length;
            return true;
        }
    }

    /// <summary>Move every queued chunk to <see cref="_taken"/> and give the readers their room back.</summary>
    private void TakeChunks()
    {
        _taken.Clear();
        lock (_chunks)
        {
            if (_chunks.Count == 0) return;
            while (_chunks.TryDequeue(out var chunk)) _taken.Add(chunk);
            _bufferedChars = 0;
            Monitor.PulseAll(_chunks);
        }
    }

    /// <summary>Emit queued chunks in arrival order; <paramref name="final"/> also flushes the held tails.</summary>
    private void Drain(IChildOutputSink sink, bool final)
    {
        StringBuilder? stderr = null;
        TakeChunks();
        foreach (var chunk in _taken)
        {
            if (chunk.IsStderr)
            {
                (stderr ??= new StringBuilder()).Append(chunk.Text);
                continue;
            }
            if (stderr is { Length: > 0 })
            {
                sink.Stderr(_assembler.Stderr(stderr.ToString()));
                stderr.Clear();
            }
            _lines.Clear();
            _assembler.Stdout(chunk.Text, _lines);
            foreach (var line in _lines)
                sink.Stdout(BashRuntime.NewBashObject(line));
        }
        if (stderr is { Length: > 0 })
            sink.Stderr(_assembler.Stderr(stderr.ToString()));
        if (!final) return;
        sink.Stderr(_assembler.StderrEnd());
        if (_assembler.StdoutEnd() is { } partial)
            sink.Stdout(BashRuntime.NewBashObject(partial, "PsBash.TextOutput", noTrailingNewline: true));
    }

    private CmdletSink? _cmdletSink;

    // One cmdlet drives an instance for its whole life; the wrapper is made once, not per record.
    private IChildOutputSink SinkFor(Cmdlet cmdlet) =>
        _cmdletSink is { } s && ReferenceEquals(s.Cmdlet, cmdlet) ? s : _cmdletSink = new CmdletSink(cmdlet);

    private sealed class CmdletSink(Cmdlet cmdlet) : IChildOutputSink
    {
        public Cmdlet Cmdlet { get; } = cmdlet;
        public bool Stopping => Cmdlet.Stopping;
        public void Stdout(object record) => Cmdlet.WriteObject(record);

        public void Stderr(string text)
        {
            if (text.Length == 0) return;
            if (Cmdlet is PSCmdlet ps) FileSystemHelpers.WriteStderrBytes(ps, text);
            else Cmdlet.WriteError(new ErrorRecord(new IOException(text), "BashError", ErrorCategory.NotSpecified, null));
        }
    }
}

/// <summary>Where <see cref="StreamingChildProcess"/> emits, on the pipeline thread: the cmdlet, or a test's collector.</summary>
internal interface IChildOutputSink
{
    /// <summary>The pipeline is stopping: kill the child, emit nothing more.</summary>
    bool Stopping { get; }

    /// <summary>One stdout record.</summary>
    void Stdout(object record);

    /// <summary>stderr text as it arrived (may be empty, may lack a newline).</summary>
    void Stderr(string text);
}

/// <summary>
/// The pure part of <see cref="StreamingChildProcess"/>: turns output chunks, cut wherever the OS pipe cut them,
/// into the records the buffered path made from the whole text. stdout: one line per <c>\n</c>, a <c>\r</c> before
/// it dropped, the unterminated rest held for the next chunk (or <see cref="StdoutEnd"/>). stderr: CRLF→LF, a
/// trailing <c>\r</c> held in case its <c>\n</c> is in the next chunk.
/// </summary>
internal sealed class ChildOutputAssembler
{
    private readonly StringBuilder _line = new();
    private bool _stderrHeldCr;

    /// <summary>Append the complete lines <paramref name="chunk"/> finishes to <paramref name="completed"/>.</summary>
    public void Stdout(string chunk, List<string> completed)
    {
        int start = 0;
        for (int i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != '\n') continue;
            _line.Append(chunk, start, i - start);
            if (_line.Length > 0 && _line[^1] == '\r') _line.Length--;
            completed.Add(_line.ToString());
            _line.Clear();
            start = i + 1;
        }
        _line.Append(chunk, start, chunk.Length - start);
    }

    /// <summary>The unterminated last line (null when the output ended with <c>\n</c> or was empty).</summary>
    public string? StdoutEnd()
    {
        if (_line.Length == 0) return null;
        var rest = _line.ToString();
        _line.Clear();
        return rest;
    }

    /// <summary>The text of a stderr chunk to emit now.</summary>
    public string Stderr(string chunk)
    {
        if (_stderrHeldCr) { chunk = "\r" + chunk; _stderrHeldCr = false; }
        if (chunk.EndsWith('\r')) { _stderrHeldCr = true; chunk = chunk[..^1]; }
        return chunk.Replace("\r\n", "\n");
    }

    /// <summary>A held <c>\r</c> that no <c>\n</c> followed.</summary>
    public string StderrEnd()
    {
        if (!_stderrHeldCr) return "";
        _stderrHeldCr = false;
        return "\r";
    }
}
