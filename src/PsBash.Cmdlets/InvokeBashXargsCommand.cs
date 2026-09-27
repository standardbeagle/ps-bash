using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashXargs</c>
/// (REFACTOR-2). Reads items from pipeline input, builds and runs a command
/// per item-batch.
///
/// Oracle parity: the real-bash oracle splits the DEFAULT input stream on
/// whitespace (blanks AND newlines), so `printf "a b c\n" | xargs -n1` runs
/// three times. <c>-0</c> (NUL-separated) and <c>-d DELIM</c> are the explicit
/// single-delimiter overrides. (The original psm1 oracle split on newlines
/// only — a divergence from GNU xargs that silently merged space-separated
/// tokens; corrected here.) <c>-I REPLACE</c> (replace-token mode —
/// run command once per input line with REPLACE substituted in each arg),
/// <c>-n N</c> (batch N items per invocation), <c>--</c> end-of-flags, and
/// the leading-word "if a runtime function named <c>Invoke-Bash{Cmd}</c>
/// exists, route through it" resolution. Default (no <c>-I</c> / <c>-n</c>):
/// all collected items joined as args to a single invocation. This cmdlet
/// reproduces every oracle branch byte-for-byte.
///
/// Beyond the oracle, the cmdlet also accepts <c>-r</c> /
/// <c>--no-run-if-empty</c> (skip the invocation entirely when no items
/// were read), <c>-t</c> (echo the command + args to stderr before each
/// run), and <c>-L N</c> (run command per N input lines — synonymous with
/// <c>-n N</c> in this implementation since the oracle's input is
/// already line-segmented). <c>-p</c> (interactive prompt), <c>-P N</c>
/// (parallel) are accepted but ignored — the oracle had no concept of
/// either, and a PowerShell runspace cannot prompt nor fork.
///
/// Pipeline-only — the oracle never accepted file operands; non-flag
/// positional tokens are always the command and its leading args.
///
/// Security (Directive 12): the command name and every arg are passed
/// positionally through
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// with a fixed parameterless script body — never concatenated into the
/// body. A token containing <c>;</c>, <c>$()</c>, scriptblock chars, or
/// backticks therefore cannot be re-parsed as PowerShell syntax; it is
/// looked up as a literal command name (or value) and passed through
/// unevaluated. No <see cref="ScriptBlock"/> construction; AOT-safe.
///
/// Flag collisions: <c>-I REPLACE</c> prefix-collides with
/// <c>-InformationAction</c> / <c>-InformationVariable</c>, declared as
/// value-bearing <c>string? I</c>. <c>-P N</c> prefix-collides with
/// <c>-PipelineVariable</c> / <c>-ProgressAction</c>, declared as nullable
/// <c>int? P</c>. <c>-n N</c>, <c>-L N</c>, <c>-r</c>, <c>-t</c>, <c>-0</c>,
/// <c>-p</c>, <c>--</c> have no PowerShell common-parameter prefix
/// collision and stay in <c>Arguments</c>, parsed by a manual scan
/// matching the oracle's <c>-ceq</c> dispatch (case-sensitive on the
/// numeric / NUL-separator forms).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashXargs")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashXargsCommand : PSCmdlet
{
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// <c>-I REPLACE</c> — value-bearing. Declared as a literal single-letter
    /// parameter so the binder routes the bare token by exact name match
    /// (beats the <c>-InformationAction</c> / <c>-InformationVariable</c>
    /// common-parameter prefix match).
    /// </summary>
    [Parameter]
    public string? I { get; set; }

    /// <summary>
    /// <c>-P N</c> — value-bearing. Declared literally as <c>P</c> for the
    /// same reason as <c>I</c> above (prefix-collides with
    /// <c>-PipelineVariable</c> / <c>-ProgressAction</c>). Accepted for
    /// argv compatibility but no parallelization is performed — the oracle
    /// did not implement it.
    /// </summary>
    [Parameter]
    public int? P { get; set; }

    /// <summary>
    /// <c>-d DELIM</c> — value-bearing input-item delimiter. Declared literally
    /// as <c>D</c> because the bare token <c>-d</c> prefix-collides with the
    /// <c>-Debug</c> common parameter. The joined <c>-dDELIM</c> and
    /// <c>--delimiter=</c> forms arrive via <see cref="Arguments"/>.
    /// </summary>
    [Parameter]
    public string? D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private readonly List<PSObject?> _pipelineItems = new();

    /// <summary>
    /// GNU exit-status aggregation across all invocations. 0 = all succeeded.
    /// 1..125 from a child maps to 123; 255 maps to 124; command-not-found maps
    /// to 127; command-found-but-not-runnable maps to 126. The first failing
    /// invocation sticks (matching GNU, which reports the aggregate, not the
    /// last child).
    /// </summary>
    private int _aggregateExit;

    protected override void ProcessRecord()
    {
        _pipelineItems.Add(InputObject);
    }

    /// <summary>Expand GNU xargs -d backslash escapes (\n \t \r \0 \\); other
    /// chars pass through, so a literal delimiter like "," is returned as-is.</summary>
    private static string ExpandDelimEscapes(string d)
    {
        if (d.IndexOf('\\') < 0) return d;
        var sb = new System.Text.StringBuilder(d.Length);
        for (int i = 0; i < d.Length; i++)
        {
            if (d[i] == '\\' && i + 1 < d.Length)
            {
                char n = d[++i];
                sb.Append(n switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', '\\' => '\\', _ => n,
                });
            }
            else
            {
                sb.Append(d[i]);
            }
        }
        return sb.ToString();
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "xargs", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "xargs"))
            {
                WriteObject(line);
            }
            return;
        }

        string? replaceStr = I;  // -I VALUE (declared param) or -IREPLACE joined
        int maxArgs = 0;
        int maxLines = 0;
        bool nullDelim = false;
        bool noRunIfEmpty = false;
        bool traceCmd = false;
        string? customDelim = D;  // -d DELIM (also set by -dDELIM / --delimiter=)
        // P is accepted via parameter binding but otherwise unused.
        _ = P;

        var operands = new List<string>();
        bool pastDoubleDash = false;

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                i++;
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-0", System.StringComparison.Ordinal) ||
                string.Equals(arg, "--null", System.StringComparison.Ordinal))
            {
                nullDelim = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-r", System.StringComparison.Ordinal) ||
                string.Equals(arg, "--no-run-if-empty", System.StringComparison.Ordinal))
            {
                noRunIfEmpty = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-t", System.StringComparison.Ordinal) ||
                string.Equals(arg, "--verbose", System.StringComparison.Ordinal))
            {
                traceCmd = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-p", System.StringComparison.Ordinal) ||
                string.Equals(arg, "--interactive", System.StringComparison.Ordinal))
            {
                // Interactive prompt — accepted but ignored (the oracle had
                // no concept of it and a PS runspace cannot prompt).
                i++;
                continue;
            }

            // -d DELIM / -dDELIM / --delimiter=DELIM / --delimiter DELIM.
            if (string.Equals(arg, "-d", System.StringComparison.Ordinal)
                || string.Equals(arg, "--delimiter", System.StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length) customDelim = args[i];
                i++;
                continue;
            }
            if (arg.Length > 2 && arg.StartsWith("-d", System.StringComparison.Ordinal))
            {
                customDelim = arg.Substring(2);
                i++;
                continue;
            }
            if (arg.StartsWith("--delimiter=", System.StringComparison.Ordinal))
            {
                customDelim = arg.Substring("--delimiter=".Length);
                i++;
                continue;
            }

            if (string.Equals(arg, "-I", System.StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length) replaceStr = args[i];
                i++;
                continue;
            }

            // -IREPLACE joined form (oracle: arg.Length > 2 && arg.StartsWith("-I")).
            if (arg.Length > 2 && arg.StartsWith("-I", System.StringComparison.Ordinal))
            {
                replaceStr = arg.Substring(2);
                i++;
                continue;
            }

            // -i[REPLACE] — GNU's obsolete replacement form. Bare `-i` means
            // `-I {}`; `-iTOK` sets TOK as the replace string. It is
            // case-sensitive against `-I` and is routed here as a whole token
            // (the emitter force-quotes it, since a declared string `I`
            // parameter would otherwise swallow the following command token).
            // It NEVER consumes a separate argument.
            if (string.Equals(arg, "-i", System.StringComparison.Ordinal))
            {
                replaceStr = "{}";
                i++;
                continue;
            }
            if (arg.Length > 2 && arg[0] == '-' && arg[1] == 'i')
            {
                replaceStr = arg.Substring(2);
                i++;
                continue;
            }

            if (string.Equals(arg, "-n", System.StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out var n)) maxArgs = n;
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-n", System.StringComparison.Ordinal))
            {
                var tail = arg.Substring(2);
                if (int.TryParse(tail, out var n)) maxArgs = n;
                i++;
                continue;
            }

            if (string.Equals(arg, "-L", System.StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out var n)) maxLines = n;
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-L", System.StringComparison.Ordinal))
            {
                var tail = arg.Substring(2);
                if (int.TryParse(tail, out var n)) maxLines = n;
                i++;
                continue;
            }

            // -PN joined form for accepted-but-ignored parallel flag.
            if (arg.Length > 2 && arg.StartsWith("-P", System.StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        // GNU: with no command operand, xargs runs `echo`. The old
        // "no command specified" error diverged from every real xargs.
        if (operands.Count == 0)
        {
            operands.Add("echo");
        }

        // Resolve command: if the leading token matches an Invoke-Bash*
        // function, route through it (oracle parity).
        var cmd = operands[0];
        var bashCmdCandidate = "Invoke-Bash" +
            char.ToUpperInvariant(cmd[0]) + cmd.Substring(1);
        bool bashCmdExists = false;
        try
        {
            var probe = InvokeCommand.InvokeScript(
                "param($n) [bool](Get-Command $n -ErrorAction SilentlyContinue)",
                bashCmdCandidate);
            foreach (var p in probe)
            {
                var baseObj = p is PSObject pso ? pso.BaseObject : p;
                if (baseObj is bool b && b) { bashCmdExists = true; break; }
            }
        }
        catch
        {
            bashCmdExists = false;
        }
        if (bashCmdExists) cmd = bashCmdCandidate;

        var cmdArgs = new List<string>();
        for (int k = 1; k < operands.Count; k++) cmdArgs.Add(operands[k]);

        // Split pipeline input into items. GNU xargs splits the DEFAULT input
        // stream on BLANKS (spaces, tabs) AND newlines, honoring single quotes,
        // double quotes, and backslash escapes so `a "b c" d` yields a, `b c`,
        // d and `a\ b` yields `a b`. `-d DELIM` (custom, with \n \t \r \0 \\
        // escapes) and `-0` (NUL) are the explicit single-delimiter overrides.
        //
        // `-I`/`-i` replacement mode reads WHOLE LINES instead: the line,
        // including internal blanks, is the replacement value. The old
        // implementation reused the whitespace tokenizer here, so
        // `printf 'a b\n' | xargs -I{} echo '[{}]'` wrongly produced two runs.
        var inputLines = new List<string>();
        bool whitespaceSplit = customDelim == null && !nullDelim;
        var delim = customDelim != null ? ExpandDelimEscapes(customDelim) : "\0";
        if (!whitespaceSplit && delim.Length == 0) whitespaceSplit = true; // empty -d is meaningless

        bool replaceMode = !string.IsNullOrEmpty(replaceStr);

        foreach (var item in _pipelineItems)
        {
            var text = BashRuntime.GetBashText(item);

            if (replaceMode)
            {
                // Whole-line mode: every line is one item. A trailing newline
                // is not an item. Leading/trailing blanks are trimmed (GNU
                // strips the delimiter run around the line). Internal blanks
                // are kept verbatim.
                foreach (var rawLine in text.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r').Trim(' ', '\t');
                    if (line.Length > 0) inputLines.Add(line);
                }
                continue;
            }

            if (whitespaceSplit)
            {
                TokenizeWhitespace(text, inputLines);
                continue;
            }

            // Single-delimiter mode (-d / -0): strip one trailing delimiter, then
            // split, dropping empty fields.
            if (text.Length > 0 && text.EndsWith(delim, System.StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - delim.Length);
            }
            if (text.Length == 0) continue;
            if (text.Contains(delim))
            {
                foreach (var part in text.Split(new[] { delim }, System.StringSplitOptions.None))
                {
                    if (part.Length > 0) inputLines.Add(part);
                }
            }
            else
            {
                inputLines.Add(text);
            }
        }

        if (noRunIfEmpty && inputLines.Count == 0)
        {
            return;
        }

        if (replaceMode)
        {
            // Replacement mode: one invocation per input line.
            foreach (var line in inputLines)
            {
                var replaced = new List<string>(cmdArgs.Count);
                foreach (var a in cmdArgs)
                {
                    replaced.Add(a.Replace(replaceStr, line));
                }
                InvokeOne(cmd, replaced, traceCmd);
            }
        }
        else
        {
            int batchSize = maxArgs > 0 ? maxArgs : (maxLines > 0 ? maxLines : 0);
            if (batchSize > 0 && inputLines.Count > 0)
            {
                for (int bi = 0; bi < inputLines.Count; bi += batchSize)
                {
                    var end = System.Math.Min(bi + batchSize, inputLines.Count);
                    var batchArgs = new List<string>(cmdArgs);
                    for (int j = bi; j < end; j++) batchArgs.Add(inputLines[j]);
                    InvokeOne(cmd, batchArgs, traceCmd);
                }
            }
            else
            {
                // Default: single invocation with all items appended.
                var allArgs = new List<string>(cmdArgs);
                allArgs.AddRange(inputLines);
                InvokeOne(cmd, allArgs, traceCmd);
            }
        }

        // GNU exit-status aggregation: 1..125 -> 123, 255 -> 124, killed by
        // signal -> 125 (not observable here), command not found -> 127,
        // command found but cannot be run -> 126. Any invocation failure sticks.
        ApplyExitStatus();
    }

    /// <summary>
    /// Splits <paramref name="text"/> on whitespace, honoring GNU xargs quote
    /// rules: a run of blanks separates items; single quotes, double quotes,
    /// and backslash escapes protect blanks from splitting. Quote characters
    /// are removed. An unterminated quote ends the current item and the
    /// remainder is discarded (GNU warns and stops at the malformed token).
    /// </summary>
    private static void TokenizeWhitespace(string text, List<string> output)
    {
        var current = new System.Text.StringBuilder();
        bool inItem = false;
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];

            if (ch == '\'' || ch == '"')
            {
                char quote = ch;
                inItem = true;
                i++;
                bool closed = false;
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == quote) { closed = true; i++; break; }
                    if (quote == '"' && c == '\\' && i + 1 < text.Length
                        && (text[i + 1] == '"' || text[i + 1] == '\\'))
                    {
                        current.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }
                    current.Append(c);
                    i++;
                }
                if (!closed) break; // unterminated quote — drop the tail
                continue;
            }

            if (ch == '\\' && i + 1 < text.Length)
            {
                current.Append(text[i + 1]);
                inItem = true;
                i += 2;
                continue;
            }

            if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r'
                || ch == '\f' || ch == '\v')
            {
                if (inItem)
                {
                    output.Add(current.ToString());
                    current.Clear();
                    inItem = false;
                }
                i++;
                continue;
            }

            current.Append(ch);
            inItem = true;
            i++;
        }

        if (inItem) output.Add(current.ToString());
    }

    /// <summary>
    /// Invokes <paramref name="cmd"/> with <paramref name="callArgs"/> bound
    /// positionally through <c>$args</c>. The script body is a fixed string
    /// (Directive 12). When <paramref name="trace"/> is true, writes the
    /// command + args to stderr first (xargs <c>-t</c>).
    /// </summary>
    private void InvokeOne(string cmd, List<string> callArgs, bool trace)
    {
        if (trace)
        {
            System.Console.Error.WriteLine(
                callArgs.Count == 0
                    ? cmd
                    : cmd + " " + string.Join(" ", callArgs));
        }

        // Build $args: [cmd, arg1, arg2, ...]. The body splats $args[1..] to
        // the command at $args[0]. No user-controlled string is ever embedded
        // in the body. A command that cannot be resolved yields 127 (GNU's
        // "command not found" status); `Get-Command -ErrorAction Stop` makes
        // the miss terminating so it is caught here rather than silently
        // leaving LASTEXITCODE stale.
        const string invokeBody =
            "$c = $args[0]; $rest = @(); " +
            "if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }; " +
            "try { $null = Get-Command -Name $c -ErrorAction Stop } " +
            "catch [System.Management.Automation.CommandNotFoundException] { -127; return }; " +
            "& $c @rest; $global:LASTEXITCODE";

        var allInvokeArgs = new object[callArgs.Count + 1];
        allInvokeArgs[0] = cmd;
        for (int j = 0; j < callArgs.Count; j++) allInvokeArgs[j + 1] = callArgs[j];

        try
        {
            var output = InvokeCommand.InvokeScript(invokeBody, allInvokeArgs);

            // The body emits the child's stdout objects, then the child's
            // exit code as the FINAL object. Write everything but that last
            // object; read the last one as the exit status.
            int childExit = 0;
            int lastIndex = output.Count - 1;
            if (lastIndex >= 0)
            {
                var baseObj = output[lastIndex] is PSObject pso
                    ? pso.BaseObject
                    : output[lastIndex];
                if (baseObj is int code) childExit = code;
            }
            for (int j = 0; j < lastIndex; j++)
            {
                WriteObject(output[j]);
            }
            if (childExit == -127)
            {
                FileSystemHelpers.WriteBashError(
                    this, $"xargs: {cmd}: No such file or directory");
                RecordNotFound();
                return;
            }
            _lastChildExit = childExit;
            RecordExit(childExit);
        }
        catch (System.Management.Automation.CommandNotFoundException)
        {
            FileSystemHelpers.WriteBashError(this, $"xargs: {cmd}: No such file or directory");
            RecordNotFound();
        }
        catch (System.Exception ex)
        {
            // A command that exists but cannot be executed is 126.
            FileSystemHelpers.WriteBashError(this, $"xargs: {cmd}: {ex.Message}");
            RecordExit(126);
        }
    }

    /// <summary>Exit code seen from the most recent child invocation.</summary>
    private int _lastChildExit;

    /// <summary>Folds a child exit code into the GNU aggregate status.</summary>
    private void RecordExit(int childExit)
    {
        int mapped;
        if (childExit == 0) return;
        else if (childExit == 255) mapped = 124;
        else mapped = 123; // 1..125, including a child's own 126/127 exit

        // The first failure wins — GNU reports the aggregate, not the last.
        if (_aggregateExit == 0) _aggregateExit = mapped;
    }

    /// <summary>
    /// Records GNU's "command not found" status (127) directly. Unlike a child
    /// that exits 127, this is xargs itself failing to resolve the command, so
    /// it must NOT be folded into the generic 123 aggregate.
    /// </summary>
    private void RecordNotFound()
    {
        if (_aggregateExit == 0) _aggregateExit = 127;
    }

    private void ApplyExitStatus()
    {
        FileSystemHelpers.SetLastExitCode(this, _aggregateExit);
    }
}
