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
/// <c>print | "cmd"</c>. The data is collected in a temporary file and the command runs when the
/// pipe is CLOSED (<c>close()</c> or end of program) as <c>{ cmd } &lt; file</c> through ps-bash itself
/// — the launcher does not forward a pipe into a <c>-c</c> command, so a live stdin pipe would starve
/// <c>sort</c>. For the filters awk programs pipe into (sort, uniq, wc, cat, tee, gzip …) this is
/// indistinguishable from gawk's concurrent pipe, because they emit their results at end of input;
/// close() returns the command's exit status and its output lands in awk's output at that moment.
/// </summary>
internal sealed class AwkPipeSink : AwkOutSink
{
    private readonly string _command;
    private readonly AwkShell? _shell;
    private readonly AwkStdout _stdout;
    private readonly string _tmp;
    private StreamWriter? _w;
    private bool _closed;

    public AwkPipeSink(string command, AwkShell? shell, AwkStdout stdout)
    {
        _command = command;
        _shell = shell;
        _stdout = stdout;
        string dir = Path.Combine(Path.GetTempPath(), "ps-bash", "awk-pipe");
        Directory.CreateDirectory(dir);
        _tmp = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        _w = new StreamWriter(new FileStream(_tmp, FileMode.Create, FileAccess.Write, FileShare.Read), RawBytes.Encoding, 1 << 16);
        stdout.Holders++;
    }

    public override void Write(string text) => _w?.Write(text);
    public override void Flush() => _w?.Flush();

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
            _w?.Dispose();
            _w = null;

            if (flushStdoutFirst) _stdout.FlushHeld();
            _stdout.Holders--;

            if (_shell is null)
                throw new AwkInterpreter.AwkRuntimeException($"fatal: cannot open pipe `{_command}' (no shell available)");

            BashRuntime.ChildProcessResult r;
            try { r = _shell.RunWithInput(_command, _tmp); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new AwkInterpreter.AwkRuntimeException($"fatal: cannot open pipe `{_command}' ({ex.Message})");
            }

            _stdout.WriteThrough(r.Stdout.Replace("\r\n", "\n"));
            if (r.Stderr.Length > 0) Console.Error.Write(r.Stderr);
            return r.TimedOut ? 124 : r.ExitCode;
        }
        finally { Cleanup(); }
    }

    public override void Abort()
    {
        if (_closed) return;
        _closed = true;
        _stdout.Holders--;
        try { _w?.Dispose(); } catch { /* best effort */ }
        Cleanup();
    }

    private void Cleanup()
    {
        try { File.Delete(_tmp); } catch { /* best effort */ }
    }
}
