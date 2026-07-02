using System.Management.Automation;
using System.Text;
using System.Threading;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTail</c> function
/// (REFACTOR-2 Phase 1c). Emits the trailing lines (or bytes) of pipeline or
/// file input, matching the bash <c>tail</c> command.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact value-flag parsing and dual-mode behavior:
/// <list type="bullet">
/// <item>Flags: <c>-n N</c> / <c>-nN</c> / <c>-n +N</c> (line count, default
/// 10; the <c>+N</c> form switches to from-line mode), <c>-c N</c> / <c>-cN</c>
/// / <c>-c +N</c> (byte count / from-byte), the legacy <c>-N</c> shorthand, a
/// bare leading positional number, <c>-f</c> / <c>--follow</c>, <c>-s SECS</c>
/// / <c>--sleep-interval SECS</c>, and <c>--</c> — parsed in the same order as
/// the psm1 oracle's manual <c>while</c> loop.</item>
/// <item>Pipeline mode: from-line mode skips the first N-1 items and emits the
/// rest; otherwise a circular buffer keeps only the last N items in memory.
/// Multi-line items are split; single-line items pass through as their
/// original typed object.</item>
/// <item>File mode: byte mode emits the last N bytes (or from byte N for
/// <c>+N</c>); from-line mode streams and emits from line N; otherwise a
/// circular buffer emits the last N lines. Each file line is a typed
/// <c>PsBash.CatLine</c> PSObject with <c>BashText</c> equal to the raw line.
/// <c>-f</c> follow mode emits the initial tail then polls the file for
/// appended content at the sleep interval.</item>
/// </list>
///
/// Common-parameter audit: <c>-n</c>, <c>-c</c>, <c>-f</c>, <c>-s</c> do not
/// prefix-collide with any PowerShell common parameter, so they are scanned out
/// of <see cref="Arguments"/> (matching the psm1 oracle's <c>$args</c> scan).
/// Operands are resolved via the psm1 <c>Resolve-BashGlob</c> slice
/// reimplemented in C# (a <see cref="PSCmdlet"/> reaches
/// <see cref="PSCmdlet.SessionState"/>). The <c>--help</c> path delegates to
/// the psm1 <c>Show-BashHelp</c>; a follow-mode error delegates to the psm1
/// <c>Write-BashError</c> — both via string-bodied
/// <c>InvokeCommand.InvokeScript</c>, no ScriptBlock construction (AOT-safe).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTail")]
[OutputType(typeof(PSObject))]
[OutputType(typeof(string))]
public sealed class InvokeBashTailCommand : PSCmdlet
{
    // -c VALUE prefix-collides with -Confirm under PowerShell's
    // case-insensitive binder; without an explicit declaration the bare
    // -c gets eaten by -Confirm and the value lands as a positional
    // (treated as a file operand). Declared as a value-bearing string
    // parameter so 'tail -c 30 file' binds correctly.
    [Parameter] public string? C { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    /// <summary>
    /// Valid GNU <c>tail</c> options ps-bash does not implement. An unknown
    /// option-looking token falls through the scan into the operand (file) list;
    /// it is classified via <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>
    /// (specific "not supported" if listed, else bash-parity "unrecognized
    /// option") instead of being reported as a missing file.
    /// </summary>
    private static readonly HashSet<string> ValidButUnsupported = new(StringComparer.Ordinal)
    {
        // -q/--quiet/--silent accepted (no-op); --lines parsed (alias of -n).
        // -v/--verbose unsupported (ps-bash tail emits no "==> name <==" headers).
        "-v", "-z", "-F",
        "--verbose", "--zero-terminated",
        "--retry", "--max-unchanged-stats", "--pid",
        "--follow-retry",
    };

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "tail", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tail"))
            {
                WriteObject(line);
            }
            return;
        }

        int count = 10;
        int? byteCount = null;
        bool fromLine = false;
        bool followFile = false;
        double sleepInterval = 1.0;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        // Honour the explicit -C value parameter (collision fix for -c
        // vs -Confirm). Accepts the same +N / N forms the inline scan
        // below handles.
        if (!string.IsNullOrEmpty(C))
        {
            var cVal = C!;
            if (cVal.StartsWith("+", StringComparison.Ordinal)
                && int.TryParse(cVal.Substring(1), out int cp))
            {
                byteCount = cp;
                fromLine = true;
            }
            else if (int.TryParse(cVal, out int cn))
            {
                byteCount = cn;
            }
        }

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                i++;
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            if (arg == "-f" || arg == "--follow")
            {
                followFile = true;
                i++;
                continue;
            }

