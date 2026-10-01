using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRg</c> function
/// (REFACTOR-2 Phase 4 follow-on). Internal regex-search implementation
/// that mirrors the psm1 oracle byte-for-byte, with an opt-in passthrough
/// to the ripgrep binary <c>rg.exe</c>.
///
/// Native passthrough, enabled by setting <c>PSBASH_RG_NATIVE</c> to a truthy
/// value: <c>Get-Command rg -CommandType Application</c> via
/// parameter-bound <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>;
/// when found, shells out via <see cref="Process"/> with
/// <c>UseShellExecute=false</c>, <c>RedirectStandardOutput=true</c>, and
/// arguments bound via <see cref="ProcessStartInfo.ArgumentList"/>
/// (Directive 12: no shell, no string concatenation into the command line).
/// Captured stdout is emitted line-by-line as bare <c>PsBash.TextOutput</c>
/// strings.
///
/// Default internal mode: the cmdlet runs a ripgrep-flavoured subset over pipeline or recursive
/// file-mode input: <c>-i -S -s -w -x -c -l -n -N -o -v -F -u</c> (counted), <c>-g GLOB</c>
/// (repeatable, <c>!negation</c>), <c>-A/-B/-C N</c>, <c>-e PATTERN</c> (repeatable, OR),
/// <c>--hidden --no-ignore --color WHEN</c>, plus the accepted no-ops <c>--no-heading --no-messages
/// --no-config --mmap --no-mmap</c>. Exit status is ripgrep's: 0 a line matched, 1 none, 2 an error.
///
/// Option parsing is the shared ORDERED parser (<see cref="ArgParser"/>, ripgrep 14.1 rules: no long
/// option abbreviation, attached short values, options after operands; see <see cref="Plan"/>); the
/// ripgrep options the engine does not run (<c>-t -j -m --json -P -r ...</c>) are refused with exit 2
/// rather than silently dropped. The transpiler single-quotes every flag
/// (<c>PsEmitter.OrderedArgCommands</c>), so the whole argv reaches <see cref="Arguments"/> verbatim and
/// in order — which is also exactly what the native passthrough forwards. Typed directly at PowerShell the
/// binder still intercepts colliding bare flags, so the decoys <see cref="I"/> <see cref="V"/>
/// <see cref="C"/> <see cref="W"/> <see cref="O"/> <see cref="A"/> <see cref="B"/> <see cref="E"/> remain and
/// are re-injected as ordinary tokens before parsing.
/// Output: when the internal fallback runs and produces a match, the
/// cmdlet emits a typed <c>PsBash.RgMatch</c> PSObject per match with
/// <c>FileName</c>, <c>LineNumber</c>, <c>Line</c>, and <c>BashText</c>
/// properties (oracle parity). <c>-c</c> / <c>-l</c> emit bare strings
/// via <see cref="BashRuntime.NewBashObject"/>. The native-passthrough
/// branch emits the rg binary's stdout as bare <c>PsBash.TextOutput</c>
/// strings (one per line). Errors route through <see cref="FileSystemHelpers.WriteBashError"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRg")]
[OutputType("PsBash.RgMatch")]
[OutputType(typeof(string))]
public sealed class InvokeBashRgCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-i</c> (ignore case). Prefix-collides with <c>-InformationAction</c>.</summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>Bash <c>-v</c> (invert match). Prefix-collides with <c>-Verbose</c>.</summary>
    [Parameter] public SwitchParameter V { get; set; }

    /// <summary>Bash <c>-c</c> (count-only). Prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    /// <summary>Bash <c>-w</c> (word-regexp). Prefix-collides with <c>-WarningAction</c>.</summary>
    [Parameter] public SwitchParameter W { get; set; }

    /// <summary>Bash <c>-o</c> (only matching). Prefix-collides with <c>-OutBuffer</c> / <c>-OutVariable</c>.</summary>
    [Parameter] public SwitchParameter O { get; set; }

    /// <summary>
    /// Bash <c>-A N</c> (after-context). The bare token <c>-A</c> prefix-matches
    /// the cmdlet's own <see cref="Arguments"/> parameter — same hazard
    /// <c>ls</c> / <c>grep</c> hit. Declared as <c>int?</c>.
    /// </summary>
    [Parameter] public int? A { get; set; }

    /// <summary>
    /// Bash <c>-B N</c> (before-context). Same hazard as <see cref="A"/>.
    /// </summary>
    [Parameter] public int? B { get; set; }

    /// <summary>
    /// Bash <c>-e PATTERN</c>. The bare token prefix-collides with <c>-ErrorAction</c> /
    /// <c>-ErrorVariable</c>; declared value-bearing so ONE direct <c>Invoke-BashRg -e x</c> binds
    /// here (a REPEATED bare <c>-e</c> typed at PowerShell still hits the binder — quote the flags, or
    /// use the transpiler, which single-quotes them).
    /// </summary>
    [Parameter] public string[]? E { get; set; }

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

    private const string OptIgnoreCase = "ignore-case", OptSmartCase = "smart-case",
        OptCaseSensitive = "case-sensitive", OptWord = "word", OptLine = "line", OptCount = "count",
        OptFilesWith = "files-with", OptLineNumber = "line-number", OptNoLineNumber = "no-line-number",
        OptOnly = "only", OptInvert = "invert", OptFixed = "fixed", OptUnrestricted = "unrestricted",
        OptHidden = "hidden", OptNoIgnore = "no-ignore", OptGlob = "glob", OptAfter = "after",
        OptBefore = "before", OptContext = "context", OptRegexp = "regexp", OptColor = "color",
        OptHeading = "heading", OptNoHeading = "no-heading", OptNoop = "noop";

    /// <summary>
    /// ripgrep options the internal engine does not run (refused, exit 2, instead of being silently
    /// dropped). With <c>PSBASH_RG_NATIVE</c> and a real <c>rg</c> on PATH none of this matters: the
    /// native binary receives the argv verbatim and runs them all.
    /// </summary>
    private static readonly string[] RgValidButUnsupported =
    {
        "-t", "-T", "-j", "-z", "-U", "-P", "-r", "-f", "-m", "-d", "-L", "-0", "-E", "-a", "-p", "-b", "-q",
        "-H", "-I", "-.",
        "--type", "--type-not", "--type-add", "--type-clear", "--type-list", "--threads", "--json",
        "--search-zip", "--multiline", "--multiline-dotall", "--pcre2", "--replace", "--file", "--max-count",
        "--max-depth", "--maxdepth", "--max-filesize", "--follow", "--sort", "--sortr", "--files", "--stats",
        "--vimgrep", "--passthru", "--null", "--column", "--debug", "--trace", "--pre",
        "--pre-glob", "--encoding", "--text", "--binary", "--iglob", "--ignore-file", "--no-ignore-dot",
        "--no-ignore-global", "--no-ignore-parent", "--no-ignore-files", "--pretty", "--null-data",
        "--byte-offset", "--quiet", "--files-without-match", "--with-filename", "--no-filename", "--trim",
        "--line-buffered", "--block-buffered",
    };

    /// <summary>
    /// rg's option surface (ripgrep 14.1; lexopt-style: NO long-option abbreviation, attached short
    /// values <c>-A2 -g*.rs -epat</c>, counted <c>-uu</c>, options after operands). Usage errors exit 2.
    /// <c>--color WHEN</c> takes a REQUIRED value (<c>--color never</c> / <c>--color=never</c>);
    /// <c>--no-heading --no-messages --no-config --mmap --no-mmap</c> are accepted no-ops.
    /// </summary>
    private static readonly OptSpecSet RgSpec = new(
        new[]
        {
            new OptSpec(OptIgnoreCase, 'i', "ignore-case"),
            new OptSpec(OptSmartCase, 'S', "smart-case"),
            new OptSpec(OptCaseSensitive, 's', "case-sensitive"),
            new OptSpec(OptWord, 'w', "word-regexp"),
            new OptSpec(OptLine, 'x', "line-regexp"),
            new OptSpec(OptCount, 'c', "count"),
            new OptSpec(OptFilesWith, 'l', "files-with-matches"),
            new OptSpec(OptLineNumber, 'n', "line-number"),
            new OptSpec(OptNoLineNumber, 'N', "no-line-number"),
            new OptSpec(OptOnly, 'o', "only-matching"),
            new OptSpec(OptInvert, 'v', "invert-match"),
            new OptSpec(OptFixed, 'F', "fixed-strings"),
            new OptSpec(OptUnrestricted, 'u', "unrestricted"),
            new OptSpec(OptHidden, '\0', "hidden"),
            new OptSpec(OptNoIgnore, '\0', "no-ignore"),
            new OptSpec(OptNoIgnore, '\0', "no-ignore-vcs"),
            new OptSpec(OptGlob, 'g', "glob", OptKind.Value),
            new OptSpec(OptAfter, 'A', "after-context", OptKind.Value),
            new OptSpec(OptBefore, 'B', "before-context", OptKind.Value),
            new OptSpec(OptContext, 'C', "context", OptKind.Value),
            new OptSpec(OptRegexp, 'e', "regexp", OptKind.Value),
            new OptSpec(OptColor, '\0', "color", OptKind.Value),
            new OptSpec(OptHeading, '\0', "heading"),
            new OptSpec(OptNoHeading, '\0', "no-heading"),
            new OptSpec(OptNoop, '\0', "no-messages"),
            new OptSpec(OptNoop, '\0', "no-config"),
            new OptSpec(OptNoop, '\0', "mmap"),
            new OptSpec(OptNoop, '\0', "no-mmap"),
            new OptSpec(OptSpecSet.HelpId, 'h', "help"),
            new OptSpec(OptSpecSet.VersionId, 'V', "version"),
        },
        RgValidButUnsupported,
        allowAbbrev: false,
        usageExitCode: 2);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, RgSpec);

    private static readonly string[] ColorWhenWords = { "never", "auto", "always", "ansi" };

    internal sealed class RgArgs
    {
        public ParsedArgs Parsed = null!;
        public string? Error;
        public int ErrorExit = 2;

        public bool WordRegexp, LineRegexp, CountOnly, FilesOnly, OnlyMatching, Invert, Fixed;

        /// <summary>Last of -n / -N: true / false, or null = unset (ripgrep then decides from the terminal).</summary>
        public bool? LineNumberMode;

        /// <summary>Last of --heading / --no-heading, or null = unset (the terminal decides).</summary>
        public bool? HeadingMode;

        /// <summary>False only for an explicit -N (the scan-test view of the flag).</summary>
        public bool LineNumbers => LineNumberMode != false;

        /// <summary>Last of -i / -s / -S wins (ripgrep): 'i', 's', 'S', or '\0' = default (sensitive).</summary>
        public char CaseMode;

        /// <summary>-u count: 1 = no ignore rules, 2 = also hidden (3 = also binary, not distinguished).</summary>
        public int Unrestricted;

        public bool Hidden, NoIgnore;
        public int After, Before;
        public List<string> Globs = new(), Patterns = new(), Operands = new();

        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + interpret. <c>-e PATTERN</c> (repeatable, OR) replaces the first-operand pattern; the last of
    /// <c>-i -s -S</c> wins; <c>-A/-B</c> beat <c>-C</c> in any order; <c>-uu</c> also includes hidden files;
    /// a non-numeric context length or an unknown <c>--color</c> WHEN is a usage error (exit 2).
    /// </summary>
    internal static RgArgs Plan(string[] args)
    {
        var r = new RgArgs { Parsed = ScanArgs(args) };
        r.Operands = r.Parsed.Operands();
        if (r.Parsed.HasError) return r;
        if (r.Parsed.Has(OptSpecSet.HelpId) || r.Parsed.Has(OptSpecSet.VersionId)) return r;

        int after = -1, before = -1, both = -1;
        foreach (var tok in r.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            string? v = tok.Value;
            switch (tok.OptId)
            {
                case OptIgnoreCase: r.CaseMode = 'i'; break;
                case OptSmartCase: r.CaseMode = 'S'; break;
                case OptCaseSensitive: r.CaseMode = 's'; break;
                case OptWord: r.WordRegexp = true; break;
                case OptLine: r.LineRegexp = true; break;
                case OptCount: r.CountOnly = true; break;
                case OptFilesWith: r.FilesOnly = true; break;
                case OptLineNumber: r.LineNumberMode = true; break;
                case OptNoLineNumber: r.LineNumberMode = false; break;
                case OptHeading: r.HeadingMode = true; break;
                case OptNoHeading: r.HeadingMode = false; break;
                case OptOnly: r.OnlyMatching = true; break;
                case OptInvert: r.Invert = true; break;
                case OptFixed: r.Fixed = true; break;
                case OptUnrestricted: r.Unrestricted++; break;
                case OptHidden: r.Hidden = true; break;
                case OptNoIgnore: r.NoIgnore = true; break;
                case OptGlob: r.Globs.Add(v!); break;
                case OptRegexp: r.Patterns.Add(v!); break;
                case OptColor:
                    if (Array.IndexOf(ColorWhenWords, v) < 0)
                    {
                        r.Error = $"rg: error parsing flag --color: choice '{v}' is unrecognized";
                        return r;
                    }
                    break;
                case OptAfter: case OptBefore: case OptContext:
                {
                    if (!TryParseContext(v!, out int n))
                    {
                        r.Error = $"rg: error parsing flag {(tok.OptId == OptAfter ? "-A" : tok.OptId == OptBefore ? "-B" : "-C")}: value is not a valid number: invalid digit found in string";
                        return r;
                    }
                    if (tok.OptId == OptAfter) after = n;
                    else if (tok.OptId == OptBefore) before = n;
                    else both = n;
                    break;
                }
            }
        }

        if (r.Unrestricted >= 1) r.NoIgnore = true;
        if (r.Unrestricted >= 2) r.Hidden = true;
        r.After = after >= 0 ? after : Math.Max(both, 0);
        r.Before = before >= 0 ? before : Math.Max(both, 0);
        return r;
    }

    private static bool TryParseContext(string s, out int n)
    {
        n = 0;
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (c < '0' || c > '9') return false;
        n = BashRuntime.ParseCountClamped(s);
        return true;
    }

    protected override void EndProcessing()
    {
        FileSystemHelpers.SetLastExitCode(this, 0);

        // Direct PowerShell calls: a bare -i/-v/-c/-w/-o/-A/-B/-e binds a declared decoy parameter (or a
        // common parameter) instead of reaching Arguments; re-inject them as the ordinary tokens they
        // stand for. Transpiled bash never gets here: the emitter single-quotes every flag
        // (OrderedArgCommands), so the whole argv arrives verbatim and in order.
        var args = BashRuntime.PrependDecoys(Arguments,
            (I.IsPresent, "-i"), (V.IsPresent, "-v"), (C.IsPresent, "-c"), (W.IsPresent, "-w"), (O.IsPresent, "-o"));
        if (A is not null || B is not null || E is { Length: > 0 })
        {
            var extra = new List<string>();
            if (A is { } av) { extra.Add("-A"); extra.Add(av.ToString()); }
            if (B is { } bv) { extra.Add("-B"); extra.Add(bv.ToString()); }
            if (E != null) foreach (var pat in E) { extra.Add("-e"); extra.Add(pat); }
            extra.AddRange(args);
            args = extra.ToArray();
        }

        // Native passthrough is opt-in (PSBASH_RG_NATIVE) and only applies with no pipeline input. The
        // binary gets the argv VERBATIM, before any validation here: it understands every ripgrep flag.
        if (_pipeline.Count == 0 && NativeRgPassthroughEnabled() && TryRunNativeRg(args))
        {
            return;
        }

        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "rg", plan.Parsed)) return;
        if (plan.Parsed.Has(OptSpecSet.VersionId))
        {
            FileSystemHelpers.TryHandleVersion(this, "rg", new[] { "--version" });
            return;
        }
        if (plan.Parsed.Has(OptSpecSet.HelpId))
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "rg"))
            {
                WriteObject(line);
            }
            return;
        }
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, plan.ErrorExit);
            return;
        }

        bool wordRegexp = plan.WordRegexp;
        bool countOnly = plan.CountOnly;
        bool invertMatch = plan.Invert;
        bool filesOnly = plan.FilesOnly;
        // ripgrep's terminal-dependent defaults (see StdoutIsTerminal): line numbers and headings are ON
        // for a terminal, OFF for a pipe; an explicit -n/-N/--heading/--no-heading always wins. Searching
        // stdin never defaults to line numbers, even on a terminal (oracle: `cat f | rg x` under a tty).
        bool tty = StdoutIsTerminal();
        bool showLineNumbers = plan.LineNumberMode ?? tty;
        bool heading = plan.HeadingMode ?? tty;
        bool onlyMatching = plan.OnlyMatching;
        bool fixedStrings = plan.Fixed;
        bool lineRegexp = plan.LineRegexp;
        bool includeHidden = plan.Hidden;
        bool noIgnore = plan.NoIgnore;
        int afterContext = plan.After;
        int beforeContext = plan.Before;

        // -e PATTERN (repeatable) makes every operand a path; otherwise the first operand is the pattern.
        var patterns = new List<string>(plan.Patterns);
        var operands = plan.Operands;
        if (patterns.Count == 0)
        {
            if (operands.Count == 0)
            {
                FileSystemHelpers.WriteBashError(this, "rg: ripgrep requires at least one pattern to execute a search");
                FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
            patterns.Add(operands[0]);
            operands.RemoveAt(0);
        }

        // --- Internal fallback (psm1 oracle parity) ---
        var fileOperands = operands;

        // Case resolution: the last of -i / -s / -S wins; -S (smart-case) is insensitive only when
        // no pattern has an uppercase letter.
        bool effectiveIgnore = plan.CaseMode switch
        {
            'i' => true,
            'S' => !patterns.Exists(PatternHasUpper),
            _ => false,
        };

        var parts = new List<string>(patterns.Count);
        foreach (var raw in patterns)
        {
            string pattern = raw;
            if (fixedStrings) pattern = Regex.Escape(pattern);
            if (wordRegexp) pattern = "\\b" + pattern + "\\b";
            // -x / --line-regexp: anchor to the whole line.
            if (lineRegexp) pattern = "^(?:" + pattern + ")$";
            parts.Add(BashRuntime.TranslatePosixClasses(pattern));
        }
        string combined = parts.Count == 1 ? parts[0] : string.Join("|", parts.Select(p => "(?:" + p + ")"));

        var regexOpts = RegexOptions.None;
        if (effectiveIgnore) regexOpts |= RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            // .NET has no POSIX bracket classes; without this `[[:digit:]]` matches
            // nothing and reports no error (see BashRuntime.TranslatePosixClasses).
            regex = new Regex(combined, regexOpts);
        }
        catch (ArgumentException ex)
        {
            FileSystemHelpers.WriteBashError(this, $"rg: invalid regular expression: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        // --- Pipeline mode ---
        if (_pipeline.Count > 0 && fileOperands.Count == 0)
        {
            RunPipelineMode(regex, invertMatch, countOnly, onlyMatching, plan.LineNumberMode == true);
            return;
        }

        // --- File mode (recursive by default; cwd if no operands) ---
        RunFileMode(regex, fileOperands, invertMatch, showLineNumbers, heading, countOnly,
            filesOnly, onlyMatching, includeHidden, noIgnore, plan.Globs,
            beforeContext, afterContext);
    }

    /// <summary>
    /// Is this output headed for a terminal? ripgrep keys its defaults (line numbers, headings, colour) on
    /// it. The host process's own stdout is always a pipe/IPC frame, so the signal is the launcher's
    /// hand-off: <c>PSBASH_PTY_ATTACHED=1</c> (interactive shell under a PTY). <c>PSBASH_RG_TTY</c>
    /// (<c>1</c>/<c>0</c>) overrides it (tests, wrappers that know better). Known limit: an interactive
    /// <c>rg x | less</c> still counts as a terminal; <c>-c</c> one-shot runs never do.
    /// </summary>
    internal static bool StdoutIsTerminal()
    {
        var o = Environment.GetEnvironmentVariable("PSBASH_RG_TTY")?.Trim();
        if (o is { Length: > 0 })
            return BashRuntime.IsHostConfigTruthy("PSBASH_RG_TTY");
        return BashRuntime.IsHostConfigTruthy("PSBASH_PTY_ATTACHED");
    }

    /// <summary>Smart-case test: does the raw pattern contain an uppercase letter?</summary>
    private static bool PatternHasUpper(string pattern)
    {
        foreach (var c in pattern)
        {
            if (char.IsUpper(c)) return true;
        }
        return false;
    }

    private bool TryRunNativeRg(string[] nativeArgs)
    {
        string? nativeSource = null;
        try
        {
            var probe = InvokeCommand.InvokeScript(
                "Get-Command rg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1");
            if (probe.Count > 0 && probe[0] != null)
            {
                nativeSource = probe[0].Properties["Source"]?.Value as string;
            }
        }
        catch
        {
            // Probe failed; treat as no native rg.
        }

        if (string.IsNullOrEmpty(nativeSource))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = nativeSource!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = SessionState.Path.CurrentLocation.Path,
            };
            // The argv, verbatim and in order (decoy-bound switches of a DIRECT PowerShell call were
            // re-injected in front by EndProcessing; transpiled bash never has any).
            foreach (var arg in nativeArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            // Bounded spawn + concurrent stdout/stderr drain + kill-tree on timeout
            // (BashRuntime.RunChildProcess). The old code drained stderr only AFTER
            // the stdout ReadLine loop, so a large stderr burst from rg could fill
            // its pipe buffer and deadlock; and the unbounded WaitForExit could
            // wedge the host. Native rg here never reads stdin (only reached when
            // _pipeline.Count == 0), so the helper's closed stdin is safe.
            var spawn = BashRuntime.RunChildProcess(psi);

            // Emit one object per stdout line — ReadLine semantics: split on \n with
            // no spurious trailing empty line.
            var outText = spawn.Stdout.Replace("\r\n", "\n");
            if (outText.EndsWith('\n'))
                outText = outText.Substring(0, outText.Length - 1);
            if (outText.Length > 0)
            {
                foreach (var ln in outText.Split('\n'))
                    WriteObject(BashRuntime.NewBashObject(ln));
            }
            FileSystemHelpers.SetLastExitCode(this, spawn.ExitCode);
            return true;
        }
        catch
        {
            // Native invocation failed; fall through to internal engine.
            return false;
        }
    }

    private static bool NativeRgPassthroughEnabled() => BashRuntime.IsHostConfigTruthy("PSBASH_RG_NATIVE");

    private void RunPipelineMode(Regex regex, bool invertMatch, bool countOnly, bool onlyMatching, bool lineNumbers)
    {
        int matchCount = 0;
        int lineNo = 0;
        string Pfx() => lineNumbers ? lineNo + ":" : "";

        foreach (var item in _pipeline)
        {
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');

            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    lineNo++;
                    bool isMatch = regex.IsMatch(subLine);
                    if (invertMatch) isMatch = !isMatch;
                    if (isMatch)
                    {
                        matchCount++;
                        if (!countOnly)
                        {
                            if (onlyMatching)
                            {
                                foreach (Match m in regex.Matches(subLine))
                                {
                                    WriteObject(BashRuntime.NewBashObject(Pfx() + m.Value));
                                }
                            }
                            else
                            {
                                WriteObject(BashRuntime.NewBashObject(Pfx() + subLine));
                            }
                        }
                    }
                }
            }
            else
            {
                lineNo++;
                bool isMatch = regex.IsMatch(trimmed);
                if (invertMatch) isMatch = !isMatch;
                if (isMatch)
                {
                    matchCount++;
                    if (!countOnly)
                    {
                        if (onlyMatching)
                        {
                            foreach (Match m in regex.Matches(trimmed))
                            {
                                WriteObject(BashRuntime.NewBashObject(Pfx() + m.Value));
                            }
                        }
                        else if (lineNumbers)
                        {
                            WriteObject(BashRuntime.NewBashObject(Pfx() + trimmed));
                        }
                        else
                        {
                            // Plain rg is a FILTER: pass the original object through (rg
                            // terminates every output line, so strip a stale missing-newline flag).
                            WriteObject(BashRuntime.PassTerminated(item));
                        }
                    }
                }
            }
        }

        // ripgrep: 0 = a line matched, 1 = none.
        FileSystemHelpers.SetLastExitCode(this, matchCount == 0 ? 1 : 0);

        if (countOnly)
        {
            WriteObject(BashRuntime.NewBashObject(matchCount.ToString()));
        }
    }

    private void RunFileMode(
        Regex regex, List<string> fileOperands, bool invertMatch,
        bool showLineNumbers, bool heading, bool countOnly, bool filesOnly, bool onlyMatching,
        bool includeHidden, bool noIgnore, List<string> globs,
        int beforeContext, int afterContext)
    {
        var searchTargets = fileOperands.Count > 0 ? fileOperands : new List<string> { "." };
        // ripgrep filters by default (.gitignore + hidden + .git). We approximate
        // that with the shared directory-prune set; PSBASH_SEARCH_NO_IGNORE or the
        // --no-ignore / -u flags turn it off. Pruning happens BEFORE descent in
        // EnumerateSearchFiles, so .git / bin / obj / node_modules are never
        // walked — this is the fix for the old AllDirectories walk that descended
        // into them and went silent long enough to trip the host idle-timeout.
        bool includeIgnored = noIgnore || BashFileSystem.DefaultFilteringDisabled();

        // Resolve each target to either a single file or a lazy directory walk.
        // multipleFiles must be known before emitting, so derive it from the
        // target shapes (any directory target, or more than one file target).
        var sources = new List<IEnumerable<string>>();
        bool pathError = false;
        bool anyTargetIsDir = false;
        int fileTargetCount = 0;

        foreach (var target in searchTargets)
        {
            string resolved;
            try
            {
                resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(target);
            }
            catch
            {
                FileSystemHelpers.WriteBashError(this, $"rg: {target}: No such file or directory (os error 2)");
                pathError = true;
                continue;
            }

            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                FileSystemHelpers.WriteBashError(this, $"rg: {target}: No such file or directory (os error 2)");
                pathError = true;
                continue;
            }

            if (Directory.Exists(resolved))
            {
                anyTargetIsDir = true;
                sources.Add(WalkSearchDir(resolved, includeIgnored, includeHidden, globs));
            }
            else
            {
                fileTargetCount++;
                sources.Add(new[] { resolved });
            }
        }

        bool multipleFiles = anyTargetIsDir || fileTargetCount > 1;
        var matchedFiles = new List<string>();
        var perFileCounts = new Dictionary<string, int>();
        // Files actually read, in walk order — drives the multi-file count pass.
        var scanned = new List<string>();
        int totalMatchCount = 0;
        // Binary files are skipped (NUL probe) like ripgrep; the same
        // PSBASH_SEARCH_NO_IGNORE escape hatch searches them too.
        bool skipBinary = !BashFileSystem.DefaultFilteringDisabled();
        // ripgrep heading layout: the path on its own line, the file's matches beneath it, a blank line
        // between files. Only when several files are searched; the lines then carry no path prefix.
        bool useHeading = heading && multipleFiles;
        bool prefixPath = multipleFiles && !useHeading;
        bool anyHeading = false;

        foreach (var source in sources)
        foreach (var filePath in source)
        {
            var lines = ReadFileLines(filePath, skipBinary);
            if (lines == null) continue;
            scanned.Add(filePath);

            var matchIndices = new List<int>();
            for (int li = 0; li < lines.Length; li++)
            {
                bool isMatch = regex.IsMatch(lines[li]);
                if (invertMatch) isMatch = !isMatch;
                if (isMatch) matchIndices.Add(li);
            }

            int fileMatchCount = matchIndices.Count;
            totalMatchCount += fileMatchCount;
            perFileCounts[filePath] = fileMatchCount;

            if (filesOnly)
            {
                if (fileMatchCount > 0) matchedFiles.Add(filePath);
                continue;
            }

            if (countOnly) continue;

            var emitLines = new SortedSet<int>();
            foreach (var mi in matchIndices)
            {
                int start = Math.Max(0, mi - beforeContext);
                int end = Math.Min(lines.Length - 1, mi + afterContext);
                for (int li = start; li <= end; li++) emitLines.Add(li);
            }

            if (useHeading && emitLines.Count > 0)
            {
                if (anyHeading) WriteObject(BashRuntime.NewBashObject(""));
                WriteObject(BashRuntime.NewBashObject(filePath));
                anyHeading = true;
            }

            foreach (var li in emitLines)
            {
                string line = lines[li];
                int lineNum = li + 1;

                if (onlyMatching && matchIndices.Contains(li))
                {
                    foreach (Match m in regex.Matches(line))
                    {
                        string matchText = m.Value;
                        string bashText = BuildBashText(filePath, lineNum, matchText, prefixPath, showLineNumbers);
                        WriteObject(BuildRgMatch(filePath, lineNum, line, bashText));
                    }
                    continue;
                }

                string bt = BuildBashText(filePath, lineNum, line, prefixPath, showLineNumbers);
                WriteObject(BuildRgMatch(filePath, lineNum, line, bt));
            }
        }

        // ripgrep: 0 = a line matched, 1 = none, 2 = an error (an unreadable path) even when others matched.
        FileSystemHelpers.SetLastExitCode(this, pathError ? 2 : totalMatchCount == 0 ? 1 : 0);

        if (filesOnly)
        {
            foreach (var fp in matchedFiles)
            {
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
    /// Lazy directory walk for one search-root directory: the shared
    /// dir-pruning enumerator (<see cref="BashFileSystem.EnumerateSearchFiles"/>)
    /// with the repeatable <c>-g</c>/<c>--glob</c> filename filters applied: a file must match one of
    /// the positive globs (when any were given) and none of the <c>!negated</c> ones. Kept as
    /// an iterator so the caller streams matches as files are visited.
    /// </summary>
    private static IEnumerable<string> WalkSearchDir(
        string root, bool includeIgnored, bool includeHidden, List<string> globs)
    {
        var include = new List<WildcardPattern>();
        var exclude = new List<WildcardPattern>();
        foreach (var g in globs)
        {
            if (g.StartsWith('!') && g.Length > 1) exclude.Add(WildcardPattern.Get(g.Substring(1), WildcardOptions.IgnoreCase));
            else include.Add(WildcardPattern.Get(g, WildcardOptions.IgnoreCase));
        }

        foreach (var fp in BashFileSystem.EnumerateSearchFiles(root, includeIgnored, includeHidden))
        {
            string name = Path.GetFileName(fp);
            bool skip = false;
            foreach (var g in exclude) { if (g.IsMatch(name)) { skip = true; break; } }
            if (skip) continue;
            if (include.Count > 0)
            {
                bool hit = false;
                foreach (var g in include) { if (g.IsMatch(name)) { hit = true; break; } }
                if (!hit) continue;
            }
            yield return fp;
        }
    }

    private static string BuildBashText(string filePath, int lineNum, string body, bool multipleFiles, bool showLineNumbers)
    {
        if (multipleFiles && showLineNumbers) return $"{filePath}:{lineNum}:{body}";
        if (multipleFiles) return $"{filePath}:{body}";
        if (showLineNumbers) return $"{lineNum}:{body}";
        return body;
    }

    /// <summary>
    /// Build a typed <c>PsBash.RgMatch</c> PSObject (oracle parity).
    /// </summary>
    private static PSObject BuildRgMatch(string fileName, int lineNumber, string line, string bashText)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.RgMatch");
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty("LineNumber", lineNumber));
        obj.Properties.Add(new PSNoteProperty("Line", line));
        obj.Properties.Add(new PSNoteProperty("BashText", bashText));
        return obj;
    }

    private static string[]? ReadFileLines(string path, bool skipBinary)
    {
        try
        {
            // null => binary (NUL in first 8 KB) or IO error => caller skips it.
            if (skipBinary && BashFileSystem.IsBinary(path)) return null;
            var list = new List<string>();
            foreach (var l in BashFileSystem.ReadLines(path)) list.Add(l);
            return list.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
