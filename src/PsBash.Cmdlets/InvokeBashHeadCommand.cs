using System.Management.Automation;
using System.Reflection;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashHead</c> function
/// (REFACTOR-2 Phase 1c). Emits the leading lines (or bytes) of pipeline or
/// file input, matching the bash <c>head</c> command.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact value-flag parsing and dual-mode behavior:
/// <list type="bullet">
/// <item>Flags: <c>-n N</c> / <c>-nN</c> (line count, default 10), <c>-c N</c>
/// / <c>-cN</c> (byte count), the legacy <c>-N</c> shorthand, a bare leading
/// positional number, and <c>--</c> to end flag scanning — all parsed in the
/// same order as the psm1 oracle's manual <c>while</c> loop.</item>
/// <item>Pipeline mode (no operands, pipeline non-empty): byte mode joins all
/// input with <c>\n</c> and emits the first N UTF-8 bytes as a string; line
/// mode emits the first N lines, splitting multi-line items and passing
/// single-line items through as their original typed object.</item>
/// <item>File mode: operands resolved via the psm1 <c>Resolve-BashGlob</c>
/// (reimplemented in C# — a <see cref="PSCmdlet"/> reaches
/// <see cref="PSCmdlet.SessionState"/>); byte mode emits the first N bytes of
/// each file; line mode streams the first N lines, each as a typed
/// <c>PsBash.CatLine</c> PSObject whose <c>BashText</c> is the raw line with no
/// trailing newline — exactly as the psm1 oracle emitted.</item>
/// </list>
///
/// Common-parameter audit: <c>-n</c> and <c>-c</c> do not prefix-collide with
/// any PowerShell common parameter, so they are safely scanned out of
/// <see cref="Arguments"/> rather than declared as parameters (matching the
/// psm1 oracle's <c>$args</c> scan). The <c>--help</c> path delegates to the
/// psm1 <c>Show-BashHelp</c>; a file-read error delegates to the psm1
/// <c>Write-BashError</c> — both via string-bodied
/// <c>InvokeCommand.InvokeScript</c>, no ScriptBlock construction (AOT-safe).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashHead")]
[OutputType(typeof(PSObject))]
[OutputType(typeof(string))]
public sealed class InvokeBashHeadCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Valid GNU <c>head</c> options ps-bash does not implement. An option-looking
    /// token that is not a recognized flag falls through this cmdlet's static
    /// <c>ParseArgs</c> into the operand (file) list; rather than report it as a
    /// missing file, it is classified via
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>: in this set →
    /// "recognized but not supported"; otherwise bash-parity "unrecognized option".
    /// </summary>
    private static readonly HashSet<string> ValidButUnsupported = new(StringComparer.Ordinal)
    {
        // -q/--quiet/--silent accepted (no-op); --lines/--bytes parsed (aliases
        // of -n/-c). -v/--verbose (force per-file headers) is unsupported because
        // ps-bash head does not emit the "==> name <==" headers at all.
        "-v", "-z",
        "--verbose", "--zero-terminated",
    };

    private readonly List<PSObject> _pipeline = new();
    // Streaming state for line-mode pipeline: parse flags lazily on first
    // record so an infinite upstream (e.g. `yes`) doesn't block on EndProcessing.
    private bool _flagsParsed;
    private int _lineCount = 10;
    private int? _byteCount;
    private int _emitted;
    private bool _streamingLineMode;
    private bool _suppress; // --help or arg-only path; do not stream

    private void ParseFlagsOnce()
    {
        if (_flagsParsed) return;
        _flagsParsed = true;
        ParseArgs(Arguments ?? Array.Empty<string>(),
            out _lineCount, out _byteCount, out var operands, out var help);
        // We stream the pipeline only when:
        //   - no --help (which goes through EndProcessing)
        //   - no file operands (file mode runs in EndProcessing)
        //   - line mode (byte mode needs to join everything first)
        _streamingLineMode = !help && operands.Count == 0 && _byteCount == null;
        if (help || operands.Count > 0) _suppress = true;
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseFlagsOnce();

        if (_streamingLineMode)
        {
            if (_emitted >= _lineCount)
            {
                StopUpstream();
                return;
            }
            string text = BashRuntime.GetBashText(InputObject);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    if (_emitted >= _lineCount) break;
                    WriteObject(subLine);
                    _emitted++;
                }
            }
            else
            {
                WriteObject(InputObject);
                _emitted++;
            }
            if (_emitted >= _lineCount)
            {
                StopUpstream();
            }
            return;
        }

        // Non-streaming paths (byte mode pipeline, file mode) still need
        // the full input buffered; EndProcessing handles them.
        if (!_suppress)
        {
            _pipeline.Add(InputObject);
        }
    }

    /// <summary>
    /// Stops the upstream pipeline once we have emitted enough lines.
    /// PowerShell's internal <c>StopUpstreamCommandsException</c> is the same
    /// mechanism <c>Select-Object -First N</c> uses; it is internal, so we
    /// reach it via reflection. Falls back to a benign return on failure
    /// (the cmdlet still produces correct output; only early-stop is missed).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "StopUpstreamCommandsException is an internal SMA type that is " +
            "always present in the PowerShell host runspace where this cmdlet executes " +
            "(the non-AOT ps-bash-host); it is never trimmed away.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "The SMA assembly and the resolved exception type are present at " +
            "runtime in the host; reflecting over its constructors is safe.")]
    private void StopUpstream()
    {
        var t = typeof(PSObject).Assembly.GetType(
            "System.Management.Automation.StopUpstreamCommandsException");
        if (t == null) return;
        var ctor = t.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault();
        if (ctor == null) return;
        Exception ex;
        try
        {
            ex = (Exception)ctor.Invoke(new object[] { this });
        }
        catch
        {
            return;
        }
        throw ex;
    }

    protected override void EndProcessing()
    {
        // If ProcessRecord streamed the line-mode pipeline already, we're done.
        if (_flagsParsed && _streamingLineMode)
        {
            return;
        }

        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "head", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "head"))
            {
                WriteObject(line);
            }
            return;
        }

        ParseArgs(args, out int count, out int? byteCount,
            out var operands, out _);

        // An option-looking operand is an unknown flag that fell through the
        // scan, not a file — classify it instead of reporting a missing file.
        if (FileSystemHelpers.TryWriteOperandOptionError(this, "head", operands, ValidButUnsupported))
        {
            return;
        }

        // Pipeline mode
        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            if (byteCount != null)
            {
                var sb = new StringBuilder();
                for (int k = 0; k < _pipeline.Count; k++)
                {
                    if (k > 0) sb.Append('\n');
                    sb.Append(BashRuntime.GetBashText(_pipeline[k]));
                }
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                int take = Math.Min(byteCount.Value, bytes.Length);
                WriteObject(Encoding.UTF8.GetString(bytes, 0, take));
                return;
            }

            int emitted = 0;
            foreach (var item in _pipeline)
            {
                if (emitted >= count) break;
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        if (emitted >= count) break;
                        WriteObject(subLine);
                        emitted++;
                    }
                }
                else
                {
                    WriteObject(item);
                    emitted++;
                }
            }
            return;
        }

        // File mode
        foreach (var filePath in ResolveGlob(operands))
        {
            if (byteCount != null)
            {
                try
                {
                    // Stream at most N bytes — never read the whole file just to
                    // take the head of it. Chunked so a huge -c N on a small file
                    // doesn't pre-allocate N (reads stop at EOF).
                    using var fs = BashFileSystem.OpenRead(filePath);
                    int remaining = byteCount.Value;
                    using var ms = new MemoryStream();
                    var chunk = new byte[Math.Min(remaining, 65536)];
                    int n;
                    while (remaining > 0 &&
                           (n = fs.Read(chunk, 0, Math.Min(chunk.Length, remaining))) > 0)
                    {
                        ms.Write(chunk, 0, n);
                        remaining -= n;
                    }
                    WriteObject(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(this, $"head: cannot read '{filePath}': {ex.Message}");
                }
                continue;
            }

            StreamReader? reader = OpenReader(filePath, "head");
            if (reader == null) continue;

            try
            {
                int li = 0;
                string? line;
                while (li < count && (line = reader.ReadLine()) != null)
                {
                    li++;
                    var obj = new PSObject();
                    obj.TypeNames.Insert(0, "PsBash.CatLine");
                    obj.Properties.Add(new PSNoteProperty("LineNumber", li));
                    obj.Properties.Add(new PSNoteProperty("Content", line));
                    obj.Properties.Add(new PSNoteProperty("FileName", filePath));
                    obj.Properties.Add(new PSNoteProperty(
                        "BashText", BashRuntime.NormalizeBashText(line)));
                    WriteObject(obj);
                }
            }
            finally
            {
                reader.Dispose();
            }
        }
    }

    private static void ParseArgs(string[] args, out int count, out int? byteCount,
        out List<string> operands, out bool help)
    {
        count = 10;
        byteCount = null;
        operands = new List<string>();
        help = false;
        bool pastDoubleDash = false;

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

            if (arg == "--help")
            {
                help = true;
                i++;
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            // Long-form aliases of -n / -c.
            if (arg.StartsWith("--lines=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--lines=".Length), out int ln)) count = ln;
                i++;
                continue;
            }
            if (arg == "--lines")
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out int ln)) count = ln;
                i++;
                continue;
            }
            if (arg.StartsWith("--bytes=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--bytes=".Length), out int bc)) byteCount = bc;
                i++;
                continue;
            }
            if (arg == "--bytes")
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out int bc)) byteCount = bc;
                i++;
                continue;
            }

            // -q / --quiet / --silent: never print the per-file "==> name <=="
            // header. ps-bash head never prints those headers, so this is the
            // effective behavior already — accept the flag as a no-op so it is
            // honored rather than refused.
            if (arg == "-q" || arg == "--quiet" || arg == "--silent")
            {
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
                if (i < args.Length && int.TryParse(args[i], out int n))
                {
                    count = n;
                }
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

            if (arg == "-c")
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out int c))
                {
                    byteCount = c;
                }
                i++;
                continue;
            }

            // Legacy -N shorthand (e.g. head -5).
            if (arg.Length > 1 && arg[0] == '-' && IsAllDigits(arg.Substring(1)))
            {
                count = BashRuntime.ParseCountClamped(arg.AsSpan(1));
                i++;
                continue;
            }

            // Bare leading positional number (e.g. head 5).
            if (operands.Count == 0 && arg.Length > 0 && IsAllDigits(arg))
            {
                count = BashRuntime.ParseCountClamped(arg.AsSpan());
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }
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
    /// psm1 oracle: <c>Open-BashFileReader</c> — opens a sequential-scan
    /// <see cref="FileStream"/>, skips a UTF-8 BOM if present, and wraps it in a
    /// BOM-less UTF-8 <see cref="StreamReader"/>. On failure emits a bash-style
    /// error via the psm1 <c>Write-BashError</c> sink and returns <c>null</c>.
    /// </summary>
    private StreamReader? OpenReader(string path, string command)
    {
        FileStream fs;
        try
        {
            fs = BashFileSystem.OpenRead(path);
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