            // --lines=N / --lines N / --lines +N  (alias of -n, incl. the +N
            // "from line N onward" form).
            if (arg.StartsWith("--lines=", StringComparison.Ordinal))
            {
                var val = arg.Substring("--lines=".Length);
                if (val.StartsWith("+", StringComparison.Ordinal)
                    && int.TryParse(val.Substring(1), out int lp)) { count = lp; fromLine = true; }
                else if (int.TryParse(val, out int ln)) { count = ln; }
                i++;
                continue;
            }
            if (arg == "--lines")
            {
                i++;
                if (i < args.Length)
                {
                    var val = args[i];
                    if (val.StartsWith("+", StringComparison.Ordinal)
                        && int.TryParse(val.Substring(1), out int lp)) { count = lp; fromLine = true; }
                    else if (int.TryParse(val, out int ln)) { count = ln; }
                }
                i++;
                continue;
            }

            // -q / --quiet / --silent: never print per-file headers — already the
            // ps-bash behavior, so accept the flag as a no-op rather than refuse it.
            if (arg == "-q" || arg == "--quiet" || arg == "--silent")
            {
                i++;
                continue;
            }

            if (arg == "--sleep-interval")
            {
                i++;
                if (i < args.Length && double.TryParse(args[i], out double s))
                {
                    sleepInterval = s;
                }
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-s", StringComparison.Ordinal)
                && double.TryParse(arg.Substring(2), out double sj))
            {
                sleepInterval = sj;
                i++;
                continue;
            }

            if (arg == "-s")
            {
                i++;
                if (i < args.Length && double.TryParse(args[i], out double s2))
                {
                    sleepInterval = s2;
                }
                i++;
                continue;
            }

            // -c +N (from byte N onward).
            if (arg.Length > 3 && arg.StartsWith("-c+", StringComparison.Ordinal)
                && IsAllDigits(arg.Substring(3)))
            {
                byteCount = BashRuntime.ParseCountClamped(arg.AsSpan(3));
                fromLine = true;
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-c", StringComparison.Ordinal)
                && IsAllDigits(arg.Substring(2)))
            {
                byteCount = BashRuntime.ParseCountClamped(arg.AsSpan(2));
                i++;
                continue;
            }

            if (arg == "-c" || arg == "--bytes")
            {
                i++;
                if (i < args.Length)
                {
                    var val = args[i];
                    if (val.StartsWith("+", StringComparison.Ordinal)
                        && int.TryParse(val.Substring(1), out int cp))
                    {
                        byteCount = cp;
                        fromLine = true;
                    }
                    else if (int.TryParse(val, out int c))
                    {
                        byteCount = c;
                    }
                }
                i++;
                continue;
            }

            // -n +N (from line N onward).
            if (arg.Length > 3 && arg.StartsWith("-n+", StringComparison.Ordinal)
                && IsAllDigits(arg.Substring(3)))
            {
                count = BashRuntime.ParseCountClamped(arg.AsSpan(3));
                fromLine = true;
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-n", StringComparison.Ordinal)
                && IsAllDigits(arg.Substring(2)))
            {
                count = BashRuntime.ParseCountClamped(arg.AsSpan(2));
                i++;
                continue;
            }

            if (arg == "-n")
            {
                i++;
                if (i < args.Length)
                {
                    var val = args[i];
                    if (val.StartsWith("+", StringComparison.Ordinal)
                        && int.TryParse(val.Substring(1), out int np))
                    {
                        count = np;
                        fromLine = true;
                    }
                    else if (int.TryParse(val, out int n))
                    {
                        count = n;
                    }
                }
                i++;
                continue;
            }

            // Legacy -N shorthand (e.g. tail -5).
            if (arg.Length > 1 && arg[0] == '-' && IsAllDigits(arg.Substring(1)))
            {
                count = BashRuntime.ParseCountClamped(arg.AsSpan(1));
                i++;
                continue;
            }

