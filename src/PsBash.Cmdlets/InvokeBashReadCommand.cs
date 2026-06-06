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
/// <c>Environment.SetEnvironmentVariable</c> so the emitted
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
        var varNames = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a == "-r" || a == "-s") { i++; continue; }
            if (a == "-p" && i + 1 < args.Length) { prompt = args[i + 1]; i += 2; continue; }
            if (a == "-a" && i + 1 < args.Length) { arrayName = args[i + 1]; i += 2; continue; }
            if (a == "-n" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var n)) maxChars = n;
                i += 2; continue;
            }
            if (a == "-N" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var n)) exactChars = n;
                i += 2; continue;
            }
            if (a == "-t" && i + 1 < args.Length)
            {
                if (double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t))
                    timeoutSecs = t;
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
        if (varNames.Count == 0 && arrayName == null)
        {
            varNames.Add("REPLY");
        }

        // Source the input line.
        string? inputLine;
        if (_pipeline.Count > 0)
        {
            // Pipeline mode — concatenate pipeline item text, strip trailing
            // newlines (oracle's `-replace "`r`n","`n" -replace "`n$",''` slice).
            var sb = new System.Text.StringBuilder();
            foreach (var item in _pipeline)
            {
                var text = BashRuntime.GetBashText(item);
                if (!string.IsNullOrEmpty(text)) sb.Append(text);
            }
            inputLine = sb.ToString().Replace("\r\n", "\n");
            if (inputLine.EndsWith('\n')) inputLine = inputLine.Substring(0, inputLine.Length - 1);
        }
        else
        {
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

        // Apply -n / -N character limits.
        if (exactChars.HasValue && inputLine.Length > exactChars.Value)
        {
            inputLine = inputLine.Substring(0, exactChars.Value);
        }
        else if (maxChars.HasValue && inputLine.Length > maxChars.Value)
        {
            inputLine = inputLine.Substring(0, maxChars.Value);
        }

        // Assign.
        if (arrayName != null)
        {
            // -a ARR: whitespace-split the line into an indexed array.
            string[] parts = inputLine.Length == 0
                ? Array.Empty<string>()
                : System.Text.RegularExpressions.Regex.Split(inputLine, @"\s+");
            AssignVariable(arrayName, parts);
            // Also assign positional names (oracle parity — `-a` did not
            // suppress positional name assignment, though typical usage has
            // none alongside).
        }

        if (varNames.Count == 1)
        {
            AssignVariable(varNames[0], inputLine);
        }
        else if (varNames.Count > 1)
        {
            // Multi-variable: whitespace-split; last variable gets the
            // remainder (joined with single space) — oracle parity.
            var parts = System.Text.RegularExpressions.Regex.Split(inputLine, @"\s+");
            for (int j = 0; j < varNames.Count; j++)
            {
                string val;
                if (j < varNames.Count - 1)
                {
                    val = j < parts.Length ? parts[j] : "";
                }
                else
                {
                    // Last variable gets the rest.
                    if (j < parts.Length)
                    {
                        var rest = new string[parts.Length - j];
                        Array.Copy(parts, j, rest, 0, rest.Length);
                        val = string.Join(" ", rest);
                    }
                    else
                    {
                        val = "";
                    }
                }
                AssignVariable(varNames[j], val);
            }
        }

        FileSystemHelpers.SetLastExitCode(this, 0);
    }

    /// <summary>
    /// Assign value to a variable in the caller's scope plus the process
    /// environment block (oracle parity — the psm1 oracle set both
    /// <c>Set-Variable</c> and <c>Set-Item Env:NAME</c>, so subsequent
    /// <c>$env:NAME</c> expansions in transpiled bash see the value).
    /// </summary>
    private void AssignVariable(string name, object value)
    {
        SessionState.PSVariable.Set(name, value);
        // Mirror the oracle's Set-Item Env:$name slice. For array values,
        // join with spaces — bash exports arrays as scalars too.
        string envVal = value switch
        {
            string s => s,
            string[] arr => string.Join(' ', arr),
            _ => value?.ToString() ?? ""
        };
        try { Environment.SetEnvironmentVariable(name, envVal); }
        catch { /* env-set may fail for restricted names; non-fatal */ }
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
