using PsBash.Core;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashSed</c> function
/// (REFACTOR-2 Phase 3). Stream-edits pipeline or file input, matching the bash
/// <c>sed</c> command.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashSed</c> together
/// with its pure helpers <c>ConvertFrom-SedExpression</c> and
/// <c>Test-SedAddress</c>. All three are reimplemented here in C#:
/// <list type="bullet">
/// <item><b>Expression parsing</b> (<see cref="ParseExpression"/>) — reproduces
/// <c>ConvertFrom-SedExpression</c>: an optional address prefix
/// (<c>/regex/</c>, <c>/start/,/end/</c>, <c>N</c>, <c>N,M</c>, <c>N,$</c>)
/// followed by a command (<c>s</c>, <c>d</c>, <c>D</c>, <c>p</c>, <c>P</c>,
/// <c>N</c>, <c>q</c>, <c>a</c>, <c>i</c>, <c>c</c>, <c>y</c>). The <c>s</c>
/// command's replacement backreference translation (<c>\1</c>-<c>\9</c> →
/// <c>$1</c>-<c>$9</c>, <c>\&amp;</c> → <c>$0</c>) and the BRE→.NET
/// metacharacter escaping (when not in extended-regex mode) match the oracle
/// byte-for-byte.</item>
/// <item><b>Address matching + cycle engine</b> (<see cref="SedEngine"/>) — a push state machine
/// (one record in, output out as each cycle completes; range addresses tracked incrementally)
/// reproducing the
/// pattern-space loop with multi-line pattern space (<c>N</c>/<c>D</c>),
/// restart-cycle semantics, <c>p</c>/<c>P</c> printing, <c>a</c>/<c>i</c>/<c>c</c>
/// insert/append, <c>q</c> early-quit, and <c>y</c> transliteration.</item>
/// </list>
/// Records STREAM: pipeline input is fed to the engine per record and file operands are read through a streaming
/// reader (no whole-file read), so retained memory is the pattern space plus a one-record lookahead.
/// File mode resolves operands through <see cref="FileSystemHelpers.ResolveOperandPaths"/> (so a
/// diagnostic names the operand as typed), reads with CRLF normalization, and supports
/// <c>-i[SUFFIX]</c> in-place rewrite with a backup; without <c>-i</c>/<c>-s</c> the files are ONE
/// continuous stream like GNU (line numbers and <c>$</c> span them). Pipeline
/// mode preserves original typed objects where a one-to-one line mapping holds,
/// matching the oracle. File-read / file-write errors emit a bash-style error
/// through <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord);
/// <c>--help</c> delegates to <c>Show-BashHelp</c>.
///
/// Option parsing is the shared ORDERED parser (<see cref="ArgParser"/>, GNU sed 4.9 option table —
/// see <see cref="Plan"/>): repeated <c>-e</c>/<c>-f</c>, bundles (<c>-ne</c>, <c>-nE</c>, <c>-i.bak</c>),
/// unique-prefix long options, the first operand is the script only when no <c>-e</c>/<c>-f</c> was
/// given, usage errors exit 1. The transpiler single-quotes every flag
/// (<c>PsEmitter.OrderedArgCommands</c>), so the whole argv reaches <see cref="Arguments"/> verbatim and
/// in order. Typed directly at PowerShell the binder still intercepts <c>-e</c> (prefix of
/// <c>-ErrorAction</c>; case-insensitive, so <c>-E</c> too), <c>-r</c> and <c>-i</c>: they are declared
/// as <see cref="Expression"/> / <see cref="R"/> / <see cref="I"/> and re-injected as ordinary tokens
/// before parsing, and the psm1 <c>Invoke-BashSed</c> proxy hands every argument over as a literal string
/// so a repeated <c>-e</c> never meets the binder.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSed")]
[OutputType(typeof(PSObject))]
[OutputType(typeof(string))]
public sealed class InvokeBashSedCommand : PSCmdlet
{
    /// <summary>
    /// The bash <c>-e</c> (script expression) value-bearing flag — declared
    /// explicitly because the bare token <c>-e</c> prefix-collides with
    /// <c>-ErrorAction</c> / <c>-ErrorVariable</c> common parameters. Repeatable
    /// (<c>sed -e ... -e ...</c>). See the class remarks.
    /// </summary>
    [Parameter]
    [Alias("e")]
    public string[]? Expression { get; set; }

    /// <summary>
    /// The bash <c>-r</c> (extended / ERE regex) switch — declared explicitly so
    /// the extended-regex bit survives even though <c>-E</c> collides with the
    /// <c>-e</c> expression parameter under case-insensitive binding.
    /// </summary>
    [Parameter]
    public SwitchParameter R { get; set; }

