using System.Text;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// awk's own standard output: complete lines go to the emit callback (one BashObject per line).
/// While an OUTPUT PIPE is open (<see cref="Holders"/> &gt; 0) lines are HELD instead and released by
/// <see cref="FlushHeld"/>, which reproduces gawk's block-buffered stdout when it is a pipe (the case
/// for every captured run): a command started with <c>print | "sort"</c> writes its output when it is
/// closed or at the end of the program, and anything awk itself printed after the pipe was opened
/// is flushed only after that — <c>BEGIN{print "h"} {print | "sort"} END{print "f"}</c> prints
/// h, the sorted lines, then f. The held text is capped (<see cref="HeldCap"/> chars) so a long stream
/// cannot exhaust memory; reaching it flushes, as a full stdio buffer would.
/// </summary>
internal sealed class AwkStdout
{
    private const int HeldCap = 1 << 16;

    private readonly Action<string> _emit;
    private readonly StringBuilder _partial = new();
    private readonly List<string> _held = new();
    private int _heldChars;

    public AwkStdout(Action<string> emit) { _emit = emit; }

    /// <summary>Number of open output pipes; while non-zero, completed lines are held.</summary>
    public int Holders { get; set; }

    public void Write(string text)
    {
        _partial.Append(text);
        int start = 0;
        for (int i = 0; i < _partial.Length; i++)
        {
            if (_partial[i] != '\n') continue;
            Line(_partial.ToString(start, i - start));
            start = i + 1;
        }
        if (start > 0) _partial.Remove(0, start);
    }

    private void Line(string line)
    {
        if (Holders == 0) { _emit(line); return; }
        _held.Add(line);
        _heldChars += line.Length + 1;
        if (_heldChars > HeldCap) FlushHeld();
    }

    /// <summary>Emit every held line, oldest first.</summary>
    public void FlushHeld()
    {
        if (_held.Count == 0) return;
        foreach (var l in _held) _emit(l);
        _held.Clear();
        _heldChars = 0;
    }

    /// <summary>Text from a child command: straight to the output, never held (it IS the real stdout).</summary>
    public void WriteThrough(string text)
    {
        if (text.Length == 0) return;
        if (Holders == 0) { Write(text); return; }
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            _emit(text.Substring(start, i - start));
            start = i + 1;
        }
        if (start < text.Length) _emit(text.Substring(start));
    }

    /// <summary>End of program: the unterminated last line, if any.</summary>
    public void FlushPartial()
    {
        if (_partial.Length == 0) return;
        _emit(_partial.ToString());
        _partial.Clear();
    }
}

/// <summary>One open output redirection (<c>&gt; f</c>, <c>&gt;&gt; f</c>, <c>| cmd</c>) keyed by its name until <c>close()</c> / end of program.</summary>
internal abstract class AwkOutSink
{
    public abstract void Write(string text);

    /// <summary>Publish buffered data (<c>fflush</c>, and before any command runs).</summary>
    public virtual void Flush() { }

    /// <summary>Finish and release the stream. Returns what <c>close()</c> reports: 0 for a file, the exit status for a command.</summary>
    public abstract int Close();

    /// <summary>End-of-program close (differs from an explicit <c>close()</c> only for pipes).</summary>
    public virtual int CloseAtExit() => Close();

    /// <summary>Drop the stream WITHOUT running anything (host stop): release handles, delete temp data.</summary>
    public virtual void Abort() { try { Close(); } catch { /* best effort */ } }
}

/// <summary><c>&gt; "/dev/stdout"</c> / <c>"-"</c>: awk's own stdout, flushed (held lines released) when closed.</summary>
internal sealed class AwkStdoutSink : AwkOutSink
{
    private readonly AwkStdout _stdout;
    public AwkStdoutSink(AwkStdout stdout) { _stdout = stdout; }
    public override void Write(string text) => _stdout.Write(text);
    public override int Close() { _stdout.FlushHeld(); return 0; }
}

/// <summary><c>&gt; "/dev/stderr"</c>.</summary>
internal sealed class AwkStderrSink : AwkOutSink
{
    public override void Write(string text) => Console.Error.Write(text);
    public override void Flush() => Console.Error.Flush();
    public override int Close() => 0;
}

/// <summary><c>&gt; "/dev/null"</c>.</summary>
internal sealed class AwkNullSink : AwkOutSink
{
    public override void Write(string text) { }
    public override int Close() => 0;
}

/// <summary>
/// An output file. Opened (truncated or appended) when first named; every later print to the same
/// name writes to the open stream. Bytes go through the RawBytes codec, so escaped-byte markers
/// come back out as the original bytes.
/// </summary>
internal sealed class AwkFileSink : AwkOutSink
{
    private readonly StreamWriter _w;

    private AwkFileSink(StreamWriter w) { _w = w; }

