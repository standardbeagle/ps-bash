using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// A child process whose output streams to the cmdlet as it runs, with no time budget: a nested shell
/// (<c>bash build.sh | tee log</c>, <c>sh -c make</c>) must show progress and may run for hours. Start it with
/// <see cref="BashRuntime.StartChildProcessStreaming"/>.
/// <para>
/// <b>Threads.</b> Two reader threads move stdout / stderr text into ONE queue in arrival order; everything that
/// touches the pipeline (<c>WriteObject</c>, the stderr writer) runs on the cmdlet's pipeline thread in
/// <see cref="PumpAvailable"/>, <see cref="WriteStdin"/> and <see cref="Complete"/>. <see cref="Kill"/> is the
/// one member safe from another thread (<c>StopProcessing</c>).
/// </para>
/// <para>
/// <b>Records.</b> stdout is emitted line by line exactly as <see cref="BashRuntime.EmitBashLines"/> would split the
/// whole output (CRLF→LF, one record per <c>\n</c>, an unterminated last line flagged); stderr is emitted as it
/// arrives (a prompt without a newline shows at once), CRLF→LF.
/// </para>
/// </summary>
public sealed class StreamingChildProcess : IDisposable
{
    /// <summary>After the child exits, how long its pipes may stay open (a backgrounded grandchild holding them).</summary>
    private static readonly TimeSpan ExitDrainGrace = TimeSpan.FromSeconds(5);

    private readonly Process _proc;
    private readonly ConcurrentQueue<(bool IsStderr, string Text)> _chunks = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly ChildOutputAssembler _assembler = new();
    private readonly List<string> _lines = new();
    private Stream? _stdin;
    private int _openStreams = 2;
    private int _killed;
    private bool _completed;

    internal StreamingChildProcess(Process proc, bool keepStdinOpen)
    {
        _proc = proc;
        if (keepStdinOpen)
            _stdin = proc.StandardInput.BaseStream;
        else
            try { proc.StandardInput.Close(); } catch { /* the child may not have opened it */ }
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => Signal();
        }
        catch { /* already gone: the wait loops poll HasExited too */ }
        StartReader(proc.StandardOutput, isStderr: false);
        StartReader(proc.StandardError, isStderr: true);
    }

    /// <summary>True while the child's stdin is open for <see cref="WriteStdin"/>.</summary>
    public bool StdinOpen => _stdin is not null;

    /// <summary>
    /// Write <paramref name="bytes"/> to the child's stdin, emitting the child's output while the write waits on a
    /// full pipe (the child may print before it reads). False — and stdin is closed — when the child no longer
    /// reads it (exited, or closed fd 0): bash's broken pipe. Pipeline thread only.
    /// </summary>
    public bool WriteStdin(Cmdlet cmdlet, byte[] bytes)
    {
        var stdin = _stdin;
        if (stdin is null) return false;
        if (bytes.Length == 0) return true;
        if (HasExited()) { CloseStdin(); return false; }
        var write = Task.Run(() => { stdin.Write(bytes, 0, bytes.Length); stdin.Flush(); });
        var handles = new WaitHandle[] { _signal, ((IAsyncResult)write).AsyncWaitHandle };
        while (!write.IsCompleted)
        {
            Drain(cmdlet, final: false);
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
        try { _proc.StandardInput.Close(); } catch { /* broken pipe on the final flush; the handle still closes */ }
    }

    /// <summary>Emit whatever output has arrived, without waiting. Pipeline thread only.</summary>
    public void PumpAvailable(Cmdlet cmdlet) => Drain(cmdlet, final: false);

    /// <summary>
    /// Close stdin, emit the child's output until it exits and its pipes close, and return its exit code. When the
    /// cmdlet is stopping the child is killed and nothing more is emitted. Pipeline thread only; once.
    /// </summary>
    public int Complete(Cmdlet cmdlet)
    {
        if (_completed) return SafeExitCode();
        _completed = true;
        CloseStdin();
        var exitedAt = (DateTime?)null;
        while (true)
        {
            if (cmdlet.Stopping) { Kill(); return SafeExitCode(); }
            bool streamsDone = Volatile.Read(ref _openStreams) == 0;
            Drain(cmdlet, final: false);
            if (streamsDone) break;
            if (HasExited())
            {
                exitedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - exitedAt > ExitDrainGrace) break;
            }
            _signal.WaitOne(200);
        }
        Drain(cmdlet, final: true);
        return SafeExitCode();
    }

    /// <summary>Kill the child's whole process tree. Safe from any thread; idempotent.</summary>
    public void Kill()
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0) return;
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
        }
        catch { /* already gone / race */ }
        Signal();
    }

    public void Dispose()
    {
        if (!HasExited()) Kill();
        CloseStdin();
        try { _proc.Dispose(); } catch { }
        _signal.Dispose();
    }

    private bool HasExited()
    {
        try { return _proc.HasExited; } catch { return true; }
    }

    private int SafeExitCode()
    {
        try
        {
            if (!_proc.WaitForExit(2_000)) return 124;
            return _proc.ExitCode;
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
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    _chunks.Enqueue((isStderr, new string(buffer, 0, read)));
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

    /// <summary>Emit queued chunks in arrival order; <paramref name="final"/> also flushes the held tails.</summary>
    private void Drain(Cmdlet cmdlet, bool final)
    {
        StringBuilder? stderr = null;
        while (_chunks.TryDequeue(out var chunk))
        {
            if (chunk.IsStderr)
            {
                (stderr ??= new StringBuilder()).Append(chunk.Text);
                continue;
            }
            if (stderr is { Length: > 0 })
            {
                WriteStderr(cmdlet, _assembler.Stderr(stderr.ToString()));
                stderr.Clear();
            }
            _lines.Clear();
            _assembler.Stdout(chunk.Text, _lines);
            foreach (var line in _lines)
                cmdlet.WriteObject(BashRuntime.NewBashObject(line));
        }
        if (stderr is { Length: > 0 })
            WriteStderr(cmdlet, _assembler.Stderr(stderr.ToString()));
        if (!final) return;
        WriteStderr(cmdlet, _assembler.StderrEnd());
        if (_assembler.StdoutEnd() is { } partial)
            cmdlet.WriteObject(BashRuntime.NewBashObject(partial, "PsBash.TextOutput", noTrailingNewline: true));
    }

    private static void WriteStderr(Cmdlet cmdlet, string text)
    {
        if (text.Length == 0) return;
        if (cmdlet is PSCmdlet ps) FileSystemHelpers.WriteStderrBytes(ps, text);
        else cmdlet.WriteError(new ErrorRecord(new IOException(text), "BashError", ErrorCategory.NotSpecified, null));
    }
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
