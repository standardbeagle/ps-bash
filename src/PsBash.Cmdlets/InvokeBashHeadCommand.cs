using PsBash.Core;
using System.Management.Automation;
using System.Reflection;
using System.Text;
using PsBash.Cmdlets.Args;

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
/// psm1 <c>Show-BashHelp</c>; a file-read error goes through
/// <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord).
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

    /// <summary>Decoy for the unsupported <c>-v</c> (--verbose, per-file headers).
    /// Bare <c>-v</c> silently bound <c>-Verbose</c>; re-injected via
    /// <see cref="ArgsWithDecoys"/> so the classifier fires exit 2.</summary>
    [Parameter] public SwitchParameter V { get; set; }

    /// <summary>Arguments with decoy-bound classifier flags re-injected (bare -v never
    /// reaches Arguments — it binds -Verbose). Used at both the streaming and
    /// EndProcessing parse sites so the flag is seen consistently.</summary>
    private string[] ArgsWithDecoys()
        => BashRuntime.PrependDecoys(Arguments, (V.IsPresent, "-v"));

    /// <summary>
    /// Valid GNU <c>head</c> options ps-bash does not implement, refused loudly (exit 2) by the
    /// shared parser. -q/--quiet/--silent are accepted (no-op); --lines/--bytes are aliases of
    /// -n/-c. -v/--verbose (force per-file headers) is unsupported because ps-bash head does not
    /// emit the "==> name &lt;==" headers at all; -z/--zero-terminated needs NUL records.
    /// (A string[] on purpose: CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] HeadValidButUnsupported = Array.Empty<string>();

    private const string OptLines = "lines", OptBytes = "bytes", OptQuiet = "quiet", OptNum = "num",
        OptVerbose = "verbose", OptZero = "zero";

    /// <summary>
    /// head's option surface (GNU coreutils 9.4: -c -n -q -v -z + long forms; -NUM obsolete
    /// shorthand). Built once for the shared ordered parser.
    /// </summary>
    private static readonly OptSpecSet HeadSpec = new(
        new[]
        {
            new OptSpec(OptBytes, 'c', "bytes", OptKind.Value),
            new OptSpec(OptLines, 'n', "lines", OptKind.Value),
            new OptSpec(OptQuiet, 'q', "quiet"),
            new OptSpec(OptQuiet, '\0', "silent"),
            new OptSpec(OptVerbose, 'v', "verbose"),
            new OptSpec(OptZero, 'z', "zero-terminated"),
        },
        validButUnsupported: HeadValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        digitOptionWording: "invalid trailing option");

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, HeadSpec);

    /// <summary>The resolved meaning of a head argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class HeadArgs
    {
        public ParsedArgs Parsed = null!;
        /// <summary>Lines to print; negative = all but the last K (GNU <c>-n -K</c>).</summary>
        public int Count = 10;
        /// <summary>Bytes to print when <see cref="BytesMode"/> (negative = all but the last K).</summary>
        public int ByteCount;
        /// <summary>True when the LAST of -c / -n on the line was -c (GNU: last one wins).</summary>
        public bool BytesMode;
        /// <summary>The LAST of -q/-v: when "==> name <==" headers print (GNU: -q never, -v always).</summary>
        public FileHeaders.Mode Headers = FileHeaders.Mode.Default;
        /// <summary>-z: records end at NUL instead of newline (line mode only).</summary>
        public bool Zero;
        public List<string> Operands = new();
        /// <summary>A usage error the scan itself cannot see (bad NUM, misplaced -NUM); exit 1.</summary>
        public string? Error;

        /// <summary>True when nothing further should execute: scan error, NUM error, --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + resolve. GNU rules kept: last -c/-n wins; NUM takes a sign and multiplier suffix
    /// (<see cref="GnuNumber"/>) and an invalid one is an error; the obsolete <c>-NUM</c> is only
    /// valid as the FIRST argument (<c>head -n1 -5</c> is "invalid trailing option"). The legacy
    /// ps-bash extension — a bare leading positional number is the line count (<c>head 5</c>,
    /// Pester-pinned) — is preserved.
    /// </summary>
    internal static HeadArgs Plan(string[] args)
    {
        // The obsolete first argument -NUM[bkmlcqvz]* is rewritten to the options it stands for;
        // every later digit is a "trailing option" error from the scan itself.
        if (args.Length > 0 && TryExpandObsoleteNum(args[0], out var expanded, out var obsoleteError))
        {
            if (obsoleteError is not null)
                return new HeadArgs { Parsed = ScanArgs(Array.Empty<string>()), Error = obsoleteError };
            args = expanded.Concat(args.Skip(1)).ToArray();
        }

        var h = new HeadArgs { Parsed = ScanArgs(args) };
        h.Operands = h.Parsed.Operands();
        if (h.Parsed.HasError) return h;

        foreach (var tok in h.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptLines:
                    if (!GnuNumber.TryParse(tok.Value!, out int lines, out char lsign))
                    {
                        h.Error = $"head: invalid number of lines: '{tok.Value}'";
                        return h;
                    }
                    h.Count = lsign == '-' ? -lines : lines;
                    h.BytesMode = false;
                    break;
                case OptBytes:
                    if (!GnuNumber.TryParse(tok.Value!, out int bytes, out char bsign))
                    {
                        h.Error = $"head: invalid number of bytes: '{tok.Value}'";
                        return h;
                    }
                    h.ByteCount = bsign == '-' ? -bytes : bytes;
                    h.BytesMode = true;
                    break;
                case OptQuiet: h.Headers = FileHeaders.Mode.Never; break;
                case OptVerbose: h.Headers = FileHeaders.Mode.Always; break;
                case OptZero: h.Zero = true; break;
            }
        }

        // Legacy bare leading positional number (head 5). Never after `--`.
        foreach (var tok in h.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Operand) continue;
            if (!tok.AfterDoubleDash && IsAllDigits(tok.Raw))
            {
                h.Count = BashRuntime.ParseCountClamped(tok.Raw.AsSpan());
                h.Operands.RemoveAt(0);
            }
            break; // only the first operand qualifies
        }
        return h;
    }

    /// <summary>
    /// GNU head's obsolete first argument <c>-NUM[bkmlcqvz]*</c> (oracle-checked, coreutils 9.4):
    /// <c>b</c>/<c>k</c>/<c>m</c> multiply by 512/1024/1M AND select bytes, <c>c</c> selects bytes with no
    /// multiplier, <c>l</c> selects lines (keeping any multiplier: <c>-2kl</c> = 2048 lines), and
    /// <c>q</c>/<c>v</c>/<c>z</c> are the ordinary flags; the last letter of each kind wins. Any other
    /// letter is "invalid trailing option". Returns false when <paramref name="arg"/> is not of this shape.
    /// </summary>
    internal static bool TryExpandObsoleteNum(string arg, out List<string> tokens, out string? error)
    {
        tokens = new List<string>();
        error = null;
        if (arg.Length < 2 || arg[0] != '-' || !char.IsAsciiDigit(arg[1])) return false;

        int i = 1;
        while (i < arg.Length && char.IsAsciiDigit(arg[i])) i++;
        string digits = arg.Substring(1, i - 1);

        bool bytes = false, quiet = false, verbose = false, zero = false;
        string suffix = "";
        for (; i < arg.Length; i++)
        {
            switch (arg[i])
            {
                case 'b': suffix = "b"; bytes = true; break;
                case 'k': suffix = "k"; bytes = true; break;
                case 'm': suffix = "m"; bytes = true; break;
                case 'c': suffix = ""; bytes = true; break;
                case 'l': bytes = false; break;
                case 'q': quiet = true; break;
                case 'v': verbose = true; break;
                case 'z': zero = true; break;
                default:
                    error = $"head: invalid trailing option -- {arg[i]}";
                    return true;
            }
        }

        tokens.Add(bytes ? "-c" : "-n");
        tokens.Add(digits + suffix);
        if (quiet) tokens.Add("-q");
        if (verbose) tokens.Add("-v");
        if (zero) tokens.Add("-z");
        return true;
    }

    private readonly List<PSObject> _pipeline = new();
    // Streaming state for line-mode pipeline: parse flags lazily on first
    // record so an infinite upstream (e.g. `yes`) doesn't block on EndProcessing.
    private bool _flagsParsed;
    private int _lineCount = 10;
    private int? _byteCount;
    private int _emitted;
    private bool _streamingLineMode;
    // `head -c N` (N >= 0) on a pipe streams too: it keeps only the bytes still owed, so an endless
    // upstream (`yes | head -c 5`) is cut off after the Nth byte instead of being buffered forever.
    private bool _streamingByteMode;
    private long _bytesRemaining;
    private bool _suppress; // --help or arg-only path; do not stream
    private FileHeaders.Mode _headerMode;
    private bool _zero;
    private bool _anyHeader;       // a header was already written (the next one gets the blank separator)
    private bool _stdinHeaderDone; // streaming pipeline mode printed its "standard input" header

    private void ParseFlagsOnce()
    {
        if (_flagsParsed) return;
        _flagsParsed = true;
        var h = Plan(ArgsWithDecoys());
        _lineCount = h.Count;
        _headerMode = h.Headers;
        _zero = h.Zero;
        _byteCount = h.BytesMode ? h.ByteCount : null;
        // We stream the pipeline only when:
        //   - the argv is clean (a scan/NUM error or --help/--version goes through EndProcessing)
        //   - no file operands (file mode runs in EndProcessing)
        //   - line mode (byte mode needs to join everything first)
        //   - a NON-negative line count. GNU `head -n -K` means "all lines but the
        //     last K", which is undecidable while streaming (you can't know which are
        //     the last K until input ends) — buffer and resolve in EndProcessing.
        // -z reads NUL-terminated records out of the whole byte stream, so it buffers (EndProcessing).
        _streamingLineMode = !h.Declined && h.Operands.Count == 0 && _byteCount == null && _lineCount >= 0 && !_zero;
        _streamingByteMode = !h.Declined && h.Operands.Count == 0 && _byteCount is >= 0;
        _bytesRemaining = _byteCount ?? 0;
        // A "-" operand reads the pipeline, so it must still be collected.
        if (h.Declined || (h.Operands.Count > 0 && !h.Operands.Contains("-"))) _suppress = true;
    }

    private void WriteHeader(string name)
    {
        WriteObject(FileHeaders.Record(name, first: !_anyHeader));
        _anyHeader = true;
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseFlagsOnce();

        if ((_streamingLineMode || _streamingByteMode) && !_stdinHeaderDone)
        {
            _stdinHeaderDone = true;
            if (_headerMode == FileHeaders.Mode.Always) WriteHeader(FileHeaders.StandardInput);
        }

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
                // A multi-line record (printf 'b\na') is split into text lines; the last
                // piece keeps the source's missing newline (head copies bytes).
                bool unterminated = BashRuntime.IsUnterminated(InputObject);
                var pieces = trimmed.Split('\n');
                for (int p = 0; p < pieces.Length; p++)
                {
                    if (_emitted >= _lineCount) break;
                    WriteObject(BashRuntime.TextRecord(pieces[p], unterminated && p == pieces.Length - 1));
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

        if (_streamingByteMode)
        {
            if (_bytesRemaining <= 0)
            {
                StopUpstream();
                return;
            }
            // This record's share of the byte stream (BashText + terminator unless exact), cut at
            // the bytes still owed; the slice is a TRANSFORMER output: fresh text, exact bytes.
            byte[] recBytes = RawBytes.GetBytes(BashRuntime.RecordStreamText(new object[] { InputObject }));
            int take = (int)Math.Min(_bytesRemaining, recBytes.Length);
            foreach (var rec in BashRuntime.ByteSliceRecords(RawBytes.GetString(recBytes, 0, take)))
                WriteObject(rec);
            _bytesRemaining -= take;
            if (_bytesRemaining <= 0) StopUpstream();
            return;
        }

        // Non-streaming paths (negative byte count, file mode) still need
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
        if (_flagsParsed && (_streamingLineMode || _streamingByteMode))
        {
            return;
        }
        _stdinHeaderDone = true;

        var args = ArgsWithDecoys();

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

        // Shared ordered parser: bundles (-qn2), attached values (-n5, --lines=5), abbreviations
        // (--li=1), `--`, and the unsupported/unknown classifier in ONE scan.
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "head", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "head", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError); // exit 1, GNU's usage status
            return;
        }

        int count = plan.Count;
        int? byteCount = plan.BytesMode ? plan.ByteCount : null;
        var operands = plan.Operands;
        _headerMode = plan.Headers;
        _zero = plan.Zero;

        // Pipeline mode
        if (operands.Count == 0)
        {
            // `head -v` on an EMPTY stdin still prints the header (GNU).
            if (_pipeline.Count == 0 && _headerMode == FileHeaders.Mode.Always) WriteHeader(FileHeaders.StandardInput);
            if (_pipeline.Count > 0)
            {
                if (_headerMode == FileHeaders.Mode.Always) WriteHeader(FileHeaders.StandardInput);
                EmitFromRecords(_pipeline, byteCount, count);
            }
            return;
        }

        // File mode
        bool headers = FileHeaders.Wanted(_headerMode, operands.Count);
        foreach (var filePath in ResolveGlob(operands))
        {
            if (filePath == "-")
            {
                if (headers) WriteHeader(FileHeaders.StandardInput);
                EmitFromRecords(_pipeline, byteCount, count);
                continue;
            }
            if (headers && File.Exists(filePath)) WriteHeader(FileHeaders.Display(this, filePath));
            if (_zero && byteCount == null)
            {
                try
                {
                    byte[] all = BashFileSystem.ReadAllBytes(filePath);
                    foreach (var rec in BashRuntime.ByteSliceRecords(ZeroHead(RawBytes.GetString(all), count)))
                        WriteObject(rec);
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(this,
                        $"head: cannot open '{filePath.Replace('\\', '/')}' for reading: {FileSystemHelpers.ReadErrorMessage(ex)}");
                }
                continue;
            }
            EmitFile(filePath, byteCount, count);
        }
    }

    /// <summary>
    /// GNU <c>head -z -n N</c> over a whole byte stream: the first N NUL-terminated pieces (a negative
    /// count = all but the last K). The final piece may be unterminated and is then copied as it is.
    /// </summary>
    internal static string ZeroHead(string text, int count)
    {
        if (text.Length == 0) return text;
        var pieces = new List<string>(text.Split('\0'));
        bool endsWithNul = text.EndsWith('\0');
        if (endsWithNul) pieces.RemoveAt(pieces.Count - 1);
        int total = pieces.Count;
        int limit = count >= 0 ? Math.Min(count, total) : Math.Max(0, total + count);
        var sb = new StringBuilder();
        for (int i = 0; i < limit; i++)
        {
            sb.Append(pieces[i]);
            if (i < total - 1 || endsWithNul) sb.Append('\0');
        }
        return sb.ToString();
    }

    /// <summary>The pipeline-mode body (also serves a "-" operand): head of buffered records.</summary>
    private void EmitFromRecords(List<PSObject> pipeline, int? byteCount, int count)
    {
        {
            if (_zero && byteCount == null)
            {
                foreach (var rec in BashRuntime.ByteSliceRecords(
                             ZeroHead(BashRuntime.RecordStreamText(pipeline), count)))
                    WriteObject(rec);
                return;
            }
            if (byteCount != null)
            {
                byte[] bytes = RawBytes.GetBytes(BashRuntime.RecordStreamText(pipeline));
                // GNU head: -c N takes the first N bytes; -c -K takes all but the last K.
                int take = byteCount.Value >= 0
                    ? Math.Min(byteCount.Value, bytes.Length)
                    : Math.Max(0, bytes.Length + byteCount.Value);
                // Byte slice: a TRANSFORMER — fresh text, exact bytes (no record boundary added).
                foreach (var rec in BashRuntime.ByteSliceRecords(RawBytes.GetString(bytes, 0, take)))
                    WriteObject(rec);
                return;
            }

            // GNU head: -n N emits the first N lines; -n -K emits all but the last K.
            // For the negative form the emit limit needs the total line count, which
            // the buffered pipeline provides.
            int limit = count;
            if (count < 0)
            {
                int total = 0;
                foreach (var item in pipeline)
                    total += ItemLines(BashRuntime.GetBashText(item)).Length;
                limit = Math.Max(0, total + count);
            }

            int emitted = 0;
            foreach (var item in pipeline)
            {
                if (emitted >= limit) break;
                var lines = ItemLines(BashRuntime.GetBashText(item));
                if (lines.Length <= 1)
                {
                    // head is a FILTER: a single-line item is one of its own output lines,
                    // so pass the ORIGINAL object through (LsEntry, CatLine, …).
                    WriteObject(item);
                    emitted++;
                }
                else
                {
                    bool unterminated = BashRuntime.IsUnterminated(item);
                    for (int p = 0; p < lines.Length; p++)
                    {
                        if (emitted >= limit) break;
                        WriteObject(BashRuntime.TextRecord(lines[p], unterminated && p == lines.Length - 1));
                        emitted++;
                    }
                }
            }
            return;
        }
    }

    /// <summary>File mode body for one resolved file (header already written).</summary>
    private void EmitFile(string filePath, int? byteCount, int count)
    {
        {
            if (byteCount != null)
            {
                try
                {
                    if (byteCount.Value < 0)
                    {
                        // GNU head -c -K: all but the last K bytes. This needs the
                        // whole file, so read it fully and drop the trailing K.
                        byte[] all = BashFileSystem.ReadAllBytes(filePath);
                        int take = Math.Max(0, all.Length + byteCount.Value);
                        foreach (var rec in BashRuntime.ByteSliceRecords(RawBytes.GetString(all, 0, take)))
                            WriteObject(rec);
                        return;
                    }
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
                    foreach (var rec in BashRuntime.ByteSliceRecords(
                                 RawBytes.GetString(ms.GetBuffer(), 0, (int)ms.Length)))
                        WriteObject(rec);
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(this, $"head: cannot read '{filePath}': {ex.Message}");
                }
                return;
            }

            // Line mode over text lines that remember whether the LAST one had its newline: GNU head
            // copies a missing final newline through (the next header's separator then ends it).
            try
            {
                if (count < 0)
                {
                    // GNU head -n -K: emit all lines but the last K. Buffer the file's
                    // lines, then emit the first (total - K).
                    var allLines = BashFileSystem.ReadTextLines(filePath).ToList();
                    int emit = Math.Max(0, allLines.Count + count);
                    for (int idx = 0; idx < emit; idx++)
                    {
                        WriteCatLine(allLines[idx].Text, idx + 1, filePath, !allLines[idx].HasTrailingNewline);
                    }
                }
                else
                {
                    int li = 0;
                    if (count == 0) return;
                    foreach (var line in BashFileSystem.ReadTextLines(filePath))
                    {
                        li++;
                        WriteCatLine(line.Text, li, filePath, !line.HasTrailingNewline);
                        if (li >= count) break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                // GNU: head: cannot open 'x' for reading: No such file or directory (path shown as
                // typed by OperandDisplay at the stderr sink).
                FileSystemHelpers.WriteBashError(this,
                    $"head: cannot open '{filePath.Replace('\\', '/')}' for reading: {FileSystemHelpers.ReadErrorMessage(ex)}");
            }
        }
    }

    /// <summary>
    /// Emit one <c>PsBash.CatLine</c> object for a file-mode line (shared by the
    /// positive first-N and the negative all-but-last-K paths).
    /// </summary>
    private void WriteCatLine(string line, int lineNumber, string filePath, bool unterminated = false)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.CatLine");
        obj.Properties.Add(new PSNoteProperty("LineNumber", lineNumber));
        obj.Properties.Add(new PSNoteProperty("Content", line));
        obj.Properties.Add(new PSNoteProperty("FileName", filePath));
        obj.Properties.Add(new PSNoteProperty(
            "BashText", BashRuntime.NormalizeBashText(line)));
        if (unterminated) obj.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
        WriteObject(obj);
    }

    /// <summary>
    /// Split a pipeline item's BashText into its constituent lines. A trailing <c>'\n'</c>
    /// TERMINATES the last line, so the split's spurious trailing empty element is dropped
    /// — but genuine interior/trailing BLANK lines are KEPT. Used by the <c>head -n -K</c>
    /// line count + emit so both agree with GNU. (The old <c>TrimEnd('\n')</c> stripped ALL
    /// trailing newlines, undercounting <c>"a\nb\n\n\n"</c> as 2 lines instead of 4.)
    /// </summary>
    internal static string[] ItemLines(string text)
    {
        if (text.EndsWith('\n'))
        {
            var parts = text.Split('\n');
            return parts[..^1]; // drop the terminator's trailing empty element
        }
        return text.Split('\n');
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
    /// error via <see cref="FileSystemHelpers.WriteBashError"/> and returns <c>null</c>.
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
            // GNU: head: cannot open 'x' for reading: No such file or directory (path shown as typed
            // by OperandDisplay at the stderr sink).
            FileSystemHelpers.WriteBashError(this,
                $"{command}: cannot open '{path.Replace('\\', '/')}' for reading: {FileSystemHelpers.ReadErrorMessage(ex)}");
            return null;
        }

        var bom = new byte[3];
        int read = fs.Read(bom, 0, 3);
        bool hasBom = read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
        if (!hasBom && read > 0)
        {
            fs.Seek(0, SeekOrigin.Begin);
        }

        return new StreamReader(fs, RawBytes.Encoding, detectEncodingFromByteOrderMarks: false);
    }

    /// <summary>
    /// Reimplements the psm1 <c>Resolve-BashGlob</c> slice in C# (see
    /// <see cref="InvokeBashWcCommand"/> for the rationale).
    /// </summary>
    private IEnumerable<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        foreach (var rawP in paths)
        {
            if (rawP == "-") { yield return "-"; continue; }
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
                var resolvedLiteral = FileSystemHelpers.ProviderPath(this, p);
                OperandDisplay.Remember(this, resolvedLiteral, rawP);
                yield return resolvedLiteral;
            }
        }
    }
}
