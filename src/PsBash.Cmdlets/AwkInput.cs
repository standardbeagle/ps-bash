using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>A pull source of input records (the main input, or stdin).</summary>
internal interface IAwkRecordSource
{
    /// <summary>The next record, or null at end of input.</summary>
    string? Next();
}

/// <summary>
/// A stream a <c>getline</c> reads from and <c>close()</c> ends: an open file, a running command, or
/// the shared stdin. Lives from its first <c>getline</c> until <c>close(name)</c> / end of program.
/// </summary>
internal interface IAwkLineStream
{
    string? ReadLine();

    /// <summary>Release the stream. Returns what <c>close()</c> reports: 0 for a file, the exit status for a command.</summary>
    int Close();
}

/// <summary>
/// The main input as a pull source: the file operands in order (or, with none, the buffered stdin
/// records), one <see cref="IEnumerator{T}"/> open at a time. <paramref name="onFile"/> fires as each
/// input is entered so the machine can reset FNR and set FILENAME — which is why plain <c>getline</c>
/// crossing a file boundary updates both exactly like the main loop does.
/// </summary>
internal sealed class AwkMainInput : IAwkRecordSource, IDisposable
{
    private readonly IReadOnlyList<string> _files;
    private readonly Func<string, IEnumerator<string>?> _open;
    private readonly Action<string> _onFile;
    private readonly IReadOnlyList<string>? _stdin;
    private IEnumerator<string>? _cur;
    private int _next;
    private bool _stdinStarted;

    /// <param name="files">File operands; empty means stdin mode.</param>
    /// <param name="open">Opens one operand (reporting its own failure); null = skip it.</param>
    /// <param name="onFile">Called with the file name (empty for stdin) when an input starts.</param>
    /// <param name="stdin">The buffered stdin records (stdin mode only).</param>
    public AwkMainInput(
        IReadOnlyList<string> files, Func<string, IEnumerator<string>?> open, Action<string> onFile,
        IReadOnlyList<string>? stdin)
    {
        _files = files;
        _open = open;
        _onFile = onFile;
        _stdin = stdin;
    }

    public string? Next()
    {
        while (true)
        {
            if (_cur != null)
            {
                if (_cur.MoveNext()) return _cur.Current;
                _cur.Dispose();
                _cur = null;
                continue;
            }

            if (_files.Count == 0)
            {
                if (_stdinStarted || _stdin is null) return null;
                _stdinStarted = true;
                _onFile("");
                _cur = _stdin.GetEnumerator();
                continue;
            }

            if (_next >= _files.Count) return null;
            string file = _files[_next++];
            var e = _open(file);
            if (e is null) continue;
            _onFile(file);
            _cur = e;
        }
    }

    public void Dispose() { _cur?.Dispose(); _cur = null; }
}

/// <summary>A plain list of records as a source (the stdin a file-mode program reads via <c>getline &lt; "-"</c>).</summary>
internal sealed class AwkListSource : IAwkRecordSource
{
    private readonly IReadOnlyList<string> _records;
    private int _i;
    public AwkListSource(IReadOnlyList<string> records) { _records = records; }
    public string? Next() => _i < _records.Count ? _records[_i++] : null;
}

/// <summary>
/// An open input file. Created by <see cref="Open"/>, which reads the first record eagerly so a
/// missing / unreadable / directory operand is a <c>getline</c> return of -1 (no message, as gawk)
/// rather than an exception later.
/// </summary>
internal sealed class AwkFileStream : IAwkLineStream
{
    private readonly IEnumerator<string>? _e;
    private string? _first;

    private AwkFileStream(IEnumerator<string>? e, string? first) { _e = e; _first = first; }

    public static AwkFileStream? Open(string path)
    {
        // /dev/null is an empty file everywhere (BashFileSystem serves it too, but a directory test
        // must not see it first).
        if (FileSystemHelpers.IsNullDevice(path)) return new AwkFileStream(null, null);
        if (Directory.Exists(path)) return null;
        IEnumerator<string>? e = null;
        try
        {
            e = BashFileSystem.ReadLines(path).GetEnumerator();
            string? first = e.MoveNext() ? e.Current : null;
            return new AwkFileStream(first is null ? null : e, first);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException)
        {
            e?.Dispose();
            return null;
        }
    }

