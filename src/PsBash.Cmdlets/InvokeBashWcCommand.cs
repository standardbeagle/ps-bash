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
    private static readonly string[] WcValidButUnsupported = { "--files0-from", "--total" };

    private const string OptLines = "lines", OptWords = "words", OptBytes = "bytes",
        OptChars = "chars", OptMaxLine = "maxline";

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
        public List<string> Operands = new();

        /// <summary>True when nothing further should execute: scan error or --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>Scan + resolve the column selectors (any order, any bundling, repeats are harmless).</summary>
    internal static WcArgs Plan(string[] args)
    {
        var p = ScanArgs(args);
        return new WcArgs
        {
            Parsed = p,
            Lines = p.Has(OptLines),
            Words = p.Has(OptWords),
            Bytes = p.Has(OptBytes),
            Chars = p.Has(OptChars),
            MaxLine = p.Has(OptMaxLine),
            Operands = p.Operands(),
        };
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
    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
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
        _totalBytes += Encoding.UTF8.GetByteCount(text);
        foreach (char c in text)
        {
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
        foreach (var c in s)
        {
            if (!char.IsLowSurrogate(c)) n++;
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
        }

        // Pipeline mode: counts were streamed in ProcessRecord. Always emit, like GNU wc on an
        // empty stdin (`echo -n '' | wc -c` prints 0; `printf '' | wc` prints 0 0 0).
        if (_operands.Count == 0)
        {
            WriteObject(BuildResult(
                _totalLines, _totalWords, _totalBytes, _totalChars, _maxLine, string.Empty));
            return;
        }

        // File mode
        int grandLines = 0, grandWords = 0, grandBytes = 0, grandChars = 0, grandMax = 0;
        bool multipleFiles = _operands.Count > 1;

        foreach (var filePath in ResolveGlob(_operands))
        {
            // The null device (/dev/null, NUL) is an empty file, not a missing one.
            if (!File.Exists(filePath) && !Directory.Exists(filePath) && !FileSystemHelpers.IsNullDevice(filePath))
            {
                WriteBashError($"wc: {filePath}: No such file or directory");
                continue;
            }

            long fileBytes;
            try
            {
                fileBytes = new FileInfo(filePath).Length;
                using var fs = BashFileSystem.OpenRead(filePath);
                var bom = new byte[3];
                if (fs.Read(bom, 0, 3) >= 3
                    && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                {
                    fileBytes -= 3;
                }
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
                bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                    || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
                string msg = notFound ? "No such file or directory" : ex.Message;
                WriteBashError($"wc: {normalized}: {msg}");
                continue;
            }

            grandLines += lineCount;
            grandWords += wordCount;
            grandBytes += (int)fileBytes;
            grandChars += charCount;
            if (maxLineLen > grandMax) grandMax = maxLineLen;

            WriteObject(BuildResult(
                lineCount, wordCount, (int)fileBytes, charCount, maxLineLen, filePath));
        }

        if (multipleFiles)
        {
            WriteObject(BuildResult(
                grandLines, grandWords, grandBytes, grandChars, grandMax, "total"));
        }
    }

    private static int CountWords(string text)
    {
        // Count maximal runs of non-whitespace — identical to
        // Split(WhitespaceChars, RemoveEmptyEntries).Length but without
        // allocating (and discarding) a word array on every line.
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r') inWord = false;
            else if (!inWord) { words++; inWord = true; }
        }
        return words;
    }

    private static (int Lines, int Words, int Chars, int MaxLine) CountFileText(string path)
    {
        using var fs = BashFileSystem.OpenRead(path);
        using var reader = new StreamReader(
            fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        int lines = 0;
        int words = 0;
        int chars = 0;     // code points, including newlines (GNU -m)
        int maxLine = 0;   // longest line in code points, excluding the newline
        int curLine = 0;
        bool inWord = false;
        var buffer = new char[16384];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
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
                yield return SessionState.Path.GetUnresolvedProviderPathFromPSPath(p);
            }
        }
    }

    private void WriteBashError(string message)
    {
        FileSystemHelpers.WriteBashError(this, message);
    }
}
