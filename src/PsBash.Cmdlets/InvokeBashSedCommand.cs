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
public sealed class InvokeBashSedCommand : BashCmdlet
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
    private bool _pipelineMode, _pDone, _pFinished, _sawRecord, _bufferStdin;
    private char _term = '\n';
    private SedEngine? _engine;
    private RecordSink? _sink;
    private Action? _fileRun;
    private readonly StringBuilder _nulTail = new();
    private List<PSObject>? _stdinBuffer;
    private SedRuntime? _rt;
    private RecordSink? _debugSink;
    private readonly Dictionary<string, StreamWriter> _outFiles = new();
    private readonly Dictionary<string, IEnumerator<BashFileSystem.TextLine>?> _readFiles = new();

    /// <summary>
    /// Hooks the new commands call: <c>w</c> files are created/truncated NOW (GNU opens every named file at
    /// program start, even one that is never written), <c>R</c> files are opened lazily and stay open, <c>r</c>
    /// reads the whole file each time, <c>e</c> runs through ps-bash itself.
    /// </summary>
    private void WireRuntime(SedRuntime rt, List<SedCommand> commands)
    {
        foreach (var c in commands)
        {
            if (c.FileName != null && c.Type is 'w' or 'W' or 's')
                OpenOutFile(c.FileName);
        }

        rt.ReadFile = name =>
        {
            try
            {
                string path = FileSystemHelpers.ProviderPath(this, name);
                if (!File.Exists(path)) return null;
                return BashFileSystem.ReadAllText(path);
            }
            catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex)) { return null; }
        };
        rt.ReadLine = name =>
        {
            if (!_readFiles.TryGetValue(name, out var en))
            {
                try
                {
                    string path = FileSystemHelpers.ProviderPath(this, name);
                    en = File.Exists(path) ? BashFileSystem.ReadTextLines(path).GetEnumerator() : null;
                }
                catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex)) { en = null; }
                _readFiles[name] = en;
            }
            return en != null && en.MoveNext() ? en.Current : null;
        };
        rt.WriteFile = (name, line, end) =>
        {
            if (DevicePath.Classify(name) == DeviceKind.Stderr) { FileSystemHelpers.WriteStderr(this, line); return; }
            if (!_outFiles.TryGetValue(name, out var w)) return;
            // Each w file has its own missing newline (SedEnd): owed by an unterminated last line, supplied by the next write.
            if (_owedNewline.Remove(name)) w.Write('\n');
            w.Write(line);
            if (end == SedEnd.Newline) w.Write('\n');
            else if (end == SedEnd.Owed) _owedNewline.Add(name);
        };
        rt.RunCommand = RunShell;
        rt.Error = EmitError;
    }

    private readonly HashSet<string> _owedNewline = new();

    private void OpenOutFile(string name)
    {
        if (DevicePath.Classify(name) is DeviceKind.Stdout or DeviceKind.Stderr or DeviceKind.Null || _outFiles.ContainsKey(name)) return;
        try
        {
            string path = FileSystemHelpers.ProviderPath(this, name);
            _outFiles[name] = RedirectFileWriter.OpenText(path, append: false, this);
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            EmitError($"sed: couldn't open file {name}: {FileSystemHelpers.ReadErrorMessage(ex)}");
            FileSystemHelpers.SetLastExitCode(this, 4);
        }
    }

    private void CloseFiles()
    {
        foreach (var w in _outFiles.Values)
            try { w.Dispose(); } catch { /* best effort */ }
        _outFiles.Clear();
        foreach (var e in _readFiles.Values) e?.Dispose();
        _readFiles.Clear();
    }

    // Ctrl+C also ends a running `e` command, which has no time limit.
    private readonly CancellationTokenSource _stop = new();

    protected override void StopProcessing()
    {
        _stop.Cancel();
        CloseFiles();
    }

    /// <summary>
    /// <c>e</c>: the command line runs through ps-bash itself (<c>ps-bash -c</c>, the same resolution awk's
    /// <c>system()</c>/<c>getline</c> use; <c>cmd /c</c> / <c>/bin/sh -c</c> only when no ps-bash executable is
    /// found), stdin closed, stdout returned. Bounded by <see cref="BashRuntime.RunChildProcess"/>.
    /// </summary>
    private string RunShell(string command)
    {
        // Everything sed itself wrote to a w file must be on disk for the command to see it.
        foreach (var w in _outFiles.Values) RedirectFileWriter.FlushText(w);

        var psi = new System.Diagnostics.ProcessStartInfo();
        string? exe = InvokeBashBashCommand.ResolvePsBashExecutable(this);
        if (!string.IsNullOrEmpty(exe))
        {
            psi.FileName = exe;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        else if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /s /c \"" + command + "\"";
        }
        else
        {
            psi.FileName = PsBash.Core.Platform.SystemTools.PosixShell;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        try
        {
            var loc = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
            if (!string.IsNullOrEmpty(loc)) psi.WorkingDirectory = loc;
        }
        catch { /* inherit */ }

        var res = BashRuntime.RunChildProcess(psi, BashRuntime.NoTimeLimit, null, _stop.Token);
        if (!string.IsNullOrEmpty(res.Stderr)) FileSystemHelpers.WriteStderr(this, res.Stderr.TrimEnd('\n', '\r'));
        return res.Stdout.Replace("\r\n", "\n");
    }

    internal const string OptQuiet = "quiet", OptExpr = "expression", OptFile = "file",
        OptExtended = "extended", OptInPlace = "in-place", OptSeparate = "separate",
        OptNullData = "null-data", OptLineLen = "line-length", OptNoop = "noop", OptDebug = "debug", OptFollowSymlinks = "follow-symlinks";

    /// <summary>GNU sed options ps-bash refuses (exit 2): none left — <c>--debug</c> is implemented.</summary>
    private static readonly string[] SedValidButUnsupported = Array.Empty<string>();

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
            new OptSpec(OptFollowSymlinks, '\0', "follow-symlinks"),
            new OptSpec(OptDebug, '\0', "debug"),
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
        public bool Quiet, Extended, Separate, NullData, InPlace, Debug, FollowSymlinks;

        /// <summary>The <c>-l N</c> wrap width of the <c>l</c> command (GNU default 70, 0 = never wrap).</summary>
        public int LineLength = 70;

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
                case OptDebug: s.Debug = true; break;
                case OptFollowSymlinks: s.FollowSymlinks = true; break;
                case OptLineLen:
                    if (int.TryParse(tok.Value, out int ll) && ll >= 0) s.LineLength = ll;
                    break;
                // OptNoop: accepted, no effect (nothing buffers).
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
        // Each script keeps where it came from (`-e` number, `-f` file and line), for GNU's error positions.
        var scripts = new List<SedScript>();
        bool anyScript = false;   // a script was read (an emptied `#n` one counts: it is no longer "first")
        int exprNo = 0;

        foreach (var (isFile, value) in plan.Sources)
        {
            if (!isFile)
            {
                exprNo++;
                string chunk = value;
                if (!anyScript && TryStripHashN(ref chunk))
                    suppressDefault = true;   // `#n` on the first line of the first script acts like -n
                anyScript = true;
                // GNU joins -e chunks with a newline, so `-e 'a\' -e text` is one multi-line command.
                if (scripts.Count > 0 && scripts[^1].File == null && scripts[^1].Text.EndsWith('\\'))
                    scripts[^1] = scripts[^1] with { Text = scripts[^1].Text + "\n" + chunk };
                else if (chunk.Length > 0 || scripts.Count > 0)
                    scripts.Add(new SedScript(chunk, ExprNo: exprNo));
                continue;
            }

            // -f script file: ONE sed script; newlines separate commands and a trailing backslash
            // continues onto the next line (the multi-line `a\`/`i\`/`c\` text form). The text is kept whole (its
            // newlines count GNU's `line N`).
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
            int fileFirstLine = 1;
            if (!anyScript && TryStripHashN(ref raw))
            {
                suppressDefault = true;
                fileFirstLine = 2;
            }
            anyScript = true;
            scripts.Add(new SedScript(raw, File: value, FirstLine: fileFirstLine));
        }

        // First operand is the script when no -e / -f was given.
        if (plan.Sources.Count == 0 && operands.Count > 0)
        {
            scripts.Add(new SedScript(operands[0]));
            operands.RemoveAt(0);
        }

        if (scripts.Count == 0 && !anyScript)
        {
            EmitError("Usage: sed [OPTION]... {script-only-if-no-other-script} [input-file]...");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        if (!BuildProgram(scripts, plan.Extended, out var commands, out string? buildError, out int buildCode))
        {
            EmitError(buildError!);
            FileSystemHelpers.SetLastExitCode(this, buildCode);
            return;
        }

        if (plan.InPlace && operands.Count == 0)
        {
            EmitError("sed: no input files");
            FileSystemHelpers.SetLastExitCode(this, 4);
            return;
        }

        // The external world of the new commands (hold space, r/R/w/W/e, --debug). A script of the original
        // command set needs none of it, and then no runtime exists (the engine keeps its historical output order).
        if (plan.Debug || SedEngine.UsesRuntimeFeatures(commands))
        {
            _rt = new SedRuntime { LineLength = plan.LineLength };
            WireRuntime(_rt, commands);
            if (plan.Debug)
            {
                // The program listing precedes every record, so it can be written directly. The per-cycle
                // annotations go through the record sink (they interleave with the data it delays by one
                // record) — except under -i, where the data goes to the edited file and the debug text to stdout.
                void Direct(string line) => WriteObject(BashRuntime.NewBashObject(line + "\n"));
                _rt.Debug = Direct;
                SedDebugFormat.PrintProgram(commands, _rt);
                _rt.Debug = line => { if (_debugSink != null) _debugSink.Add(line); else Direct(line); };
            }
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
    /// One-record delay between the engine and the writer, applying GNU's missing-newline rule (<see cref="SedEnd"/>):
    /// a record that OWES its newline gets it when another record follows (<c>output_missing_newline</c>) and not at
    /// the end; a <see cref="SedEnd.Bare"/> one never gets one; every other record ends in its newline even when it
    /// is the last (<c>printf 'a\nb' | sed -n '$='</c> = <c>2\n</c>).
    /// </summary>
    private sealed class RecordSink
    {
        private readonly Action<string, bool> _write;
        private SedOut? _pending;
        public RecordSink(Action<string, bool> write) { _write = write; }
        public void Add(string text, SedEnd end)
        {
            if (_pending is { } p && !IsNothing(p)) _write(p.Text, p.End != SedEnd.Bare);
            _pending = new SedOut(text, end);
        }
        /// <summary>A <c>--debug</c> annotation line: always newline-terminated.</summary>
        public void Add(string line) => Add(line, SedEnd.Newline);
        public void Complete()
        {
            if (_pending is { } p && !IsNothing(p)) _write(p.Text, p.End == SedEnd.Newline);
            _pending = null;
        }
        // An empty Bare write (q supplying an owed newline) only settles what is owed; it is no record of its own.
        private static bool IsNothing(SedOut o) => o.End == SedEnd.Bare && o.Text.Length == 0;
    }

    /// <summary>Write one output record to stdout; a final unterminated record carries exact bytes.</summary>
    private void WriteStdoutRecord(string record, bool terminated)
    {
        if (_term == '\n')
        {
            WriteObject(BashRuntime.BytesRecord(terminated ? record + "\n" : record));
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
        _lines.Terminator = _term;
        _sink = new RecordSink(WriteStdoutRecord);
        _debugSink = _sink;
        _engine = new SedEngine(commands, suppressDefault, _sink.Add, _rt);
    }

    // Records become LINES first: a piece without its newline (`printf a`) waits for the rest (LineAssembler).
    private readonly LineAssembler _lines = new();

    protected override void ProcessRecord()
    {
        if (_lines.Feed(InputObject) is { } record) { InputObject = record; ProcessInputRecord(); }
    }

    private void ProcessInputRecord()
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

        if (_term == '\n')
        {
            var parts = BashRuntime.SplitRecord(InputObject, out bool unterminated);
            if (parts.Length == 0) return; // `printf ''`: no bytes, so no line
            _sawRecord = true;
            for (int i = 0; i < parts.Length; i++)
            {
                // Only the unterminated tail of the input is a line without its newline.
                _engine!.Feed(parts[i], terminated: !(unterminated && i == parts.Length - 1));
                if (_engine.Done) break;
            }
        }
        else
        {
            _sawRecord = true;
            FeedNulText(BashRuntime.RecordBytes(InputObject));
        }

        if (_engine!.Done)
        {
            _pDone = true;
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
        if (_lines.Flush() is { } last) { InputObject = last; ProcessInputRecord(); }
        if (_pipelineMode) FinishPipeline();
        else _fileRun?.Invoke();
        CloseFiles();
    }

    private void FinishPipeline()
    {
        if (_pFinished) return;
        _pFinished = true;
        FinishPipelineCore();
        // The pipeline's status is sed's own. The 0 set in BeginProcessing ran before any upstream stage did, so a
        // stage that failed afterwards (`printf %d x 2>&1 | sed …`) would otherwise leak its status through.
        FileSystemHelpers.SetLastExitCode(this, _rt?.ExitCode ?? 0);
    }

    private void FinishPipelineCore()
    {
        if (!_sawRecord)
        {
            // No input at all: the cycle machinery never ran, but a script can still have files to flush.
            _engine?.Finish();
            _sink?.Complete();
            return;
        }

        // -z: a tail after the last NUL is a record without its terminator.
        if (_term == '\0' && !_engine!.Done && _nulTail.Length > 0)
        {
            _engine.Feed(_nulTail.ToString(), terminated: false);
            _nulTail.Clear();
        }
        _engine!.Finish();
        _sink!.Complete();
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

    private static IEnumerable<BashFileSystem.TextLine> NulRecords(Stream fs)
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
        _lines.Terminator = _term;
        char term = _term;
        bool separate = plan.Separate || plan.InPlace;
        bool readError = false;

        // One continuous stream: a single engine/sink for all units.
        RecordSink? oneSink = null;
        SedEngine? oneEngine = null;
        bool any = false;
        bool quitAll = false;

        // Feed one unit's records. A file's last line without a newline is fed as such even mid-stream: GNU writes
        // it without one, and the next write supplies it (so `sed p f f` still has every newline, `sed -n 2p f f`
        // of a two-line `f` with no final newline does not).
        // Only READING the file is a "can't read" error. The engine runs outside the catch: a failure there is not
        // the file's (a bad regex once surfaced here as `can't read f: Invalid pattern`, printed twice).
        void Pump(IEnumerable<BashFileSystem.TextLine> lines, SedEngine engine, string? path)
        {
            using var en = lines.GetEnumerator();
            while (true)
            {
                if (!TryReadNext(en, path, out var line)) return;
                engine.Feed(line.Text, line.HasTrailingNewline);
                if (engine.Done) return;
            }
        }

        bool TryReadNext(IEnumerator<BashFileSystem.TextLine> en, string? path, out BashFileSystem.TextLine line)
        {
            line = default!;
            try
            {
                if (!en.MoveNext()) return false;
                line = en.Current;
                return true;
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                EmitError($"sed: can't read {BashPath.ToShell(path ?? "-")}: {FileSystemHelpers.ReadErrorMessage(ex)}");
                readError = true;
                return false;
            }
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
                    EmitError($"sed: can't read {BashPath.ToShell(path ?? "-")}: {FileSystemHelpers.ReadErrorMessage(ex)}");
                    readError = true;
                    continue;
                }

                any = true;
                if (_rt != null) _rt.FileName = operand == "-" ? "-" : (sources.Count == 1 ? operand : path!);
                if (!separate)
                {
                    oneSink ??= new RecordSink(WriteStdoutRecord);
                    _debugSink = oneSink;
                    oneEngine ??= new SedEngine(commands, suppressDefault, oneSink.Add, _rt);
                    Pump(lines, oneEngine, path);
                    if (oneEngine.Done) quitAll = true;
                    continue;
                }

                if (plan.InPlace && path != null)
                {
                    _debugSink = null;
                    if (!EditInPlace(path, lines, term, commands, suppressDefault, plan.FollowSymlinks, ref readError)) return;
                    continue;
                }

                var sink = new RecordSink(WriteStdoutRecord);
                _debugSink = sink;
                var engine = new SedEngine(commands, suppressDefault, sink.Add, _rt);
                Pump(lines, engine, path);
                engine.Finish();
                sink.Complete();
                if (engine.Quit) quitAll = true;   // q/Q under -s ends the whole run, as GNU
            }
        }

        if (!separate && any && oneEngine != null)
        {
            oneEngine.Finish();
            oneSink!.Complete();
        }

        if (_rt is { ExitCode: not 0 }) FileSystemHelpers.SetLastExitCode(this, _rt.ExitCode);
        else if (readError) FileSystemHelpers.SetLastExitCode(this, 2);
    }

    /// <summary>
    /// <c>-i</c>: stream the edited records into a temp file beside the original and replace it (GNU's own
    /// strategy), keeping the original's mode. A symlink is REPLACED by the edited regular file unless <c>--follow-symlinks</c> asks to edit its target (GNU). False aborts the run
    /// (the write failed, reported GNU-style); a backup failure is the caller's, before this runs.
    /// </summary>
    private bool EditInPlace(string path, IEnumerable<BashFileSystem.TextLine> lines, char term,
        List<SedCommand> commands, bool suppressDefault, bool followSymlinks, ref bool readError)
    {
        string target = path;
        try
        {
            if (followSymlinks && File.ResolveLinkTarget(path, returnFinalTarget: true) is { } link) target = link.FullName;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex)) { target = path; }

        string dir = Path.GetDirectoryName(target) is { Length: > 0 } d ? d : ".";
        string tmp = Path.Combine(dir, "sed" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new StreamWriter(fs, RawBytes.Encoding, 65536))
            {
                var sink = new RecordSink((record, terminated) =>
                {
                    writer.Write(record);
                    if (terminated) writer.Write(term);
                });
                var engine = new SedEngine(commands, suppressDefault, sink.Add, _rt);
                using (var en = lines.GetEnumerator())
                {
                    while (true)
                    {
                        // Only reading the file is a "can't read" error; the engine runs outside the catch.
                        BashFileSystem.TextLine line;
                        try
                        {
                            if (!en.MoveNext()) break;
                            line = en.Current;
                        }
                        catch (Exception ex)
                        {
                            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                            EmitError($"sed: can't read {BashPath.ToShell(path)}: {FileSystemHelpers.ReadErrorMessage(ex)}");
                            readError = true;
                            writer.Dispose();
                            File.Delete(tmp);
                            return true;
                        }
                        engine.Feed(line.Text, line.HasTrailingNewline);
                        if (engine.Done) break;
                    }
                }
                engine.Finish();
                sink.Complete();
            }

            CopyFileMode(target, tmp);
            File.Move(tmp, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) { TryDelete(tmp); throw; }
            TryDelete(tmp);
            EmitError($"sed: {BashPath.ToShell(target)}: {ex.Message}");
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
            EmitError($"sed: cannot rename {BashPath.ToShell(path)}: {ex.Message}");
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
        public string? Pattern;        // Regex (each pattern is the sed regex as typed; --debug lists it)
        public int Line;               // Line
        public int Start;              // RangeNum / Step / RangeNumToRegex
        public int End;                // RangeNum
        public int Step;               // Step
        public string? StartPattern;   // RangeRegex
        public string? EndPattern;     // RangeRegex / RangeNumToRegex
        // The same regexes compiled when the script is parsed (with their I / M flags): what the engine matches with.
        public Regex? Rx, StartRx, EndRx;
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

        // ---- commands beyond the original set (blocks, branches, hold space, files, e, l, --debug) ----
        public string? Label;          // ':' name; b/t/T target label (null = end of script)
        public int Jump = -1;          // '{' -> index of its '}'; b/t/T -> index of the target (Count = end of script)
        public int IntArg = -1;        // l / q / Q operand as typed (-1 = none)
        public string? FileName;       // r R w W; s///w
        public string? Command;        // e
        public string? SrcRegex;       // s: the regex as typed (--debug prints it)
        public string? SrcReplacement; // s: the replacement as typed (--debug prints it)
        public bool IgnoreCase, Multiline, Eval; // s flags I/i, M/m, e
        public bool Bytes;             // s: the regex names a lone byte (\xce), so it matches the pattern space's bytes
        public int EndPos;             // where this command ended in its expression (diagnostics)
        public SedScript? Script;      // the -e expression / -f file it came from (diagnostics)
        public string? EmptyRegexError; // it holds the empty regex: GNU's message when no regex was used before it ran
    }

    /// <summary>
    /// A sed regex <c>\xHH</c> of 0x80 or more is ONE BYTE (GNU: <c>s/\xce//g</c> removes the first byte of each
    /// <c>μ</c>, CE BC). When a regex has one, it is rewritten for byte matching and true is returned: each such escape
    /// becomes its escaped-byte marker and every other non-ASCII character its bytes' markers
    /// (<see cref="RawBytes.ToByteChars"/>), and the command then runs on the pattern space's bytes. An escaped
    /// backslash (<c>\\xce</c>) is not an escape.
    /// </summary>
    internal static bool TryRewriteByteEscapes(ref string pattern)
    {
        if (pattern.IndexOf("\\x", StringComparison.Ordinal) < 0) return false;
        var sb = new StringBuilder(pattern.Length);
        bool any = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                if (pattern[i + 1] == 'x' && i + 3 < pattern.Length
                    && byte.TryParse(pattern.AsSpan(i + 2, 2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out byte b)
                    && b >= 0x80)
                {
                    sb.Append(RawBytes.ToMarker(b));
                    any = true;
                    i += 3;
                    continue;
                }
                sb.Append(c).Append(pattern[i + 1]);
                i++;
                continue;
            }
            sb.Append(c);
        }
        if (!any) return false;
        pattern = RawBytes.ToByteChars(sb.ToString());
        return true;
    }

    /// <summary>
    /// Convert a GNU-sed replacement string into a .NET regex replacement string: <c>\1</c>-<c>\9</c> (and <c>\0</c>)
    /// are groups, <c>&amp;</c> the whole match, <c>\n</c> a newline, a character escape (<see cref="SedEscape"/>:
    /// <c>\t</c>, <c>\x26</c>, ...) the character itself (<c>\x26</c> is a literal <c>&amp;</c>), and a backslash before
    /// any other character is dropped. Every literal <c>$</c> becomes <c>$$</c> so .NET does not read it as a group.
    /// </summary>
    internal static string BuildReplacement(string sed)
    {
        var sb = new StringBuilder(sed.Length);
        void Literal(char ch)
        {
            if (ch == '$') sb.Append("$$"); else sb.Append(ch);
        }
        for (int i = 0; i < sed.Length; i++)
        {
            char c = sed[i];
            if (c == '\\' && i + 1 < sed.Length)
            {
                if (SedEscape.TryDecode(sed, i + 1, out char v, out int next))
                {
                    Literal(SedEscape.DataChar(v));
                    i = next - 1;
                    continue;
                }
                char n = sed[i + 1];
                if (n == 'n') sb.Append('\n');
                else if (n is >= '0' and <= '9') sb.Append("${").Append(n).Append('}');   // `\10` is group 1, then 0
                else Literal(n);                                                            // `\&` `\\` `\q`: the character
                i++;
                continue;
            }
            if (c == '&') { sb.Append("$0"); continue; }
            Literal(c);
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
    internal static List<string> SplitSedCommands(string expression) => SplitSedCommands(expression, out _);

    /// <summary>
    /// <see cref="SplitSedCommands(string)"/> that also reports where each part ended in
    /// <paramref name="expression"/> (for GNU's "char N" in a diagnostic). Besides the original commands it
    /// separates the block braces (<c>{</c> and <c>}</c> are commands of their own, and a <c>}</c> also ends
    /// the command before it: <c>/x/{p;d}</c>), cuts <c>:label</c> / <c>b label</c> at <c>;</c>, takes
    /// <c>r R w W e</c> operands to the end of the line, ends a one-line <c>a/i/c</c> text at its first
    /// unescaped newline, drops <c>#</c> comments, and lets an <c>s</c> flag run end at <c>w FILE</c>'s newline.
    /// </summary>
    internal static List<string> SplitSedCommands(string expression, out List<int> ends)
    {
        var parts = new List<string>();
        var partEnds = new List<int>();
        ends = partEnds;
        int n = expression.Length;
        int start = 0;

        void Add(int s, int e)
        {
            var p = expression.Substring(s, e - s);
            if (p.Length == 0) return;
            parts.Add(p);
            partEnds.Add(e);
        }

        // Each iteration begins at a command position (`start`): skip the address prefix (if any), then
        // dispatch on the command character. The address may itself contain `;` / newline inside a `/re/`,
        // which <see cref="SkipSedAddress"/> consumes before any separator check.
        while (start < n)
        {
            while (start < n && expression[start] is ' ' or '\t' or ';' or '\n' or '\r') start++;
            if (start >= n) break;

            int cmdPos = SkipSedAddress(expression, start);
            if (cmdPos < 0)
            {
                // An unterminated address regex runs to the end of its line (GNU stops there and reports it).
                int nl = expression.IndexOf('\n', start);
                int stop = nl < 0 ? n : nl;
                Add(start, stop);
                start = stop;
                continue;
            }

            // Optional negation / spaces between the address and the command.
            while (cmdPos < n && (expression[cmdPos] == '!' || expression[cmdPos] == ' ' || expression[cmdPos] == '\t'))
                cmdPos++;

            if (cmdPos >= n)
            {
                Add(start, n);
                break;
            }

            char c = expression[cmdPos];
            int end;
            int next;

            switch (c)
            {
                case '{':
                case '}':
                    end = next = cmdPos + 1;
                    break;

                case '#':
                {
                    int nl = expression.IndexOf('\n', cmdPos);
                    start = nl < 0 ? n : nl + 1;
                    continue;
                }

                case 's':
                case 'y':
                {
                    // The same field scanner the parser reads the command with (brackets, `\DELIM`, newlines).
                    if (SedDelimited.ScanCommandFields(expression, cmdPos, out var f1, out var f2))
                        end = FindFlagsEnd(expression, f2.End, c == 's');
                    else if (cmdPos + 1 < n)
                        end = f1.Closed ? f2.End : f1.End;   // unterminated: the part runs to where the scan stopped
                    else
                        end = n;
                    next = end;
                    break;
                }

                case 'a':
                case 'i':
                case 'c':
                    // Text command: the text runs to the first newline that is not escaped by a backslash (a
                    // following `;` inside the text is literal, matching GNU). Only the `\`/newline/space/end
                    // form is the text form; a bare `c` is a complete command and `;` after it separates.
                    if (cmdPos + 1 >= n || expression[cmdPos + 1] is '\\' or '\n' or ' ')
                    {
                        int i = cmdPos + 1;
                        while (i < n && expression[i] != '\n')
                            i += expression[i] == '\\' ? 2 : 1;
                        end = Math.Min(i, n);
                        next = end;
                    }
                    else
                    {
                        end = next = FindCmdEnd(expression, cmdPos + 1);
                    }
                    break;

                case 'r':
                case 'R':
                case 'w':
                case 'W':
                case 'e':
                {
                    int nl = expression.IndexOf('\n', cmdPos);
                    end = next = nl < 0 ? n : nl;
                    break;
                }

                case ':':
                {
                    int i = cmdPos + 1;
                    while (i < n && expression[i] is ' ' or '\t') i++;
                    while (i < n && expression[i] is not (';' or '\n' or ' ' or '\t')) i++;
                    end = next = i;
                    break;
                }

                default:
                    end = next = FindCmdEnd(expression, cmdPos + 1);
                    break;
            }

            Add(start, end);
            start = next;
        }

        return parts;
    }

    /// <summary>
    /// End of a plain command that started before <paramref name="pos"/>: the next top-level <c>;</c>,
    /// newline or <c>}</c> (a command is closed by the brace that closes its block), else the end.
    /// </summary>
    private static int FindCmdEnd(string s, int pos)
    {
        for (int i = pos; i < s.Length; i++)
        {
            if (s[i] is ';' or '\n' or '}') return i;
        }
        return s.Length;
    }

    /// <summary>
    /// End of an <c>s</c>/<c>y</c> command whose delimited part ended at <paramref name="pos"/>: the flags run to
    /// the next <c>;</c>, newline or <c>}</c>, except that an <c>s</c> <c>w FILE</c> flag and a <c>#</c> comment after
    /// the flags take the whole line.
    /// </summary>
    private static int FindFlagsEnd(string s, int pos, bool isSubst)
    {
        for (int i = pos; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is ';' or '\n' or '}') return i;
            if (isSubst && ch is 'w' or '#')
            {
                int nl = s.IndexOf('\n', i);
                return nl < 0 ? s.Length : nl;
            }
        }
        return s.Length;
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
    /// From a command position, returns the index just past an address prefix, <paramref name="pos"/> when there is
    /// none, or -1 when an address regex is unterminated. Recognizes the same grammar as
    /// <see cref="ParseExpressionCore"/> and reads every regex with the same scanner
    /// (<see cref="SedDelimited.ScanRegexAddress"/>): <c>/re/</c> or <c>\cREc</c> with its <c>I</c>/<c>M</c> flags,
    /// <c>$</c>, <c>N</c>, <c>N~S</c>, and a <c>,</c> range tail. A <c>;</c> or newline inside a regex is consumed here,
    /// so the caller never mistakes it for a command separator.
    /// </summary>
    private static int SkipSedAddress(string s, int pos)
    {
        if (pos >= s.Length) return pos;

        int p;
        if (SedDelimited.StartsRegexAddress(s, pos))
        {
            var first = SedDelimited.ScanRegexAddress(s, pos);
            if (!first.Closed) return -1;
            p = first.End;
        }
        else if (s[pos] == '$')
        {
            p = pos + 1;
        }
        else if (char.IsDigit(s[pos]))
        {
            p = pos;
            while (p < s.Length && char.IsDigit(s[p])) p++;
            if (p < s.Length && s[p] == '~')
            {
                p++;
                while (p < s.Length && char.IsDigit(s[p])) p++;
                return p;
            }
        }
        else
        {
            return pos;
        }

        // Blanks may sit between the first address and its comma (`2 ,3p`), as in the parser.
        int comma = SkipBlanks(s, p);
        if (comma >= s.Length || s[comma] != ',') return p;
        int q = SkipBlanks(s, comma + 1);
        if (SedDelimited.StartsRegexAddress(s, q))
        {
            var second = SedDelimited.ScanRegexAddress(s, q);
            return second.Closed ? second.End : -1;
        }
        if (q < s.Length && s[q] == '$') return q + 1;
        while (q < s.Length && char.IsDigit(s[q])) q++;
        return q;
    }

    private static int SkipBlanks(string s, int pos)
    {
        while (pos < s.Length && s[pos] is ' ' or '\t') pos++;
        return pos;
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
        var cmd = ParseExpressionCore(expression, extendedRegex, SedSite.Whole(expression), out string? err, out int code);
        if (cmd == null && err != null)
        {
            EmitError(err);
            FileSystemHelpers.SetLastExitCode(this, code);
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
        => BuildProgram(expressions, extendedRegex, out commands, out _, out _);

    /// <summary><see cref="BuildProgram(IReadOnlyList{SedScript}, bool, out List{SedCommand}, out string?, out int)"/>
    /// for <c>-e</c> expressions only, numbered from 1.</summary>
    internal static bool BuildProgram(
        IEnumerable<string> expressions, bool extendedRegex, out List<SedCommand> commands,
        out string? error, out int exitCode)
    {
        var scripts = new List<SedScript>();
        foreach (var e in expressions) scripts.Add(new SedScript(e, ExprNo: scripts.Count + 1));
        return BuildProgram(scripts, extendedRegex, out commands, out error, out exitCode);
    }

    /// <summary>
    /// Split, parse and LINK a script: <c>{</c> learns its <c>}</c>, <c>b t T</c> their <c>:label</c> (or the end of
    /// the script). A script error comes back as GNU's message and exit status (1 for syntax, 4 for a missing
    /// label), placed in the script it is in (<see cref="SedScript"/>); no-op commands (<c>v</c>) are dropped. A
    /// command holding the empty regex learns the run-time message GNU gives when no regex was used before it.
    /// </summary>
    internal static bool BuildProgram(
        IReadOnlyList<SedScript> scripts, bool extendedRegex, out List<SedCommand> commands,
        out string? error, out int exitCode)
    {
        commands = new List<SedCommand>();
        error = null;
        exitCode = 0;
        foreach (var script in scripts)
        {
            string expr = script.Text;
            var split = SplitSedCommands(expr, out var ends);
            for (int pi = 0; pi < split.Count; pi++)
            {
                // A part is expr[start..end) (SplitSedCommands' Add), so its offset is its end less its length.
                var site = new SedSite(script, ends[pi] - split[pi].Length);
                var c = ParseExpressionCore(split[pi], extendedRegex, site, out string? err, out int code);
                if (c == null)
                {
                    error = err ?? "sed: bad expression";
                    exitCode = code == 0 ? 1 : code;
                    return false;
                }
                c.EndPos = ends[pi];
                c.Script = script;
                if (c.Type != 'v') commands.Add(c);
            }
        }
        if (!LinkProgram(commands, out error, out exitCode)) return false;
        if (scripts.Count > 0)
        {
            // GNU reports a run-time "no previous regular expression" at the end of the last script it read.
            string noPrevious = scripts[^1].AtEnd(SedRegex.NoPreviousMessage);
            foreach (var c in commands)
                if (HasEmptyRegex(c)) c.EmptyRegexError = noPrevious;
        }
        return true;
    }

    private static bool HasEmptyRegex(SedCommand c)
    {
        var a = c.Address;
        return c.Regex == SedRegex.Last
            || (a != null && (a.Rx == SedRegex.Last || a.StartRx == SedRegex.Last || a.EndRx == SedRegex.Last));
    }

    private static bool LinkProgram(List<SedCommand> commands, out string? error, out int exitCode)
    {
        error = null;
        exitCode = 0;
        var open = new Stack<int>();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < commands.Count; i++)
        {
            var c = commands[i];
            if (c.Type == '{') open.Push(i);
            else if (c.Type == '}')
            {
                if (open.Count == 0)
                {
                    error = c.Script!.At(c.EndPos, "unexpected `}'");
                    exitCode = 1;
                    return false;
                }
                commands[open.Pop()].Jump = i;
            }
            else if (c.Type == ':' && !labels.ContainsKey(c.Label!)) labels[c.Label!] = i;
        }
        if (open.Count > 0)
        {
            // GNU: an -e script names the expression at char 0, a file the line of the `{`.
            var brace = commands[open.Peek()];
            var s = brace.Script!;
            error = s.File == null ? $"sed: -e expression #{s.ExprNo}, char 0: unmatched `{{'" : s.At(brace.EndPos, "unmatched `{'");
            exitCode = 1;
            return false;
        }
        foreach (var c in commands)
        {
            if (c.Type is not ('b' or 't' or 'T')) continue;
            if (c.Label == null) { c.Jump = commands.Count; continue; }
            if (!labels.TryGetValue(c.Label, out int target))
            {
                error = $"sed: can't find label for jump to `{c.Label}'";
                exitCode = 4;
                return false;
            }
            c.Jump = target;
        }
        return true;
    }

    /// <summary>
    /// One script as sed read it: an <c>-e</c> expression (<paramref name="ExprNo"/> counts only the <c>-e</c>
    /// options, from 1) or a <c>-f</c> <paramref name="File"/> whose <paramref name="Text"/> starts at line
    /// <paramref name="FirstLine"/> (2 once a leading <c>#n</c> line was taken off). GNU places an error in an
    /// expression as <c>sed: -e expression #N, char M:</c> and in a file as <c>sed: file F line L:</c>, L being 1 plus
    /// the newlines read before the error.
    /// </summary>
    internal sealed record SedScript(string Text, string? File = null, int ExprNo = 1, int FirstLine = 1)
    {
        /// <summary>GNU's message for an error at <paramref name="offset"/> in <see cref="Text"/>.</summary>
        public string At(int offset, string message)
        {
            if (File == null) return $"sed: -e expression #{ExprNo}, char {offset}: {message}";
            int line = FirstLine;
            int end = Math.Min(offset, Text.Length);
            for (int i = 0; i < end; i++) if (Text[i] == '\n') line++;
            return $"sed: file {File} line {line}: {message}";
        }

        /// <summary>GNU's message for an error found after the whole script was read (<c>char 0</c> / its last line).</summary>
        public string AtEnd(string message) => File == null ? $"sed: -e expression #{ExprNo}, char 0: {message}" : At(Text.Length, message);
    }

    /// <summary>
    /// Where one split command sits in its script: GNU's prefix counts characters (or lines) from the start of that
    /// script, so an error in the second command of <c>/a/d;/b\(/d</c> is at char 10, not 5.
    /// <paramref name="Base"/> is the command's offset in the script's text.
    /// </summary>
    internal readonly record struct SedSite(SedScript Script, int Base)
    {
        public static SedSite Whole(string expression) => new(new SedScript(expression), 0);

        public string Expression => Script.Text;

        public string At(int local, string message) => Script.At(Base + local, message);

        /// <summary>
        /// GNU reads the <c>;</c> or newline that ends an <c>s</c> / <c>y</c> command before it compiles the regex, so
        /// an error then is one character past the command's text (but not past a closing <c>}</c>).
        /// </summary>
        public int AfterCommand(int local)
        {
            int abs = Base + local;
            return abs < Expression.Length && Expression[abs] is ';' or '\n' ? local + 1 : local;
        }
    }

    /// <summary>
    /// Pure expression parser (no runspace / error emission). Returns the parsed
    /// command, or <c>null</c> with <paramref name="errMsg"/> / <paramref name="errCode"/>
    /// set on a parse failure. The instance <see cref="ParseExpression"/> wraps this
    /// and emits the bash-style error; the fused stage uses the return value only.
    /// Every delimited field (address regex, <c>s</c>, <c>y</c>) is read by <see cref="SedDelimited"/>, the scanner
    /// <see cref="SplitSedCommands(string)"/> cut the command out with, and every regex is compiled here by
    /// <see cref="SedRegex.TryCompile"/>, so a bad one is a script error before any input is read.
    /// </summary>
    private static SedCommand? ParseExpressionCore(
        string expression, bool extendedRegex, SedSite site, out string? errMsg, out int errCode)
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

        // A regex error at `at`: GNU's message there, status 1, or its panic (status 4, no position).
        SedCommand? FailRegex(int at, string message) =>
            SedRegex.IsPanic(message) ? Fail("sed: " + message, 4) : Fail(site.At(at, message), 1);

        // A regex address at `at` (`/re/` or `\cREc`, its I/M flags), compiled; `source` is the regex as typed.
        // Null on success; else GNU's message (to report at `end`).
        string? RegexAddress(int at, out int end, out Regex rx, out string source)
        {
            var a = SedDelimited.ScanRegexAddress(expression, at);
            end = a.End;
            rx = null!;
            source = a.Pattern;
            if (!a.Closed) return "unterminated address regex";
            var opts = RegexOptions.None;
            if (a.IgnoreCase) opts |= RegexOptions.IgnoreCase;
            if (a.Multiline) opts |= RegexOptions.Multiline;
            return SedRegex.TryCompile(a.Pattern, extendedRegex, opts, out rx, out string msg) ? null : msg;
        }

        // GNU allows blanks between a first address and its comma (`2 ,3p`, `/b/ ,/c/p`): the comma's index, or -1.
        int CommaAfter(int p)
        {
            int q = SkipBlanks(expression, p);
            return q < expression.Length && expression[q] == ',' ? q : -1;
        }

        SedCommand? Parse()
        {
        SedAddress? addr = null;
        int pos = 0;

        // Address prefix.
        if (SedDelimited.StartsRegexAddress(expression, pos))
        {
            if (RegexAddress(pos, out pos, out var rx1, out string src1) is { } bad1) return FailRegex(pos, bad1);
            addr = new SedAddress { Type = AddressType.Regex, Pattern = src1, Rx = rx1 };

            // Range: /start/,/end/ (either regex may use a custom delimiter: \,start,,\|end|)
            if (CommaAfter(pos) is var comma && comma >= 0)
            {
                pos = SkipBlanks(expression, comma + 1);
                if (SedDelimited.StartsRegexAddress(expression, pos))
                {
                    if (RegexAddress(pos, out pos, out var rx2, out string src2) is { } bad2) return FailRegex(pos, bad2);
                    addr = new SedAddress
                    {
                        Type = AddressType.RangeRegex,
                        StartPattern = addr.Pattern,
                        StartRx = rx1,
                        EndPattern = src2,
                        EndRx = rx2,
                    };
                }
                else if (pos < expression.Length && (expression[pos] == '$' || char.IsDigit(expression[pos])))
                {
                    // /re/,$ and /re/,N: the range runs to the end of input / through line N (End; EndPattern stays null).
                    int endNum = int.MaxValue;
                    if (expression[pos] == '$') pos++;
                    else
                    {
                        int numStart = pos;
                        while (pos < expression.Length && char.IsDigit(expression[pos])) pos++;
                        if (!int.TryParse(expression.AsSpan(numStart, pos - numStart), out endNum)) endNum = int.MaxValue;
                    }
                    addr = new SedAddress
                    {
                        Type = AddressType.RangeRegex, StartPattern = addr.Pattern, StartRx = rx1, End = endNum,
                    };
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
            else if (CommaAfter(pos) is var numComma && numComma >= 0)
            {
                pos = SkipBlanks(expression, numComma + 1);
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
                else if (SedDelimited.StartsRegexAddress(expression, pos))
                {
                    // N,/re/ — numeric start, regex end. With N==0 the end regex
                    // may match the very first line (GNU's 0,/re/ idiom).
                    if (RegexAddress(pos, out pos, out var rxEnd, out string srcEnd) is { } badEnd) return FailRegex(pos, badEnd);
                    addr = new SedAddress
                    {
                        Type = AddressType.RangeNumToRegex,
                        Start = startNum,
                        EndPattern = srcEnd,
                        EndRx = rxEnd,
                    };
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
        while (pos < expression.Length && expression[pos] is '!' or ' ' or '\t')
        {
            if (expression[pos] == '!') { negate = true; }
            pos++;
        }

        string remaining = expression.Substring(pos);
        if (remaining.Length == 0)
        {
            return Fail(site.At(pos, "missing command"), 1);
        }

        char cmdChar = remaining[0];
        switch (cmdChar)
        {
            case 's':
            {
                // The fields come from the scanner SplitSedCommands used: `\DELIM` is the delimiter, a delimiter inside
                // a regex bracket expression does not end it, `\&` stays escaped even under `s&..&..&`.
                if (!SedDelimited.ScanCommandFields(remaining, 0, out var reField, out var replField))
                {
                    int stop = remaining.Length < 2 ? remaining.Length : reField.Closed ? replField.End : reField.End;
                    return Fail(site.At(pos + stop, "unterminated `s' command"), 1);
                }

                string searchPattern = reField.Text;
                string replacement = replField.Text;
                string flags = remaining.Substring(replField.End);   // flags, incl. `w FILE`
                string srcRegex = searchPattern, srcReplacement = replacement;

                // Parse the substitution flags: g, p, e, i/I, m/M, a number N (with g: "Nth and onward"), `w FILE`.
                // A digit run is ONE number (`s/x/y/10`); TryParse guards an absurd count (`s/x/y/9999999999`).
                // Blanks are skipped, `#` starts a comment, anything else is GNU's "unknown option to `s'". GNU
                // refuses a second g, p or number and a zero number (i, m, e may repeat).
                bool global = false;
                int nth = 0;
                bool sawNumber = false;
                bool printOnSub = false, evalFlag = false, multiline = false, ignoreCase = false;
                string? wFile = null;
                int flagsEnd = remaining.Length;   // where the command's text ends (a `#` comment is not part of it)
                for (int fi = 0; fi < flags.Length; fi++)
                {
                    char f = flags[fi];
                    int at = pos + replField.End + fi + 1;   // GNU's char: just past this flag
                    if (char.IsAsciiDigit(f))
                    {
                        if (sawNumber) return Fail(site.At(at, "multiple number options to `s' command"), 1);
                        sawNumber = true;
                        int numStart = fi;
                        while (fi + 1 < flags.Length && char.IsAsciiDigit(flags[fi + 1])) fi++;
                        if (!int.TryParse(flags.AsSpan(numStart, fi - numStart + 1), out nth)) nth = int.MaxValue;
                        else if (nth == 0)
                            return Fail(site.At(pos + replField.End + fi + 1, "number option to `s' command may not be zero"), 1);
                    }
                    else if (f == 'g')
                    {
                        if (global) return Fail(site.At(at, "multiple `g' options to `s' command"), 1);
                        global = true;
                    }
                    else if (f == 'p')
                    {
                        if (printOnSub) return Fail(site.At(at, "multiple `p' options to `s' command"), 1);
                        printOnSub = true;
                    }
                    else if (f == 'e') { evalFlag = true; }
                    else if (f == 'm' || f == 'M') { multiline = true; }
                    else if (f == 'i' || f == 'I') { ignoreCase = true; }
                    else if (f is ' ' or '\t') { }
                    else if (f == '#') { flagsEnd = replField.End + fi; break; }
                    else if (f == 'w')
                    {
                        wFile = flags.Substring(fi + 1).TrimStart();
                        if (wFile.Length == 0)
                            return Fail(site.At(site.AfterCommand(pos + remaining.Length), "missing filename in r/R/w/W commands"), 1);
                        break;
                    }
                    else
                    {
                        return Fail(site.At(at, "unknown option to `s'"), 1);
                    }
                }
                // GNU compiles the regex once the command (and the `;` / newline after it) has been read.
                int compileAt = flagsEnd == remaining.Length ? site.AfterCommand(pos + flagsEnd) : pos + flagsEnd;

                // An empty regex (`s//repl/`) reuses the last regex used when it runs (SedRegex.Last; the engine
                // resolves it, or ends the run with GNU's "no previous regular expression").
                int maxRef = SedRegex.MaxReplacementReference(replacement);
                replacement = BuildReplacement(replacement);
                bool byteMatch = TryRewriteByteEscapes(ref searchPattern);
                if (byteMatch) replacement = RawBytes.ToByteChars(replacement);

                var regexOpts = RegexOptions.None;
                if (ignoreCase)
                {
                    regexOpts |= RegexOptions.IgnoreCase;
                }
                if (multiline) regexOpts |= RegexOptions.Multiline;

                // The address and `s` regexes compile through one helper: GNU dialect (POSIX classes, BRE escapes)
                // to .NET, and a parse failure in GNU's words at GNU's character position.
                if (!SedRegex.TryCompile(searchPattern, extendedRegex, regexOpts, out var regex, out string regexError))
                    return FailRegex(compileAt, regexError);
                if (regex != SedRegex.Last && maxRef > regex.GetGroupNumbers().Length - 1)
                    return Fail(site.At(compileAt, $"invalid reference \\{maxRef} on `s' command's RHS"), 1);

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
                    SrcRegex = srcRegex,
                    SrcReplacement = srcReplacement,
                    IgnoreCase = ignoreCase,
                    Multiline = multiline,
                    Eval = evalFlag,
                    FileName = wFile,
                    Bytes = byteMatch,
                };
            }
            case 'd':
            case 'D':
            case 'p':
            case 'P':
            case 'N':
            case '=':
            case 'n':
            case 'g':
            case 'G':
            case 'h':
            case 'H':
            case 'x':
            case 'z':
            case 'F':
                return new SedCommand { Type = cmdChar, Address = addr, Negate = negate };
            case 'q':
            case 'Q':
            case 'l':
            {
                // Q quits like q but WITHOUT auto-printing the pattern space.
                // q/Q accept an optional exit code; `l` an optional wrap width.
                int exitCode = 0, intArg = -1;
                if (remaining.Length > 1)
                {
                    string qArg = remaining.Substring(1).Trim();
                    if (qArg.Length > 0 && Regex.IsMatch(qArg, @"^\d+$")
                        && int.TryParse(qArg, out int parsed))
                    {
                        exitCode = parsed;
                        intArg = parsed;
                    }
                    else if (qArg.Length > 0)
                    {
                        return Fail(site.At(pos + remaining.Length, "extra characters after command"), 1);
                    }
                }
                return new SedCommand
                {
                    Type = cmdChar, Address = addr, Negate = negate, ExitCode = exitCode, IntArg = intArg,
                };
            }
            case 'a':
            case 'i':
            case 'c':
            {
                string text = remaining.Length > 1 ? remaining.Substring(1) : string.Empty;
                text = text.TrimStart('\\').TrimStart();
                // A backslash before a newline continues the text onto the next line (the backslash is not text).
                text = text.Replace("\\\r\n", "\n").Replace("\\\n", "\n");
                return new SedCommand { Type = cmdChar, Address = addr, Negate = negate, Text = text };
            }
            case '{':
                return new SedCommand { Type = '{', Address = addr, Negate = negate };
            case '}':
                if (addr != null) return Fail(site.At(pos + 1, "} doesn't want any addresses"), 1);
                return new SedCommand { Type = '}' };
            case ':':
            {
                string label = remaining.Substring(1).Trim();
                if (addr != null) return Fail(site.At(pos + 1, ": doesn't want any addresses"), 1);
                if (label.Length == 0) return Fail(site.At(pos + 1, "\":\" lacks a label"), 1);
                return new SedCommand { Type = ':', Label = label };
            }
            case 'b':
            case 't':
            case 'T':
            {
                string label = remaining.Substring(1).Trim();
                return new SedCommand
                {
                    Type = cmdChar, Address = addr, Negate = negate, Label = label.Length == 0 ? null : label,
                };
            }
            case 'r':
            case 'R':
            case 'w':
            case 'W':
            {
                string file = remaining.Substring(1).TrimStart();
                if (file.Length == 0)
                    return Fail(site.At(site.AfterCommand(pos + remaining.Length), "missing filename in r/R/w/W commands"), 1);
                return new SedCommand { Type = cmdChar, Address = addr, Negate = negate, FileName = file };
            }
            case 'e':
                return new SedCommand
                {
                    Type = 'e', Address = addr, Negate = negate, Command = remaining.Substring(1).TrimStart(),
                };
            case 'v':
            {
                // `v [VERSION]`: fails when the script asks for a newer sed than the 4.9 this follows.
                string ver = remaining.Substring(1).Trim();
                if (ver.Length > 0 && Version.TryParse(ver, out var wanted) && wanted > new Version(4, 9))
                    return Fail(site.At(pos + remaining.Length, "expected newer version of sed"), 1);
                return new SedCommand { Type = 'v' };
            }
            case 'y':
            {
                // Both strings come from the shared scanner (no bracket expressions in a `y`; `\DELIM` is the delimiter).
                if (!SedDelimited.ScanCommandFields(remaining, 0, out var srcField, out var dstField))
                {
                    int stop = remaining.Length < 2 ? remaining.Length : srcField.Closed ? dstField.End : srcField.End;
                    return Fail(site.At(pos + stop, "unterminated `y' command"), 1);
                }
                string source = SedEscape.TransliterationText(srcField.Text), dest = SedEscape.TransliterationText(dstField.Text);
                if (source.Length != dest.Length)
                    return Fail(site.At(pos + dstField.End, "strings for `y' command are different lengths"), 1);
                int junk = SkipBlanks(remaining, dstField.End);
                if (junk < remaining.Length && remaining[junk] != '#')
                    return Fail(site.At(pos + junk + 1, "extra characters after command"), 1);
                return new SedCommand
                {
                    Type = 'y',
                    Address = addr,
                    Negate = negate,
                    Source = source,
                    Dest = dest,
                };
            }
            default:
                // Every GNU 4.9 command is implemented, so anything else is a script error (GNU: exit 1).
                return Fail(site.At(pos + 1, $"unknown command: `{cmdChar}'"), 1);
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
            string resolved = FileSystemHelpers.ProviderPath(this, path);
            return File.Exists(resolved) ? resolved : null;
        }
        catch
        {
            return null;
        }
    }
}
