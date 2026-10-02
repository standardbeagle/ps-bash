using System.Collections;
using System.Collections.Specialized;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet <c>Invoke-BashJq</c>: jq 1.7 on .NET. The language is a real parser + generator-based evaluator
/// (<see cref="JqParser"/>, <see cref="JqInterp"/>: paths, assignment, <c>reduce</c>/<c>foreach</c>/<c>label</c>, destructuring and
/// <c>?//</c>, user functions with closures, string interpolation and <c>@formats</c>, regular expressions), the built-ins are
/// <see cref="JqNatives"/> plus <see cref="JqPrelude"/> (jq source), and numbers keep the text they were written as like jq 1.7
/// (<see cref="JqNumber"/>). This class is the command line: <see cref="JqOptions"/>, input reading (files / pipeline, raw, slurp, stream),
/// output layout, error reporting and exit statuses.
/// <para>
/// <b>Exit status</b>: 0; with <c>-e</c> 1 when the last output was <c>false</c>/<c>null</c>, 4 when there was none; 2 usage / file
/// problems; 3 compile errors (reported before any input is read); 5 runtime errors and malformed input; <c>halt_error</c>'s status.
/// </para>
/// <para>
/// <b>Not implemented</b> (exit 2): <c>-C</c> colour output and the date builtins; <c>--seq</c> input parsing, <c>$__prog_args</c>, modules,
/// <c>getpath/1</c>-based <c>limit</c> optimisations. INTENTIONAL DIFFERENCES: after a missing input file the later files are still
/// read completely; <c>repeat/1</c> reproduces the 1.7 binary the oracle ran (see <see cref="JqNatives"/>).
/// </para>
/// <para>
/// Argv comes verbatim (the transpiler single-quotes every dash word: <c>jq</c> is on <c>PsEmitter.OrderedArgCommands</c>); the
/// <c>C</c>/<c>E</c>/<c>A</c> decoy switches only exist for direct PowerShell calls and are re-injected as <c>-c</c>/<c>-e</c>/<c>-a</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashJq")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashJqCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Decoy for <c>-c</c> (compact): the bare token prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    /// <summary>Decoy for <c>-e</c> (exit status): the bare token is ambiguous between <c>-ErrorAction</c> and <c>-ErrorVariable</c>.</summary>
    [Parameter] public SwitchParameter E { get; set; }

    /// <summary>Decoy for <c>-a</c> (ASCII output): the bare token prefix-matches this cmdlet's own <c>-Arguments</c>.</summary>
    [Parameter] public SwitchParameter A { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipeline.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (C.IsPresent, "-c"), (E.IsPresent, "-e"), (A.IsPresent, "-a"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "jq")) WriteObject(line);
            return;
        }

        if (!JqOptions.TryParse(args, ReadSmallFile, out var options, out var usage))
        {
            Say(usage!.Message);
            FileSystemHelpers.SetLastExitCode(this, usage.ExitCode);
            return;
        }
        if (options.Help)
        {
            WriteObject(BashRuntime.NewBashObject(JqOptions.Usage + "\n"));
            return;
        }
        if (options.Version)
        {
            WriteObject(BashRuntime.NewBashObject("jq-1.7\n"));
            return;
        }

        int code;
        try { code = Run(options); }
        catch (JqHalt halt)
        {
            if (halt.Text != null) FileSystemHelpers.WriteStderr(this, halt.Text.TrimEnd('\n'));
            code = halt.Code;
        }
        FileSystemHelpers.SetLastExitCode(this, code);
    }

    private string ReadSmallFile(string name)
    {
        var path = SessionState.Path.GetUnresolvedProviderPathFromPSPath(name);
        if (!File.Exists(path)) throw new FileNotFoundException(name);
        return BashFileSystem.ReadAllTextRaw(path);
    }

    private void Say(string message) => FileSystemHelpers.WriteStderr(this, message);

    // ───────────── the run ─────────────

    private sealed class InputSource
    {
        public string? File;
        public string Text = "";
    }

    private int Run(JqOptions o)
    {
        // Compile (parse + name check) before any input is touched.
        var globals = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in o.Named) globals[(string)e.Key] = e.Value;
        var env = new OrderedDictionary();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = e.Value as string ?? "";
        // Windows names the variable "Path"; scripts written for jq on Linux ask for $ENV.PATH.
        if (OperatingSystem.IsWindows() && env.Contains("Path") && !env.Contains("PATH")) env["PATH"] = env["Path"];
        globals["ENV"] = env;
        globals["ARGS"] = new OrderedDictionary { ["positional"] = o.Positional.ToArray(), ["named"] = NamedObject(o.Named) };

        JNode program;
        try
        {
            program = JqParser.Parse(o.Program!);
            var errors = JqChecker.Check(program, globals.Keys);
            if (errors.Count > 0)
            {
                foreach (var (message, line) in errors) Say($"jq: error: {message} at <top-level>, line {line}:\n{o.Program}");
                Say($"jq: {errors.Count} compile error{(errors.Count == 1 ? "" : "s")}");
                return 3;
            }
        }
        catch (JqCompileException ex)
        {
            // a few messages come without the program echo
            string echo = ex.Message.StartsWith("Top-level program not given", StringComparison.Ordinal) ? "" : "\n" + o.Program;
            Say($"jq: error: {ex.Message}{echo}\njq: 1 compile error");
            return 3;
        }

        var sources = LoadSources(o, out bool missingFile);
        var inputs = ReadValues(o, sources).GetEnumerator();
        bool parseError = false;
        string? currentFile = null;
        // (-1, null) until the first input has been read: errors then say "<unknown>" like jq does under -n.
        (int Line, string? File) position = (-1, null);
        JqInterp? interpRef = null;

        (bool, object?) Next()
        {
            bool has;
            try { has = inputs.MoveNext(); }
            catch (JqJsonException ex)
            {
                Say($"jq: parse error: {ex.Message}");
                parseError = true;
                return (false, null);
            }
            if (!has) { position = (-1, null); return (false, null); }
            position = (inputs.Current.Line, inputs.Current.File);
            currentFile = inputs.Current.File;
            if (interpRef != null) { interpRef.InputLine = inputs.Current.Line; interpRef.CurrentFilename = currentFile ?? "<stdin>"; }
            return (true, inputs.Current.Value);
        }

        var interp = new JqInterp(Next, globals)
        {
            StderrSink = text => FileSystemHelpers.WriteStderr(this, text),
            DebugSink = text => FileSystemHelpers.WriteStderr(this, text),
        };
        interpRef = interp;

        var write = o.WriteOptions;
        object? last = null;
        bool anyOutput = false;

        // jq's exit status is the status of the LAST input processed (an error on an earlier input is forgotten once a later one succeeds);
        // 0 ok, 5 runtime error; -e then looks at the last output.
        int RunOne(object? input)
        {
            if (position.Line >= 0) interp.CurrentFilename = currentFile ?? "<stdin>";
            try
            {
                foreach (var result in interp.Run(program, JqEnvRoot, input))
                {
                    anyOutput = true;
                    last = result;
                    Emit(result, o, write);
                }
                return 0;
            }
            catch (JqError ex)
            {
                Say(ErrorLine(ex, position));
                return 5;
            }
            catch (JqBreak)
            {
                Say($"jq: error (at {PositionText(position)}): break");
                return 5;
            }
            catch (JqCompileException ex)
            {
                // a name only the runtime could resolve (the checker should have caught it)
                Say($"jq: error: {ex.Message} at <top-level>, line 1:\n{o.Program}\njq: 1 compile error");
                return 3;
            }
        }

        int status = 0;
        if (o.NullInput)
        {
            status = RunOne(null);
        }
        else
        {
            while (true)
            {
                var (ok, value) = Next();
                if (!ok) break;
                status = RunOne(value);
                if (status == 3) break;
            }
        }

        if (parseError) status = 5;
        if (missingFile) status = 2;
        if (status != 0) return status;
        if (o.ExitStatus)
        {
            if (!anyOutput) return 4;
            return JqValue.IsTruthy(last) ? 0 : 1;
        }
        return 0;
    }
    private static JEnv JqEnvRoot { get; } = new();

    private static OrderedDictionary NamedObject(OrderedDictionary named)
    {
        var d = new OrderedDictionary();
        foreach (DictionaryEntry e in named) d[e.Key] = e.Value;
        return d;
    }

    private static string PositionText((int Line, string? File) p) =>
        p.Line < 0 ? "<unknown>" : $"{p.File ?? "<stdin>"}:{p.Line}";

    private static string ErrorLine(JqError ex, (int Line, string? File) position)
    {
        string at = PositionText(position);
        if (ex.Value is string s) return $"jq: error (at {at}): {s}";
        return $"jq: error (at {at}) (not a string): {JqValue.ToCompactJson(ex.Value)}";
    }

    // ───────────── output ─────────────

    private void Emit(object? result, JqOptions o, JqValue.WriteOptions write)
    {
        // -a re-encodes through the JSON writer even with -r (a string stays quoted and escaped, as in jq 1.7)
        string text = o.RawOutput && !o.AsciiOutput && result is string s ? s : JqValue.ToJson(result, write);
        if (o.Seq) text = "\u001e" + text;
        WriteObject(BashRuntime.TextRecord(o.JoinOutput ? text : text + "\n", unterminated: o.JoinOutput));
    }

    // ───────────── input ─────────────

    private List<InputSource> LoadSources(JqOptions o, out bool missingFile)
    {
        missingFile = false;
        var sources = new List<InputSource>();
        if (o.Files.Count == 0)
        {
            sources.Add(new InputSource { File = null, Text = BashRuntime.RecordStreamText(_pipeline.Cast<object>()) });
            return sources;
        }
        foreach (var file in o.Files)
        {
            if (file == "-")
            {
                sources.Add(new InputSource { File = null, Text = BashRuntime.RecordStreamText(_pipeline.Cast<object>()) });
                continue;
            }
            string path;
            try { path = SessionState.Path.GetUnresolvedProviderPathFromPSPath(file); }
            catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
            {
                Say($"jq: error: Could not open {file}: {ex.Message}");
                missingFile = true;
                continue;
            }
            if (!File.Exists(path))
            {
                Say($"jq: error: Could not open {file}: No such file or directory");
                missingFile = true;
                continue;
            }
            try { sources.Add(new InputSource { File = file, Text = BashFileSystem.ReadAllTextRaw(path) }); }
            catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
            {
                Say($"jq: error: Could not open {file}: {ex.Message}");
                missingFile = true;
            }
        }
        return sources;
    }

    private readonly record struct InputValue(object? Value, string? File, int Line);

    /// <summary>The top-level values of every source, in order: JSON values, raw lines (<c>-R</c>), <c>--stream</c> events, or one slurped value.</summary>
    private static IEnumerable<InputValue> ReadValues(JqOptions o, List<InputSource> sources)
    {
        if (o.Slurp)
        {
            if (o.RawInput)
            {
                var sb = new StringBuilder();
                foreach (var s in sources) sb.Append(s.Text);
                yield return new InputValue(sb.ToString(), sources.LastOrDefault()?.File, CountLines(sb.ToString()));
                yield break;
            }
            var all = new List<object?>();
            string? lastFile = null;
            int lines = 0;
            foreach (var s in sources)
            {
                lastFile = s.File;
                var reader = new JqJsonReader(s.Text);
                while (reader.TryRead(out var v))
                {
                    all.Add(o.Stream ? null : v);
                    if (o.Stream) foreach (var ev in StreamEvents(v)) all.Add(ev);
                }
                lines = CountLines(s.Text);
            }
            if (o.Stream) all.RemoveAll(x => x == null && false);
            yield return new InputValue(o.Stream ? all.Where(x => x != null).ToArray() : all.ToArray(), lastFile, lines);
            yield break;
        }

        foreach (var s in sources)
        {
            if (o.RawInput)
            {
                int line = 0;
                int start = 0;
                string text = s.Text;
                while (start < text.Length)
                {
                    int nl = text.IndexOf('\n', start);
                    line++;
                    if (nl < 0) { yield return new InputValue(text.Substring(start), s.File, line - 1); break; }
                    yield return new InputValue(text.Substring(start, nl - start), s.File, line);
                    start = nl + 1;
                }
                continue;
            }
            var reader = new JqJsonReader(s.Text);
            while (true)
            {
                object? value;
                bool ok = reader.TryRead(out value);
                if (!ok) break;
                if (o.Stream)
                {
                    foreach (var ev in StreamEvents(value)) yield return new InputValue(ev, s.File, reader.LinesConsumedThroughLine);
                }
                else yield return new InputValue(value, s.File, reader.LinesConsumedThroughLine);
            }
        }
    }

    private static int CountLines(string text)
    {
        int n = 0;
        foreach (char c in text) if (c == '\n') n++;
        return n;
    }

    /// <summary><c>--stream</c> events of one value: <c>[path, leaf]</c> and closing <c>[path]</c> events (as <c>tostream</c>).</summary>
    private static IEnumerable<object?> StreamEvents(object? value)
    {
        var events = new List<object?>();
        void Walk(object? v, List<object?> path, bool top)
        {
            switch (v)
            {
                case IDictionary d when d.Count > 0:
                    {
                        object? lastKey = null;
                        foreach (var k in JqValue.Keys(d).ToList())
                        {
                            path.Add(k);
                            Walk(d[k], path, false);
                            path.RemoveAt(path.Count - 1);
                            lastKey = k;
                        }
                        var closing = new List<object?>(path) { lastKey };
                        events.Add(new object?[] { closing.ToArray() });
                        break;
                    }
                case object?[] a when a.Length > 0:
                    {
                        for (int i = 0; i < a.Length; i++)
                        {
                            path.Add(new JqNumber(i, i.ToString()));
                            Walk(a[i], path, false);
                            path.RemoveAt(path.Count - 1);
                        }
                        var closing = new List<object?>(path) { new JqNumber(a.Length - 1, (a.Length - 1).ToString()) };
                        events.Add(new object?[] { closing.ToArray() });
                        break;
                    }
                default:
                    events.Add(new object?[] { path.ToArray(), v });
                    break;
            }
        }
        Walk(value, new List<object?>(), true);
        return events;
    }
}