    /// <summary>
    /// The bash <c>-i</c> (in-place edit) switch — declared explicitly because
    /// the bare token <c>-i</c> prefix-collides with the PowerShell common
    /// parameters <c>-InformationAction</c> / <c>-InformationVariable</c>. An
    /// exact parameter-name match beats a common-parameter prefix match. A
    /// bundled short-flag form such as <c>-ni</c> still reaches
    /// <see cref="Arguments"/> and is recovered there.
    /// </summary>
    [Parameter]
    public SwitchParameter I { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>Set by the psm1 proxy only (see InvokeBashGrepCommand.PsBashProxy): behind its steppable pipeline a quit
    /// ignores the rest of the input instead of raising the stop-upstream signal.</summary>
    [Parameter(DontShow = true)] public SwitchParameter PsBashProxy { get; set; }

    // Streaming state: pipeline records feed the engine as they arrive (BeginProcessing resolves the script).
    private bool _pipelineMode, _pDone, _pFinished, _sawRecord, _lastUnterminated, _quitMidRecord, _bufferStdin;
    private char _term = '\n';
    private SedEngine? _engine;
    private RecordSink? _sink;
    private Action? _fileRun;
    private readonly StringBuilder _nulTail = new();
    private List<PSObject>? _stdinBuffer;

    internal const string OptQuiet = "quiet", OptExpr = "expression", OptFile = "file",
        OptExtended = "extended", OptInPlace = "in-place", OptSeparate = "separate",
        OptNullData = "null-data", OptLineLen = "line-length", OptNoop = "noop";

    /// <summary>GNU sed options ps-bash refuses (exit 2): <c>--debug</c> annotates program execution.</summary>
    private static readonly string[] SedValidButUnsupported = { "--debug" };

    /// <summary>
    /// sed's option surface (GNU sed 4.9). <c>-i[SUFFIX]</c> / <c>--in-place[=SUFFIX]</c> take an
    /// ATTACHED suffix only (<c>-i.bak</c>; <c>-i -e x</c> means no suffix). <c>-u -b -l N --posix
    /// --sandbox --follow-symlinks</c> are accepted no-ops. Usage errors exit 1. Ambiguity lists follow
    /// GNU's <c>longopts[]</c> table order (<c>--s</c> = silent, sandbox, separate).
    /// </summary>
    private static readonly OptSpecSet SedSpec = new(
        new[]
        {
            new OptSpec(OptQuiet, 'n', "quiet"),
            new OptSpec(OptQuiet, '\0', "silent"),
            new OptSpec(OptExpr, 'e', "expression", OptKind.Value),
            new OptSpec(OptFile, 'f', "file", OptKind.Value),
            new OptSpec(OptExtended, 'E', "regexp-extended"),
            new OptSpec(OptExtended, 'r', null),
            new OptSpec(OptInPlace, 'i', "in-place", OptKind.OptionalValue),
            new OptSpec(OptSeparate, 's', "separate"),
            new OptSpec(OptNullData, 'z', "null-data"),
            new OptSpec(OptNullData, '\0', "zero-terminated"),
            new OptSpec(OptLineLen, 'l', "line-length", OptKind.Value),
            new OptSpec(OptNoop, 'u', "unbuffered"),
            new OptSpec(OptNoop, 'b', "binary"),
            new OptSpec(OptNoop, '\0', "posix"),
            new OptSpec(OptNoop, '\0', "sandbox"),
            new OptSpec(OptNoop, '\0', "follow-symlinks"),
        },
        SedValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        usageExitCode: 1,
        longOptionOrder: new[]
        {
            "binary", "regexp-extended", "debug", "in-place", "expression", "file", "line-length",
            "null-data", "zero-terminated", "quiet", "posix", "silent", "sandbox", "separate",
            "unbuffered", "version", "help", "follow-symlinks",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, SedSpec);

    internal sealed class SedArgs
    {
        public ParsedArgs Parsed = null!;
        public bool Quiet, Extended, Separate, NullData, InPlace;

        /// <summary>The <c>-i</c> backup suffix (null = edit without a backup).</summary>
        public string? Suffix;

        /// <summary>Every <c>-e SCRIPT</c> / <c>-f FILE</c> in command-line order.</summary>
        public List<(bool IsFile, string Value)> Sources = new();

        public List<string> Operands = new();

        public bool Declined =>
            Parsed.HasError || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + interpret. The first non-option operand is the script only when no <c>-e</c>/<c>-f</c>
    /// was given; <c>-s</c> and <c>-i</c> treat each file as its own stream (the EndProcessing caller
    /// reads <see cref="SedArgs.Sources"/>/<see cref="SedArgs.Operands"/>; the fused core reads the same).
    /// </summary>
    internal static SedArgs Plan(string[] args)
    {
        var s = new SedArgs { Parsed = ScanArgs(args) };
        s.Operands = s.Parsed.Operands();
        if (s.Parsed.HasError) return s;
        if (s.Parsed.Has(OptSpecSet.HelpId) || s.Parsed.Has(OptSpecSet.VersionId)) return s;

        foreach (var tok in s.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptQuiet: s.Quiet = true; break;
                case OptExpr: s.Sources.Add((false, tok.Value!)); break;
                case OptFile: s.Sources.Add((true, tok.Value!)); break;
                case OptExtended: s.Extended = true; break;
                case OptSeparate: s.Separate = true; break;
                case OptNullData: s.NullData = true; break;
                case OptInPlace:
                    s.InPlace = true;
                    s.Suffix = string.IsNullOrEmpty(tok.Value) ? null : tok.Value;
                    break;
                // OptLineLen, OptNoop: accepted, no effect (only the `l` command wraps; nothing buffers).
            }
        }
        return s;
    }

    protected override void BeginProcessing()
    {
        FileSystemHelpers.SetLastExitCode(this, 0);

        // Direct PowerShell calls: -e (value-bearing decoy, also what `-E` binds case-insensitively),
        // -r and -i bind declared parameters instead of reaching Arguments. Re-inject them as the
        // ordinary tokens they stand for. Transpiled bash never gets here: the emitter single-quotes
        // every flag (OrderedArgCommands), so the whole argv arrives verbatim and in order.
        var args = BashRuntime.PrependDecoys(Arguments, (R.IsPresent, "-r"), (I.IsPresent, "-i"));
        if (Expression is { Length: > 0 })
        {
            var extra = new List<string>();
            foreach (var e in Expression) { extra.Add("-e"); extra.Add(e); }
            extra.AddRange(args);
            args = extra.ToArray();
        }

        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "sed", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "sed", plan.Parsed)) return;

        var operands = plan.Operands;
        bool suppressDefault = plan.Quiet;
        var expressions = new List<string>();

        foreach (var (isFile, value) in plan.Sources)
        {
            if (!isFile)
            {
                string chunk = value;
                if (expressions.Count == 0 && TryStripHashN(ref chunk))
                    suppressDefault = true;   // `#n` on the first line of the first script acts like -n
                // GNU joins -e chunks with a newline, so `-e 'a\' -e text` is one multi-line command.
                if (chunk.Length == 0 && expressions.Count == 0) continue;
                if (expressions.Count > 0 && expressions[^1].EndsWith('\\'))
                    expressions[^1] = expressions[^1] + "\n" + chunk;
                else
                    expressions.Add(chunk);
                continue;
            }

            // -f script file: ONE sed script; newlines separate commands and a trailing backslash
            // continues onto the next line (the multi-line `a\`/`i\`/`c\` text form).
            string? resolved = ResolveExistingPath(value);
            string? raw = null;
            if (resolved != null)
            {
                try { raw = BashFileSystem.ReadAllText(resolved); }
                catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex)) { raw = null; }
            }
            if (raw == null)
            {
                EmitError($"sed: couldn't open file {value}: No such file or directory");
                FileSystemHelpers.SetLastExitCode(this, 4);   // GNU: panic, exit 4
                return;
            }
            raw = raw.TrimEnd('\n');
            if (expressions.Count == 0 && TryStripHashN(ref raw))
                suppressDefault = true;
            if (raw.Length == 0 && expressions.Count == 0) continue;
            expressions.Add(raw);
        }

        // First operand is the script when no -e / -f was given.
        if (plan.Sources.Count == 0 && operands.Count > 0)
        {
            expressions.Add(operands[0]);
            operands.RemoveAt(0);
        }

        if (expressions.Count == 0)
        {
            EmitError("Usage: sed [OPTION]... {script-only-if-no-other-script} [input-file]...");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        var commands = new List<SedCommand>();
        foreach (var expr in expressions)
        {
            foreach (var part in SplitSedCommands(expr))
            {
                var parsed = ParseExpression(part, plan.Extended);
                if (parsed == null)
                {
                    // ParseExpression already emitted a bash-style error + exit code.
                    return;
                }
                commands.Add(parsed);
            }
        }

        if (plan.InPlace && operands.Count == 0)
        {
            EmitError("sed: no input files");
            FileSystemHelpers.SetLastExitCode(this, 4);
            return;
        }

        // No operand, or a lone `-` (the pipeline itself): stream records through the engine as they arrive.
        if (operands.Count == 0 || (operands.Count == 1 && operands[0] == "-" && !plan.InPlace))
        {
            StartPipeline(plan, commands, suppressDefault);
            return;
        }

        // File operands run in EndProcessing; pipeline input is ignored unless a `-` operand names it.
        _bufferStdin = operands.Contains("-");
        _fileRun = () => RunFiles(plan, operands, commands, suppressDefault);
    }

    /// <summary>
    /// GNU: when the first two characters of the first script are <c>#n</c> followed by a newline (or the
    /// script ends there), the script acts as if <c>-n</c> was given. Removes that line; true when it was there.
    /// </summary>
    internal static bool TryStripHashN(ref string script)
    {
        if (script == "#n") { script = ""; return true; }
        if (!script.StartsWith("#n\n", StringComparison.Ordinal)) return false;
        script = script.Substring(3);
        return true;
    }

    /// <summary>
    /// One-record delay between the engine and the writer: a record is released as terminated when the next one
    /// arrives, and the LAST gets the input's own terminator state (<see cref="Complete"/>) — the contract
    /// "every output record is newline-terminated except the final one when the input had no final newline".
    /// </summary>
    private sealed class RecordSink
    {
        private readonly Action<string, bool> _write;
        private string? _pending;
        public RecordSink(Action<string, bool> write) { _write = write; }
        public void Add(string record)
        {
            if (_pending != null) _write(_pending, true);
            _pending = record;
        }
        public void Complete(bool trailingTerminator)
        {
            if (_pending != null) _write(_pending, trailingTerminator);
            _pending = null;
        }
    }

    /// <summary>Write one output record to stdout; a final unterminated record carries exact bytes.</summary>
    private void WriteStdoutRecord(string record, bool terminated)
    {
        if (_term == '\n')
        {
            WriteObject(terminated
                ? BashRuntime.NewBashObject(record + "\n")
                : BashRuntime.NewBashObject(record, "PsBash.TextOutput", noTrailingNewline: true));
        }
        else
        {
            // -z: the record terminator is NUL; a NoTrailingNewline record carries exact bytes.
            WriteObject(BashRuntime.NewBashObject(
                terminated ? record + _term : record, "PsBash.TextOutput", noTrailingNewline: true));
        }
    }

    // ---- pipeline mode: records stream through the engine as they arrive -------------------------------

    private void StartPipeline(SedArgs plan, List<SedCommand> commands, bool suppressDefault)
    {
        _pipelineMode = true;
        _term = plan.NullData ? '\0' : '\n';
        _sink = new RecordSink(WriteStdoutRecord);
        _engine = new SedEngine(commands, suppressDefault, _sink.Add);
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;
        if (_bufferStdin)
        {
            // A `-` operand among file operands (rare): the pipeline is one unit among several, so it is
            // buffered until its turn. A lone `-` / no operand streams below.
            (_stdinBuffer ??= new List<PSObject>()).Add(InputObject);
            return;
        }
        if (!_pipelineMode) return;
        if (_pDone)
        {
            if (!PsBashProxy) UpstreamStop.Throw(this);
            return;
        }

        _sawRecord = true;
        _lastUnterminated = InputObject.Properties["NoTrailingNewline"]?.Value is true
            && !BashRuntime.GetBashText(InputObject).EndsWith("\n");

        bool quitMidRecord = false;
        if (_term == '\n')
        {
            string trimmed = BashRuntime.GetBashText(InputObject).TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                var parts = trimmed.Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    _engine!.Feed(parts[i]);
                    if (_engine.Done) { quitMidRecord = i < parts.Length - 1; break; }
                }
            }
            else
            {
                _engine!.Feed(trimmed);
            }
        }
        else
        {
            FeedNulText(BashRuntime.RecordStreamText(new object[] { InputObject }));
        }

        if (_engine!.Done)
        {
            _pDone = true;
            _quitMidRecord = quitMidRecord;
            FinishPipeline();
            if (!PsBashProxy) UpstreamStop.Throw(this);
        }
    }

    /// <summary>-z: the byte stream is cut into NUL-terminated records; only the partial tail is retained.</summary>
    private void FeedNulText(string chunk)
    {
        if (chunk.IndexOf('\0') < 0) { _nulTail.Append(chunk); return; }
        string all = _nulTail.Length == 0 ? chunk : _nulTail.Append(chunk).ToString();
        _nulTail.Clear();
        int start = 0;
        while (true)
        {
            int z = all.IndexOf('\0', start);
            if (z < 0) break;
            _engine!.Feed(all.Substring(start, z - start));
            start = z + 1;
            if (_engine.Done) return;
        }
        _nulTail.Append(all, start, all.Length - start);
    }

    protected override void EndProcessing()
    {
        if (_pipelineMode) FinishPipeline();
        else _fileRun?.Invoke();
    }

    private void FinishPipeline()
    {
        if (_pFinished) return;
        _pFinished = true;
        if (!_sawRecord) return;

        bool trailing;
        if (_term == '\0')
        {
            if (!_engine!.Done && _nulTail.Length > 0)
            {
                _engine.Feed(_nulTail.ToString());
                _nulTail.Clear();
                trailing = false;
            }
            else
            {
                trailing = true;
            }
        }
        else
        {
            // A quit in the middle of a record leaves lines unread, so the last one written had a newline.
            trailing = _quitMidRecord || !_lastUnterminated;
        }
        _engine!.Finish();
        _sink!.Complete(trailing);
    }

    // ---- file operands ----------------------------------------------------------------------------------

    /// <summary>The records of a text: split on <paramref name="term"/>, the terminator after the last record
    /// removed (and remembered), an empty text having no records at all. Used for the buffered `-` operand.</summary>
    private static List<string> SplitRecords(string text, char term, out bool trailingTerminator)
    {
        trailingTerminator = true;
        if (text.Length == 0) return new List<string>();
        trailingTerminator = text[^1] == term;
        if (trailingTerminator) text = text.Substring(0, text.Length - 1);
        return new List<string>(text.Split(term));
    }

    /// <summary>
    /// Lazily stream a file's records, split on <paramref name="term"/> (CRLF normalized like the whole-document
    /// reader, BOM-aware). Opens the file eagerly so an unreadable operand throws at the call, not mid-stream.
    /// </summary>
    private static IEnumerable<BashFileSystem.TextLine> OpenRecords(string path, char term)
    {
        if (term == '\n') return BashFileSystem.ReadTextLines(path);
        return NulRecords(BashFileSystem.OpenRead(path));
    }

    private static IEnumerable<BashFileSystem.TextLine> NulRecords(FileStream fs)
    {
        using (fs)
        using (var reader = BashFileSystem.OpenDocumentReader(fs))
        {
            var sb = new StringBuilder(256);
            var buf = new char[16384];
            bool pendingCr = false;
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = buf[i];
                    if (pendingCr)
                    {
                        pendingCr = false;
                        if (c != '\n') sb.Append('\r'); // CRLF -> LF, as the whole-document read does
                    }
                    if (c == '\r') { pendingCr = true; continue; }
                    if (c == '\0')
                    {
                        yield return new BashFileSystem.TextLine(sb.ToString(), HasTrailingNewline: true);
                        sb.Clear();
                        continue;
                    }
                    sb.Append(c);
                }
            }
            if (pendingCr) sb.Append('\r');
            if (sb.Length > 0) yield return new BashFileSystem.TextLine(sb.ToString(), HasTrailingNewline: false);
        }
    }

    private static IEnumerable<BashFileSystem.TextLine> BufferedRecords(string text, char term)
    {
        var records = SplitRecords(text, term, out bool trailing);
        for (int i = 0; i < records.Count; i++)
            yield return new BashFileSystem.TextLine(records[i], i < records.Count - 1 || trailing);
    }

    /// <summary>
    /// File operands. GNU treats the files as ONE continuous stream (line numbers and <c>$</c> span
    /// them; a missing final newline is supplied between files) unless <c>-s</c> / <c>-i</c> makes each
    /// file its own. A <c>-</c> operand is the pipeline input. An unreadable operand is reported and
    /// skipped and the status becomes 2 once everything else ran. Files are read through a streaming reader
    /// (no whole-file read, so no size cap), and <c>-i</c> streams into a temp file that replaces the original.
    /// </summary>
    private void RunFiles(SedArgs plan, List<string> operands, List<SedCommand> commands, bool suppressDefault)
    {
        _term = plan.NullData ? '\0' : '\n';
        char term = _term;
        bool separate = plan.Separate || plan.InPlace;
        bool readError = false;

        // One continuous stream: a single engine/sink for all units.
        RecordSink? oneSink = null;
        SedEngine? oneEngine = null;
        bool oneTrailing = true;
        bool any = false;
        bool quitAll = false;

        // Feed one unit's records; returns the unit's trailing-terminator state.
        bool Pump(IEnumerable<BashFileSystem.TextLine> lines, SedEngine engine, string? path)
        {
            bool trailing = true;
            try
            {
                using var en = lines.GetEnumerator();
                while (en.MoveNext())
                {
                    trailing = en.Current.HasTrailingNewline;
                    engine.Feed(en.Current.Text);
                    if (engine.Done)
                    {
                        // Quit: input remains unless that was the unit's last record.
                        trailing = en.MoveNext() || trailing;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                EmitError($"sed: can't read {(path ?? "-").Replace('\\', '/')}: {FileSystemHelpers.ReadErrorMessage(ex)}");
                readError = true;
            }
            return trailing;
        }

        foreach (var operand in operands)
        {
            if (quitAll) break;

            var sources = new List<(string? Path, Func<IEnumerable<BashFileSystem.TextLine>> Open)>();
            if (operand == "-")
            {
                if (plan.InPlace)
                {
                    EmitError("sed: couldn't edit -: not a regular file");
                    FileSystemHelpers.SetLastExitCode(this, 4);
                    return;
                }
                string text = BashRuntime.RecordStreamText((_stdinBuffer ?? new List<PSObject>()).Cast<object>());
                sources.Add((null, () => BufferedRecords(text, term)));
            }
            else
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, operand))
                {
                    string p = filePath;
                    sources.Add((p, () => OpenRecords(p, term)));
                }
            }

            foreach (var (path, open) in sources)
            {
                if (quitAll) break;
                IEnumerable<BashFileSystem.TextLine> lines;
                try
                {
                    if (plan.InPlace && plan.Suffix != null && !BackUp(path!, plan.Suffix)) { readError = true; continue; }
                    lines = open();
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    // GNU: sed: can't read x: No such file or directory
                    EmitError($"sed: can't read {(path ?? "-").Replace('\\', '/')}: {FileSystemHelpers.ReadErrorMessage(ex)}");
                    readError = true;
                    continue;
                }

                any = true;
                if (!separate)
                {
                    oneSink ??= new RecordSink(WriteStdoutRecord);
                    oneEngine ??= new SedEngine(commands, suppressDefault, oneSink.Add);
                    oneTrailing = Pump(lines, oneEngine, path);
                    if (oneEngine.Done) quitAll = true;
                    continue;
                }

                if (plan.InPlace && path != null)
                {
                    if (!EditInPlace(path, lines, term, commands, suppressDefault, ref readError)) return;
                    continue;
                }

                var sink = new RecordSink(WriteStdoutRecord);
                var engine = new SedEngine(commands, suppressDefault, sink.Add);
                bool trailing = Pump(lines, engine, path);
                engine.Finish();
                sink.Complete(trailing);
            }
        }

        if (!separate && any && oneEngine != null)
        {
            oneEngine.Finish();
            oneSink!.Complete(oneTrailing);
        }

        if (readError) FileSystemHelpers.SetLastExitCode(this, 2);
    }

    /// <summary>
    /// <c>-i</c>: stream the edited records into a temp file beside the original and replace it (GNU's own
    /// strategy), keeping the original's mode and following a symlink to its target. False aborts the run
    /// (the write failed, reported GNU-style); a backup failure is the caller's, before this runs.
    /// </summary>
    private bool EditInPlace(string path, IEnumerable<BashFileSystem.TextLine> lines, char term,
        List<SedCommand> commands, bool suppressDefault, ref bool readError)
    {
        string target = path;
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: true) is { } link) target = link.FullName;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex)) { target = path; }

        string dir = Path.GetDirectoryName(target) is { Length: > 0 } d ? d : ".";
        string tmp = Path.Combine(dir, "sed" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            bool trailing = true;
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new StreamWriter(fs, RawBytes.Encoding, 65536))
            {
                var sink = new RecordSink((record, terminated) =>
                {
                    writer.Write(record);
                    if (terminated) writer.Write(term);
                });
                var engine = new SedEngine(commands, suppressDefault, sink.Add);
                try
                {
                    using var en = lines.GetEnumerator();
                    while (en.MoveNext())
                    {
                        trailing = en.Current.HasTrailingNewline;
                        engine.Feed(en.Current.Text);
                        if (engine.Done) { trailing = en.MoveNext() || trailing; break; }
                    }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    EmitError($"sed: can't read {path.Replace('\\', '/')}: {FileSystemHelpers.ReadErrorMessage(ex)}");
                    readError = true;
                    writer.Dispose();
                    File.Delete(tmp);
                    return true;
                }
                engine.Finish();
                sink.Complete(trailing);
            }

            CopyFileMode(target, tmp);
            File.Move(tmp, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) { TryDelete(tmp); throw; }
            TryDelete(tmp);
            EmitError($"sed: {target.Replace('\\', '/')}: {ex.Message}");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>The replacement keeps the original's permissions (Unix mode bits / Windows attributes).</summary>
    private static void CopyFileMode(string from, string to)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(to, File.GetAttributes(from) & ~FileAttributes.ReadOnly);
            else
                File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// GNU <c>-i SUFFIX</c> backup: the copy is <c>NAME+SUFFIX</c>, or, when SUFFIX contains
    /// <c>*</c>, SUFFIX with every <c>*</c> replaced by the file's base name; a result containing
    /// a directory part is relative to the edited file's directory.
    /// </summary>
    private bool BackUp(string path, string suffix)
    {
        try
        {
            string baseName = Path.GetFileName(path);
            string name = suffix.Contains('*') ? suffix.Replace("*", baseName) : baseName + suffix;
            string dir = Path.GetDirectoryName(path) ?? "";
            string target = Path.IsPathRooted(name) ? name : Path.Combine(dir, name);
            File.Copy(path, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            EmitError($"sed: cannot rename {path.Replace('\\', '/')}: {ex.Message}");
            return false;
        }
    }

    // ── sed command model ────────────────────────────────────────────────────

    internal enum AddressType
    {
        None, Regex, Line, RangeNum, RangeRegex,
        Step,            // first~step
        RangeNumToRegex, // N,/re/  (incl. the 0,/re/ special case)
        Last,            // `$` — the last input line
    }

    internal sealed class SedAddress
    {
        public AddressType Type;
        public string? Pattern;        // Regex
        public int Line;               // Line
        public int Start;              // RangeNum / Step / RangeNumToRegex
        public int End;                // RangeNum
        public int Step;               // Step
        public string? StartPattern;   // RangeRegex
        public string? EndPattern;     // RangeRegex / RangeNumToRegex
    }

    internal sealed class SedCommand
    {
        public char Type;
        public SedAddress? Address;
        public bool Negate;            // addr!cmd
        public Regex? Regex;           // s
        public string? Replacement;    // s
        public bool Global;            // s
        public int Nth;                // s — Nth-occurrence flag (0 = unset)
        public bool PrintOnSub;        // s///p
        public int ExitCode;           // q / Q
        public string? Text;           // a / i / c
        public string? Source;         // y
        public string? Dest;           // y
    }

    /// <summary>
    /// Convert a GNU-sed replacement string into a .NET regex replacement
    /// string. Handles backrefs (\1-\9 → $1-$9), whole-match & → $0, the
    /// C-escapes \n \t \r, the literalizers \&amp; and \\, a dropped backslash
    /// before any other char, and escapes a literal $ to $$ so .NET does not
    /// read it as a group reference.
    /// </summary>
    /// <summary>
    /// Translate a POSIX Basic Regular Expression (sed's default) to a .NET regex.
    /// In BRE, <c>( ) { } | + ?</c> are LITERAL unless backslash-escaped, and the
    /// backslash-escaped forms <c>\( \) \{ \} \| \+ \?</c> are the metacharacters —
    /// the exact inverse of .NET. A single-char lookbehind (the old approach) cannot
    /// tell <c>\(</c> (escaped → group) from <c>\\(</c> (escaped backslash, then a
    /// literal paren), and it never converted <c>\(</c> to a group at all. This
    /// left-to-right walk tracks the escape state exactly.
    /// </summary>
    private static string TranslateBasicRegexToNet(string bre)
    {
        var sb = new StringBuilder(bre.Length + 8);
        for (int i = 0; i < bre.Length; i++)
        {
            char c = bre[i];
            if (c == '\\' && i + 1 < bre.Length)
            {
                char n = bre[i + 1];
                switch (n)
                {
                    // BRE escaped metachar → .NET metachar (drop the backslash).
                    case '(': case ')': case '{': case '}': case '|': case '+': case '?':
                        sb.Append(n);
                        break;
                    // Everything else (\\, \., \1 backref, \n, \w, \< …) passes through
                    // verbatim — including \\, which consumes both backslashes here so a
                    // following bare metachar is correctly treated as a literal.
                    default:
                        sb.Append('\\').Append(n);
                        break;
                }
                i++; // consumed the escaped char
                continue;
            }
            switch (c)
            {
                // BRE bare metachar → literal (escape for .NET).
                case '(': case ')': case '{': case '}': case '|': case '+': case '?':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string BuildReplacement(string sed)
    {
        var sb = new StringBuilder(sed.Length);
        for (int i = 0; i < sed.Length; i++)
        {
            char c = sed[i];
            if (c == '\\' && i + 1 < sed.Length)
            {
                char n = sed[i + 1];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    case '&': sb.Append('&'); break;          // literal &, not whole-match
                    case >= '0' and <= '9': sb.Append('$').Append(n); break; // backref
                    default: sb.Append(n); break;              // \x → x
                }
                i++;
                continue;
            }
            if (c == '&') { sb.Append("$0"); continue; }       // whole match
            if (c == '$') { sb.Append("$$"); continue; }       // literal $ for .NET
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Split one sed script expression into its individual commands. GNU sed
    /// accepts several commands in ONE expression separated by <c>;</c> or a
    /// newline (<c>sed 'N;s/\n/+/'</c>, <c>sed 'N;p'</c>). The old parser treated
    /// the whole expression as a single command and silently ignored everything
    /// after the first, so <c>N;s/\n/+/</c> never joined its lines.
    /// <para>
    /// The split is delimiter-aware: a <c>;</c> inside an <c>s###</c> /
    /// <c>y###</c> substitution (any delimiter), inside the text of an
    /// <c>a\</c>/<c>i\</c>/<c>c\</c> command, or inside an address regex
    /// (<c>/;/d</c>, <c>1,/;$/d</c>) is part of that command, not a separator.
    /// Text commands run to the end of the expression (a literal newline after
    /// the leading backslash is part of the text), so once one is seen the
    /// remainder is emitted whole.
    /// </para>
    /// </summary>
    internal static List<string> SplitSedCommands(string expression)
    {
        var parts = new List<string>();
        int start = 0;

        // Each iteration begins at a command position (`start`): skip the
        // address prefix (if any), then dispatch on the command character. The
        // address may itself contain `;` / newline inside a `/re/`, which
        // <see cref="SkipSedAddress"/> consumes before any separator check.
        while (start < expression.Length)
        {
            int cmdPos = SkipSedAddress(expression, start);

            // Optional negation / spaces between the address and the command.
            while (cmdPos < expression.Length
                   && (expression[cmdPos] == '!' || expression[cmdPos] == ' '))
            {
                cmdPos++;
            }

            if (cmdPos >= expression.Length)
            {
                parts.Add(expression.Substring(start));
                break;
            }

            char c = expression[cmdPos];

            if (c == 's' || c == 'y')
            {
                if (cmdPos + 1 < expression.Length)
                {
                    char delim = expression[cmdPos + 1];
                    int segEnd = ScanDelimited(expression, cmdPos + 2, delim, 2);
                    if (segEnd >= 0)
                    {
                        int sep = FindSeparator(expression, segEnd);
                        parts.Add(expression.Substring(start, sep - start));
                        start = sep < expression.Length ? sep + 1 : expression.Length;
                        continue;
                    }
                }
            }
            else if (c == 'a' || c == 'i' || c == 'c')
            {
                // Text command: everything to the end of the expression is its
                // text (a following `;` inside the text is literal, matching
                // GNU). Only the `\`/newline/space/end form is the text form; a
                // bare `c` is a complete command and `;` after it separates.
                if (cmdPos + 1 >= expression.Length || expression[cmdPos + 1] == '\\'
                    || expression[cmdPos + 1] == '\n' || expression[cmdPos + 1] == ' ')
                {
                    parts.Add(expression.Substring(start));
                    return TrimParts(parts);
                }
            }

            // A plain command: emit up to the next real separator.
            int next = FindSeparator(expression, cmdPos + 1);
            parts.Add(expression.Substring(start, next - start));
            start = next < expression.Length ? next + 1 : expression.Length;
        }

        return TrimParts(parts);
    }

    /// <summary>
    /// Returns the index of the next top-level <c>;</c> or newline at or after
    /// <paramref name="pos"/>, or the length of <paramref name="s"/> when none.
    /// Used once a command's own delimited / text body has been consumed, so the
    /// scan is a plain separator search.
    /// </summary>
    private static int FindSeparator(string s, int pos)
    {
        for (int i = pos; i < s.Length; i++)
        {
            if (s[i] == ';' || s[i] == '\n') return i;
        }
        return s.Length;
    }

    /// <summary>
    /// From a command position, returns the index just past an address prefix,
    /// or <paramref name="pos"/> when there is none. Recognizes the same grammar
    /// as <see cref="ParseExpressionCore"/>: <c>/re/</c> (optionally
    /// <c>,/re/</c>), <c>$</c>, an optional <c>,/re/</c> / <c>,$</c> / <c>,N</c>
    /// range tail. A <c>;</c> or newline inside the regex is consumed here, so
    /// the caller never mistakes it for a command separator.
    /// </summary>
    private static int SkipSedAddress(string s, int pos)
    {
        if (pos >= s.Length) return pos;

        if (s[pos] == '/')
        {
            pos = SkipRegex(s, pos);
            if (pos < 0) return 0; // unterminated — let the parser report it
            if (pos < s.Length && s[pos] == ','
                && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                int end = SkipRegex(s, pos + 1);
                if (end >= 0) return end;
            }
            return pos;
        }

        if (s[pos] == '$')
        {
            int p = pos + 1;
            if (p < s.Length && s[p] == ',' && p + 1 < s.Length && s[p + 1] == '/')
            {
                int end = SkipRegex(s, p + 1);
                if (end >= 0) return end;
            }
            return p;
        }

        if (char.IsDigit(s[pos]))
        {
            int p = pos;
            while (p < s.Length && char.IsDigit(s[p])) p++;
            if (p < s.Length && s[p] == '~')
            {
                p++;
                while (p < s.Length && char.IsDigit(s[p])) p++;
                return p;
            }
            if (p < s.Length && s[p] == ',')
            {
                p++;
                if (p < s.Length && s[p] == '/')
                {
                    int end = SkipRegex(s, p);
                    if (end >= 0) return end;
                    return p;
                }
                if (p < s.Length && s[p] == '$') return p + 1;
                while (p < s.Length && char.IsDigit(s[p])) p++;
                return p;
            }
            return p;
        }

        return pos;
    }

    /// <summary>
    /// <paramref name="pos"/> must point at an opening <c>/</c>. Returns the
    /// index just past the closing <c>/</c>, honoring a backslash escape, or -1
    /// when unterminated.
    /// </summary>
    private static int SkipRegex(string s, int pos)
    {
        for (int i = pos + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '/') return i + 1;
        }
        return -1;
    }

    /// <summary>
    /// From <paramref name="pos"/>, scan past <paramref name="fields"/> occurrences
    /// of <paramref name="delim"/>, honoring a backslash escape. Returns the index
    /// just past the final delimiter, or -1 when the delimiter run is unterminated.
    /// </summary>
    private static int ScanDelimited(string s, int pos, char delim, int fields)
    {
        int seen = 0;
        for (int i = pos; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') { i++; continue; }
            if (c == delim)
            {
                seen++;
                if (seen == fields) return i + 1;
            }
        }
        return -1;
    }

    private static List<string> TrimParts(List<string> parts)
    {
        var result = new List<string>(parts.Count);
        foreach (var p in parts)
        {
            var t = p;
            // A leading newline is a command separator artifact, not part of any
            // command; trailing whitespace around the separator is likewise noise.
            if (t.Length > 0) result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// Reproduces the psm1 <c>ConvertFrom-SedExpression</c>. Returns
    /// <c>null</c> after emitting a bash-style error and setting
    /// <c>$global:LASTEXITCODE</c> on a parse failure.
    /// </summary>
    private SedCommand? ParseExpression(string expression, bool extendedRegex)
    {
        var cmd = ParseExpressionCore(expression, extendedRegex, out string? err, out int code);
        if (cmd == null && err != null)
        {
            EmitError(err);
            SessionState.PSVariable.Set("global:LASTEXITCODE", code);
        }
        return cmd;
    }

    /// <summary>
    /// Try to build the full command list from expressions WITHOUT emitting any
    /// error (used by the fused-pipeline streaming stage — on any parse failure it
    /// returns false so the fused lane declines and the real cmdlet reports the
    /// error). Shares <see cref="ParseExpressionCore"/> with the cmdlet, so parse
    /// semantics are identical.
    /// </summary>
    internal static bool TryBuildCommands(
        IEnumerable<string> expressions, bool extendedRegex, out List<SedCommand> commands)
    {
        commands = new List<SedCommand>();
        foreach (var expr in expressions)
        {
            foreach (var part in SplitSedCommands(expr))
            {
                var c = ParseExpressionCore(part, extendedRegex, out _, out _);
                if (c == null) return false;
                commands.Add(c);
            }
        }
        return true;
    }

    /// <summary>
    /// Pure expression parser (no runspace / error emission). Returns the parsed
    /// command, or <c>null</c> with <paramref name="errMsg"/> / <paramref name="errCode"/>
    /// set on a parse failure. The instance <see cref="ParseExpression"/> wraps this
    /// and emits the bash-style error; the fused stage uses the return value only.
    /// </summary>
    private static SedCommand? ParseExpressionCore(
        string expression, bool extendedRegex, out string? errMsg, out int errCode)
    {
        string? err = null;
        int code = 0;
        SedCommand? Fail(string message, int exitCode)
        {
            err = message;
            code = exitCode;
            return null;
        }

        var result = Parse();
        errMsg = err;
        errCode = code;
        return result;

        SedCommand? Parse()
        {
        SedAddress? addr = null;
        int pos = 0;

        // Address prefix.
        if (expression.Length > 0 && expression[pos] == '/')
        {
            pos++;
            int endSlash = expression.IndexOf('/', pos);
            if (endSlash < 0)
            {
                return Fail("sed: unterminated address regex", 1);
            }
            addr = new SedAddress
            {
                Type = AddressType.Regex,
                Pattern = expression.Substring(pos, endSlash - pos),
            };
            pos = endSlash + 1;

            // Range: /start/,/end/
            if (pos < expression.Length && expression[pos] == ',')
            {
                pos++;
                if (pos < expression.Length && expression[pos] == '/')
                {
                    pos++;
                    int endSlash2 = expression.IndexOf('/', pos);
                    if (endSlash2 < 0)
                    {
                        return Fail("sed: unterminated address regex", 1);
                    }
                    addr = new SedAddress
                    {
                        Type = AddressType.RangeRegex,
                        StartPattern = addr.Pattern,
                        EndPattern = expression.Substring(pos, endSlash2 - pos),
                    };
                    pos = endSlash2 + 1;
                }
            }
        }
        else if (expression.Length > 0 && expression[pos] == '$')
        {
            // `$` is the last-input-line address (`$a\`, `$d`, `$,/re/`).
            addr = new SedAddress { Type = AddressType.Last };
            pos++;
        }
        else if (expression.Length > 0 && char.IsDigit(expression[pos]))
        {
            var numStr = new StringBuilder();
            while (pos < expression.Length && char.IsDigit(expression[pos]))
            {
                numStr.Append(expression[pos]);
                pos++;
            }
            // TryParse guards the int.Parse overflow trap on an absurd address.
            if (!int.TryParse(numStr.ToString(), out int startNum))
            {
                startNum = int.MaxValue;
            }

            if (pos < expression.Length && expression[pos] == '~')
            {
                // first~step — match line `first` and every `step`-th line after.
                pos++;
                var stepStr = new StringBuilder();
                while (pos < expression.Length && char.IsDigit(expression[pos]))
                {
                    stepStr.Append(expression[pos]);
                    pos++;
                }
                int step = int.TryParse(stepStr.ToString(), out int sv) ? sv : 0;
                addr = new SedAddress
                {
                    Type = AddressType.Step,
                    Start = startNum,
                    Step = step,
                };
            }
            else if (pos < expression.Length && expression[pos] == ',')
            {
                pos++;
                if (pos < expression.Length && expression[pos] == '$')
                {
                    addr = new SedAddress
                    {
                        Type = AddressType.RangeNum,
                        Start = startNum,
                        End = int.MaxValue,
                    };
                    pos++;
                }
                else if (pos < expression.Length && expression[pos] == '/')
                {
                    // N,/re/ — numeric start, regex end. With N==0 the end regex
                    // may match the very first line (GNU's 0,/re/ idiom).
                    pos++;
                    int endSlashR = expression.IndexOf('/', pos);
                    if (endSlashR < 0)
                    {
                        return Fail("sed: unterminated address regex", 1);
                    }
                    addr = new SedAddress
                    {
                        Type = AddressType.RangeNumToRegex,
                        Start = startNum,
                        EndPattern = expression.Substring(pos, endSlashR - pos),
                    };
                    pos = endSlashR + 1;
                }
                else
                {
                    var numStr2 = new StringBuilder();
                    while (pos < expression.Length && char.IsDigit(expression[pos]))
                    {
                        numStr2.Append(expression[pos]);
                        pos++;
                    }
                    if (!int.TryParse(numStr2.ToString(), out int endNum))
                    {
                        endNum = int.MaxValue;
                    }
                    addr = new SedAddress
                    {
                        Type = AddressType.RangeNum,
                        Start = startNum,
                        End = endNum,
                    };
                }
            }
            else
            {
                addr = new SedAddress { Type = AddressType.Line, Line = startNum };
            }
        }

        // Optional negation between the address and the command: `addr!cmd`
        // (e.g. `2!d`, `/re/!s///`). GNU also tolerates spaces and a repeated `!`.
        bool negate = false;
        while (pos < expression.Length && (expression[pos] == '!' || expression[pos] == ' '))
        {
            if (expression[pos] == '!') { negate = true; }
            pos++;
        }

        string remaining = expression.Substring(pos);
        if (remaining.Length == 0)
        {
            return Fail("sed: missing command", 1);
        }

        char cmdChar = remaining[0];
        switch (cmdChar)
        {
            case 's':
            {
                if (remaining.Length < 2)
                {
                    return Fail("sed: bad substitution", 1);
                }
                char delim = remaining[1];
                var parts = new List<string>();
                var current = new StringBuilder();
                bool escaped = false;
                for (int ci = 2; ci < remaining.Length; ci++)
                {
                    char c = remaining[ci];
                    if (escaped)
                    {
                        if (c != delim)
                        {
                            current.Append('\\');
                        }
                        current.Append(c);
                        escaped = false;
                        continue;
                    }
                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (c == delim)
                    {
                        parts.Add(current.ToString());
                        current = new StringBuilder();
                        continue;
                    }
                    current.Append(c);
                }
                parts.Add(current.ToString());

                if (parts.Count < 2)
                {
                    return Fail("sed: bad substitution", 1);
                }

                string searchPattern = parts[0];
                string replacement = parts[1];
                string flags = parts.Count > 2 ? parts[2] : string.Empty;

                // Parse the substitution flags. GNU allows g, i/I, p, and a
                // numeric occurrence N (and the combination Ng = "Nth and
                // onward"). Accumulate digit runs so `s/x/y/10` parses as 10,
                // not three separate flags. TryParse guards the int.Parse
                // overflow trap on an absurd count (`s/x/y/9999999999`).
                bool global = false;
                int nth = 0;
                bool printOnSub = false;
                var numBuf = new StringBuilder();
                foreach (char f in flags)
                {
                    if (char.IsDigit(f)) { numBuf.Append(f); }
                    else if (f == 'g') { global = true; }
                    else if (f == 'p') { printOnSub = true; }
                }
                if (numBuf.Length > 0 && !int.TryParse(numBuf.ToString(), out nth)) { nth = 0; }

                // An empty regex (`s//repl/`) means "reuse the last regex" in sed;
                // with none to reuse it is an error. ps-bash does not track a previous
                // regex, so an empty pattern would compile to .NET's match-empty-
                // everywhere regex and inject the replacement at every position. Reject
                // it like GNU (exit 1) instead of silently mangling the input.
                if (searchPattern.Length == 0)
                {
                    return Fail(
                        "sed: -e expression #1, char 0: no previous regular expression", 1);
                }

                // Translate a GNU-sed replacement into a .NET replacement string:
                // backrefs \1-\9 → $1-$9, whole-match & → $0, C-escapes \n \t \r
                // → real control chars, \& and \\ → literal & and \, and a literal
                // $ → $$ (else .NET would read it as a group reference).
                replacement = BuildReplacement(replacement);

                var regexOpts = RegexOptions.None;
                if (flags.Contains('I') || flags.Contains('i'))
                {
                    regexOpts |= RegexOptions.IgnoreCase;
                }

                // POSIX classes BEFORE the BRE translation: the rewrite introduces
                // regex metacharacters (\s, \w) that the BRE pass must not re-escape.
                // .NET has no POSIX classes, so `s/[[:space:]]\+/_/g` silently matched
                // nothing (and reported no error) before this.
                searchPattern = BashRuntime.TranslatePosixClasses(searchPattern);

                if (!extendedRegex)
                {
                    searchPattern = TranslateBasicRegexToNet(searchPattern);
                }

                Regex regex;
                try
                {
                    regex = new Regex(searchPattern, regexOpts);
                }
                catch (ArgumentException ex)
                {
                    return Fail($"sed: {ex.Message}", 1);
                }

                return new SedCommand
                {
                    Type = 's',
                    Address = addr,
                    Negate = negate,
                    Regex = regex,
                    Replacement = replacement,
                    Global = global,
                    Nth = nth,
                    PrintOnSub = printOnSub,
                };
            }
            case 'd':
            case 'D':
            case 'p':
            case 'P':
            case 'N':
            case '=':
                return new SedCommand { Type = cmdChar, Address = addr, Negate = negate };
            case 'q':
            case 'Q':
            {
                // Q quits like q but WITHOUT auto-printing the pattern space.
                // Both accept an optional exit code.
                int exitCode = 0;
                if (remaining.Length > 1)
                {
                    string qArg = remaining.Substring(1).Trim();
                    if (qArg.Length > 0 && Regex.IsMatch(qArg, @"^\d+$")
                        && int.TryParse(qArg, out int parsed))
                    {
                        exitCode = parsed;
                    }
                }
                return new SedCommand
                {
                    Type = cmdChar, Address = addr, Negate = negate, ExitCode = exitCode,
                };
            }
            case 'a':
            case 'i':
            case 'c':
            {
                string text = remaining.Length > 1 ? remaining.Substring(1) : string.Empty;
                text = text.TrimStart('\\').TrimStart();
                return new SedCommand { Type = cmdChar, Address = addr, Negate = negate, Text = text };
            }
            case 'y':
            {
                if (remaining.Length < 2)
                {
                    return Fail("sed: bad transliteration", 1);
                }
                char delim = remaining[1];
                var parts = remaining.Substring(2).Split(delim);
                if (parts.Length < 2)
                {
                    return Fail("sed: bad transliteration", 1);
                }
                if (parts[0].Length != parts[1].Length)
                {
                    return Fail(
                        "sed: y: source and dest must be the same length", 1);
                }
                return new SedCommand
                {
                    Type = 'y',
                    Address = addr,
                    Negate = negate,
                    Source = parts[0],
                    Dest = parts[1],
                };
            }
            default:
                // A real GNU command ps-bash does not run is ps-bash's own refusal (exit 2); anything
                // else is a script error, which GNU reports with exit 1.
                if ("{}=abcdDeFgGhHilnNpPqQrRstTvwWxyz:#".Contains(cmdChar))
                    return Fail($"sed: unsupported command '{cmdChar}'", 2);
                return Fail($"sed: -e expression #1, char {pos + 1}: unknown command: `{cmdChar}'", 1);
        }
        }
    }

    private void EmitError(string message)
    {
        FileSystemHelpers.WriteBashError(this, message);
    }

    private string? ResolveExistingPath(string path)
    {
        try
        {
            string resolved = SessionState.Path
                .GetUnresolvedProviderPathFromPSPath(path);
            return File.Exists(resolved) ? resolved : null;
        }
        catch
        {
            return null;
        }
    }
}
