using System.Linq;
using System.Management.Automation;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashGrep</c> function
/// (REFACTOR-2 Phase 4 follow-on). Searches input lines (pipeline or files)
/// for a pattern, reproducing GNU coreutils <c>grep</c> byte-for-byte against
/// the original psm1 oracle.
///
/// Supports the oracle's complete flag surface: <c>-i</c> ignore-case,
/// <c>-v</c> invert, <c>-n</c> line-numbers, <c>-c</c> count-only, <c>-q</c>
/// quiet, <c>-r</c> recursive, <c>-l</c> files-with-matches, <c>-E</c>
/// extended regex (default is basic-regex with the same metachar escaping the
/// oracle applies), <c>-F</c> fixed-string, <c>-w</c> word-regexp, <c>-o</c>
/// only-matching, <c>-H</c> force filename, <c>-h</c> suppress filename,
/// <c>-A N</c> / <c>-B N</c> / <c>-C N</c> context (separated and joined
/// forms), <c>-m N</c> max-count, <c>-e PATTERN</c> multiple patterns (OR),
/// plus the long-form aliases the oracle accepts. Pattern + file operand
/// (default first operand is the pattern when no <c>-e</c> was given).
///
/// Common-parameter collisions per the playbook table — each declared as an
/// explicit parameter with a single-letter name so the binder routes the bare
/// token by exact parameter-name match (which beats a common-parameter prefix
/// match): <c>-i</c> vs <c>-InformationAction</c> → <see cref="I"/>;
/// <c>-v</c> vs <c>-Verbose</c> → <see cref="V"/>; <c>-c</c> vs
/// <c>-Confirm</c> → <see cref="C"/>; <c>-e</c> vs <c>-ErrorAction</c> →
/// <see cref="E"/> (value-bearing <c>string[]</c>, repeatable); <c>-w</c> vs
/// <c>-WarningAction</c> → <see cref="W"/>; <c>-o</c> vs <c>-OutVariable</c> /
/// <c>-OutBuffer</c> → <see cref="O"/> (an earlier audit wrongly listed <c>-o</c>
/// as collision-free — it is ambiguous and hard-crashes if undeclared). <c>-n</c>,
/// <c>-r</c>, <c>-R</c>, <c>-l</c>, <c>-F</c>, <c>-E</c>, <c>-q</c>, <c>-H</c>,
/// <c>-h</c>, <c>-A</c>, <c>-B</c>, <c>-C</c>, <c>-m</c>, <c>--include</c>,
/// <c>--exclude</c>, <c>--help</c>, and <c>--</c> have no PowerShell
/// common-parameter prefix collision and stay in <see cref="Arguments"/>.
/// Bundled short forms (<c>-ivn</c>, <c>-Ev</c>, etc.) land in
/// <see cref="Arguments"/> too — the manual scan walks them per-char,
/// matching the oracle's <c>foreach ($ch in $arg.Substring(1).ToCharArray())</c>
/// slice byte-for-byte (case-sensitive).
///
/// Note: <c>-C</c> (context, case-sensitive in the oracle's <c>^-C(\d+)$</c>
/// pattern) cannot be a distinct parameter from <c>-c</c> under PowerShell's
/// case-insensitive binder; the joined <c>-CN</c> form is recovered from
/// <see cref="Arguments"/> by the manual scan, and the separated <c>-C N</c>
/// form has the unavoidable property that a bare <c>-C</c> token binds to
/// <see cref="C"/> (count) — same residual gap as <c>sed -e A -e B</c>. The
/// common single-flag forms <c>-A N</c> / <c>-B N</c> / <c>-A2</c> etc. are
/// unaffected.
///
/// Output is a typed <c>PsBash.GrepMatch</c> PSObject per match with
/// <c>FileName</c>, <c>LineNumber</c>, <c>Line</c>, and <c>BashText</c>
/// properties (oracle parity). <c>-c</c>/<c>-l</c> emit bare-string PSObjects
/// via <see cref="BashRuntime.NewBashObject"/>. Exit code: <c>$LASTEXITCODE</c>
/// is set to 0 on any match, 1 on no match (matches grep semantics) via
/// <see cref="FileSystemHelpers.SetLastExitCode"/>. Errors route through the
/// psm1 <c>Write-BashError</c> sink via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// (AOT-safe — no <see cref="ScriptBlock"/> construction).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashGrep")]
[OutputType("PsBash.GrepMatch")]
[OutputType(typeof(string))]
public sealed class InvokeBashGrepCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-i</c> (ignore case). Prefix-collides with <c>-InformationAction</c>.</summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>Bash <c>-v</c> (invert match). Prefix-collides with <c>-Verbose</c>.</summary>
    [Parameter] public SwitchParameter V { get; set; }

    /// <summary>Bash <c>-c</c> (count only). Prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    /// <summary>Bash <c>-e PATTERN</c> (multiple patterns). Prefix-collides with <c>-ErrorAction</c>.</summary>
    [Parameter] public string[]? E { get; set; }

    /// <summary>Bash <c>-w</c> (word-regexp). Prefix-collides with <c>-WarningAction</c>.</summary>
    [Parameter] public SwitchParameter W { get; set; }

    /// <summary>
    /// Bash <c>-P</c> (perl-regexp). Declared as an explicit switch decoy: a bare <c>-P</c>
    /// otherwise prefix-binds to the <c>-PipelineVariable</c> / <c>-ProgressAction</c> common
    /// parameters and never reaches <see cref="Arguments"/>. Routed through the .NET-regex path
    /// (see the <c>--perl-regexp</c> note); the bundled (<c>-iP</c>) and long (<c>--perl-regexp</c>)
    /// forms are handled in the argument scan.
    /// </summary>
    [Parameter] public SwitchParameter P { get; set; }

    /// <summary>Bash <c>-o</c> (only-matching). Prefix-collides with <c>-OutVariable</c> /
    /// <c>-OutBuffer</c> (ambiguous → hard binder error if undeclared). Mirrors <c>rg</c>'s
    /// <c>O</c> decoy. The per-char bundle scan also maps <c>o</c>, so a bundled <c>-vo</c>
    /// still works via <see cref="Arguments"/>.</summary>
    [Parameter] public SwitchParameter O { get; set; }

    /// <summary>
    /// Bash <c>-A N</c> (after-context). The bare token <c>-A</c> prefix-matches
    /// the cmdlet's own <see cref="Arguments"/> parameter under PowerShell
    /// parameter binding — same hazard <c>ls</c> / <c>uname</c> hit. Declared
    /// as an explicit value-bearing <c>int?</c> so <c>-A 1</c> binds here. The
    /// joined form <c>-A2</c> still lands in <see cref="Arguments"/> and is
    /// recovered post-parse.
    /// </summary>
    [Parameter] public int? A { get; set; }

    /// <summary>
    /// Bash <c>-B N</c> (before-context). Same <see cref="Arguments"/>
    /// prefix-match hazard as <see cref="A"/>. Declared as an explicit
    /// value-bearing <c>int?</c>.
    /// </summary>
    [Parameter] public int? B { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    /// <summary>
    /// GNU grep options that are valid but not (yet) implemented by ps-bash.
    /// Hitting one yields a specific "recognized but not supported" message
    /// (via <see cref="FileSystemHelpers.WriteOptionError"/>) instead of the
    /// old misleading "No such file or directory" or a silent drop. Anything
    /// option-looking NOT in this set is reported as an unrecognized/invalid
    /// option (bash parity). NOTE: representative, not yet exhaustive — see the
    /// per-command flag-catalog rollout.
    /// </summary>
    private static readonly HashSet<string> ValidButUnsupported = new(StringComparer.Ordinal)
    {
        // Short forms.
        "-z", "-Z", "-a", "-b", "-D", "-d",
        "-U", "-T", "-u", "-y", "-I", "-V",
        // Long forms (bare names; the =VALUE suffix is stripped before lookup).
        "--null-data", "--null",
        "--text", "--byte-offset",
        "--binary-files", "--devices", "--directories", "--binary",
        "--initial-tab", "--include-dir",
        "--exclude-from", "--label",
        "--line-buffered", "--group-separator", "--no-group-separator",
    };

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

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "grep"))
            {
                WriteObject(line);
            }
            return;
        }

        if (FileSystemHelpers.TryHandleVersion(this, "grep", args)) return;

        bool ignoreCase = I.IsPresent;
        bool invertMatch = V.IsPresent;
        bool showLineNumbers = false;
        bool countOnly = C.IsPresent;
        bool quietMode = false;
        bool recursive = false;
        bool filesOnly = false;
        bool extendedRegex = P.IsPresent;   // -P (perl) → .NET regex path, same as -E
        bool fixedString = false;
        bool wholeWord = W.IsPresent;
        bool outputMatchOnly = O.IsPresent;
        bool forceFileName = false;
        bool suppressFileName = false;
        int maxMatches = int.MaxValue;
        int afterContext = A ?? 0;
        int beforeContext = B ?? 0;

        bool filesWithoutMatch = false;   // -L / --files-without-match
        bool lineRegexp = false;          // -x / --line-regexp
        bool noMessages = false;          // -s / --no-messages
        // Recursive filename filters (basename fnmatch), GNU grep --include/--exclude/--exclude-dir.
        var includeGlobs = new List<string>();
        var excludeGlobs = new List<string>();
        var excludeDirGlobs = new List<string>();

        var patterns = new List<string>();
        if (E != null) patterns.AddRange(E);
        var operands = new List<string>();
        bool pastDoubleDash = false;

        // PowerShell's binder is case-insensitive, so the bash conventions
        // `-e PATTERN` (multi-pattern) and `-E` (extended-regex flag) both
        // bind to the same `E` parameter. Detect the uppercase form in the
        // raw command line and switch on extended-regex mode. The pattern
        // value is already in `patterns` from the E binding above.
        var rawLine = MyInvocation?.Line ?? string.Empty;
        if (!string.IsNullOrEmpty(rawLine)
            && System.Text.RegularExpressions.Regex.IsMatch(
                rawLine, @"(?<![A-Za-z0-9])-E(?![a-zA-Z0-9])"))
        {
            extendedRegex = true;
        }

        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];

            if (pastDoubleDash)
            {
                operands.Add(a);
                i++;
                continue;
            }

            if (a == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            // -e PATTERN (literal -e in Arguments — when the binder did not
            // already consume it into E because it was preceded by other
            // tokens or carried via ValueFromRemainingArguments).
            if (a == "-e")
            {
                i++;
                if (i < args.Length) patterns.Add(args[i]);
                i++;
                continue;
            }

            // Joined -ePATTERN form (oracle did not match, but be defensive).

            // -A NUM, -B NUM, -C NUM (separated) or -A2 / -B2 / -C2 (joined).
            var ctxJoined = Regex.Match(a, @"^-([ABC])(\d+)$");
            if (ctxJoined.Success)
            {
                int v = int.Parse(ctxJoined.Groups[2].Value);
                switch (ctxJoined.Groups[1].Value)
                {
                    case "A": afterContext = v; break;
                    case "B": beforeContext = v; break;
                    case "C": afterContext = v; beforeContext = v; break;
                }
                i++;
                continue;
            }
            var ctxBare = Regex.Match(a, @"^-([ABC])$");
            if (ctxBare.Success)
            {
                string flag = ctxBare.Groups[1].Value;
                i++;
                if (i < args.Length && int.TryParse(args[i], out int v))
                {
                    switch (flag)
                    {
                        case "A": afterContext = v; break;
                        case "B": beforeContext = v; break;
                        case "C": afterContext = v; beforeContext = v; break;
                    }
                }
                i++;
                continue;
            }

            // -m NUM (max matches), -mN joined.
            var mJoined = Regex.Match(a, @"^-m(\d+)$");
            if (mJoined.Success)
            {
                maxMatches = int.Parse(mJoined.Groups[1].Value);
                i++;
                continue;
            }
            if (a == "-m")
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out int mv))
                {
                    maxMatches = mv;
                }
                i++;
                continue;
            }

            // Long-form flags (oracle parity).
            if (a == "--extended-regexp") { extendedRegex = true; i++; continue; }
            if (a == "--basic-regexp") { extendedRegex = false; i++; continue; }
            if (a == "--ignore-case") { ignoreCase = true; i++; continue; }
            if (a == "--invert-match") { invertMatch = true; i++; continue; }
            if (a == "--line-number") { showLineNumbers = true; i++; continue; }
            if (a == "--count") { countOnly = true; i++; continue; }
            if (a == "--recursive") { recursive = true; i++; continue; }
            if (a == "--files-with-matches") { filesOnly = true; i++; continue; }
            if (a == "--fixed-strings") { fixedString = true; i++; continue; }
            if (a == "--with-filename") { forceFileName = true; i++; continue; }
            if (a == "--no-filename") { suppressFileName = true; i++; continue; }
            if (a == "--word-regexp") { wholeWord = true; i++; continue; }
            if (a == "--only-matching") { outputMatchOnly = true; i++; continue; }
            if (a == "--quiet" || a == "--silent") { quietMode = true; i++; continue; }
            if (a == "--files-without-match") { filesWithoutMatch = true; i++; continue; }
            if (a == "--line-regexp") { lineRegexp = true; i++; continue; }
            if (a == "--no-messages") { noMessages = true; i++; continue; }
            // -P / --perl-regexp: ps-bash has no PCRE engine; route through .NET regex (a close
            // PCRE superset). Common patterns (\d, \b, lookaround, named groups) work identically;
            // PCRE-only syntax .NET lacks (\K, possessive ++, recursion) errors visibly rather than
            // silently mismatching. Same raw-pattern path as -E.
            if (a == "--perl-regexp") { extendedRegex = true; i++; continue; }
            // --include=GLOB / --include GLOB (and --exclude / --exclude-dir): recursive-search
            // filename filters matched against the file's BASE name, like GNU grep. Check
            // --exclude-dir before --exclude so the longer flag wins.
            if (TryTakeGlobFlag(args, ref i, "--include", includeGlobs)) continue;
            if (TryTakeGlobFlag(args, ref i, "--exclude-dir", excludeDirGlobs)) continue;
            if (TryTakeGlobFlag(args, ref i, "--exclude", excludeGlobs)) continue;
            // --color[=WHEN] / --colour[=WHEN]: GNU grep accepts these silently.
            // ps-bash emits typed BashObjects with no per-match ANSI coloring, so
            // we accept-and-ignore (parity with grep's flag surface, not its
            // coloring). WHEN must be attached with '=' in real grep, so a bare
            // --color does NOT consume the next token. Without this, the common
            // `alias grep='grep --color=auto'` makes every grep treat
            // `--color=auto` as a file operand → "No such file or directory".
            if (a == "--color" || a == "--colour"
                || a.StartsWith("--color=", StringComparison.Ordinal)
                || a.StartsWith("--colour=", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            if (a == "--max-count")
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out int mc))
                {
                    maxMatches = mc;
                }
                i++;
                continue;
            }
            var maxLong = Regex.Match(a, @"^--max-count=(\d+)$");
            if (maxLong.Success)
            {
                maxMatches = int.Parse(maxLong.Groups[1].Value);
                i++;
                continue;
            }

            // Short-flag bundle (single dash, length > 1, not --). Walk per-char
            // case-sensitively, matching the oracle's switch -CaseSensitive.
            if (a.Length > 1 && a[0] == '-' && a[1] != '-')
            {
                foreach (var ch in a.Substring(1))
                {
                    switch (ch)
                    {
                        case 'i': ignoreCase = true; break;
                        case 'v': invertMatch = true; break;
                        case 'n': showLineNumbers = true; break;
                        case 'c': countOnly = true; break;
                        case 'q': quietMode = true; break;
                        case 'r': recursive = true; break;
                        case 'R': recursive = true; break;
                        case 'l': filesOnly = true; break;
                        case 'E': extendedRegex = true; break;
                        case 'G': extendedRegex = false; break; // basic-regexp (default)
                        case 'F': fixedString = true; break;
                        case 'w': wholeWord = true; break;
                        case 'o': outputMatchOnly = true; break;
                        case 'H': forceFileName = true; break;
                        case 'h': suppressFileName = true; break;
                        case 'L': filesWithoutMatch = true; break;
                        case 'x': lineRegexp = true; break;
                        case 's': noMessages = true; break;
                        case 'P': extendedRegex = true; break; // PCRE → .NET regex (see --perl-regexp note)
                        default:
                            // Unknown short flag: a valid-but-unsupported grep
                            // option gets a specific refusal; anything else is
                            // a bash-parity "invalid option" error. getopt
                            // reports the first offending char and stops.
                            FileSystemHelpers.WriteOptionError(this, "grep", "-" + ch, ValidButUnsupported);
                            return;
                    }
                }
                i++;
                continue;
            }

            // Any remaining option-looking token (a long flag we don't handle,
            // e.g. --perl-regexp / --include=*.c, or a typo) is NOT a file
            // operand. Classify it: valid-but-unsupported → specific refusal;
            // otherwise bash-parity "unrecognized option". A lone "-" (stdin)
            // and "--" fell through above and are handled as operands.
            if (FileSystemHelpers.IsOptionLike(a))
            {
                FileSystemHelpers.WriteOptionError(this, "grep", a, ValidButUnsupported);
                return;
            }

            operands.Add(a);
            i++;
        }

        // Pattern collection: -e patterns (already accumulated) or first operand.
        if (patterns.Count == 0 && operands.Count > 0)
        {
            patterns.Add(operands[0]);
            operands.RemoveAt(0);
        }

        if (patterns.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "grep: usage: grep [options] pattern [file ...]");
        FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        // The oracle has an odd two-branch in fileOperands that ultimately
        // yields "everything but operands[0]"; but since we already removed the
        // pattern (or it came from -e), operands now contains the file list.
        var fileOperands = operands;

        // Build regex list (OR logic across multiple patterns).
        var regexOpts = RegexOptions.None;
        if (ignoreCase) regexOpts |= RegexOptions.IgnoreCase;
        var regexes = new List<Regex>();
        foreach (var pat in patterns)
        {
            string regexPattern;
            if (fixedString)
            {
                regexPattern = Regex.Escape(pat);
            }
            else if (!extendedRegex)
            {
                // Basic grep: escape ( ) { } | + ? when not already preceded
                // by a backslash. The oracle uses a -replace chain with
                // (?<!\\)\( etc. — reproduced here.
                regexPattern = EscapeBreMetas(pat);
            }
            else
            {
                regexPattern = pat;
            }

            if (wholeWord)
            {
                regexPattern = "\\b" + regexPattern + "\\b";
            }

            // -x / --line-regexp: the pattern must match the WHOLE line. Anchor it; the
            // non-capturing group keeps an inner alternation (a|b) from binding only the ends.
            if (lineRegexp)
            {
                regexPattern = "^(?:" + regexPattern + ")$";
            }

            try
            {
                regexes.Add(new Regex(regexPattern, regexOpts));
            }
            catch (ArgumentException ex)
            {
                FileSystemHelpers.WriteBashError(this, $"grep: invalid regular expression: {ex.Message}");
        FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
        }

        // --- Pipeline mode ---
        if (fileOperands.Count == 0 && !recursive)
        {
            RunPipelineMode(regexes, invertMatch, showLineNumbers, countOnly,
                quietMode, outputMatchOnly, forceFileName, maxMatches);
            return;
        }

        // --- File mode (incl. recursive) ---
        var recursiveFilter = BuildRecursiveFileFilter(includeGlobs, excludeGlobs, excludeDirGlobs);
        RunFileMode(regexes, fileOperands, recursive, invertMatch, showLineNumbers,
            countOnly, quietMode, filesOnly, filesWithoutMatch, noMessages, outputMatchOnly,
            forceFileName, suppressFileName, maxMatches, beforeContext, afterContext,
            recursiveFilter);
    }

    /// <summary>
    /// Consume a <c>--flag=GLOB</c> or <c>--flag GLOB</c> filename-filter option into
    /// <paramref name="into"/>; returns false (advancing nothing) when <paramref name="args"/>[i] is
    /// not this flag. Used for grep <c>--include</c> / <c>--exclude</c> / <c>--exclude-dir</c>.
    /// </summary>
    private static bool TryTakeGlobFlag(string[] args, ref int i, string flag, List<string> into)
    {
        var a = args[i];
        if (a.StartsWith(flag + "=", StringComparison.Ordinal))
        {
            into.Add(a.Substring(flag.Length + 1));
            i++;
            return true;
        }
        if (a == flag)
        {
            i++;
            if (i < args.Length) { into.Add(args[i]); i++; }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Build the recursive-search filename predicate from the include/exclude/exclude-dir globs, or
    /// null when none were given (so the common case adds no per-file cost). Matches each glob against
    /// the file's BASE name (and, for exclude-dir, each ancestor directory name) case-sensitively,
    /// like GNU grep. Applied only to the recursive directory walk — explicitly-named files are always
    /// searched, per grep semantics.
    /// </summary>
    private static Func<string, bool>? BuildRecursiveFileFilter(
        List<string> includeGlobs, List<string> excludeGlobs, List<string> excludeDirGlobs)
    {
        if (includeGlobs.Count == 0 && excludeGlobs.Count == 0 && excludeDirGlobs.Count == 0)
            return null;

        // Compile once (cold path — LINQ is fine here). The returned predicate runs once PER FILE on
        // the recursive walk, so it uses hand foreach over these arrays, not `.Any(lambda)`: a
        // capturing lambda would allocate a fresh closure + enumerator on every file (GC pressure on
        // a 100k-file tree).
        var inc = includeGlobs.Select(g => WildcardPattern.Get(g, WildcardOptions.None)).ToArray();
        var exc = excludeGlobs.Select(g => WildcardPattern.Get(g, WildcardOptions.None)).ToArray();
        var excDir = excludeDirGlobs.Select(g => WildcardPattern.Get(g, WildcardOptions.None)).ToArray();

        return path =>
        {
            var name = Path.GetFileName(path);

            if (inc.Length > 0)
            {
                bool included = false;
                foreach (var g in inc) { if (g.IsMatch(name)) { included = true; break; } }
                if (!included) return false;
            }

            foreach (var g in exc) { if (g.IsMatch(name)) return false; }

            if (excDir.Length > 0)
            {
                var dir = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(dir))
                {
                    var seg = Path.GetFileName(dir);
                    if (seg.Length > 0)
                        foreach (var g in excDir) { if (g.IsMatch(seg)) return false; }
                    var parent = Path.GetDirectoryName(dir);
                    if (parent == dir) break;
                    dir = parent;
                }
            }
            return true;
        };
    }

    private void RunPipelineMode(
        List<Regex> regexes, bool invertMatch, bool showLineNumbers,
        bool countOnly, bool quietMode, bool outputMatchOnly, bool forceFileName,
        int maxMatches)
    {
        int matchCount = 0;
        int lineNum = 0;

        foreach (var item in _pipeline)
        {
            if (matchCount >= maxMatches) break;

            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');

            // Branch: multi-line BashText → defensive split; else single-line.
            bool isMulti = trimmed.Contains('\n');

            if (isMulti)
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    if (matchCount >= maxMatches) break;
                    lineNum++;
                    ProcessPipelineLine(subLine, item, regexes, invertMatch,
                        showLineNumbers, countOnly, quietMode, outputMatchOnly,
                        forceFileName, lineNum, ref matchCount, asNewObject: true);
                    if (quietMode && matchCount > 0)
                    {
                        FileSystemHelpers.SetLastExitCode(this, 0);
                        return;
                    }
                }
            }
            else
            {
                lineNum++;
                string lineText = trimmed;
                ProcessPipelineLine(lineText, item, regexes, invertMatch,
                    showLineNumbers, countOnly, quietMode, outputMatchOnly,
                    forceFileName, lineNum, ref matchCount, asNewObject: false);
                if (quietMode && matchCount > 0)
                {
                    FileSystemHelpers.SetLastExitCode(this, 0);
                    return;
                }
            }
        }

        if (quietMode)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        FileSystemHelpers.SetLastExitCode(this, matchCount == 0 ? 1 : 0);

        if (countOnly)
        {
            WriteObject(BashRuntime.NewBashObject(matchCount.ToString()));
        }
    }

    private void ProcessPipelineLine(
        string lineText, PSObject originalItem, List<Regex> regexes,
        bool invertMatch, bool showLineNumbers, bool countOnly, bool quietMode,
        bool outputMatchOnly, bool forceFileName, int lineNum,
        ref int matchCount, bool asNewObject)
    {
        bool isMatch = false;
        Match? matchObject = null;
        foreach (var rx in regexes)
        {
            var m = rx.Match(lineText);
            if (m.Success)
            {
                isMatch = true;
                matchObject = m;
                break;
            }
        }
        if (invertMatch) isMatch = !isMatch;
        if (!isMatch) return;

        matchCount++;
        if (quietMode) return;
        if (countOnly) return;

        string outputText = (outputMatchOnly && matchObject != null)
            ? matchObject.Value
            : lineText;

        string prefix = "";
        if (forceFileName) prefix = "<stdin>:";
        if (showLineNumbers) prefix = prefix + lineNum + ":";

        if (prefix.Length > 0)
        {
            WriteObject(BuildGrepMatch("<stdin>", lineNum, lineText, prefix + outputText));
        }
        else if (outputMatchOnly)
        {
            WriteObject(BuildGrepMatch("<stdin>", lineNum, lineText, outputText));
        }
        else if (asNewObject)
        {
            // Multi-line split: emit a fresh GrepMatch (oracle: New-BashObject).
            WriteObject(BuildGrepMatch("<stdin>", lineNum, lineText, outputText));
        }
        else
        {
            // Single-line pipeline item: pass the original object through
            // (oracle parity — preserves typed properties).
            WriteObject(originalItem);
        }
    }

    private void RunFileMode(
        List<Regex> regexes, List<string> fileOperands, bool recursive,
        bool invertMatch, bool showLineNumbers, bool countOnly, bool quietMode,
        bool filesOnly, bool filesWithoutMatch, bool noMessages, bool outputMatchOnly,
        bool forceFileName, bool suppressFileName, int maxMatches,
        int beforeContext, int afterContext, Func<string, bool>? recursiveFilter)
    {
        // -l (files-with-matches) and -L (files-without-match) both only need to know whether each
        // file has ANY match, and neither emits per-line output — share the scanning shortcut.
        bool fileListMode = filesOnly || filesWithoutMatch;
        // File source is built lazily so a recursive search STREAMS — each file
        // is read and its matches emitted as the tree is walked, instead of
        // first draining the whole tree into a list (the old AllDirectories walk
        // went silent for 120s on a big repo and tripped the host watchdog).
        IEnumerable<string> fileSource;
        bool multipleFiles;

        if (recursive)
        {
            string searchDir = fileOperands.Count > 0 ? fileOperands[0] : ".";
            string resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(searchDir);
            if (Directory.Exists(resolved))
            {
                // grep -r searches dot-files too — only the prune set (.git,
                // bin, obj, node_modules, …) is skipped, so a stray .env is
                // still searched but .git is not. PSBASH_SEARCH_NO_IGNORE turns
                // the prune set off entirely (restores GNU grep parity).
                fileSource = BashFileSystem.EnumerateSearchFiles(
                    resolved,
                    includeIgnored: BashFileSystem.DefaultFilteringDisabled(),
                    includeHidden: true);
                // --include / --exclude / --exclude-dir apply to the recursive directory walk
                // (an explicitly-named file is always searched, per grep semantics).
                if (recursiveFilter != null)
                    fileSource = fileSource.Where(recursiveFilter);
            }
            else if (File.Exists(resolved))
            {
                fileSource = new[] { resolved };
            }
            else
            {
                fileSource = Array.Empty<string>();
            }
            // -r always prefixes filenames (oracle parity: the old
            // `|| recursive` term).
            multipleFiles = true;
        }
        else
        {
            var resolvedList = new List<string>();
            foreach (var raw in fileOperands)
            {
                bool any = false;
                foreach (var fp in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    if (File.Exists(fp) || Directory.Exists(fp))
                    {
                        resolvedList.Add(fp);
                        any = true;
                    }
                }
                if (!any)
                {
                    // -s / --no-messages suppresses the diagnostic but still sets the failure code.
                    if (!noMessages)
                    {
                        string normalized = raw.Replace('\\', '/');
                        FileSystemHelpers.WriteBashError(this, $"grep: {normalized}: No such file or directory");
                    }
                    FileSystemHelpers.SetLastExitCode(this, 2);
                }
            }
            fileSource = resolvedList;
            multipleFiles = resolvedList.Count > 1 || forceFileName;
        }

        var matchedFiles = new List<string>();
        var perFileCounts = new Dictionary<string, int>();
        // Files actually visited, in order — used by the multi-file count pass
        // below (replaces the old materialized filePaths list).
        var scanned = new List<string>();
        int totalMatchCount = 0;
        // Binary files are skipped by default (NUL probe) — the same escape hatch
        // (PSBASH_SEARCH_NO_IGNORE) that disables dir pruning also searches them.
        bool skipBinary = !BashFileSystem.DefaultFilteringDisabled();
        // Streaming fast path: with no context (-A/-B/-C) and no -m cap, every
        // matching line emits the instant it is read — nothing is buffered, so a
        // 2 GB log streams in constant memory. Context / -m need look-back or a
        // global cap, so they fall back to a per-file pass (still a STREAMED read,
        // still binary-skipped — only those rarer cases hold one file in memory).
        bool needsBuffer = beforeContext > 0 || afterContext > 0 || maxMatches != int.MaxValue;

        foreach (var filePath in fileSource)
        {
            if (totalMatchCount >= maxMatches) break;
            scanned.Add(filePath);
            // Binary files are skipped entirely (not counted, not listed) — probe
            // once here so a binary never lands in perFileCounts as a noisy ":0".
            if (skipBinary && BashFileSystem.IsBinary(filePath)) continue;
            bool showFile = multipleFiles && !suppressFileName;

            if (!needsBuffer)
            {
                int fileMatches = 0;
                int lineNum = 0;
                foreach (var line in ReadFileLinesStream(filePath))
                {
                    lineNum++;
                    var mo = MatchLine(regexes, line, invertMatch, out bool isMatch);
                    if (!isMatch) continue;
                    fileMatches++;
                    if (quietMode) { FileSystemHelpers.SetLastExitCode(this, 0); return; }
                    if (fileListMode) { matchedFiles.Add(filePath); break; }
                    if (countOnly) continue;
                    string outText = (outputMatchOnly && mo != null) ? mo.Value : line;
                    EmitGrepLine(filePath, lineNum, line, outText, showFile, showLineNumbers);
                }
                totalMatchCount += fileMatches;
                perFileCounts[filePath] = fileMatches;
                continue;
            }

            // --- context / -m fallback: this file's lines, read by streaming. ---
            var lines = ReadFileLinesArray(filePath);
            if (lines == null) continue;

            var matchIndices = new List<int>();
            var matchObjects = new Dictionary<int, Match>();
            for (int li = 0; li < lines.Length; li++)
            {
                var mo = MatchLine(regexes, lines[li], invertMatch, out bool isMatch);
                if (isMatch)
                {
                    matchIndices.Add(li);
                    if (mo != null) matchObjects[li] = mo;
                }
            }

            int fileMatchCount = matchIndices.Count;
            totalMatchCount += fileMatchCount;
            perFileCounts[filePath] = fileMatchCount;

            if (quietMode && fileMatchCount > 0)
            {
                FileSystemHelpers.SetLastExitCode(this, 0);
                return;
            }

            if (fileListMode)
            {
                if (fileMatchCount > 0) matchedFiles.Add(filePath);
                continue;
            }

            if (countOnly) continue;

            // Determine emit set (matches + context, respecting -m).
            var emitLines = new SortedSet<int>();
            int emitCount = 0;
            foreach (var mi in matchIndices)
            {
                if (emitCount >= maxMatches) break;
                int start = Math.Max(0, mi - beforeContext);
                int end = Math.Min(lines.Length - 1, mi + afterContext);
                for (int li = start; li <= end; li++)
                {
                    emitLines.Add(li);
                }
                emitCount++;
            }

            foreach (var li in emitLines)
            {
                if (totalMatchCount > maxMatches && !matchIndices.Contains(li)) break;

                string line = lines[li];
                int lineNum = li + 1;
                string outputText = (outputMatchOnly && matchObjects.TryGetValue(li, out var mo2))
                    ? mo2.Value
                    : line;
                EmitGrepLine(filePath, lineNum, line, outputText, showFile, showLineNumbers);
            }
        }

        if (quietMode)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        FileSystemHelpers.SetLastExitCode(this, totalMatchCount == 0 ? 1 : 0);

        if (filesOnly)
        {
            foreach (var fp in matchedFiles)
            {
                WriteObject(BashRuntime.NewBashObject(fp));
            }
            return;
        }

        if (filesWithoutMatch)
        {
            // -L: list the (non-binary) files that produced NO match, in scan order. perFileCounts
            // holds only processed text files; a matched file has a positive count.
            foreach (var fp in scanned)
            {
                if (perFileCounts.TryGetValue(fp, out int n) && n == 0)
                    WriteObject(BashRuntime.NewBashObject(fp));
            }
            return;
        }

        if (countOnly)
        {
            if (multipleFiles)
            {
                foreach (var fp in scanned)
                {
                    if (perFileCounts.TryGetValue(fp, out int n))
                    {
                        WriteObject(BashRuntime.NewBashObject($"{fp}:{n}"));
                    }
                }
            }
            else
            {
                WriteObject(BashRuntime.NewBashObject(totalMatchCount.ToString()));
            }
        }
    }

    /// <summary>
    /// Build a typed <c>PsBash.GrepMatch</c> PSObject. Matches the psm1
    /// oracle's <c>[PSCustomObject]@{PSTypeName='PsBash.GrepMatch'; ...}</c>
    /// shape with <c>FileName</c>, <c>LineNumber</c>, <c>Line</c>, and
    /// <c>BashText</c> properties (the format ps1xml view renders BashText
    /// directly).
    /// </summary>
    private static PSObject BuildGrepMatch(string fileName, int lineNumber, string line, string bashText)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.GrepMatch");
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty("LineNumber", lineNumber));
        obj.Properties.Add(new PSNoteProperty("Line", line));
        obj.Properties.Add(new PSNoteProperty("BashText", bashText));
        return obj;
    }

    /// <summary>
    /// BRE → .NET escape: escape ( ) { } | + ? when not already preceded by a
    /// backslash. Mirrors the oracle's <c>-replace</c> chain with
    /// <c>(?&lt;!\\)\(</c> etc.
    /// </summary>
    private static string EscapeBreMetas(string pat)
    {
        var escapeChars = new[] { '(', ')', '{', '}', '|', '+', '?' };
        foreach (var ch in escapeChars)
        {
            pat = Regex.Replace(pat, $@"(?<!\\)\{ch}", "\\" + ch);
        }
        return pat;
    }

    /// <summary>
    /// First regex that matches <paramref name="line"/> (null if none), with
    /// invert applied to <paramref name="isMatch"/>. Shared by the streaming fast
    /// path and the context/-m fallback so match semantics stay identical.
    /// </summary>
    private static Match? MatchLine(List<Regex> regexes, string line, bool invertMatch, out bool isMatch)
    {
        Match? mo = null;
        bool matched = false;
        foreach (var rx in regexes)
        {
            var m = rx.Match(line);
            if (m.Success) { matched = true; mo = m; break; }
        }
        isMatch = invertMatch ? !matched : matched;
        return mo;
    }

    /// <summary>Build the <c>file:line:</c> prefix and emit one GrepMatch.</summary>
    private void EmitGrepLine(string filePath, int lineNum, string fullLine, string outputText,
        bool showFile, bool showLineNumbers)
    {
        string bashText;
        if (showLineNumbers)
        {
            string prefix = showFile ? (filePath + ":") : "";
            bashText = prefix + lineNum + ":" + outputText;
        }
        else if (showFile)
        {
            bashText = filePath + ":" + outputText;
        }
        else
        {
            bashText = outputText;
        }
        WriteObject(BuildGrepMatch(filePath, lineNum, fullLine, bashText));
    }

    /// <summary>
    /// Stream a (known-text) file's lines for the fast path. The file is opened
    /// lazily on first enumeration; an IO error there emits the grep-style message
    /// and ends the stream cleanly. Binary skipping is done by the caller's probe.
    /// </summary>
    private IEnumerable<string> ReadFileLinesStream(string path)
    {
        IEnumerator<string>? it = null;
        try
        {
            while (true)
            {
                string current;
                try
                {
                    it ??= BashFileSystem.ReadLines(path).GetEnumerator();
                    if (!it.MoveNext()) yield break;
                    current = it.Current;
                }
                catch (Exception ex)
                {
                    EmitGrepReadError(path, ex);
                    yield break;
                }
                yield return current;
            }
        }
        finally
        {
            it?.Dispose();
        }
    }

    /// <summary>
    /// Materialize a (known-text) file's lines for the context / -m fallback —
    /// still a streamed read. Returns null on IO error (message emitted).
    /// </summary>
    private string[]? ReadFileLinesArray(string path)
    {
        try
        {
            var list = new List<string>();
            foreach (var l in BashFileSystem.ReadLines(path)) list.Add(l);
            return list.ToArray();
        }
        catch (Exception ex)
        {
            EmitGrepReadError(path, ex);
            return null;
        }
    }

    private void EmitGrepReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"grep: {normalized}: {msg}");
        FileSystemHelpers.SetLastExitCode(this, 2);
    }
}