    public string? ReadLine()
    {
        if (_first is not null) { var f = _first; _first = null; return f; }
        return _e is not null && _e.MoveNext() ? _e.Current : null;
    }

    public int Close() { _e?.Dispose(); return 0; }
}

/// <summary>A source (stdin / the main input) viewed as a <c>getline &lt;</c> stream. Closing it does not close the source.</summary>
internal sealed class AwkSourceStream : IAwkLineStream
{
    private readonly IAwkRecordSource _src;
    public AwkSourceStream(IAwkRecordSource src) { _src = src; }
    public string? ReadLine() => _src.Next();
    public int Close() => 0;
}

/// <summary>
/// A running shell command whose stdout is read line by line (<c>cmd | getline</c>). The command is
/// NOT run to completion first: lines are available as it writes them, and <see cref="Close"/>
/// closes the read end, waits a bounded time for the exit, then kills the tree.
/// </summary>
internal sealed class AwkCommandStream : IAwkLineStream
{
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(3);

    private readonly Process _proc;
    private readonly StreamReader _out;
    private readonly StringBuilder _err = new();
    private readonly Thread _errPump;
    private int _status;
    private bool _closed;

    private AwkCommandStream(Process proc)
    {
        _proc = proc;
        _out = proc.StandardOutput;
        _errPump = new Thread(() =>
        {
            try
            {
                var buf = new char[4096];
                int n;
                while ((n = proc.StandardError.Read(buf, 0, buf.Length)) > 0)
                    lock (_err) _err.Append(buf, 0, n);
            }
            catch { /* killed mid-read */ }
        }) { IsBackground = true };
        _errPump.Start();
    }

