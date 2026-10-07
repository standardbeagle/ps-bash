using PsBash.Core;
using System.Management.Automation;
using System.Text;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashWc</c> function
/// (REFACTOR-2 Phase 1c). Counts lines, words, and bytes of pipeline or file
/// input, matching the bash <c>wc</c> command.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact behavior:
/// <list type="bullet">
/// <item>Flags <c>-l</c> / <c>-w</c> / <c>-c</c> select a single column; with
/// none, all three columns are emitted. Parsed via
/// <see cref="BashRuntime.ConvertFromBashArgs"/> (the same boolean-flag parser
/// the psm1 oracle used).</item>
/// <item>Pipeline mode: each input item is split on <c>\n</c>; words are
/// whitespace-delimited (space/tab/CR/LF, empties removed); bytes are the
/// UTF-8 byte count of each line plus one for the line's <c>\n</c>.</item>
/// <item>File mode: operands are resolved via the psm1 <c>Resolve-BashGlob</c>
/// (reachable because a <see cref="PSCmdlet"/> has
/// <see cref="PSCmdlet.SessionState"/>); a missing file emits a bash-style
/// error and is skipped; the byte count is the file length minus a UTF-8 BOM
/// if present; line/word counts come from the CRLF-normalized text.</item>
/// <item>Multiple files emit a trailing <c>total</c> row.</item>
/// <item>Each result is a typed <c>PsBash.WcResult</c> PSObject carrying
/// <c>Lines</c>, <c>Words</c>, <c>Bytes</c>, <c>FileName</c>, and the formatted
/// <c>BashText</c> — the same column padding the psm1 oracle produced
/// (7-wide single column; 7/8/8 for the three-column form), left-trimmed.</item>
/// </list>
///
/// psm1-only dependencies, and why a clean migration is still possible:
/// <c>Resolve-BashGlob</c> needs the <c>$PWD</c> path provider and the
/// <c>--help</c> path needs the script-scoped help tables — both are reachable
/// from a <see cref="PSCmdlet"/> via <see cref="PSCmdlet.SessionState"/> /
/// <c>InvokeCommand.InvokeScript</c> with a string body, so the cmdlet stays
/// AOT-safe with no ScriptBlock construction on the hot path. The
/// <c>Resolve-BashGlob</c> glob slice is reimplemented here in C# rather than
/// calling back into psm1, matching the oracle's logic exactly.
///
/// Common-parameter collision: the bash flag <c>-w</c> prefix-collides with the
/// PowerShell common parameters <c>-WarningAction</c> / <c>-WarningVariable</c>
/// — an unbound <c>-w</c> would be rejected as ambiguous before reaching
/// <see cref="Arguments"/>. It is therefore declared as an explicit
/// <see cref="SwitchParameter"/> (<see cref="W"/>): an exact parameter-name
/// match beats a common-parameter prefix match, so <c>Invoke-BashWc -w</c>
/// binds the way the psm1 oracle's <c>$args</c> scan did. <c>-c</c>
/// (bytes-only) likewise prefix-collides with <c>-Confirm</c> and is declared
/// as <see cref="C"/> (an earlier audit wrongly called <c>-c</c> collision-free
/// — a bare <c>-c</c> was silently bound to <c>-Confirm</c> and the byte count
/// dropped). <c>-l</c> has no colliding prefix and stays in
/// <see cref="Arguments"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashWc")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashWcCommand : PSCmdlet
{
    /// <summary>
    /// The bash <c>-w</c> (words-only) switch — declared explicitly because the
    /// bare token <c>-w</c> prefix-collides with <c>-WarningAction</c> /
    /// <c>-WarningVariable</c> common parameters. See the class remarks.
    /// </summary>
    [Parameter]
    public SwitchParameter W { get; set; }

    /// <summary>
    /// The bash <c>-c</c> (bytes-only) switch — declared explicitly because the
    /// bare token <c>-c</c> prefix-collides with the <c>-Confirm</c> common
    /// parameter and would otherwise be silently bound (the flag dropped, byte
    /// count never selected) before reaching <see cref="Arguments"/>. An exact
    /// parameter-name match beats the common-parameter prefix match.
    /// </summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // Parsed-once state.
    private bool _parsed;
    private bool _linesOnly, _wordsOnly, _bytesOnly, _charsOnly, _maxLineOnly;
    private List<string> _operands = new();
    // True when stdin must NOT be counted: file operands present (file mode
    // ignores stdin) or a --help / --version request.
    private bool _suppressStdin;
    // Streamed counters — wc needs only running totals, never the buffered
    // pipeline. int (not long) for byte-exact parity with the buffered oracle.
    private int _totalLines, _totalWords, _totalBytes, _totalChars, _maxLine;

    /// <summary>
    /// Valid GNU <c>wc</c> options ps-bash does not implement, refused loudly (exit 2) by the
    /// shared parser. <c>--files0-from=F</c> (NUL-separated file list) and <c>--total=WHEN</c> are
    /// GNU 9.4 options with no implementation here. (A string[] on purpose:
    /// CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] WcValidButUnsupported = Array.Empty<string>();

    private const string OptLines = "lines", OptWords = "words", OptBytes = "bytes",
        OptChars = "chars", OptMaxLine = "maxline", OptTotal = "total", OptFiles0 = "files0from";

    /// <summary>GNU <c>--total=WHEN</c>: when the "total" line prints.</summary>
    internal enum TotalMode { Auto, Always, Only, Never }

    /// <summary>
    /// wc's option surface (GNU coreutils 9.4: -c -m -l -L -w + --bytes --chars --lines
    /// --max-line-length --words, --files0-from, --total). Built once for the shared ordered parser.
    /// </summary>
    private static readonly OptSpecSet WcSpec = new(
        new[]
        {
            new OptSpec(OptBytes, 'c', "bytes"),
            new OptSpec(OptChars, 'm', "chars"),
            new OptSpec(OptLines, 'l', "lines"),
            new OptSpec(OptMaxLine, 'L', "max-line-length"),
            new OptSpec(OptWords, 'w', "words"),
            new OptSpec(OptTotal, '\0', "total", OptKind.Value),
            new OptSpec(OptFiles0, '\0', "files0-from", OptKind.Value),
        },
        validButUnsupported: WcValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, WcSpec);

    /// <summary>The resolved meaning of a wc argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class WcArgs
    {
        public ParsedArgs Parsed = null!;
        public bool Lines, Words, Bytes, Chars, MaxLine;
        public TotalMode Total = TotalMode.Auto;
        /// <summary>--files0-from=F ("-" = stdin): the NUL-separated list of names to count.</summary>
        public string? Files0From;
        public List<string> Operands = new();
        /// <summary>A usage error the scan cannot see (bad --total WHEN, operands with --files0-from); exit 1.</summary>
        public string? Error;

        /// <summary>True when nothing further should execute: scan error or --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    private static readonly string[] TotalWords = { "auto", "always", "only", "never" };

    /// <summary>Scan + resolve the column selectors (any order, any bundling, repeats are harmless).</summary>
    internal static WcArgs Plan(string[] args)
    {
        var p = ScanArgs(args);
        var w = new WcArgs
        {
            Parsed = p,
            Lines = p.Has(OptLines),
            Words = p.Has(OptWords),
            Bytes = p.Has(OptBytes),
            Chars = p.Has(OptChars),
            MaxLine = p.Has(OptMaxLine),
            Operands = p.Operands(),
        };
        if (p.HasError) return w;

        // --total=WHEN: argmatch (exact word, else a unique prefix); the LAST one wins.
        if (p.Last(OptTotal)?.Value is { } when)
        {
            var hits = TotalWords.Where(t => t.StartsWith(when, StringComparison.Ordinal)).ToList();
            if (when.Length == 0 || hits.Count == 0 || (hits.Count > 1 && !TotalWords.Contains(when)))
            {
                string what = hits.Count > 1 && when.Length > 0 ? "ambiguous" : "invalid";
                w.Error = $"wc: {what} argument '{when}' for '--total'\nValid arguments are:\n"
                          + string.Join("\n", TotalWords.Select(t => $"  - '{t}'"))
                          + "\nTry 'wc --help' for more information.";
                return w;
            }
            string chosen = TotalWords.Contains(when) ? when : hits[0];
            w.Total = chosen switch { "always" => TotalMode.Always, "only" => TotalMode.Only, "never" => TotalMode.Never, _ => TotalMode.Auto };
        }

        if (p.Last(OptFiles0)?.Value is { } f0)
        {
            w.Files0From = f0;
            if (w.Operands.Count > 0)
            {
                w.Error = $"wc: extra operand '{w.Operands[0]}'\nfile operands cannot be combined with --files0-from\nTry 'wc --help' for more information.";
            }
        }
        return w;
    }

    /// <summary>Arguments with the decoy-bound flags re-injected (bare -w binds -WarningAction, bare
    /// -c would bind -Confirm on a cmdlet that supports it).</summary>
    private string[] ArgsWithDecoys() => BashRuntime.PrependDecoys(Arguments, (W.IsPresent, "-w"), (C.IsPresent, "-c"));

    private WcArgs? _plan;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = ArgsWithDecoys();

        // --help / --version short-circuit before flag parsing (oracle order).
        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        // Shared ordered parser: bundles (-lw, -wc), attached long forms, abbreviations
        // (--li), `--`, and the unsupported/unknown classifier in ONE scan. A scan error is
        // reported from EndProcessing; stdin is not counted for it.
        _plan = Plan(args);
        _linesOnly = _plan.Lines;
        _wordsOnly = _plan.Words;
        _bytesOnly = _plan.Bytes;
        _charsOnly = _plan.Chars;
        _maxLineOnly = _plan.MaxLine;
        _operands = _plan.Operands;
        _suppressStdin = _plan.Declined || _operands.Count > 0;
    }

    // --files0-from: the pipeline is the name list (or an entry "-"), so it is kept, not counted.
    private readonly List<object> _stdinRecords = new();

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_plan is { Files0From: not null, Declined: false }) { _stdinRecords.Add(InputObject); return; }
        if (_suppressStdin) return;

        // Stream the counts instead of buffering the pipe — wc only ever needs
        // running totals. Per-record accumulation is exactly the buffered
        // oracle's per-item loop (no cross-item state in the counting).
        // Count the BYTE STREAM the record renders to (record terminator contract): an
        // unterminated record (printf 'abc', echo -n) contributes exactly its bytes, a normal
        // record BashText + "\n". -l therefore counts newlines, as GNU does, and an unterminated
        // record glues to the next one (words, -L) because the state carries across records.
        string text = BashRuntime.GetBashText(InputObject);
        if (!text.EndsWith('\n') && !BashRuntime.IsUnterminated(InputObject)) text += "\n";
        AccumulateStream(text);
    }

    // Cross-record state: a word / line may span an unterminated record boundary.
    private bool _inWord;
    private int _curLine;

    private void AccumulateStream(string text)
    {
        _totalBytes += RawBytes.GetByteCount(text);
        char prev = '\0';
        foreach (char c in text)
        {
            // An escaped byte (invalid UTF-8, see RawBytes) is not a character: GNU wc in a UTF-8 locale
            // counts it in -c only, and it neither starts nor ends a word (not printable, not space).
            if (RawBytes.IsMarkerChar(c) && !char.IsHighSurrogate(prev)) { prev = c; continue; }
            prev = c;
            bool isLow = char.IsLowSurrogate(c);
            if (!isLow) _totalChars++;
            if (c == '\n')
            {
                _totalLines++;
                if (_curLine > _maxLine) _maxLine = _curLine;
                _curLine = 0;
            }
            else if (!isLow)
            {
                _curLine++;
            }

            if (c is ' ' or '\t' or '\n' or '\r') _inWord = false;
            else if (!_inWord) { _totalWords++; _inWord = true; }
        }
        if (_curLine > _maxLine) _maxLine = _curLine; // a final unterminated line still counts for -L
    }

    /// <summary>Unicode code points (scalar values) in a string — a surrogate
    /// pair counts as one, matching GNU <c>wc -m</c> per-character counting.</summary>
    private static int CountCodePoints(string s)
    {
        int n = 0;
        char prev = '\0';
        foreach (var c in s)
        {
            bool escapedByte = RawBytes.IsMarkerChar(c) && !char.IsHighSurrogate(prev);
            if (!escapedByte && !char.IsLowSurrogate(c)) n++;
            prev = c;
        }
        return n;
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "wc", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "wc"))
            {
                WriteObject(line);
            }
            return;
        }

        if (_plan is { } plan)
        {
            if (FileSystemHelpers.TryWriteParseError(this, "wc", plan.Parsed)) return;
            if (FileSystemHelpers.TryHandleInfoOptions(this, "wc", plan.Parsed)) return;
            if (plan.Error is { } planError)
            {
                FileSystemHelpers.WriteBashError(this, planError); // exit 1, GNU's usage status
                return;
            }
        }
        var totalMode = _plan?.Total ?? TotalMode.Auto;

        // Pipeline mode: counts were streamed in ProcessRecord. Always emit, like GNU wc on an
        // empty stdin (`echo -n '' | wc -c` prints 0; `printf '' | wc` prints 0 0 0).
        if (_operands.Count == 0 && _plan?.Files0From is null)
        {
            // --total=only prints the single count line without a name; always adds a total line too.
            if (totalMode != TotalMode.Only)
                WriteObject(BuildResult(_totalLines, _totalWords, _totalBytes, _totalChars, _maxLine, string.Empty));
            if (totalMode is TotalMode.Always or TotalMode.Only)
                WriteObject(BuildResult(_totalLines, _totalWords, _totalBytes, _totalChars, _maxLine,
                    totalMode == TotalMode.Only ? string.Empty : "total"));
            return;
        }

        // File mode
        int grandLines = 0, grandWords = 0, grandBytes = 0, grandChars = 0, grandMax = 0;
        IEnumerable<string> names = _operands;
        int nameCount = _operands.Count;
        if (_plan?.Files0From is { } listSource)
        {
            var listed = ReadFiles0List(listSource);
            if (listed is null) return;
            names = listed;
            nameCount = listed.Count;
        }
        bool multipleFiles = nameCount > 1;
        bool files0 = _plan?.Files0From is not null;
        bool printTotal = totalMode switch
        {
            TotalMode.Always or TotalMode.Only => true,
            TotalMode.Never => false,
            _ => multipleFiles,
        };

        foreach (var filePath in files0 ? names.Select(n => n == "-" ? n : ResolveVerbatim(n)) : ResolveGlob(_operands))
        {
            if (filePath == "-" && files0)
            {
                var (sl, sw, sc, sm, sx) = CountRecords();
                grandLines += sl; grandWords += sw; grandBytes += sc; grandChars += sm; if (sx > grandMax) grandMax = sx;
                if (totalMode != TotalMode.Only)
                    WriteObject(BuildResult(sl, sw, sc, sm, sx, string.Empty));
                continue;
            }
            // The null device (/dev/null, NUL) is an empty file, not a missing one.
            if (!File.Exists(filePath) && !Directory.Exists(filePath) && !FileSystemHelpers.IsNullDevice(filePath))
            {
                WriteBashError($"wc: {filePath}: No such file or directory");
                continue;
            }

            long fileBytes;
            try
            {
                // The file's exact size: a BOM is bytes like any other (GNU counts it).
                fileBytes = new FileInfo(filePath).Length;
                if (FileSystemHelpers.IsNullDevice(filePath)) fileBytes = 0;
            }
            catch
            {
                // If BOM peek fails, use raw file size; if even FileInfo failed
                // the file was unreadable — fall back to 0 to mirror best-effort.
                try { fileBytes = new FileInfo(filePath).Length; }
                catch { fileBytes = 0; }
            }

            int lineCount;
            int wordCount;
            int charCount;
            int maxLineLen;
            try
            {
                (lineCount, wordCount, charCount, maxLineLen) = CountFileText(filePath);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                string normalized = filePath.Replace('\\', '/');
                WriteBashError($"wc: {normalized}: {FileSystemHelpers.ReadErrorMessage(ex)}");
                continue;
            }

            grandLines += lineCount;
            grandWords += wordCount;
            grandBytes += (int)fileBytes;
            grandChars += charCount;
            if (maxLineLen > grandMax) grandMax = maxLineLen;

            if (totalMode != TotalMode.Only)
                WriteObject(BuildResult(
                    lineCount, wordCount, (int)fileBytes, charCount, maxLineLen, filePath));
        }

        if (printTotal)
        {
            WriteObject(BuildResult(
                grandLines, grandWords, grandBytes, grandChars, grandMax,
                totalMode == TotalMode.Only ? string.Empty : "total"));
        }
    }

    /// <summary>A --files0-from entry is used verbatim: no globbing, resolved against the PowerShell location.</summary>
    private string ResolveVerbatim(string name)
    {
        var resolved = FileSystemHelpers.ProviderPath(this, FileSystemHelpers.NormalizeOperandPath(name));
        OperandDisplay.Remember(this, resolved, name);
        return resolved;
    }

    /// <summary>The counts of the buffered pipeline (the "-" entry of a --files0-from list).</summary>
    private (int Lines, int Words, int Bytes, int Chars, int Max) CountRecords()
    {
        _totalLines = _totalWords = _totalBytes = _totalChars = _maxLine = 0;
        _inWord = false; _curLine = 0;
        foreach (var rec in _stdinRecords)
        {
            string text = BashRuntime.GetBashText(rec);
            if (!text.EndsWith('\n') && !BashRuntime.IsUnterminated(rec)) text += "\n";
            AccumulateStream(text);
        }
        _stdinRecords.Clear();
        return (_totalLines, _totalWords, _totalBytes, _totalChars, _maxLine);
    }

    /// <summary>
    /// The NUL-separated name list of <c>--files0-from=SRC</c> (SRC = "-" is the pipeline). An empty
    /// name is GNU's <c>wc: SRC:N: invalid zero-length file name</c> (exit 1, the rest still counted):
    /// it is kept in the list as a null marker so the entry numbering and the total count stay GNU's.
    /// Returns null (diagnostic written) when the list cannot be opened.
    /// </summary>
    private List<string>? ReadFiles0List(string source)
    {
        string text;
        if (source == "-")
        {
            text = RawBytes.GetString(RawBytes.GetBytes(BashRuntime.RecordStreamText(_stdinRecords.Cast<object>())));
            _stdinRecords.Clear();
        }
        else
        {
            try { text = RawBytes.GetString(BashFileSystem.ReadAllBytes(FileSystemHelpers.ProviderPath(this, source))); }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                WriteBashError($"wc: cannot open '{source}' for reading: {FileSystemHelpers.ReadErrorMessage(ex)}");
                return null;
            }
        }

        var pieces = text.Split('\0').ToList();
        if (pieces.Count > 0 && pieces[^1].Length == 0) pieces.RemoveAt(pieces.Count - 1);
        var names = new List<string>();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Length == 0)
            {
                WriteBashError($"wc: {source}:{i + 1}: invalid zero-length file name");
                continue;
            }
            names.Add(pieces[i]);
        }
        return names;
    }

    private static int CountWords(string text)
    {
        // Count maximal runs of non-whitespace — identical to
        // Split(WhitespaceChars, RemoveEmptyEntries).Length but without
        // allocating (and discarding) a word array on every line.
        int words = 0;
        bool inWord = false;
        char prev = '\0';
        foreach (char c in text)
        {
            if (RawBytes.IsMarkerChar(c) && !char.IsHighSurrogate(prev)) { prev = c; continue; }
            prev = c;
            if (c is ' ' or '\t' or '\n' or '\r') inWord = false;
            else if (!inWord) { words++; inWord = true; }
        }
        return words;
    }

    private static (int Lines, int Words, int Chars, int MaxLine) CountFileText(string path)
    {
        using var fs = BashFileSystem.OpenRead(path);
        using var reader = BashFileSystem.OpenRawReader(fs, leaveOpen: true);

        int lines = 0;
        int words = 0;
        int chars = 0;     // code points, including newlines (GNU -m)
        int maxLine = 0;   // longest line in code points, excluding the newline
        int curLine = 0;
        bool inWord = false;
        var buffer = new char[16384];
        int read;
        char prev = '\0';
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                // An escaped byte (invalid UTF-8) is no character and no word boundary (see AccumulateStream).
                if (RawBytes.IsMarkerChar(c) && !char.IsHighSurrogate(prev)) { prev = c; continue; }
                prev = c;
                // Count code points: a surrogate pair (high+low) is one char.
                bool isLow = char.IsLowSurrogate(c);
                if (!isLow) chars++;

                if (c == '\n')
                {
                    lines++;
                    if (curLine > maxLine) maxLine = curLine;
                    curLine = 0;
                }
                else if (!isLow)
                {
                    curLine++;
                }

                bool isWordChar = c is not (' ' or '\t' or '\n' or '\r');
                if (isWordChar)
                {
                    if (!inWord)
                    {
                        words++;
                        inWord = true;
                    }
                }
                else
                {
                    inWord = false;
                }
            }
        }
        // A final line without a trailing newline still counts toward -L.
        if (curLine > maxLine) maxLine = curLine;

        return (lines, words, chars, maxLine);
    }

    /// <summary>
    /// Format the wc output line (the exact <c>BashText</c> the cmdlet emits),
    /// shared with the fused-pipeline streaming stage so the fused lane's bytes
    /// are identical to the unfused path by construction. Column selection +
    /// 7/8-wide padding + leading-whitespace trim, then
    /// <see cref="BashRuntime.NormalizeBashText"/>.
    /// </summary>
    internal static string FormatWcText(
        bool linesOnly, bool wordsOnly, bool charsOnly, bool bytesOnly, bool maxLineOnly,
        int lines, int words, int bytes, int chars, int maxLine, string fileName)
    {
        // GNU prints the SELECTED columns in a fixed order: lines, words, chars,
        // bytes, max-line-length. With no selector, the default is lines/words/bytes.
        var cols = new List<int>(5);
        if (linesOnly) cols.Add(lines);
        if (wordsOnly) cols.Add(words);
        if (charsOnly) cols.Add(chars);
        if (bytesOnly) cols.Add(bytes);
        if (maxLineOnly) cols.Add(maxLine);
        if (cols.Count == 0) { cols.Add(lines); cols.Add(words); cols.Add(bytes); }

        var sb = new StringBuilder();
        for (int k = 0; k < cols.Count; k++)
        {
            // Width parity with the oracle: first column 7-wide, the rest 8-wide
            // (so a single column is 7 and the default triple is 7/8/8).
            sb.Append(cols[k].ToString().PadLeft(k == 0 ? 7 : 8));
        }
        if (fileName.Length > 0)
        {
            sb.Append(' ').Append(fileName);
        }

        // psm1 oracle: ($parts -join '') -replace '^\s+', ' ' then TrimStart().
        // Net effect is a plain TrimStart of leading whitespace.
        return BashRuntime.NormalizeBashText(sb.ToString().TrimStart());
    }

    /// <summary>Count words (maximal non-whitespace runs) — shared with the fused stage.</summary>
    internal static int CountWordsInLine(string text) => CountWords(text);

    /// <summary>Count Unicode code points — shared with the fused stage.</summary>
    internal static int CountCodePointsInLine(string s) => CountCodePoints(s);

    private PSObject BuildResult(
        int lines, int words, int bytes, int chars, int maxLine, string fileName)
    {
        fileName = fileName.Length > 0 && fileName != "total" ? OperandDisplay.Rewrite(this, fileName) : fileName;
        string bashText = FormatWcText(
            _linesOnly, _wordsOnly, _charsOnly, _bytesOnly, _maxLineOnly,
            lines, words, bytes, chars, maxLine, fileName);

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.WcResult");
        obj.Properties.Add(new PSNoteProperty("Lines", lines));
        obj.Properties.Add(new PSNoteProperty("Words", words));
        obj.Properties.Add(new PSNoteProperty("Bytes", bytes));
        obj.Properties.Add(new PSNoteProperty("Chars", chars));
        obj.Properties.Add(new PSNoteProperty("MaxLineLength", maxLine));
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty("BashText", BashRuntime.NormalizeBashText(bashText)));
        return obj;
    }

    /// <summary>
    /// Reimplements the psm1 <c>Resolve-BashGlob</c> slice in C#: a path with
    /// <c>*</c> or <c>?</c> is expanded against the current location; if it
    /// matches nothing it passes through literally so the caller emits its own
    /// error. A literal path is resolved against the shell's <c>$PWD</c> via
    /// <see cref="PathIntrinsics.GetUnresolvedProviderPathFromPSPath"/> — the
    /// same provider-aware resolution the psm1 oracle used (not
    /// <see cref="Directory.GetCurrentDirectory"/>).
    /// </summary>
    private IEnumerable<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
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
                    // No matches — fall through to literal passthrough.
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
                var lit = FileSystemHelpers.ProviderPath(this, p);
                OperandDisplay.Remember(this, lit, p);
                yield return lit;
            }
        }
    }

    private void WriteBashError(string message)
    {
        FileSystemHelpers.WriteBashError(this, message);
    }
}