            // Bare leading positional number (e.g. tail 5).
            if (operands.Count == 0 && arg.Length > 0 && IsAllDigits(arg))
            {
                count = BashRuntime.ParseCountClamped(arg.AsSpan());
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        // An option-looking operand is an unknown flag that fell through the
        // scan, not a file — classify it instead of reporting a missing file.
        if (FileSystemHelpers.TryWriteOperandOptionError(this, "tail", operands, ValidButUnsupported))
        {
            return;
        }

        // Pipeline mode
        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            if (fromLine)
            {
                int skip = count - 1;
                int idx = 0;
                foreach (var item in _pipeline)
                {
                    string text = BashRuntime.GetBashText(item);
                    string trimmed = text.TrimEnd('\n');
                    if (trimmed.Contains('\n'))
                    {
                        foreach (var subLine in trimmed.Split('\n'))
                        {
                            if (idx >= skip) WriteObject(subLine);
                            idx++;
                        }
                    }
                    else
                    {
                        if (idx >= skip) WriteObject(item);
                        idx++;
                    }
                }
            }
            else
            {
                int cap = Math.Max(count, 1);
                var buf = new object[cap];
                int bufLen = 0, pos = 0;
                foreach (var item in _pipeline)
                {
                    string text = BashRuntime.GetBashText(item);
                    string trimmed = text.TrimEnd('\n');
                    if (trimmed.Contains('\n'))
                    {
                        foreach (var subLine in trimmed.Split('\n'))
                        {
                            buf[pos] = subLine;
                            pos = (pos + 1) % cap;
                            if (bufLen < cap) bufLen++;
                        }
                    }
                    else
                    {
                        buf[pos] = item;
                        pos = (pos + 1) % cap;
                        if (bufLen < cap) bufLen++;
                    }
                }

                int start = bufLen < cap ? 0 : pos;
                for (int k = 0; k < bufLen; k++)
                {
                    WriteObject(buf[(start + k) % cap]);
                }
            }
            return;
        }

        // File mode
        var resolvedFiles = ResolveGlob(operands).ToList();
        if (resolvedFiles.Count == 0)
        {
            return;
        }

        string firstFile = resolvedFiles[0];

        // -c bytes mode
        if (byteCount != null)
        {
            EmitFileBytes(firstFile, byteCount.Value, fromLine, "tail");
            return;
        }

        if (followFile)
        {
            FollowFile(firstFile, count, fromLine, sleepInterval);
            return;
        }