    /// <summary>Start <paramref name="psi"/> with stdin at EOF and stdout/stderr piped. Null if it cannot start.</summary>
    public static AwkCommandStream? Start(ProcessStartInfo psi)
    {
        Prepare(psi);
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return null;
            try { proc.StandardInput.Close(); } catch { /* child never opened it */ }
            return new AwkCommandStream(proc);
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            proc?.Dispose();
            return null;
        }
    }

    internal static void Prepare(ProcessStartInfo psi)
    {
        BashVariableStore.ApplyTo(psi);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding ??= PsBash.Core.RawBytes.Encoding;
        psi.StandardErrorEncoding ??= PsBash.Core.RawBytes.Encoding;
    }

    /// <summary>One line: ends at LF; a CR directly before it is dropped (a Windows child ends lines CRLF).</summary>
    public string? ReadLine()
    {
        if (_closed) return null;
        try
        {
            StringBuilder? sb = null;
            int c;
            while ((c = _out.Read()) >= 0)
            {
                if (c == '\n')
                {
                    if (sb is null) return "";
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                (sb ??= new StringBuilder()).Append((char)c);
            }
            return sb is { Length: > 0 } ? sb.ToString() : null;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            return null; // killed / pipe broken
        }
    }

    /// <summary>Kill the process tree now (host StopProcessing); a blocked <see cref="ReadLine"/> then returns null.</summary>
    public void Kill()
    {
        try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    public int Close()
    {
        if (_closed) return _status;
        _closed = true;
        try { _out.Dispose(); } catch { /* ignore */ }
        try
        {
            if (!_proc.WaitForExit((int)CloseWait.TotalMilliseconds))
            {
                Kill();
                _proc.WaitForExit(2_000);
            }
            _status = _proc.ExitCode;
        }
        catch { _status = -1; }
        _errPump.Join(TimeSpan.FromSeconds(1));
        string err;
        lock (_err) err = _err.ToString();
        if (err.Length > 0) Console.Error.Write(err);
        try { _proc.Dispose(); } catch { /* ignore */ }
        return _status;
    }
}

/// <summary>
/// Runs awk's shell command lines (<c>cmd | getline</c>, <c>system()</c>, <c>print | cmd</c>) through ps-bash ITSELF —
/// the same resolution <c>bash -c</c> uses — so <c>"seq 3" | getline</c> and <c>"date +%s" | getline</c>
/// work on Windows. Fallbacks when no ps-bash executable can be found: <c>sh -c</c> where there is one,
/// else <c>cmd.exe /c</c> on Windows. Tracks every stream it started so <see cref="KillAll"/> (host
/// StopProcessing, from another thread) can end them.
/// </summary>
internal sealed class AwkShell
{
    private readonly PSCmdlet _cmdlet;
    private readonly ConcurrentBag<AwkCommandStream> _streams = new();
    private string? _exe;
    private bool _exeResolved;

    public AwkShell(PSCmdlet cmdlet) { _cmdlet = cmdlet; }

    /// <summary>Start <paramref name="command"/> for reading; null when it cannot be started.</summary>
    public AwkCommandStream? Start(string command)
    {
        var s = AwkCommandStream.Start(BuildStartInfo(command));
        if (s is not null) _streams.Add(s);
        return s;
    }

    /// <summary>Run <paramref name="command"/> to completion (<c>system()</c>), capturing its output.</summary>
    public BashRuntime.ChildProcessResult Run(string command) =>
        BashRuntime.RunChildProcess(BuildStartInfo(command));

    /// <summary>
    /// Run <paramref name="command"/> to completion with the contents of <paramref name="inputPath"/> as
    /// its stdin (<c>print | "cmd"</c>), capturing its output. The launcher does not forward a pipe into
    /// a <c>-c</c> command, so the input is attached as a file redirection of the whole command line.
    /// </summary>
    public BashRuntime.ChildProcessResult RunWithInput(string command, string inputPath) =>
        BashRuntime.RunChildProcess(BuildStartInfo(command, inputPath));

    public void KillAll()
    {
        foreach (var s in _streams) s.Kill();
    }

    /// <summary>
    /// <c>cat 'file' | command</c> for a plain command / pipeline (the shape awk programs pipe into:
    /// <c>sort -n</c>, <c>sort | uniq -c</c>, <c>cat &gt;&gt; log</c>). Anything with list operators, groups or
    /// substitutions (<c>a; b</c>, <c>x &amp;&amp; y</c>, <c>(…)</c>) gets the redirection on a brace group
    /// instead — ps-bash feeds a compound command's stdin only to <c>read</c>-style consumers, so the
    /// commands inside such a group may not see the data.
    /// </summary>
    internal static string WithInput(string command, string inputPath)
    {
        string quoted = "'" + inputPath.Replace("'", "'\\''") + "'";
        return IsPlainPipeline(command)
            ? "cat " + quoted + " | " + command
            : "{ " + command + "\n} < " + quoted;
    }

    /// <summary>No unquoted list operator (<c>; &amp; &amp;&amp; ||</c>), group, redirection from a file, newline or substitution.</summary>
    internal static bool IsPlainPipeline(string command)
    {
        bool single = false, dbl = false;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (single) { if (c == '\'') single = false; continue; }
            if (c == '\\') { i++; continue; }
            if (c == '`') return false;
            if (c == '$' && i + 1 < command.Length && command[i + 1] == '(') return false;
            if (dbl) { if (c == '"') dbl = false; continue; }
            switch (c)
            {
                case '\'': single = true; break;
                case '"': dbl = true; break;
                case ';': case '&': case '(': case ')': case '{': case '}': case '<': case '\n': case '\r':
                    return false;
                case '|':
                    if (i + 1 < command.Length && command[i + 1] == '|') return false;
                    break;
            }
        }
        return !single && !dbl && command.Trim().Length > 0;
    }

    private ProcessStartInfo BuildStartInfo(string command, string? inputPath = null)
    {
        if (!_exeResolved)
        {
            _exe = InvokeBashBashCommand.ResolvePsBashExecutable(_cmdlet);
            _exeResolved = true;
        }

        var psi = new ProcessStartInfo();
        if (!string.IsNullOrEmpty(_exe))
        {
            psi.FileName = _exe;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(inputPath is null ? command : WithInput(command, inputPath));
        }
        else if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /s /c \"" + (inputPath is null ? command : "(" + command + ") < \"" + inputPath + "\"") + "\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(inputPath is null ? command : WithInput(command, inputPath));
        }

        try
        {
            var loc = _cmdlet.SessionState.Path.CurrentFileSystemLocation.ProviderPath;
            if (!string.IsNullOrEmpty(loc)) psi.WorkingDirectory = loc;
        }
        catch { /* not a filesystem location: inherit */ }
        return psi;
    }
}
