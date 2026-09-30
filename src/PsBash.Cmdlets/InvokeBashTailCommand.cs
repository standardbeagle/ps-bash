using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading;
using PsBash.Cmdlets.Args;

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
/// the psm1 <c>Show-BashHelp</c>; a follow-mode error goes through
/// <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord).
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

    /// <summary>Decoy for the unsupported <c>-v</c> (--verbose, per-file headers).
    /// Bare <c>-v</c> silently bound <c>-Verbose</c>; re-injected below so the
    /// classifier fires exit 2.</summary>
    [Parameter] public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    /// <summary>
    /// Valid GNU <c>tail</c> options ps-bash does not implement, refused loudly (exit 2) by the
    /// shared parser. -q/--quiet/--silent are accepted (no-op); --lines/--bytes alias -n/-c.
    /// -v/--verbose (ps-bash tail emits no "==> name &lt;==" headers), -z, and the follow-by-name
    /// family (-F, --retry, --pid, --max-unchanged-stats) are refused. (A string[] on purpose:
    /// CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] TailValidButUnsupported =
    {
        "-v", "-z", "-F",
        "--verbose", "--zero-terminated",
        "--retry", "--max-unchanged-stats", "--pid",
    };

    private const string OptLines = "lines", OptBytes = "bytes", OptQuiet = "quiet", OptNum = "num",
        OptFollow = "follow", OptSleep = "sleep";

    /// <summary>
    /// tail's option surface (GNU coreutils 9.4: -c -f -F -n -q -s -v -z + long forms; -NUM
    /// obsolete shorthand). <c>--follow</c> takes an OPTIONAL attached value (<c>name</c> /
    /// <c>descriptor</c>), which ps-bash treats alike (it follows the resolved path).
    /// </summary>
    private static readonly OptSpecSet TailSpec = new(
        new[]
        {
            new OptSpec(OptBytes, 'c', "bytes", OptKind.Value),
            new OptSpec(OptLines, 'n', "lines", OptKind.Value),
            new OptSpec(OptFollow, 'f', null),                            // -f takes no value in a bundle
            new OptSpec(OptFollow, '\0', "follow", OptKind.OptionalValue), // --follow[=name|descriptor]
            new OptSpec(OptQuiet, 'q', "quiet"),
            new OptSpec(OptQuiet, '\0', "silent"),
            new OptSpec(OptSleep, 's', "sleep-interval", OptKind.Value),
        },
        validButUnsupported: TailValidButUnsupported,
        allowAbbrev: true,
        numericShorthandId: OptNum,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, TailSpec);

    /// <summary>The resolved meaning of a tail argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class TailArgs
    {
        public ParsedArgs Parsed = null!;
        /// <summary>Lines to print (magnitude; a leading <c>-</c> means the same as none).</summary>
        public int Count = 10;
        /// <summary>GNU <c>-n +N</c>: print from line N onward.</summary>
        public bool FromLine;
        public int ByteCount;
        /// <summary>GNU <c>-c +N</c>: print from byte N onward.</summary>
        public bool BytesFromStart;
        /// <summary>True when the LAST of -c / -n on the line was -c (GNU: last one wins).</summary>
        public bool BytesMode;
        public bool Follow;
        public double SleepInterval = 1.0;
        public List<string> Operands = new();
        /// <summary>A usage error the scan itself cannot see (bad NUM, bad -s, bad --follow value); exit 1.</summary>
        public string? Error;

        /// <summary>True when nothing further should execute: scan error, value error, --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + resolve. GNU rules kept: last -c/-n wins; NUM takes a sign (<c>+</c> = from the
    /// start) and a multiplier suffix (<see cref="GnuNumber"/>), an invalid one is an error; the
    /// obsolete <c>-NUM</c> is only valid as the FIRST argument; the obsolete <c>+NUM</c> first
    /// argument (<c>tail +2</c>) means <c>-n +2</c>. The legacy ps-bash extension — a bare leading
    /// positional number is the line count (<c>tail 5</c>, Pester-pinned) — is preserved.
    /// </summary>
    internal static TailArgs Plan(string[] args)
    {
        var t = new TailArgs { Parsed = ScanArgs(args) };
        t.Operands = t.Parsed.Operands();
        if (t.Parsed.HasError) return t;

        foreach (var tok in t.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                // GNU: the obsolete -NUM is only valid as the FIRST argument and not combined with
                // any other option (`tail -5 -n1`, `tail -n1 -5` are both "invalid context").
                case OptNum when tok.ArgIndex != 0 || t.Parsed.Tokens.Any(x => x.Kind == ArgTokKind.Option && x.OptId != OptNum):
                    t.Error = $"tail: option used in invalid context -- {tok.Value![0]}";
                    return t;
                case OptNum:
                case OptLines:
                    if (!GnuNumber.TryParse(tok.Value!, out int lines, out char lsign))
                    {
                        t.Error = $"tail: invalid number of lines: '{tok.Value}'";
                        return t;
                    }
                    t.Count = lines;
                    t.FromLine = lsign == '+';
                    t.BytesMode = false;
                    break;
                case OptBytes:
                    if (!GnuNumber.TryParse(tok.Value!, out int bytes, out char bsign))
                    {
                        t.Error = $"tail: invalid number of bytes: '{tok.Value}'";
                        return t;
                    }
                    t.ByteCount = bytes;
                    t.BytesFromStart = bsign == '+';
                    t.BytesMode = true;
                    break;
                case OptFollow:
                    if (tok.Value is { } how && !IsFollowHow(how))
                    {
                        t.Error = $"tail: invalid argument '{how}' for '--follow'\n"
                                  + "Valid arguments are:\n  - 'descriptor'\n  - 'name'";
                        return t;
                    }
                    t.Follow = true;
                    break;
                case OptSleep:
                    if (!double.TryParse(tok.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double secs) || secs < 0)
                    {
                        t.Error = $"tail: invalid number of seconds: '{tok.Value}'";
                        return t;
                    }
                    t.SleepInterval = secs;
                    break;
            }
        }

        // Legacy operand forms, on the FIRST operand only and never after `--`:
        //   tail +2  -> GNU obsolete -n +2 (only as the very first argument)
        //   tail 5   -> ps-bash extension: line count 5 (Pester-pinned)
        foreach (var tok in t.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Operand) continue;
            if (!tok.AfterDoubleDash)
            {
                if (tok.ArgIndex == 0 && tok.Raw.Length > 1 && tok.Raw[0] == '+' && IsAllDigits(tok.Raw.AsSpan(1)))
                {
                    t.Count = BashRuntime.ParseCountClamped(tok.Raw.AsSpan(1));
                    t.FromLine = true;
                    t.BytesMode = false;
                    t.Operands.RemoveAt(0);
                }
                else if (IsAllDigits(tok.Raw))
                {
                    t.Count = BashRuntime.ParseCountClamped(tok.Raw.AsSpan());
                    t.Operands.RemoveAt(0);
                }
            }
            break; // only the first operand qualifies
        }
        return t;
    }

    /// <summary>GNU argmatch: any non-empty prefix of <c>name</c> or <c>descriptor</c>.</summary>
    private static bool IsFollowHow(string v) =>
        v.Length > 0 && ("name".StartsWith(v, StringComparison.Ordinal)
                         || "descriptor".StartsWith(v, StringComparison.Ordinal));

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    /// <summary>Arguments with the decoy-bound flags re-injected (bare -v binds -Verbose; -c binds
    /// the value decoy C). A value decoy becomes the two elements <c>-c VALUE</c>.</summary>
    private string[] ArgsWithDecoys()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (V.IsPresent, "-v"));
        if (C is not null) args = new[] { "-c", C }.Concat(args).ToArray();
        return args;
    }

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        // Re-inject the decoy-bound flags (-v, -c VALUE) so the shared parser sees them.
        var args = ArgsWithDecoys();

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

        // Shared ordered parser: bundles (-qn2), attached values (-n5, --lines=5, --bytes=5),
        // abbreviations (--li=1), `--`, and the unsupported/unknown classifier in ONE scan.
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "tail", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "tail", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError); // exit 1, GNU's usage status
            return;
        }

        int count = plan.Count;
        int? byteCount = plan.BytesMode ? plan.ByteCount : null;
        bool fromLine = plan.FromLine;
        bool bytesFromStart = plan.BytesFromStart;
        bool followFile = plan.Follow;
        double sleepInterval = plan.SleepInterval;
        var operands = plan.Operands;

        // Pipeline mode
        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            // -c on a pipe (the pipeline branch used to run BEFORE any byte handling, so `-c` was
            // silently ignored and the last N LINES came out). The bytes are the items joined by
            // '\n' plus the terminator GNU sees on stdin (omitted when the final record says it
            // had none, e.g. `printf x`).
            if (byteCount != null)
            {
                EmitPipelineBytes(byteCount.Value, bytesFromStart);
                return;
            }

            if (fromLine)            {
                int skip = count - 1;
                int idx = 0;
                foreach (var item in _pipeline)
                {
                    string text = BashRuntime.GetBashText(item);
                    string trimmed = text.TrimEnd('\n');
                    if (trimmed.Contains('\n'))
                    {
                        bool unterminated = BashRuntime.IsUnterminated(item);
                        var pieces = trimmed.Split('\n');
                        for (int p = 0; p < pieces.Length; p++)
                        {
                            if (idx >= skip)
                                WriteObject(BashRuntime.TextRecord(pieces[p], unterminated && p == pieces.Length - 1));
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
                        bool unterminated = BashRuntime.IsUnterminated(item);
                        var pieces = trimmed.Split('\n');
                        for (int p = 0; p < pieces.Length; p++)
                        {
                            buf[pos] = BashRuntime.TextRecord(pieces[p], unterminated && p == pieces.Length - 1);
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

                if (count == 0) bufLen = 0; // GNU: 	ail -n 0 prints nothing (the ring buffer is sized max(count, 1))
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
            EmitFileBytes(firstFile, byteCount.Value, bytesFromStart, "tail");
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

                    if (count == 0) bufLen = 0; // GNU: 	ail -n 0 prints nothing
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
                        fs.Seek(filePos, SeekOrigin.Begin);
                        long avail = info.Length - filePos;
                        // Cap the per-poll read so a huge burst append doesn't allocate
                        // unboundedly; the remainder is picked up on the next poll.
                        int toRead = (int)Math.Min(avail, FollowReadCap);
                        var buffer = new byte[toRead];
                        int read = fs.Read(buffer, 0, toRead);

                        var (advance, follows) = SplitFollowChunk(buffer, read, avail, FollowReadCap);
                        if (advance == 0) continue; // fragment withheld — re-read next poll
                        foreach (var l in follows)
                        {
                            foreach (var obj in BashRuntime.EmitBashLines(l))
                            {
                                WriteObject(obj);
                            }
                        }
                        filePos += advance; // advance past COMPLETE lines only
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

    // Per-poll read cap for tail -f: bounds the byte[] a huge burst append allocates.
    private const long FollowReadCap = 64L << 20;

    /// <summary>
    /// Pure core of the <c>tail -f</c> poll: given a chunk of <paramref name="read"/>
    /// bytes read from the follow position (with <paramref name="avail"/> total bytes
    /// pending, capped by <paramref name="capBytes"/>), return the COMPLETE lines to emit
    /// and the number of bytes to advance the follow position past.
    /// <para>GNU <c>tail -f</c> emits only complete lines: a trailing newline-less
    /// fragment is WITHHELD (advance 0) until its newline arrives — EXCEPT when the
    /// pending data exceeds the cap (a pathological unterminated &gt;64MB run), which is
    /// force-emitted so following cannot stall. <c>'\n'</c> (0x0A) never occurs
    /// mid-UTF-8-sequence, so the byte-level newline scan is safe; a CRLF's <c>\r</c> is
    /// stripped to match the old <c>StreamReader.ReadLine</c> behaviour. Extracted from
    /// the I/O loop so the withhold/advance logic is unit-testable.</para>
    /// </summary>
    internal static (int Advance, List<string> Lines) SplitFollowChunk(
        byte[] buffer, int read, long avail, long capBytes)
    {
        var lines = new List<string>();
        if (read <= 0) return (0, lines);

        int lastNl = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNl < 0)
        {
            if (avail <= capBytes) return (0, lines); // withhold the whole fragment
            lastNl = read - 1;                          // cap exceeded — force-emit
        }

        var text = Encoding.UTF8.GetString(buffer, 0, lastNl + 1);
        var parts = text.Split('\n');
        int emitCount = text.EndsWith('\n') ? parts.Length - 1 : parts.Length;
        for (int k = 0; k < emitCount; k++)
        {
            var l = parts[k];
            if (l.EndsWith('\r')) l = l[..^1];
            lines.Add(l);
        }
        return (lastNl + 1, lines);
    }

    /// <summary>
    /// <c>tail -c N</c> / <c>-c +N</c> over pipeline input: slice the UTF-8 bytes of the joined
    /// records and emit the surviving text one line per object, like the file-mode byte path.
    /// </summary>
    private void EmitPipelineBytes(int byteCount, bool fromByte)
    {
        byte[] all = Encoding.UTF8.GetBytes(BashRuntime.RecordStreamText(_pipeline));
        long safeCount = Math.Max(byteCount, 0);
        long start = fromByte
            ? Math.Min(Math.Max(safeCount - 1, 0), all.Length)
            : Math.Max(0, all.Length - safeCount);
        string text = Encoding.UTF8.GetString(all, (int)start, (int)(all.Length - start));
        // Byte slice: a TRANSFORMER — fresh text records carrying exactly the slice's bytes.
        foreach (var rec in BashRuntime.ByteSliceRecords(text)) WriteObject(rec);
    }

    private void EmitFileBytes(string path, int byteCount, bool fromByte, string command)
    {
        try
        {
            using var fs = BashFileSystem.OpenRead(path);
            long safeCount = Math.Max(byteCount, 0);
            // `-c +N` starts AT byte N (1-based): skip N-1. (The old code skipped N.)
            long start = fromByte
                ? Math.Min(Math.Max(safeCount - 1, 0), fs.Length)
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

            if (count == 0) bufLen = 0; // GNU: 	ail -n 0 -f starts with nothing
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

    /// <summary>
    /// psm1 oracle: <c>Open-BashFileReader</c> — sequential-scan
    /// <see cref="FileStream"/>, BOM skip, BOM-less UTF-8
    /// <see cref="StreamReader"/>. On failure emits a bash-style error via
    /// <see cref="FileSystemHelpers.WriteBashError"/> and returns <c>null</c>.
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