    /// <summary>Open <paramref name="path"/>; throws <see cref="AwkInterpreter.AwkRuntimeException"/> (gawk's fatal "can't redirect") when it cannot be opened.</summary>
    public static AwkFileSink Open(string name, string path, bool append)
    {
        try
        {
            var fs = new FileStream(
                path, append ? FileMode.Append : FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return new AwkFileSink(new StreamWriter(fs, RawBytes.Encoding, 1 << 16));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException)
        {
            string reason = ex is UnauthorizedAccessException && Directory.Exists(path)
                ? "Is a directory"
                : FileSystemHelpers.ReadErrorMessage(ex);
            throw new AwkInterpreter.AwkRuntimeException($"fatal: can't redirect to `{name}' ({reason})");
        }
    }

    public override void Write(string text) => _w.Write(text);
    public override void Flush() => _w.Flush();

    public override int Close()
    {
        try { _w.Dispose(); } catch (IOException) { return -1; }
        return 0;
    }
}

/// <summary>
/// <c>print | "cmd"</c>: the command is started when the pipe is first written (a child ps-bash with
/// piped stdin/stdout/stderr), awk's records are written to its stdin as they are printed (buffered like
/// gawk's stdio pipe: published on <c>fflush</c>, <c>close()</c>, a full buffer or the end of the program)
/// and its stdout/stderr are pumped by background threads into a queue that is drained on the cmdlet
/// thread (output objects may only be written there) — straight into awk's output, never held, because it
/// IS the real stdout. <c>close()</c> closes the command's stdin, waits for it to finish and returns its
/// exit status. The child's launcher forwards the redirected stdin to the first stdin reader, so
/// compound commands (<c>a; b</c>) get stdin too. A command that exits while awk is still printing is
/// gawk's <c>fatal: print to "cmd" failed: Broken pipe</c> (exit 2).
/// </summary>
internal sealed class AwkPipeSink : AwkOutSink
{
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(120);

    private readonly string _command;
    private readonly AwkStdout _stdout;
    private readonly System.Diagnostics.Process _proc;
    private readonly StreamWriter _w;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(bool Err, string Text)> _queue = new();
    private readonly Thread _outPump;
    private readonly Thread _errPump;
    private bool _closed;

    public AwkPipeSink(string command, AwkShell? shell, AwkStdout stdout)
    {
        _command = command;
        _stdout = stdout;
        if (shell is null)
            throw new AwkInterpreter.AwkRuntimeException($"fatal: cannot open pipe `{command}' (no shell available)");
        _proc = shell.StartPipe(command)
            ?? throw new AwkInterpreter.AwkRuntimeException($"fatal: cannot open pipe `{command}' (cannot start the command)");
        _w = new StreamWriter(_proc.StandardInput.BaseStream, RawBytes.Encoding, 1 << 16);
        _outPump = Pump(_proc.StandardOutput, err: false);
        _errPump = Pump(_proc.StandardError, err: true);
        stdout.Holders++;
    }

    private Thread Pump(StreamReader reader, bool err)
    {
        var t = new Thread(() =>
        {
            try
            {
                var buf = new char[4096];
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    _queue.Enqueue((err, new string(buf, 0, n)));
            }
            catch { /* killed mid-read */ }
        }) { IsBackground = true };
        t.Start();
        return t;
    }

    /// <summary>Hand what the command has written so far to awk's output (cmdlet thread only).</summary>
    private void Drain()
    {
        while (_queue.TryDequeue(out var item))
        {
            if (item.Err) Console.Error.Write(item.Text);
            else _stdout.WriteThrough(item.Text.Replace("\r\n", "\n"));
        }
    }

    public override void Write(string text)
    {
        if (_closed) return;
        try { _w.Write(text); }
        catch (IOException) { throw BrokenPipe(); }
        Drain();
    }

    public override void Flush()
    {
        if (_closed) return;
        try { _w.Flush(); }
        catch (IOException) { /* the command is gone: reported at the next write / close */ }
        Drain();
    }

    private AwkInterpreter.AwkRuntimeException BrokenPipe()
    {
        Drain();
        return new AwkInterpreter.AwkRuntimeException($"fatal: print to \"{_command}\" failed: Broken pipe");
    }

    public override int Close() => CloseCore(flushStdoutFirst: true);

    // At program end gawk closes its pipes first and flushes stdout last, so the command's output
    // precedes the held lines; an explicit close() writes the pending stdout out before the command's.
    public override int CloseAtExit() => CloseCore(flushStdoutFirst: false);

    private int CloseCore(bool flushStdoutFirst)
    {
        if (_closed) return 0;
        _closed = true;
        try
        {
            try { _w.Dispose(); } catch (IOException) { /* command already exited */ }

            if (flushStdoutFirst) _stdout.FlushHeld();
            _stdout.Holders--;

            bool timedOut = !_proc.WaitForExit((int)RunLimit.TotalMilliseconds);
            if (timedOut)
            {
                try { _proc.Kill(entireProcessTree: true); } catch { /* gone */ }
                _proc.WaitForExit(2_000);
            }
            _outPump.Join(TimeSpan.FromSeconds(5));
            _errPump.Join(TimeSpan.FromSeconds(5));
            Drain();
            return timedOut ? 124 : _proc.ExitCode;
        }
        finally { try { _proc.Dispose(); } catch { /* ignore */ } }
    }

    public override void Abort()
    {
        if (_closed) return;
        _closed = true;
        _stdout.Holders--;
        try { _proc.Kill(entireProcessTree: true); } catch { /* gone */ }
        try { _w.Dispose(); } catch { /* best effort */ }
        try { _proc.Dispose(); } catch { /* ignore */ }
    }
}