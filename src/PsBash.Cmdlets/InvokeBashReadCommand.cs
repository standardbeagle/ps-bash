using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRead</c> function
/// (REFACTOR-2 follow-on). Implements the bash <c>read</c> builtin: read one
/// line of input into one or more shell variables in the caller's scope.
///
/// Behavioral parity oracle: the original psm1 function. Pipeline input is
/// preferred when present (the oracle's <c>@($input)</c> collection branch);
/// otherwise the cmdlet falls back to <c>[Console]::In.ReadLine()</c> — the
/// PTY-safe slave-fd read that does not require <c>Read-Host</c>
/// (<c>ExitTrackingHost.ReadLine()</c> throws <c>NotSupportedException</c>).
///
/// Flag surface (oracle parity + task spec):
/// <list type="bullet">
/// <item><c>-r</c>: raw — do not process backslash escapes (the oracle ignored
/// backslash escapes already; the flag is accepted as a no-op for parity).</item>
/// <item><c>-p PROMPT</c>: prompt written to stdout before reading (oracle's
/// <c>[Console]::Out.Write("${prompt}: ")</c> branch).</item>
/// <item><c>-a ARR</c>: read whitespace-split words into the named indexed
/// array variable (cmdlet addition; the oracle treated <c>-a NAME</c> as just
/// another single variable name).</item>
/// <item><c>-n N</c>: read at most N characters (added per task spec).</item>
/// <item><c>-N N</c>: read exactly N characters (added per task spec).</item>
/// <item><c>-t T</c>: timeout in seconds (added per task spec). On timeout,
/// returns with exit code 1 and no variable assignment.</item>
/// <item><c>-s</c>: silent — accepted as a no-op (PSCmdlet pipeline cannot
/// suppress terminal echo independently of the host).</item>
/// </list>
///
/// Colliding flags declared as explicit parameters (playbook collision table):
/// <list type="bullet">
/// <item><c>-p PROMPT</c>: bare <c>-p</c> prefix-matches <c>-PipelineVariable</c>
/// / <c>-ProgressAction</c>. Declared as <c>string? P</c>.</item>
/// <item><c>-a ARR</c>: bare <c>-a</c> prefix-matches the cmdlet's own
/// <c>-Arguments</c> catch-all. Declared as <c>string? A</c>.</item>
/// </list>
/// <c>-r</c>, <c>-n</c>, <c>-N</c>, <c>-t</c>, <c>-s</c> have no PowerShell
/// common-parameter prefix overlap and stay in <c>Arguments</c>.
///
/// Variables are written via
/// <see cref="PSVariableIntrinsics.Set(string, object)"/> (the runspace-scope
/// equivalent of the oracle's <c>Set-Variable -Scope 1</c>) plus
/// <c>BashVariableStore.Set</c> so the emitted
/// <c>$env:NAME</c> expansion that follows a <c>read NAME</c> in transpiled
/// bash resolves the just-read value (oracle parity — it set both).
///
/// Directive 12: each destination variable name is checked for PowerShell
/// scriptblock metacharacters (<c>$ ( ; { ` "</c>) before assignment; a hit
/// emits a bash-style <c>read: '<NAME>': not a valid identifier</c> error
/// and skips the assignment, defeating injection through the variable-name
/// path.
///
/// Exit code: 0 on success, 1 on EOF / timeout / invalid identifier.
/// No stdout (variable side-effect only).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRead")]
public sealed class InvokeBashReadCommand : PSCmdlet
{
    /// <summary>
    /// <c>-p PROMPT</c> — write PROMPT (followed by ": ") to stdout before
    /// reading. Declared explicitly because the bare token <c>-p</c>
    /// prefix-matches <c>-PipelineVariable</c> / <c>-ProgressAction</c>.
    /// </summary>
    [Parameter]
    public string? P { get; set; }

    /// <summary>
    /// <c>-a ARR</c> — destination array variable name (whitespace-split).
    /// Declared explicitly because the bare token <c>-a</c> prefix-matches
    /// the cmdlet's own <c>-Arguments</c> parameter under the cmdlet binder.
    /// </summary>
    [Parameter]
    public string? A { get; set; }

