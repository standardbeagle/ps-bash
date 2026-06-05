using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;

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
/// <item><b>Address matching</b> (<see cref="TestAddress"/>) — reproduces
/// <c>Test-SedAddress</c>, including the stateful <c>range_regex</c> walk over
/// all input lines.</item>
/// <item><b>Cycle engine</b> (<see cref="ProcessLines"/>) — reproduces the
/// pattern-space loop with multi-line pattern space (<c>N</c>/<c>D</c>),
/// restart-cycle semantics, <c>p</c>/<c>P</c> printing, <c>a</c>/<c>i</c>/<c>c</c>
/// insert/append, <c>q</c> early-quit, and <c>y</c> transliteration.</item>
/// </list>
/// File mode resolves operands via the same <c>Resolve-BashGlob</c> slice the
/// other migrated cmdlets reimplement in C# (<see cref="ResolveGlob"/>), reads
/// with CRLF normalization, and supports <c>-i</c> in-place rewrite; pipeline
/// mode preserves original typed objects where a one-to-one line mapping holds,
/// matching the oracle. File-read / file-write errors emit a bash-style error
/// through the psm1 <c>Write-BashError</c> sink via a string-bodied
/// <c>InvokeCommand.InvokeScript</c> (no ScriptBlock construction — AOT-safe);
/// <c>--help</c> delegates to <c>Show-BashHelp</c>.
///
/// Common-parameter collision: the bash flag <c>-e</c> (expression) prefix-
/// collides with the PowerShell common parameters <c>-ErrorAction</c> /
/// <c>-ErrorVariable</c> — an unbound <c>-e</c> would be rejected as ambiguous
/// before reaching <see cref="Arguments"/>. It is therefore declared as an
/// explicit value-bearing <see cref="Expression"/> parameter (named <c>-e</c>):
/// an exact parameter-name match beats a common-parameter prefix match. Because
/// PowerShell parameter names are case-insensitive, <c>-E</c> also binds here;
/// the psm1 oracle treated <c>-E</c> (extended regex) and <c>-e</c>
/// (expression) case-sensitively, so the extended-regex flag is recovered
/// independently: <c>-r</c> is an explicit <see cref="R"/> switch (no colliding
/// prefix), and a bundled short-flag form such as <c>-rn</c> or <c>-nE</c> is
/// recovered from <see cref="Arguments"/> by <see cref="EndProcessing"/>. The
/// remaining flags <c>-n</c>, <c>-i</c>, and <c>-f</c> have no colliding prefix
/// and stay in <see cref="Arguments"/>.
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

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "sed", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "sed"))
            {
                WriteObject(line);
            }
            return;
        }

        bool suppressDefault = false;
        bool inPlace = I.IsPresent;
        bool extendedRegex = R.IsPresent;
        string? scriptFile = null;
        var expressions = new List<string>();
        var operands = new List<string>();

        // -e expressions arrive via the explicit Expression parameter (the
        // common-parameter collision fix). Preserve their order.
        if (Expression != null)
        {
            expressions.AddRange(Expression);
        }

        // Workaround for `sed -e A -e B`: PowerShell's binder rejects
        // repeated -e because Expression is a single array parameter
        // ("specified more than once"). If MyInvocation.Line shows
        // multiple `-e <value>` occurrences, reparse them ourselves and
        // replace the binder's view of Expression.
        var rawLine = MyInvocation?.Line ?? string.Empty;
        if (!string.IsNullOrEmpty(rawLine))
        {
            // Match `-e` followed by either a quoted or whitespace-delimited
            // value. Handles single/double quotes and bare tokens.
            var eMatches = System.Text.RegularExpressions.Regex.Matches(
                rawLine,
                @"(?<![A-Za-z0-9])-e\s+(?:'([^']*)'|""([^""]*)""|([^\s]+))");
            if (eMatches.Count >= 2)
            {
                expressions.Clear();
                foreach (System.Text.RegularExpressions.Match m in eMatches)
                {
                    var v = m.Groups[1].Success ? m.Groups[1].Value
                          : m.Groups[2].Success ? m.Groups[2].Value
                          : m.Groups[3].Value;
                    expressions.Add(v);
                }
            }

            // PowerShell's case-insensitive binder treats `-E` as `-e`, so
            // `sed -E PATTERN` ends up binding Expression=PATTERN and never
            // sets extended-regex mode. Detect literal uppercase `-E` in the
            // raw line and switch on extended-regex.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    rawLine, @"(?<![A-Za-z0-9])-E(?![a-zA-Z0-9])"))
            {
                extendedRegex = true;
            }
        }

        // Parse the residual Arguments. -e / -E never appear here (bound by the
        // Expression parameter); -n / -i / -f / -r / bundled short flags do.
        bool pastDoubleDash = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                continue;
            }

            if (arg == "-f")
            {
                if (i + 1 < args.Length)
                {
                    scriptFile = args[++i];
                }
                continue;
            }

            if (arg.Length > 1 && arg[0] == '-' && !arg.StartsWith("--"))
            {
                // Bundled short-flag form: -n, -i, -E, -r, -f recovered from the
                // bundle, matching the psm1 oracle's per-char scan.
                bool fConsumed = false;
                foreach (char ch in arg.Substring(1))
                {
                    switch (ch)
                    {
                        case 'n': suppressDefault = true; break;
                        case 'i': inPlace = true; break;
                        case 'E': extendedRegex = true; break;
                        case 'r': extendedRegex = true; break;
                        case 'f':
                            if (!fConsumed && i + 1 < args.Length)
                            {
                                scriptFile = args[++i];
                                fConsumed = true;
                            }
                            break;
                    }
                }
                continue;
            }

            operands.Add(arg);
        }

        // -f script file: each non-empty trimmed line is a separate command.
        if (scriptFile != null)
        {
            string? resolved = ResolveExistingPath(scriptFile);
            if (resolved == null)
            {
                EmitError($"sed: can't read {scriptFile}");
                SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
                return;
            }

            string scriptText;
            try
            {
                scriptText = BashFileSystem.ReadAllTextRaw(resolved);
            }
            catch
            {
                EmitError($"sed: can't read {scriptFile}");
                SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
                return;
            }

            foreach (var scriptLine in scriptText.Split('\n'))
            {
                string trimmed = scriptLine.Trim();
                if (trimmed.Length > 0)
                {
                    expressions.Add(trimmed);
                }
            }
        }

        // First operand is the expression when no -e / -f was given.
        if (expressions.Count == 0 && operands.Count > 0)
        {
            expressions.Add(operands[0]);
            operands.RemoveAt(0);
        }

        if (expressions.Count == 0)
        {
            EmitError("sed: usage: sed [options] expression [file ...]");
            SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
            return;
        }

        var commands = new List<SedCommand>();
        foreach (var expr in expressions)
        {
            var parsed = ParseExpression(expr, extendedRegex);
            if (parsed == null)
            {
                // ParseExpression already emitted a bash-style error + exit code.
                return;
            }
            commands.Add(parsed);
        }

        // Thread the -n flag into the (static, pure-transform) cycle engine.
        SuppressDefault = suppressDefault;

        // File mode (including in-place).
        if (operands.Count > 0)
        {
            foreach (var filePath in ResolveGlob(operands))
            {
                string? rawText = ReadFileText(filePath);
                if (rawText == null)
                {
                    continue;
                }

                bool hadTrailingNewline = rawText.EndsWith("\n");
                if (hadTrailingNewline)
                {
                    rawText = rawText.Substring(0, rawText.Length - 1);
                }
                var lines = rawText.Split('\n');

                var outputLines = ProcessLines(lines, commands);

                if (inPlace)
                {
                    var sb = new StringBuilder();
                    sb.Append(string.Join("\n", outputLines));
                    if (hadTrailingNewline)
                    {
                        sb.Append('\n');
                    }
                    if (!WriteFileText(filePath, sb.ToString()))
                    {
                        return;
                    }
                }
                else
                {
                    foreach (var outLine in outputLines)
                    {
                        WriteObject(BashRuntime.NewBashObject(outLine + "\n"));
                    }
                }
            }
            return;
        }

        // Pipeline mode.
        if (_pipeline.Count == 0)
        {
            return;
        }

        var allLines = new List<string>();
        var origItems = new List<object?>();
        foreach (var item in _pipeline)
        {
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    allLines.Add(subLine);
                    origItems.Add(null);
                }
            }
            else
            {
                allLines.Add(trimmed);
                origItems.Add(item);
            }
        }

        var pipeOutput = ProcessLines(allLines.ToArray(), commands);

        for (int oi = 0; oi < pipeOutput.Count; oi++)
        {
            if (oi < origItems.Count && origItems[oi] is PSObject orig
                && orig.Properties["BashText"] != null)
            {
                try
                {
                    orig.Properties["BashText"].Value =
                        BashRuntime.NormalizeBashText(pipeOutput[oi] + "\n");
                    WriteObject(orig);
                    continue;
                }
                catch (System.Management.Automation.SetValueException)
                {
                    // Read-only BashText (e.g. ScriptProperty on bare string).
                    // Fall through to emit a fresh BashObject.
                }
            }
            WriteObject(BashRuntime.NewBashObject(pipeOutput[oi] + "\n"));
        }
    }

    // ── sed command model ────────────────────────────────────────────────────

    private enum AddressType { None, Regex, Line, RangeNum, RangeRegex }

    private sealed class SedAddress
    {
        public AddressType Type;
        public string? Pattern;        // Regex
        public int Line;               // Line
        public int Start;              // RangeNum
        public int End;                // RangeNum
        public string? StartPattern;   // RangeRegex
        public string? EndPattern;     // RangeRegex
    }

    private sealed class SedCommand
    {
        public char Type;
        public SedAddress? Address;
        public Regex? Regex;           // s
        public string? Replacement;    // s
        public bool Global;            // s
        public int ExitCode;           // q
        public string? Text;           // a / i / c
        public string? Source;         // y
        public string? Dest;           // y
    }

    /// <summary>
    /// Reproduces the psm1 <c>ConvertFrom-SedExpression</c>. Returns
    /// <c>null</c> after emitting a bash-style error and setting
    /// <c>$global:LASTEXITCODE</c> on a parse failure.
    /// </summary>
    private SedCommand? ParseExpression(string expression, bool extendedRegex)
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
                return ParseFail("sed: unterminated address regex", 2);
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
                        return ParseFail("sed: unterminated address regex", 2);
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
        else if (expression.Length > 0 && char.IsDigit(expression[pos]))
        {
            var numStr = new StringBuilder();
            while (pos < expression.Length && char.IsDigit(expression[pos]))
            {
                numStr.Append(expression[pos]);
                pos++;
            }
            int startNum = int.Parse(numStr.ToString());

            if (pos < expression.Length && expression[pos] == ',')
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
                else
                {
                    var numStr2 = new StringBuilder();
                    while (pos < expression.Length && char.IsDigit(expression[pos]))
                    {
                        numStr2.Append(expression[pos]);
                        pos++;
                    }
                    addr = new SedAddress
                    {
                        Type = AddressType.RangeNum,
                        Start = startNum,
                        End = int.Parse(numStr2.ToString()),
                    };
                }
            }
            else
            {
                addr = new SedAddress { Type = AddressType.Line, Line = startNum };
            }
        }

        string remaining = expression.Substring(pos);
        if (remaining.Length == 0)
        {
            return ParseFail("sed: missing command", 2);
        }

        char cmdChar = remaining[0];
        switch (cmdChar)
        {
            case 's':
            {
                if (remaining.Length < 2)
                {
                    return ParseFail("sed: bad substitution", 2);
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
                    return ParseFail("sed: bad substitution", 2);
                }

                string searchPattern = parts[0];
                string replacement = parts[1];
                string flags = parts.Count > 2 ? parts[2] : string.Empty;
                bool global = flags.Contains('g');

                // Backreference translation: \1-\9 → $1-$9, \& → $0.
                replacement = Regex.Replace(replacement, @"\\(\d)", "$$$1");
                replacement = Regex.Replace(replacement, @"\\&", "$$0");

                var regexOpts = RegexOptions.None;
                if (flags.Contains('I') || flags.Contains('i'))
                {
                    regexOpts |= RegexOptions.IgnoreCase;
                }

                if (!extendedRegex)
                {
                    // BRE: ( ) { } | + ? are literal unless backslash-escaped.
                    searchPattern = Regex.Replace(searchPattern, @"(?<!\\)\(", @"\(");
                    searchPattern = Regex.Replace(searchPattern, @"(?<!\\)\)", @"\)");
                    searchPattern = Regex.Replace(searchPattern, @"(?<!\\)\{", @"\{");
                    searchPattern = Regex.Replace(searchPattern, @"(?<!\\)\}", @"\}");
                    searchPattern = Regex.Replace(searchPattern, @"(?<!\\)\|", @"\|");
                    searchPattern = Regex.Replace(searchPattern, @"(?<!\\)\+", @"\+");
                    searchPattern = Regex.Replace(searchPattern, @"(?<!\\)\?", @"\?");
                }

                Regex regex;
                try
                {
                    regex = new Regex(searchPattern, regexOpts);
                }
                catch (ArgumentException ex)
                {
                    return ParseFail($"sed: {ex.Message}", 2);
                }

                return new SedCommand
                {
                    Type = 's',
                    Address = addr,
                    Regex = regex,
                    Replacement = replacement,
                    Global = global,
                };
            }
            case 'd':
            case 'D':
            case 'p':
            case 'P':
            case 'N':
                return new SedCommand { Type = cmdChar, Address = addr };
            case 'q':
            {
                int exitCode = 0;
                if (remaining.Length > 1)
                {
                    string qArg = remaining.Substring(1).Trim();
                    if (qArg.Length > 0 && Regex.IsMatch(qArg, @"^\d+$"))
                    {
                        exitCode = int.Parse(qArg);
                    }
                }
                return new SedCommand { Type = 'q', Address = addr, ExitCode = exitCode };
            }
            case 'a':
            case 'i':
            case 'c':
            {
                string text = remaining.Length > 1 ? remaining.Substring(1) : string.Empty;
                text = text.TrimStart('\\').TrimStart();
                return new SedCommand { Type = cmdChar, Address = addr, Text = text };
            }
            case 'y':
            {
                if (remaining.Length < 2)
                {
                    return ParseFail("sed: bad transliteration", 2);
                }
                char delim = remaining[1];
                var parts = remaining.Substring(2).Split(delim);
                if (parts.Length < 2)
                {
                    return ParseFail("sed: bad transliteration", 2);
                }
                if (parts[0].Length != parts[1].Length)
                {
                    return ParseFail(
                        "sed: y: source and dest must be the same length", 2);
                }
                return new SedCommand
                {
                    Type = 'y',
                    Address = addr,
                    Source = parts[0],
                    Dest = parts[1],
                };
            }
            default:
                return ParseFail($"sed: unsupported command '{cmdChar}'", 2);
        }
    }

    private SedCommand? ParseFail(string message, int exitCode)
    {
        EmitError(message);
        SessionState.PSVariable.Set("global:LASTEXITCODE", exitCode);
        return null;
    }

    /// <summary>
    /// Reproduces the psm1 <c>Test-SedAddress</c>.
    /// </summary>
    private static bool TestAddress(
        SedCommand cmd, string line, int lineNum, string[] allLines)
    {
        var addr = cmd.Address;
        if (addr == null)
        {
            return true;
        }

        switch (addr.Type)
        {
            case AddressType.Regex:
                return Regex.IsMatch(line, addr.Pattern!);
            case AddressType.Line:
                return lineNum == addr.Line;
            case AddressType.RangeNum:
                return lineNum >= addr.Start && lineNum <= addr.End;
            case AddressType.RangeRegex:
            {
                bool inRange = false;
                bool rangeActive = false;
                for (int ri = 0; ri < allLines.Length; ri++)
                {
                    if (!rangeActive
                        && Regex.IsMatch(allLines[ri], addr.StartPattern!))
                    {
                        rangeActive = true;
                    }
                    if (rangeActive && ri + 1 == lineNum)
                    {
                        inRange = true;
                    }
                    if (rangeActive && ri + 1 != lineNum
                        && Regex.IsMatch(allLines[ri], addr.EndPattern!))
                    {
                        rangeActive = false;
                    }
                    if (rangeActive && ri + 1 == lineNum
                        && Regex.IsMatch(allLines[ri], addr.EndPattern!))
                    {
                        rangeActive = false;
                    }
                    if (ri + 1 > lineNum)
                    {
                        break;
                    }
                }
                return inRange;
            }
        }
        return false;
    }

    /// <summary>
    /// Reproduces the psm1 <c>$processLines</c> closure: the sed cycle engine.
    /// </summary>
    private static List<string> ProcessLines(
        string[] inputLines, List<SedCommand> commands)
    {
        var outputLines = new List<string>();
        int totalLines = inputLines.Length;
        int li = 0;

        while (li < totalLines)
        {
            string patternSpace = inputLines[li];
            int lineNum = li + 1;
            bool quit = false;

            bool restartCycle = true;
            while (restartCycle)
            {
                restartCycle = false;
                var printedLines = new List<string>();
                var appendTexts = new List<string>();
                var insertTexts = new List<string>();
                bool deleted = false;

                foreach (var cmd in commands)
                {
                    if (deleted)
                    {
                        break;
                    }
                    if (quit && cmd.Type != 'q')
                    {
                        continue;
                    }

                    string firstLine = patternSpace.Contains('\n')
                        ? patternSpace.Substring(0, patternSpace.IndexOf('\n'))
                        : patternSpace;

                    if (!TestAddress(cmd, firstLine, lineNum, inputLines))
                    {
                        continue;
                    }

                    switch (cmd.Type)
                    {
                        case 's':
                            patternSpace = cmd.Global
                                ? cmd.Regex!.Replace(patternSpace, cmd.Replacement!)
                                : cmd.Regex!.Replace(patternSpace, cmd.Replacement!, 1);
                            break;
                        case 'd':
                            deleted = true;
                            break;
                        case 'D':
                        {
                            int nlIdx = patternSpace.IndexOf('\n');
                            if (nlIdx >= 0)
                            {
                                patternSpace = patternSpace.Substring(nlIdx + 1);
                            }
                            else
                            {
                                deleted = true;
                                patternSpace = string.Empty;
                            }
                            if (!deleted && patternSpace.Length > 0)
                            {
                                restartCycle = true;
                            }
                            break;
                        }
                        case 'p':
                            printedLines.Add(patternSpace);
                            break;
                        case 'P':
                        {
                            int nlIdx = patternSpace.IndexOf('\n');
                            printedLines.Add(nlIdx >= 0
                                ? patternSpace.Substring(0, nlIdx)
                                : patternSpace);
                            break;
                        }
                        case 'N':
                            li++;
                            if (li < totalLines)
                            {
                                patternSpace += "\n" + inputLines[li];
                            }
                            break;
                        case 'q':
                            quit = true;
                            break;
                        case 'a':
                            appendTexts.Add(cmd.Text!);
                            break;
                        case 'i':
                            insertTexts.Add(cmd.Text!);
                            break;
                        case 'c':
                            deleted = true;
                            appendTexts.Add(cmd.Text!);
                            break;
                        case 'y':
                        {
                            var sb = new StringBuilder(patternSpace.Length);
                            foreach (char ch in patternSpace)
                            {
                                int idx = cmd.Source!.IndexOf(ch);
                                sb.Append(idx >= 0 ? cmd.Dest![idx] : ch);
                            }
                            patternSpace = sb.ToString();
                            break;
                        }
                    }

                    if (restartCycle)
                    {
                        break;
                    }
                }

                if (restartCycle)
                {
                    continue;
                }

                foreach (var insText in insertTexts)
                {
                    outputLines.Add(insText);
                }
                foreach (var pLine in printedLines)
                {
                    outputLines.Add(pLine);
                }
                if (!deleted)
                {
                    // Suppression: -n was modeled by the psm1 oracle's
                    // $suppressDefault. ProcessLines receives that decision
                    // through the caller; emulate by checking the sentinel
                    // below. (See EndProcessing — suppressDefault is folded in
                    // via the SuppressDefault field on the first call.)
                    if (!SuppressDefault)
                    {
                        if (patternSpace.Contains('\n'))
                        {
                            foreach (var psLine in patternSpace.Split('\n'))
                            {
                                outputLines.Add(psLine);
                            }
                        }
                        else
                        {
                            outputLines.Add(patternSpace);
                        }
                    }
                }
                foreach (var appText in appendTexts)
                {
                    outputLines.Add(appText);
                }
            }

            li++;

            if (quit)
            {
                break;
            }
        }

        return outputLines;
    }

    /// <summary>
    /// The <c>-n</c> suppress-default-output flag. <see cref="ProcessLines"/> is
    /// static (it is a pure transform); this instance-set, statically-read field
    /// threads the flag in without changing the method signature. It is set once
    /// per cmdlet invocation in <see cref="EndProcessing"/> before any
    /// <see cref="ProcessLines"/> call. Cmdlet instances do not run concurrently
    /// in a single runspace, so a static carrier is safe here.
    /// </summary>
    [ThreadStatic]
    private static bool SuppressDefault;

    // ── file IO + glob (psm1 Resolve-BashGlob / Read-BashFileBytes /
    //    Write-BashFileText slices reimplemented in C#) ───────────────────────

    private string? ReadFileText(string path)
    {
        try
        {
            return BashFileSystem.ReadAllText(path);
        }
        catch (Exception ex)
        {
            string normalized = path.Replace('\\', '/');
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            EmitError($"sed: {normalized}: {msg}");
            return null;
        }
    }

    private bool WriteFileText(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
            return true;
        }
        catch (Exception ex)
        {
            string normalized = path.Replace('\\', '/');
            EmitError($"sed: {normalized}: {ex.Message}");
            return false;
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

    private IEnumerable<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
            {
                var matched = new List<string>();
                try
                {
                    foreach (var resolved in SessionState.Path
                                 .GetResolvedProviderPathFromPSPath(p, out _))
                    {
                        matched.Add(resolved);
                    }
                }
                catch
                {
                    // No matches — literal passthrough.
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
                yield return SessionState.Path
                    .GetUnresolvedProviderPathFromPSPath(p);
            }
        }
    }
}
