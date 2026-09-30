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
/// through <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord);
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
        // Scope the raw-line scan to sed's own pipeline segment so another command's
        // `-e`/`-E` (e.g. `sed -e … f | grep -e foo`) cannot be swallowed as sed scripts.
        var rawLine = BashRuntime.CurrentPipelineSegment(MyInvocation);
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

            try
            {
                // A script FILE is ONE sed script: commands are separated by
                // newlines, and a trailing backslash continues onto the next line
                // (the multi-line `a\`/`i\`/`c\` text form). Adding each trimmed
                // line as its own expression shredded those continuations — a
                // `$a\` line lost its text to the next "expression" and reported
                // "unsupported command 'APP'". Join the raw lines instead;
                // SplitSedCommands then splits real command boundaries and keeps
                // the text runs intact.
                var raw = BashFileSystem.ReadAllText(resolved);
                expressions.Add(raw.TrimEnd('\n'));
            }
            catch
            {
                EmitError($"sed: can't read {scriptFile}");
                SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
                return;
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
            foreach (var part in SplitSedCommands(expr))
            {
                var parsed = ParseExpression(part, extendedRegex);
                if (parsed == null)
                {
                    // ParseExpression already emitted a bash-style error + exit code.
                    return;
                }
                commands.Add(parsed);
            }
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
                    for (int oi = 0; oi < outputLines.Count; oi++)
                    {
                        bool isLast = oi == outputLines.Count - 1;
                        if (isLast && !hadTrailingNewline)
                        {
                            WriteObject(BashRuntime.NewBashObject(
                                outputLines[oi], "PsBash.TextOutput",
                                noTrailingNewline: true));
                        }
                        else
                        {
                            WriteObject(BashRuntime.NewBashObject(outputLines[oi] + "\n"));
                        }
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
        foreach (var item in _pipeline)
        {
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    allLines.Add(subLine);
                }
            }
            else
            {
                allLines.Add(trimmed);
            }
        }

        // Whether the pipeline's last record ends in a newline in the emitted
        // byte stream. A record is NOT newline-terminated only when it is an
        // explicit NoTrailingNewline object AND its BashText does not already end
        // in \n (printf '%s' with no newline). Everything else — a plain string,
        // a normal BashObject, or a NoTrailingNewline object whose text DOES end
        // in \n (printf '%s\n', which embeds the newline) — is terminated. Reusing
        // the input object (the old code) copied its flag onto REWRITTEN text, so
        // a single-line `printf 'x.y\n' | sed 's/\./-/'` lost its newline and the
        // next command concatenated (`x-yZ`).
        bool inputTrailingNewline = true;
        if (_pipeline.Count > 0 && _pipeline[^1] is PSObject tailPso
            && tailPso.Properties["NoTrailingNewline"]?.Value is true
            && !BashRuntime.GetBashText(tailPso).EndsWith("\n"))
        {
            inputTrailingNewline = false;
        }

        var pipeOutput = ProcessLines(allLines.ToArray(), commands);

        for (int oi = 0; oi < pipeOutput.Count; oi++)
        {
            bool isLast = oi == pipeOutput.Count - 1;
            // Every emitted record gets a newline except the FINAL one when the
            // input itself had no trailing newline. NewBashObject normalizes away
            // the newline on the default path, so pass it explicitly as the
            // no-trailing-newline byte-exact form for that last record.
            if (isLast && !inputTrailingNewline)
            {
                WriteObject(BashRuntime.NewBashObject(
                    pipeOutput[oi], "PsBash.TextOutput", noTrailingNewline: true));
            }
            else
            {
                WriteObject(BashRuntime.NewBashObject(pipeOutput[oi] + "\n"));
            }
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
                return Fail("sed: unterminated address regex", 2);
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
                        return Fail("sed: unterminated address regex", 2);
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
                        return Fail("sed: unterminated address regex", 2);
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
            return Fail("sed: missing command", 2);
        }

        char cmdChar = remaining[0];
        switch (cmdChar)
        {
            case 's':
            {
                if (remaining.Length < 2)
                {
                    return Fail("sed: bad substitution", 2);
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
                    return Fail("sed: bad substitution", 2);
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
                    return Fail($"sed: {ex.Message}", 2);
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
                    return Fail("sed: bad transliteration", 2);
                }
                char delim = remaining[1];
                var parts = remaining.Substring(2).Split(delim);
                if (parts.Length < 2)
                {
                    return Fail("sed: bad transliteration", 2);
                }
                if (parts[0].Length != parts[1].Length)
                {
                    return Fail(
                        "sed: y: source and dest must be the same length", 2);
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
                return Fail($"sed: unsupported command '{cmdChar}'", 2);
        }
        }
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
            case AddressType.Last:
                return lineNum == allLines.Length;
            case AddressType.RangeNum:
                return lineNum >= addr.Start && lineNum <= addr.End;
            case AddressType.Step:
            {
                // first~step: match `first`, then every step-th line after it.
                // step <= 0 degenerates to just `first` (GNU). first may be 0,
                // in which case 0~step matches multiples of step.
                if (addr.Step <= 0) { return lineNum == addr.Start; }
                return lineNum >= Math.Max(addr.Start, 1)
                    && (lineNum - addr.Start) % addr.Step == 0;
            }
            case AddressType.RangeNumToRegex:
            {
                // N,/re/ — active from line max(Start,1); ends on the first line
                // whose text matches the end regex (inclusive). The 0,/re/ idiom
                // (Start == 0) lets the end regex match the very first line.
                int begin = Math.Max(addr.Start, 1);
                if (lineNum < begin) { return false; }
                bool canEndOnStart = addr.Start == 0;
                for (int ri = begin; ri <= lineNum && ri <= allLines.Length; ri++)
                {
                    if ((ri > begin || canEndOnStart)
                        && Regex.IsMatch(allLines[ri - 1], addr.EndPattern!))
                    {
                        // Range closed at line ri — only lines begin..ri are in range.
                        return lineNum <= ri;
                    }
                }
                return true; // end regex never matched up to here — still in range.
            }
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
    /// True when a <c>c</c> command's address is a RANGE and the line after
    /// <paramref name="lineNum"/> is still inside that range. Used so the change
    /// text is emitted once per range rather than once per line. A non-range
    /// address always returns false (emit on every matching line).
    /// </summary>
    private static bool RangeContinuesPast(
        SedCommand cmd, string firstLine, int lineNum, string[] allLines)
    {
        var addr = cmd.Address;
        if (addr is null) { return false; }
        switch (addr.Type)
        {
            case AddressType.RangeNum:
            case AddressType.RangeRegex:
            case AddressType.RangeNumToRegex:
                break;
            default:
                return false;
        }

        if (lineNum >= allLines.Length) { return false; }
        string nextFirst = allLines[lineNum];
        bool nextMatched = TestAddress(cmd, nextFirst, lineNum + 1, allLines);
        return cmd.Negate ? !nextMatched : nextMatched;
    }

    /// <summary>
    /// Reproduces the psm1 <c>$processLines</c> closure: the sed cycle engine.
    /// </summary>
    internal static List<string> ProcessLines(
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
                    if (quit && cmd.Type != 'q' && cmd.Type != 'Q')
                    {
                        continue;
                    }

                    string firstLine = patternSpace.Contains('\n')
                        ? patternSpace.Substring(0, patternSpace.IndexOf('\n'))
                        : patternSpace;

                    bool matched = TestAddress(cmd, firstLine, lineNum, inputLines);
                    if (cmd.Negate) { matched = !matched; }
                    if (!matched)
                    {
                        continue;
                    }

                    switch (cmd.Type)
                    {
                        case 's':
                        {
                            // Walk every match and decide per-occurrence whether to
                            // substitute, so the four GNU forms all fall out of one
                            // path: first-only (count == 1), g (count >= 1), Nth
                            // (count == N), and Ng (count >= N).
                            int target = cmd.Nth > 0 ? cmd.Nth : 1;
                            int count = 0;
                            bool subbed = false;
                            patternSpace = cmd.Regex!.Replace(patternSpace, m =>
                            {
                                count++;
                                bool hit = cmd.Global ? count >= target : count == target;
                                if (hit) { subbed = true; }
                                return hit ? m.Result(cmd.Replacement!) : m.Value;
                            });
                            // s///p: print the pattern space only if a sub happened.
                            if (cmd.PrintOnSub && subbed) { printedLines.Add(patternSpace); }
                            break;
                        }
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
                            else
                            {
                                // No next line: GNU sed ends the run here without
                                // executing later commands. Auto-print still
                                // applies (so `N;s…` emits the final line), but
                                // -n suppresses it (so `-n 'N;p'` emits nothing
                                // more). Later commands are skipped via `quit`.
                                quit = true;
                            }
                            break;
                        case 'q':
                            quit = true;
                            break;
                        case 'Q':
                            // Quit immediately WITHOUT auto-printing this line.
                            quit = true;
                            deleted = true;
                            break;
                        case '=':
                            // Print the current line number (before the line text).
                            printedLines.Add(lineNum.ToString());
                            break;
                        case 'a':
                            appendTexts.Add(cmd.Text!);
                            break;
                        case 'i':
                            insertTexts.Add(cmd.Text!);
                            break;
                        case 'c':
                            deleted = true;
                            // A range `c` prints its text ONCE for the whole range,
                            // at the range's final line (GNU); a per-line `c` prints
                            // it on every matching line. TestAddress matched this
                            // line, so emit unless a LATER line is still in range.
                            if (!RangeContinuesPast(cmd, firstLine, lineNum, inputLines))
                            {
                                appendTexts.Add(cmd.Text!);
                            }
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
    internal static bool SuppressDefault;

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
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            // GNU: sed: can't read x: No such file or directory
            EmitError($"sed: can't read {path.Replace('\\', '/')}: {FileSystemHelpers.ReadErrorMessage(ex)}");
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
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
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