        // Normal mode: emit last N lines (or from line N) per file.
        foreach (var filePath in resolvedFiles)
        {
            if (fromLine)
            {
                StreamReader? reader = OpenReader(filePath, "tail");
                if (reader == null) continue;
                try
                {
                    int li = 0;
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        li++;
                        if (li >= count)
                        {
                            WriteObject(MakeCatLine(li, line, filePath));
                        }
                    }
                }
                finally
                {
                    reader.Dispose();
                }
            }
            else
            {
                StreamReader? reader = OpenReader(filePath, "tail");
                if (reader == null) continue;
                try
                {
                    int cap = Math.Max(count, 1);
                    var buf = new string[cap];
                    int bufLen = 0, total = 0, pos = 0;
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        buf[pos] = line;
                        pos = (pos + 1) % cap;
                        if (bufLen < cap) bufLen++;
                        total++;
                    }

                    int start = bufLen < cap ? 0 : pos;
                    int lineNumOffset = total - bufLen;
                    for (int k = 0; k < bufLen; k++)
                    {
                        int idx = (start + k) % cap;
                        WriteObject(MakeCatLine(lineNumOffset + k + 1, buf[idx], filePath));
                    }
                }
                finally
                {
                    reader.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// psm1 oracle: <c>-f</c> follow mode — emit the initial tail, then poll the
    /// file at <paramref name="sleepInterval"/> seconds for appended content,
    /// re-reading only from the last known position. A shrunk file resets the
    /// position (truncation / rotation). Runs until the pipeline is stopped.
    /// </summary>
    private void FollowFile(string filePath, int count, bool fromLine, double sleepInterval)
    {
        try
        {
            EmitInitialFollowTail(filePath, count, fromLine);

            long filePos = new FileInfo(filePath).Length;

            while (!Stopping)
            {
                Thread.Sleep((int)(sleepInterval * 1000));
                var info = new FileInfo(filePath);
                if (info.Length > filePos)
                {
                    try
                    {
                        using var fs = new FileStream(
                            filePath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        fs.Seek(filePos, SeekOrigin.Begin);
                        string? line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            foreach (var obj in BashRuntime.EmitBashLines(line))
                            {
                                WriteObject(obj);
                            }
                        }
                        filePos = fs.Position;
                    }
                    catch
                    {
                        continue;
                    }
                }
                else if (info.Length < filePos)
                {
                    filePos = 0;
                }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"tail: cannot follow file: {ex.Message}");
        }
    }

    private void EmitFileBytes(string path, int byteCount, bool fromByte, string command)
    {
        try
        {
            using var fs = BashFileSystem.OpenRead(path);
            long safeCount = Math.Max(byteCount, 0);
            long start = fromByte
                ? Math.Min(safeCount, fs.Length)
                : Math.Max(0, fs.Length - safeCount);
            fs.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(
                fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                foreach (var obj in BashRuntime.EmitBashLines(line))
                {
                    WriteObject(obj);
                }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            WriteFileReadError(path, command, ex);
        }
    }

    private void EmitInitialFollowTail(string filePath, int count, bool fromLine)
    {
        StreamReader? reader = OpenReader(filePath, "tail");
        if (reader == null) return;

        try
        {
            if (fromLine)
            {
                int lineNumber = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (lineNumber >= count)
                    {
                        foreach (var obj in BashRuntime.EmitBashLines(line))
                        {
                            WriteObject(obj);
                        }
                    }
                }
                return;
            }

            int cap = Math.Max(count, 1);
            var buf = new string[cap];
            int bufLen = 0, pos = 0;
            string? current;
            while ((current = reader.ReadLine()) != null)
            {
                buf[pos] = current;
                pos = (pos + 1) % cap;
                if (bufLen < cap) bufLen++;
            }

            int start = bufLen < cap ? 0 : pos;
            for (int k = 0; k < bufLen; k++)
            {
                foreach (var obj in BashRuntime.EmitBashLines(buf[(start + k) % cap]))
                {
                    WriteObject(obj);
                }
            }
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void WriteFileReadError(string path, string command, Exception ex)
    {
        string normalized = path.Replace('\\', '/');
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        FileSystemHelpers.WriteBashError(this, $"{command}: {normalized}: {msg}");
    }

    private static PSObject MakeCatLine(int lineNumber, string content, string fileName)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.CatLine");
        obj.Properties.Add(new PSNoteProperty("LineNumber", lineNumber));
        obj.Properties.Add(new PSNoteProperty("Content", content));
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty(
            "BashText", BashRuntime.NormalizeBashText(content)));
        return obj;
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// psm1 oracle: <c>Open-BashFileReader</c> — sequential-scan
    /// <see cref="FileStream"/>, BOM skip, BOM-less UTF-8
    /// <see cref="StreamReader"/>. On failure emits a bash-style error via the
    /// psm1 <c>Write-BashError</c> sink and returns <c>null</c>.
    /// </summary>
    private StreamReader? OpenReader(string path, string command)
    {
        FileStream fs;
        try
        {
            fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.SequentialScan);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            string normalized = path.Replace('\\', '/');
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            FileSystemHelpers.WriteBashError(this, $"{command}: {normalized}: {msg}");
            return null;
        }

        var bom = new byte[3];
        int read = fs.Read(bom, 0, 3);
        bool hasBom = read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
        if (!hasBom && read > 0)
        {
            fs.Seek(0, SeekOrigin.Begin);
        }

        return new StreamReader(fs, new UTF8Encoding(false));
    }

    /// <summary>
    /// Reimplements the psm1 <c>Resolve-BashGlob</c> slice in C# (see
    /// <see cref="InvokeBashWcCommand"/> for the rationale).
    /// </summary>
    private IEnumerable<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        foreach (var rawP in paths)
        {
            var p = FileSystemHelpers.NormalizeOperandPath(rawP);
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
            {
                var matched = new List<string>();
                try
                {
                    foreach (var resolved in SessionState.Path.GetResolvedProviderPathFromPSPath(
                                 p, out _))
                    {
                        matched.Add(resolved);
                    }
                }
                catch
                {
                    // No matches — literal passthrough.
                }

                if (matched.Count == 0)
                {
                    yield return p;
                }
                else
                {
                    foreach (var m in matched)
                    {
                        yield return m;
                    }
                }
            }
            else
            {
                yield return SessionState.Path.GetUnresolvedProviderPathFromPSPath(p);
            }
        }
    }
}
