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
/// FNR reset per file. File-open errors emit through the psm1
/// <c>Write-BashError</c> sink. Output is one BashObject per output line.
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

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipeline.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "awk", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "awk"))
                WriteObject(line);
            return;
        }

        string? fieldSep = null;
        var varAssignments = new List<string>();
        var programFiles = new List<string>();
        string? programText = null;
        var files = new List<string>();
        bool pastDoubleDash = false;

        if (V != null) varAssignments.AddRange(V);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (pastDoubleDash) { AddOperand(arg, ref programText, programFiles, files); continue; }
            if (arg == "--") { pastDoubleDash = true; continue; }

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
                    return;
                }
                if (!File.Exists(resolved))
                {
                    FileSystemHelpers.WriteBashError(this, $"awk: can't open source file {pf}: No such file or directory");
                    SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
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

        int fileError = 0;

        machine.RunBegin();
        if (!machine.Exited)
        {
            if (files.Count > 0)
            {
                foreach (var file in files)
                {
                    string resolved;
                    try { resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(file); }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        FileSystemHelpers.WriteBashError(this, $"awk: can't open file {file}: {ex.Message}");
                        fileError = 2;
                        continue;
                    }
                    if (!File.Exists(resolved))
                    {
                        FileSystemHelpers.WriteBashError(this, $"awk: can't open file {file}: No such file or directory");
                        fileError = 2;
                        continue;
                    }

                    machine.StartFile(file);
                    foreach (var record in BashFileSystem.ReadLines(resolved))
                    {
                        machine.ProcessRecord(record);
                        if (machine.Exited) break;
                    }
                    if (machine.Exited) break;
                }
            }
            else
            {
                machine.StartFile("");
                foreach (var record in PipelineRecords())
                {
                    machine.ProcessRecord(record);
                    if (machine.Exited) break;
                }
            }
        }

        machine.RunEnd();
        machine.Flush();

        int exit = machine.ExitCode != 0 ? machine.ExitCode : fileError;
        SessionState.PSVariable.Set("global:LASTEXITCODE", exit);
    }

    private static void AddOperand(string arg, ref string? programText, List<string> programFiles, List<string> files)
    {
        if (programFiles.Count == 0 && programText == null) programText = arg;
        else files.Add(arg);
    }

    /// <summary>
    /// Records from the pipeline, split on the default record separator (\n,
    /// with a trailing \r of a CRLF stripped). Streams a line at a time with a
    /// small carry-over buffer instead of materializing all input at once, so a
    /// large piped stream is bounded by one item + one partial line.
    /// </summary>
    private IEnumerable<string> PipelineRecords()
    {
        string leftover = "";
        foreach (var item in _pipeline)
        {
            string text = BashRuntime.GetBashText(item);
            if (text.Length == 0) continue;
            if (leftover.Length != 0) { text = leftover + text; leftover = ""; }
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                yield return text.Substring(start, end - start);
                start = i + 1;
            }
            if (start < text.Length) leftover = text.Substring(start);
        }
        if (leftover.Length > 0)
        {
            if (leftover[^1] == '\r') leftover = leftover[..^1];
            if (leftover.Length > 0) yield return leftover;
        }
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
