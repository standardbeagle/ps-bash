using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashAwk</c> function web
/// (REFACTOR-2 follow-on). The psm1 implementation was a regex/string-scan
/// approximation of awk; this cmdlet drives a real recursive-descent interpreter
/// (<see cref="AwkInterpreter"/> → <see cref="AwkParser"/> →
/// <see cref="AwkMachine"/>), closing the five differential parity gaps the psm1
/// version could not express (field string-concat, <c>+=</c> accumulation,
/// <c>index()</c> in print position, <c>split()</c> into an array, <c>if/else</c>).
///
/// Flags: <c>-F FS</c> field separator (also joined <c>-FFS</c>), <c>-v VAR=VAL</c>
/// pre-BEGIN assignment, <c>-f FILE</c> program file. <c>-v</c> prefix-collides
/// with the <c>-Verbose</c> common parameter (a bare <c>-v</c> would bind to
/// <c>-Verbose</c> and the assignment would be lost), so it is declared as the
/// value-bearing <see cref="V"/> decoy; the joined <c>-vVAR=VAL</c> form and all
/// other flags flow through <see cref="Arguments"/> via
/// <c>ValueFromRemainingArguments</c> and the manual scan in
/// <see cref="EndProcessing"/>.
///
/// Input: with no file operands, records come from the pipeline (stdin mode);
/// otherwise each operand is a data file read via the streaming
/// <see cref="BashFileSystem"/> primitive with NR cumulative across files and
/// FNR reset per file. File-open errors emit through
/// <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord). Output is one BashObject per output line.
///
/// Oracle: GNU awk via <c>AwkDifferentialTests</c> (byte-level bash parity) and
/// the hand-asserted <c>InvokeBashAwkFileModeTests</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashAwk")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashAwkCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// <c>-v VAR=VAL</c> — declared explicitly because the bare token <c>-v</c>
    /// prefix-collides with the <c>-Verbose</c> common parameter and would
    /// otherwise be silently bound (the assignment dropped) before reaching
    /// <see cref="Arguments"/>. Value-bearing so <c>-v x=5</c> captures the
    /// assignment text; repeated <c>-v</c> accumulate.
    /// </summary>
    [Parameter]
    public string[]? V { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // INPUT MODES (memory bound). Three, chosen once the program is parsed:
    //  1. stdin, STREAMING (no file operands, no main-input getline): BEGIN runs in BeginProcessing,
    //     each pipeline record is fed to the machine in ProcessRecord as it arrives, END runs in
    //     EndProcessing. Nothing collects stdin.
    //  2. stdin, BUFFERED (no file operands, program has a plain `getline [var]` or `getline < "-"`):
    //     a main-input getline PULLS the next record from inside a rule, but this cmdlet is push-driven
    //     (the pipeline hands us records in ProcessRecord). Decided statically from the AST
    //     (AwkProgram.UsesMainInput): only such programs buffer stdin (O(input) memory); every record is
    //     collected in ProcessRecord and the WHOLE run (BEGIN, main loop, END) happens in EndProcessing
    //     as a pull loop over the buffer — BEGIN is deferred too, so a BEGIN-time getline sees the first
    //     record. Output stays on the cmdlet thread (no worker thread, no queue).
    //  3. FILE operands: the machine pulls from AwkMainInput (files opened lazily, one at a time), BEGIN
    //     runs in BeginProcessing, the main loop and END in EndProcessing; getline crosses file
    //     boundaries exactly like the main loop (FNR reset, FILENAME updated). If the program has a
    //     main-input getline the pipeline (if any) is buffered for `getline < "-"` and BEGIN is deferred
    //     to EndProcessing too, so a BEGIN-time `getline < "-"` sees the piped records.
    private AwkMachine? _machine;
    private AwkProgram? _program;
    private AwkShell? _shell;
    private List<string> _files = new();
    private readonly List<string> _stdinBuffer = new();
    private int _fileError;
    private bool _stdinMode;
    private bool _bufferStdin;  // mode 2, or mode 3 with a getline that may read the pipeline
    private bool _halt;     // version/help/usage/syntax/runtime error: ignore everything that follows

    protected override void BeginProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "awk", args)) { _halt = true; return; }
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "awk"))
                WriteObject(line);
            _halt = true;
            return;
        }

        string? fieldSep = null;
        var varAssignments = new List<string>();
        var programFiles = new List<string>();
        string? programText = null;
        var files = new List<string>();
        bool pastDoubleDash = false;
        bool optionsEnded = false;

        if (V != null) varAssignments.AddRange(V);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (pastDoubleDash || optionsEnded) { AddOperand(arg, ref programText, programFiles, files); continue; }
            if (arg == "--") { pastDoubleDash = true; continue; }
            // POSIX: option parsing ends at the first operand (the program text, or the first
            // file when -f supplied the program). `awk '{print}' -v x=1` names a FILE `-v`.
            if (arg.Length == 0 || arg[0] != '-' || arg == "-") { optionsEnded = true; AddOperand(arg, ref programText, programFiles, files); continue; }

            if (arg == "-F")
            {
                if (i + 1 < args.Length) fieldSep = ProcessEscapes(args[++i]);
                continue;
            }
            if (arg.Length > 2 && arg.StartsWith("-F", StringComparison.Ordinal))
            {
                fieldSep = ProcessEscapes(arg.Substring(2));
                continue;
            }
            if (arg == "-v")
            {
                if (i + 1 < args.Length) varAssignments.Add(args[++i]);
                continue;
            }
            if (arg.Length > 2 && arg.StartsWith("-v", StringComparison.Ordinal))
            {
                varAssignments.Add(arg.Substring(2));
                continue;
            }
            if (arg == "-f" || arg == "--file")
            {
                if (i + 1 < args.Length) programFiles.Add(args[++i]);
                continue;
            }
            if (arg.Length > 2 && arg.StartsWith("-f", StringComparison.Ordinal))
            {
                programFiles.Add(arg.Substring(2));
                continue;
            }

            AddOperand(arg, ref programText, programFiles, files);
        }

        // Program text: -f files concatenated, else the first non-flag operand.
        if (programFiles.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var pf in programFiles)
            {
                string resolved;
                try { resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(pf); }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(this, $"awk: can't open source file {pf}: {ex.Message}");
                    SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
                    _halt = true;
                    return;
                }
                if (!File.Exists(resolved))
                {
                    FileSystemHelpers.WriteBashError(this, $"awk: fatal: cannot open source file `{pf}' for reading: No such file or directory");
                    SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
                    _halt = true;
                    return;
                }
                sb.Append(BashFileSystem.ReadAllText(resolved));
                sb.Append('\n');
            }
            programText = sb.ToString();
        }

        if (programText == null)
        {
            FileSystemHelpers.WriteBashError(this, "awk: usage: awk [options] program [file ...]");
            SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
            _halt = true;
            return;
        }

        AwkProgram program;
        try
        {
            program = AwkInterpreter.Parse(programText);
        }
        catch (AwkInterpreter.AwkSyntaxException ex)
        {
            FileSystemHelpers.WriteBashError(this, ex.Message);
            SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
            _halt = true;
            return;
        }

        var machine = new AwkMachine(program, line => WriteObject(BashRuntime.NewBashObject(line + "\n")));

        if (fieldSep != null) machine.SetFieldSeparator(fieldSep);
        foreach (var assign in varAssignments)
        {
            int eq = assign.IndexOf('=');
            if (eq > 0)
            {
                string name = assign.Substring(0, eq);
                string value = ProcessEscapes(assign.Substring(eq + 1));
                machine.SetVarInitial(name, AwkValue.StrNum(value));
            }
        }

        _machine = machine;
        _program = program;
        _files = files;
        _stdinMode = files.Count == 0;
        _bufferStdin = program.UsesMainInput;

        _shell = new AwkShell(this);
        machine.Shell = _shell;
        machine.ResolvePath = ResolveGetlineFile;

        if (_stdinMode && _bufferStdin)
        {
            // Mode 2: everything (BEGIN included) runs in EndProcessing over the buffered records.
            var source = new AwkMainInput(Array.Empty<string>(), _ => null, machine.StartFile, _stdinBuffer);
            machine.MainInput = source;
            machine.StdinInput = source;
            return;
        }

        if (!_stdinMode)
        {
            // Mode 3. `getline < "-"` reads the pipeline, which is only collected (ProcessRecord)
            // when the program can ask for it — and then BEGIN waits for it too (EndProcessing), so a
            // BEGIN-time `getline < "-"` sees the piped records.
            machine.MainInput = new AwkMainInput(files, OpenOperand, machine.StartFile, null);
            if (_bufferStdin)
            {
                machine.StdinInput = new AwkListSource(_stdinBuffer);
                return;
            }
        }

        // BEGIN runs before the first record. A runtime fault (bad dynamic regex, field index past
        // the ceiling, a regex that blows the match-time budget) must surface as an awk error with
        // whatever output was already produced flushed — never an unhandled .NET exception, which
        // would tear down the shared host runspace.
        Guarded(() =>
        {
            machine.RunBegin();
            if (!machine.Exited && _stdinMode) machine.StartFile("");
        });
    }

    protected override void ProcessRecord()
    {
        if (_halt || _machine is null || InputObject is null) return;
        var machine = _machine;

        if (_bufferStdin)
        {
            // Modes 2 and 3: collect; the machine pulls from the buffer in EndProcessing / via getline.
            _stdinBuffer.AddRange(SplitRecords(InputObject));
            return;
        }
        if (!_stdinMode || machine.Exited) return;

        Guarded(() =>
        {
            foreach (var record in SplitRecords(InputObject))
            {
                machine.ProcessRecord(record);
                if (machine.Exited) break;
            }
        });
    }

    protected override void EndProcessing()
    {
        if (_machine is null || _program is null) return;
        var machine = _machine;
        var program = _program;

        try
        {
            if (_halt) return; // finally still closes whatever was opened before the fault

            bool ok = Guarded(() =>
            {
                if (_bufferStdin) machine.RunBegin(); // deferred: see the INPUT MODES note

                // Files / buffered stdin are pulled through the machine's own main input, so a getline
                // inside a rule and this loop consume ONE stream. A program with neither a main rule nor
                // END never reads its input (gawk: `awk 'BEGIN{...}' missing-file` is not an error).
                if (!machine.Exited && !(_stdinMode && !_bufferStdin) && (program.Main.Count > 0 || program.End.Count > 0))
                {
                    var input = machine.MainInput!;
                    string? record;
                    while (!machine.Exited && (record = input.Next()) is not null)
                        machine.ProcessRecord(record);
                }

                machine.RunEnd();
                machine.CloseAll(); // output pipes run here; their output precedes the last held stdout lines
            });
            if (!ok) return;

            machine.Flush();

            int exit = machine.ExitCode != 0 ? machine.ExitCode : _fileError;
            SessionState.PSVariable.Set("global:LASTEXITCODE", exit);
        }
        finally
        {
            // Faulted / halted runs: still flush and close every output file, run the output pipes
            // (a second CloseAll after a clean one finds nothing left to do).
            Guarded2(machine.CloseAll);
        }
    }

    /// <summary>Run a final cleanup step; an awk fault inside it is reported like any other, never thrown.</summary>
    private void Guarded2(Action body)
    {
        try { body(); }
        catch (AwkInterpreter.AwkRuntimeException ex)
        {
            FileSystemHelpers.WriteBashError(this, $"awk: {ex.Message}");
            SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
        }
    }

    protected override void StopProcessing()
    {
        // Ctrl-C / host stop: a getline blocked on a running command must not outlive the pipeline,
        // and EndProcessing will not run — release the output files and drop pipe temp data here.
        _shell?.KillAll();
        _machine?.AbortOutputs();
    }

    /// <summary>
    /// Open one file operand for the main input: an enumerator over its records, or null (reported,
    /// exit status 2) when it does not exist or cannot be named. The input then skips to the next operand.
    /// </summary>
    private IEnumerator<string>? OpenOperand(string file)
    {
        string resolved;
        try { resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(file); }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"awk: can't open file {file}: {ex.Message}");
            _fileError = 2;
            return null;
        }
        if (!File.Exists(resolved))
        {
            FileSystemHelpers.WriteBashError(this, $"awk: fatal: cannot open file `{file}' for reading: No such file or directory");
            _fileError = 2;
            return null;
        }
        return BashFileSystem.ReadLines(resolved).GetEnumerator();
    }

    /// <summary>A getline file name as a path: relative names resolve against the PowerShell location.</summary>
    private string ResolveGetlineFile(string name)
    {
        try { return SessionState.Path.GetUnresolvedProviderPathFromPSPath(name); }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            return name;
        }
    }

    /// <summary>Run <paramref name="body"/>, mapping awk runtime faults to an awk error + exit 2.
    /// Returns false (and halts further processing) when it faulted.</summary>
    private bool Guarded(Action body)
    {
        if (_halt) return false;
        try
        {
            body();
            return true;
        }
        catch (AwkInterpreter.AwkRuntimeException ex)
        {
            _machine?.Flush();
            FileSystemHelpers.WriteBashError(this, $"awk: {ex.Message}");
            SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
            _halt = true;
            return false;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            _machine?.Flush();
            FileSystemHelpers.WriteBashError(this, "awk: regular expression match timed out");
            SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
            _halt = true;
            return false;
        }
    }

    private static void AddOperand(string arg, ref string? programText, List<string> programFiles, List<string> files)
    {
        if (programFiles.Count == 0 && programText == null) programText = arg;
        else files.Add(arg);
    }

    /// <summary>
    /// Records from ONE pipeline item. In ps-bash's model each pipeline object is one
    /// record — but typed objects (LsEntry, CatLine, …) carry a bare
    /// <c>BashText</c> with no trailing newline, while text sources (printf /
    /// echo -e) carry one with a trailing newline (and may pack several lines
    /// into one object). So per item: drop a single trailing line terminator,
    /// then split any remaining embedded newlines. This keeps <c>ls | awk</c>
    /// (objects without newlines) from being concatenated into one record while
    /// still splitting a multi-line text object into multiple records.
    /// </summary>
    private static IEnumerable<string> SplitRecords(PSObject item)
    {
        string text = BashRuntime.GetBashText(item);
        if (text.Length == 0) yield break;
        // Drop exactly one trailing line terminator (the object's own line break).
        if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 2);
        else if (text[^1] == '\n' || text[^1] == '\r') text = text.Substring(0, text.Length - 1);

        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            yield return text.Substring(start, end - start);
            start = i + 1;
        }
        yield return start == 0 ? text : text.Substring(start);
    }

    /// <summary>
    /// Process C-style escapes in <c>-F</c> / <c>-v</c> values (awk does this for
    /// command-line FS and variable assignments, e.g. <c>-F'\t'</c> → a tab).
    /// </summary>
    private static string ProcessEscapes(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            char nx = s[++i];
            switch (nx)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                default: sb.Append('\\'); sb.Append(nx); break;
            }
        }
        return sb.ToString();
    }
}