    /// <summary>
    /// <c>-d DELIM</c> — read up to DELIM (NUL when empty) instead of a newline. Declared explicitly because the bare
    /// token <c>-d</c> prefix-matches <c>-Debug</c>.
    /// </summary>
    [Parameter]
    public string? D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    /// <summary>
    /// Upper bound (ms) on a no-<c>-t</c> read from REDIRECTED stdin (a pipe/file or a
    /// non-interactive host). A real producer's line arrives in &lt;1 ms, so this never
    /// fires in normal use; it only stops an input source that never produces (e.g. a
    /// test host whose stdin stays open and empty) from hanging forever. Overridable via
    /// <c>PSBASH_READ_REDIRECT_TIMEOUT_MS</c>. Does NOT apply to a real interactive TTY,
    /// which blocks indefinitely so the user can type.
    /// </summary>
    private static int RedirectedReadTimeoutMs
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("PSBASH_READ_REDIRECT_TIMEOUT_MS");
            return int.TryParse(raw, out var v) && v > 0 ? v : 2000;
        }
    }

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipeline.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (FileSystemHelpers.TryHandleVersion(this, "read", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "read"))
            {
                WriteObject(line);
            }
            return;
        }

        string? prompt = P;
        string? arrayName = A;
        int? maxChars = null;
        int? exactChars = null;
        double? timeoutSecs = null;
        char? recordDelim = D is null ? null : D.Length == 0 ? '\0' : D[0];   // -d DELIM: read up to DELIM (NUL for an empty DELIM) instead of a line
        bool recordTerminated = true;
        int? fromFd = null;   // -u N: read from descriptor N (opened by `exec N<file`) instead of stdin
        var varNames = new List<string>();

        // De-bundle short flags: `-ra` -> `-r` `-a`. Without this a bundled value
        // flag like the array flag in `read -ra parts` was unrecognized (it starts
        // with '-' so it was not a var name either), so `-a` never fired and the
        // whole line landed in a scalar `parts`. bash puts a value-taking flag
        // last in a bundle; its value is the following argument (handled below).
        // A value flag may carry its value in the same token (`-rn1`, `-n5`, `-d,`): split it off.
        var debundled = new List<string>();
        bool raw = false;
        foreach (var a in args)
        {
            if (a.Length > 2 && a[0] == '-' && a[1] != '-' && char.IsLetter(a[1]))
            {
                for (int k = 1; k < a.Length; k++)
                {
                    debundled.Add("-" + a[k]);
                    if ("pandNtu".IndexOf(a[k]) >= 0)
                    {
                        if (k + 1 < a.Length) debundled.Add(a.Substring(k + 1));
                        break;
                    }
                }
            }
            else
                debundled.Add(a);
        }
        args = debundled.ToArray();

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a == "-r") { raw = true; i++; continue; }
            if (a == "-s") { i++; continue; }
            if (a == "-p" && i + 1 < args.Length) { prompt = args[i + 1]; i += 2; continue; }
            if (a == "-a" && i + 1 < args.Length) { arrayName = args[i + 1]; i += 2; continue; }
            if ((a == "-n" || a == "-N") && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out var n) || n < 0)
                {
                    FileSystemHelpers.WriteBashError(this, $"read: {args[i + 1]}: invalid number");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
                if (a == "-n") maxChars = n; else exactChars = n;
                i += 2; continue;
            }
            if (a == "-d" && i + 1 < args.Length)
            {
                recordDelim = args[i + 1].Length == 0 ? '\0' : args[i + 1][0];
                i += 2; continue;
            }
            if (a == "-u" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out var ufd) || ufd < 0)
                {
                    FileSystemHelpers.WriteBashError(this, $"read: {args[i + 1]}: invalid file descriptor specification");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
                fromFd = ufd;
                i += 2; continue;
            }
            if (a == "-t" && i + 1 < args.Length)
            {
                if (double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t))
                    timeoutSecs = Math.Abs(t);   // bash takes the absolute value of a negative timeout
                i += 2; continue;
            }
            // Non-flag token: a destination variable name. Validate up-front
            // (Directive 12 — reject metacharacters that could re-parse as
            // PowerShell when the emitter later expands $NAME).
            if (!a.StartsWith('-'))
            {
                if (!IsValidIdentifier(a))
                {
                    FileSystemHelpers.WriteBashError(
                        this, $"read: `{a}': not a valid identifier");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
                varNames.Add(a);
            }
            i++;
        }

        // -a takes priority over positional names when both are present
        // (the oracle's behavior — -a was an additional name candidate; the
        // cmdlet promotes it to the sole/array destination).
        if (arrayName != null)
        {
            if (!IsValidIdentifier(arrayName))
            {
                FileSystemHelpers.WriteBashError(
                    this, $"read: `{arrayName}': not a valid identifier");
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
        }

        // Default variable name when none given is REPLY (bash default).
        bool noNames = varNames.Count == 0 && arrayName == null;   // `read` / `read -n 3`: REPLY keeps the line as read
        if (noNames)
        {
            varNames.Add("REPLY");
        }

        // `read -t 0` only asks whether input is available; it consumes nothing and assigns nothing.
        if (timeoutSecs == 0 && fromFd is null)
        {
            bool available = _pipeline.Count > 0 || SharedStdin.Get(SessionState) is not null || Console.IsInputRedirected;
            FileSystemHelpers.SetLastExitCode(this, available ? 0 : 1);
            return;
        }

        // Source the input line.
        string? inputLine;
        string pushBack = "";   // input read past what this call consumes (-n), returned to the front of stdin
        if (fromFd is int ufd2 && ufd2 > 2)
        {
            try
            {
                if (exactChars is int fdN) { inputLine = BashFdTable.ReadRecord(ufd2, null, fdN, out recordTerminated); }
                else if (recordDelim is char fdD) { inputLine = BashFdTable.ReadRecord(ufd2, fdD, -1, out recordTerminated); }
                else { inputLine = BashFdTable.ReadLine(ufd2); }
            }
            catch (InvalidOperationException ex)
            {
                FileSystemHelpers.WriteBashError(this, $"read: {ex.Message}");
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
        }
        else if (exactChars is int exactN && _pipeline.Count == 0
                 && SharedStdin.TryDequeueChars(SessionState, exactN, out var exactText, out var exactComplete))
        {
            // -N N: exactly N characters; neither a newline nor -d ends it.
            inputLine = exactText;
            recordTerminated = exactComplete;
        }
        else if (recordDelim is char rd && exactChars is null && _pipeline.Count == 0
                 && SharedStdin.TryDequeueRecord(SessionState, rd, out var record, out recordTerminated))
        {
            inputLine = record;
        }
        else if (_pipeline.Count > 0)
        {
            // Pipeline mode (here-document / here-string / piped records): the stream's bytes, one record per
            // line unless it is the unterminated tail. Take one line (or -d record, or -N characters) from it and
            // hand the rest back to the shared stdin for the next read.
            string text = BashRuntime.RecordStreamText(_pipeline).Replace("\r\n", "\n");
            string rest = "";
            if (exactChars is int en)
            {
                recordTerminated = text.Length >= en;
                if (text.Length > en) { rest = text.Substring(en); text = text.Substring(0, en); }
                inputLine = text;
            }
            else if (recordDelim is char pd)
            {
                // -d DELIM: up to the first DELIM.
                int at = text.IndexOf(pd);
                recordTerminated = at >= 0;
                if (at >= 0) { rest = text.Substring(at + 1); text = text.Substring(0, at); }
                inputLine = text;
            }
            else
            {
                int pos = 0;
                var lineSb = new System.Text.StringBuilder();
                while (true)
                {
                    int at = text.IndexOf('\n', pos);
                    if (at < 0) { lineSb.Append(text, pos, text.Length - pos); recordTerminated = false; break; }
                    lineSb.Append(text, pos, at - pos);
                    pos = at + 1;
                    recordTerminated = true;
                    if (!raw && EndsWithOddBackslashes(lineSb)) { lineSb.Length--; continue; }   // backslash-newline continues the line
                    break;
                }
                rest = text.Substring(pos);
                inputLine = lineSb.ToString();
            }
            if (rest.Length > 0) pushBack = rest;
        }
        else if (recordDelim is null && exactChars is null && TryDequeueSharedStdinLine(raw, out var queuedLine, out recordTerminated))
        {
            // A brace group used as a pipe target drains the incoming pipeline
            // into $global:__BashStdIn (the emitter's BraceGroupStdinPrelude), so
            // `echo hi | { read y; ...; }` reaches this branch. Consume ONE line —
            // bash's `read` takes a single line from the group's fd 0.
            inputLine = queuedLine;
        }
        else
        {
            recordTerminated = true;
            // Interactive fallback — write prompt then [Console]::In.ReadLine().
            // Task spec: preserve the oracle's PTY-safe slave-fd read path.
            if (prompt != null)
            {
                Console.Out.Write($"{prompt}: ");
                Console.Out.Flush();
            }

            try
            {
                if (timeoutSecs.HasValue)
                {
                    // Read with timeout via async task race.
                    var readTask = System.Threading.Tasks.Task.Run(() => Console.In.ReadLine());
                    var ms = (int)(timeoutSecs.Value * 1000);
                    if (!readTask.Wait(ms))
                    {
                        // Timeout — exit 1, no assignment.
                        FileSystemHelpers.SetLastExitCode(this, 1);
                        return;
                    }
                    inputLine = readTask.Result;
                }
                else if (Console.IsInputRedirected)
                {
                    // Redirected stdin (a real pipe/file, OR a non-interactive host such as
                    // the xUnit test runspace whose stdin stays open with no data). A genuine
                    // producer delivers its line and EOFs within milliseconds; a no-data
                    // source would otherwise BLOCK FOREVER on ReadLine and hang the whole
                    // process (this is what wedged the test suite). Bound the wait so the
                    // no-data case resolves to EOF (exit 1) instead of hanging. A real
                    // interactive TTY takes the unbounded branch below so the user can type.
                    var readTask = System.Threading.Tasks.Task.Run(() => Console.In.ReadLine());
                    if (!readTask.Wait(RedirectedReadTimeoutMs))
                    {
                        FileSystemHelpers.SetLastExitCode(this, 1);
                        return;
                    }
                    inputLine = readTask.Result;
                }
                else
                {
                    // Real interactive console — block for the user.
                    inputLine = Console.In.ReadLine();
                }
            }
            catch (InvalidOperationException)
            {
                // Console handle closed mid-read — treat as EOF.
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
            catch (NotSupportedException)
            {
                // Some hosts (e.g. the interactive ExitTrackingHost) throw on a console
                // read — treat as EOF rather than crashing.
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
        }

        if (inputLine == null)
        {
            // EOF.
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        // -n N: at most N characters; what was read past them stays in the input for the next read.
        if (maxChars.HasValue && exactChars is null && inputLine.Length >= maxChars.Value && fromFd is null)
        {
            string tail = inputLine.Substring(maxChars.Value) + (recordTerminated ? (recordDelim ?? '\n').ToString() : "");
            inputLine = inputLine.Substring(0, maxChars.Value);
            recordTerminated = true;
            if (tail.Length > 0) pushBack = tail + pushBack;
        }
        if (pushBack.Length > 0) SharedStdin.PushFront(SessionState, pushBack);

        // Assign.
        if (arrayName != null)
        {
            // -a ARR: split the line into an indexed array on $IFS (bash uses IFS,
            // not just whitespace — `IFS=, read -ra parts` must split on commas).
            string[] parts = SplitByIfs(inputLine, BashVariableStore.Get("IFS"));
            AssignVariable(arrayName, parts);
            // Also assign positional names (oracle parity — `-a` did not
            // suppress positional name assignment, though typical usage has
            // none alongside).
        }

        if (noNames)
        {
            // REPLY gets the line exactly as read: no IFS trimming or splitting (backslash escapes still apply without -r).
            AssignVariable("REPLY", raw ? inputLine : RemoveBackslashEscapes(inputLine));
        }
        else if (varNames.Count == 1)
        {
            // A lone variable still has the IFS WHITESPACE trimmed from both ends (`read x <<< "  a  "` is `a`);
            // `IFS= read` (empty IFS) keeps the line exactly.
            string? ifsValue = BashVariableStore.Get("IFS");
            char[] ifsWhitespace = (ifsValue ?? " \t\n").Where(ch => ch is ' ' or '\t' or '\n').ToArray();
            string one = ifsWhitespace.Length == 0 ? inputLine : inputLine.Trim(ifsWhitespace);
            AssignVariable(varNames[0], raw ? one : RemoveBackslashEscapes(one));
        }
        else if (varNames.Count > 1)
        {
            // Multi-variable: split the line on $IFS into at most varNames.Count
            // fields, with the last field keeping every remaining character
            // (separators included) — bash `read a b` on `1,2,3` gives b="2,3".
            var parts = SplitByIfsForRead(inputLine, BashVariableStore.Get("IFS"), varNames.Count);
            for (int j = 0; j < varNames.Count; j++)
                AssignVariable(varNames[j], j < parts.Length ? parts[j] : "");
        }

        FileSystemHelpers.SetLastExitCode(this, recordTerminated ? 0 : 1);   // -d: the stream ended before the delimiter
    }

    /// <summary>
    /// Dequeue one line from the shared bash stdin queue (<c>$global:__BashStdIn</c>)
    /// when a brace-group pipe stage populated it. Returns false when the queue is
    /// absent, empty, or not a queue, so the caller falls through to the console
    /// read. The cast goes through <see cref="PSObject.Base(object)"/> so a
    /// PSObject-wrapped value still resolves.
    /// </summary>
    private bool TryDequeueSharedStdinLine(bool raw, out string? line, out bool terminated)
    {
        if (!SharedStdin.TryDequeueLine(SessionState, out line, out terminated)) return false;
        // Without -r a trailing backslash joins the next line.
        var sb = new System.Text.StringBuilder(line);
        while (!raw && terminated && EndsWithOddBackslashes(sb))
        {
            sb.Length--;
            if (!SharedStdin.TryDequeueLine(SessionState, out var next, out terminated)) { terminated = false; break; }
            sb.Append(next);
        }
        line = sb.ToString();
        return true;
    }

    private static bool EndsWithOddBackslashes(System.Text.StringBuilder sb)
    {
        int n = 0;
        for (int k = sb.Length - 1; k >= 0 && sb[k] == '\\'; k--) n++;
        return (n & 1) == 1;
    }

    /// <summary>Without <c>-r</c>, a backslash quotes the next character and is dropped (a lone trailing one too).</summary>
    private static string RemoveBackslashEscapes(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\') { if (k + 1 < s.Length) sb.Append(s[++k]); }
            else sb.Append(s[k]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Assign value to a variable in the caller's scope plus the process
    /// environment block (oracle parity — the psm1 oracle set both
    /// <c>Set-Variable</c> and <c>Set-Item Env:NAME</c>, so subsequent
    /// <c>$env:NAME</c> expansions in transpiled bash see the value).
    /// </summary>
    private void AssignVariable(string name, object value)
    {
        FileSystemHelpers.SetShellVariable(SessionState, name, value);
        // Mirror the oracle's Set-Item Env:$name slice. For array values,
        // join with spaces — bash exports arrays as scalars too.
        string envVal = value switch
        {
            string s => s,
            string[] arr => string.Join(' ', arr),
            _ => value?.ToString() ?? ""
        };
        try { BashVariableStore.Set(name, envVal); }
        catch { /* env-set may fail for restricted names; non-fatal */ }
    }

    /// <summary>
    /// Split a line into fields the way bash does using <c>$IFS</c>:
    /// <list type="bullet">
    /// <item>unset IFS → default: split on runs of whitespace, ignoring leading/
    /// trailing whitespace;</item>
    /// <item>empty IFS → no splitting (one field = the whole line);</item>
    /// <item>otherwise → whitespace IFS chars fold (runs collapse, ends trimmed)
    /// while each non-whitespace IFS char is its own delimiter that preserves
    /// empty fields between adjacent delimiters (so <c>IFS=,</c> on <c>"a,,c"</c>
    /// yields <c>a</c>, <c>""</c>, <c>c</c>).</item>
    /// </list>
    /// </summary>
    internal static string[] SplitByIfs(string line, string? ifs)
    {
        if (ifs is null)
            return line.Length == 0 ? Array.Empty<string>()
                : System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s+");
        if (ifs.Length == 0)
            return line.Length == 0 ? Array.Empty<string>() : new[] { line };

        var ws = new System.Text.StringBuilder();
        var nonWs = new System.Text.StringBuilder();
        foreach (var c in ifs)
            (char.IsWhiteSpace(c) ? ws : nonWs).Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));

        string wsCls = ws.Length > 0 ? "[" + ws + "]" : null!;
        if (line.Length == 0) return Array.Empty<string>();

        if (nonWs.Length == 0)
        {
            // IFS is all whitespace: fold runs, trim ends (the default shape).
            var trimmed = System.Text.RegularExpressions.Regex.Replace(
                line, $"^{wsCls}+|{wsCls}+$", "");
            return trimmed.Length == 0 ? Array.Empty<string>()
                : System.Text.RegularExpressions.Regex.Split(trimmed, wsCls + "+");
        }

        // Mixed/non-whitespace IFS: a delimiter is a non-ws IFS char optionally
        // padded by whitespace IFS, OR a run of whitespace IFS. Trim leading/
        // trailing whitespace-IFS first so it doesn't create phantom end fields.
        string body = wsCls is null ? line
            : System.Text.RegularExpressions.Regex.Replace(line, $"^{wsCls}+|{wsCls}+$", "");
        string nonWsCls = "[" + nonWs + "]";
        string delim = wsCls is null
            ? nonWsCls
            : $"{wsCls}*{nonWsCls}{wsCls}*|{wsCls}+";
        return System.Text.RegularExpressions.Regex.Split(body, delim);
    }

    /// <summary>
    /// Split a line into AT MOST <paramref name="count"/> fields for the
    /// multi-variable <c>read a b c</c> form. Unlike <see cref="SplitByIfs"/>
    /// (which returns every field), the split stops after
    /// <paramref name="count"/> − 1 delimiters; the final field keeps every
    /// remaining character, separators included — exactly bash's
    /// "last variable absorbs the rest" rule (<c>IFS=, read a b</c> on
    /// <c>1,2,3</c> gives <c>a=1</c>, <c>b=2,3</c>).
    ///
    /// Built on the same delimiter regex as <see cref="SplitByIfs"/> so the two
    /// cannot drift: leading/trailing whitespace-IFS is trimmed, a whitespace
    /// run is one delimiter, and a non-whitespace IFS char is its own delimiter.
    /// </summary>
    internal static string[] SplitByIfsForRead(string line, string? ifs, int count)
    {
        if (count <= 0) return Array.Empty<string>();
        if (count == 1) return line.Length == 0 ? Array.Empty<string>() : new[] { line };
        if (ifs is null)
            return line.Length == 0 ? Array.Empty<string>()
                : new System.Text.RegularExpressions.Regex(@"\s+").Split(line.Trim(), count);
        if (ifs.Length == 0)
            return line.Length == 0 ? Array.Empty<string>() : new[] { line };

        var ws = new System.Text.StringBuilder();
        var nonWs = new System.Text.StringBuilder();
        foreach (var c in ifs)
            (char.IsWhiteSpace(c) ? ws : nonWs).Append(
                System.Text.RegularExpressions.Regex.Escape(c.ToString()));

        string wsCls = ws.Length > 0 ? "[" + ws + "]" : null!;
        if (line.Length == 0) return Array.Empty<string>();

        if (nonWs.Length == 0)
        {
            var trimmed = System.Text.RegularExpressions.Regex.Replace(
                line, $"^{wsCls}+|{wsCls}+$", "");
            return trimmed.Length == 0 ? Array.Empty<string>()
                : new System.Text.RegularExpressions.Regex(wsCls + "+").Split(trimmed, count);
        }

        string body = wsCls is null ? line
            : System.Text.RegularExpressions.Regex.Replace(line, $"^{wsCls}+|{wsCls}+$", "");
        string nonWsCls = "[" + nonWs + "]";
        string delim = wsCls is null
            ? nonWsCls
            : $"{wsCls}*{nonWsCls}{wsCls}*|{wsCls}+";
        return new System.Text.RegularExpressions.Regex(delim).Split(body, count);
    }

    /// <summary>
    /// Bash-compatible identifier check plus the Directive-12 metacharacter
    /// rejection. A valid identifier starts with a letter or underscore and
    /// contains only letters, digits, and underscores.
    /// </summary>
    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Quick metacharacter scan — these would re-parse as PowerShell.
        foreach (var c in name)
        {
            if (c == '$' || c == '(' || c == ')' || c == ';' || c == '{' || c == '}'
                || c == '`' || c == '"' || c == '\'') return false;
        }
        // Strict POSIX identifier: [A-Za-z_][A-Za-z0-9_]*
        var first = name[0];
        if (!(char.IsLetter(first) || first == '_')) return false;
        for (int k = 1; k < name.Length; k++)
        {
            var c = name[k];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }
}
