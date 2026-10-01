using System.Linq;
using System.Management.Automation;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashGrep</c> function
/// (REFACTOR-2 Phase 4 follow-on). Searches input lines (pipeline or files)
/// for a pattern, reproducing GNU coreutils <c>grep</c> byte-for-byte against
/// the original psm1 oracle.
///
/// Option parsing is the shared ORDERED parser (<see cref="ArgParser"/>, GNU grep 3.11 option table —
/// see <see cref="Plan"/>): bundles (<c>-ie PAT</c>, <c>-ePAT</c>, <c>-1n</c>), repeated <c>-e</c> /
/// <c>-f</c> / <c>--include</c> / <c>--exclude</c> / <c>--exclude-dir</c>, <c>-A/-B/-C N</c> and the
/// <c>-NUM</c> shorthand, <c>--color[=WHEN]</c>, unique-prefix long options, usage errors exit 2.
/// The first operand is the PATTERN unless <c>-e</c>/<c>-f</c> was given.
///
/// <para><b>Direct PowerShell calls.</b> The transpiler single-quotes every flag
/// (<c>PsEmitter.OrderedArgCommands</c>) so the whole argv reaches <see cref="Arguments"/> verbatim and in
/// order. Typed directly at PowerShell the binder still intercepts colliding bare flags, so the decoys
/// <see cref="I"/> <see cref="V"/> <see cref="C"/> <see cref="W"/> <see cref="P"/> <see cref="O"/>
/// <see cref="D"/> <see cref="A"/> <see cref="B"/> <see cref="E"/> remain and are re-injected as ordinary
/// tokens before parsing; the psm1 <c>Invoke-BashGrep</c> proxy hands every argument over as a literal
/// string so a repeated <c>-e</c> or a bundle like <c>-ve</c> never reaches the binder.</para>
/// Output is a typed <c>PsBash.GrepMatch</c> PSObject per match with
/// <c>FileName</c>, <c>LineNumber</c>, <c>Line</c>, and <c>BashText</c>
/// properties (oracle parity). <c>-c</c>/<c>-l</c> emit bare-string PSObjects
/// via <see cref="BashRuntime.NewBashObject"/>. Exit code: <c>$LASTEXITCODE</c>
/// is set to 0 on any match, 1 on no match, 2 on an unreadable operand (grep semantics) via
/// <see cref="FileSystemHelpers.SetLastExitCode"/>. Errors route through
/// <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord); an unreadable operand makes
/// the status 2 even if another operand matched.
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

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-d</c> (--directories) / <c>-D</c>
    /// (--devices). Bare <c>-d</c> silently bound <c>-Debug</c> and <c>-D</c> likewise,
    /// so the classifier never fired. A single switch catches both (case-insensitive
    /// binder); re-injected as <c>-d</c> so grep's scan emits exit 2. Both are
    /// unsupported, so the shared representative token is harmless.
    /// </summary>
    [Parameter] public SwitchParameter D { get; set; }

    /// <summary>
    /// Set by the psm1 proxy only. A cmdlet that stops its upstream (-q, -m N) raises the engine's stop-upstream
    /// signal, which is routed to the pipeline that CONTAINS this command; behind the proxy's steppable pipeline that
    /// is not the caller's pipeline, so the signal would kill the whole statement. Behind the proxy the cmdlet just
    /// ignores the rest of its input instead (the transpiled path calls the cmdlet directly and stops the upstream).
    /// </summary>
    [Parameter(DontShow = true)] public SwitchParameter PsBashProxy { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>GNU's label for stdin in a prefixed line (<c>grep -H x &lt; f</c>).</summary>
    private const string StdinLabel = "(standard input)";

    /// <summary>GNU's default <c>--group-separator</c>, printed between non-adjacent context groups.</summary>
    private const string GroupSeparator = "--";

    /// <summary>A context group was already printed (a later group, even in another file, gets a separator).</summary>
    private bool _contextGroupEmitted;

    /// <summary>-A/-B/-C/-NUM present (possibly 0): the buffered window path runs and "--" separates groups.</summary>
    private bool _contextRequested;

    /// <summary>
    /// An input operand could not be read (missing / unreadable). GNU grep then exits 2
    /// whatever else matched — even under <c>-s</c> — except <c>-q</c> with a selected
    /// line (0). Sticky, because the final match-count status would otherwise overwrite it.
    /// </summary>
    private bool _operandReadError;

    private const string OptBasic = "G", OptExtended = "E", OptFixed = "F", OptPerl = "P",
        OptRegexp = "regexp", OptFile = "file", OptIgnoreCase = "ignore-case",
        OptNoIgnoreCase = "no-ignore-case", OptWord = "word", OptLineRegexp = "line-regexp",
        OptNoMessages = "no-messages", OptInvert = "invert", OptMax = "max-count",
        OptLineNumber = "line-number", OptLineBuffered = "line-buffered", OptWithName = "with-filename",
        OptNoName = "no-filename", OptOnly = "only-matching", OptQuiet = "quiet",
        OptRecursive = "recursive", OptInclude = "include", OptExclude = "exclude",
        OptExcludeDir = "exclude-dir", OptExcludeFrom = "exclude-from", OptFilesWithout = "files-without",
        OptFilesWith = "files-with", OptCount = "count", OptAfter = "after", OptBefore = "before",
        OptContext = "context", OptContextNum = "context-num", OptColor = "color", OptBinary = "binary";

    /// <summary>
    /// GNU grep options that are valid but not implemented by ps-bash. Refused loudly (exit 2) rather than
    /// silently dropped. Written as typed; the <c>=VALUE</c> suffix of a long option is not part of the name.
    /// </summary>
    private static readonly string[] GrepValidButUnsupported =
    {
        "-z", "-Z", "-a", "-b", "-D", "-d", "-T", "-u", "-I",
        "--null-data", "--null", "--text", "--byte-offset", "--binary-files", "--devices",
        "--directories", "--initial-tab", "--label", "--group-separator", "--no-group-separator",
        "--unix-byte-offsets",
    };

    /// <summary>
    /// grep's option surface (GNU grep 3.11). <c>-y</c> is the obsolete <c>-i</c>; <c>-V</c> is
    /// <c>--version</c>; <c>--color</c>/<c>--colour</c> take an optional attached WHEN (accepted, no colouring);
    /// <c>-NUM</c> anywhere in a bundle is the context shorthand. Ambiguity lists follow GNU's
    /// <c>long_options[]</c> table order (<c>--ex</c> = extended-regexp, exclude, exclude-from, exclude-dir).
    /// </summary>
    private static readonly OptSpecSet GrepSpec = new(
        new[]
        {
            new OptSpec(OptBasic, 'G', "basic-regexp"),
            new OptSpec(OptExtended, 'E', "extended-regexp"),
            new OptSpec(OptFixed, 'F', "fixed-strings"),
            new OptSpec(OptFixed, '\0', "fixed-regexp"),
            new OptSpec(OptPerl, 'P', "perl-regexp"),
            new OptSpec(OptRegexp, 'e', "regexp", OptKind.Value),
            new OptSpec(OptFile, 'f', "file", OptKind.Value),
            new OptSpec(OptIgnoreCase, 'i', "ignore-case"),
            new OptSpec(OptIgnoreCase, 'y', null),
            new OptSpec(OptNoIgnoreCase, '\0', "no-ignore-case"),
            new OptSpec(OptWord, 'w', "word-regexp"),
            new OptSpec(OptLineRegexp, 'x', "line-regexp"),
            new OptSpec(OptNoMessages, 's', "no-messages"),
            new OptSpec(OptInvert, 'v', "invert-match"),
            new OptSpec(OptMax, 'm', "max-count", OptKind.Value),
            new OptSpec(OptLineNumber, 'n', "line-number"),
            new OptSpec(OptLineBuffered, '\0', "line-buffered"),
            new OptSpec(OptWithName, 'H', "with-filename"),
            new OptSpec(OptNoName, 'h', "no-filename"),
            new OptSpec(OptOnly, 'o', "only-matching"),
            new OptSpec(OptQuiet, 'q', "quiet"),
            new OptSpec(OptQuiet, '\0', "silent"),
            new OptSpec(OptRecursive, 'r', "recursive"),
            new OptSpec(OptRecursive, 'R', "dereference-recursive"),
            new OptSpec(OptInclude, '\0', "include", OptKind.Value),
            new OptSpec(OptExclude, '\0', "exclude", OptKind.Value),
            new OptSpec(OptExcludeDir, '\0', "exclude-dir", OptKind.Value),
            new OptSpec(OptExcludeFrom, '\0', "exclude-from", OptKind.Value),
            new OptSpec(OptFilesWithout, 'L', "files-without-match"),
            new OptSpec(OptFilesWith, 'l', "files-with-matches"),
            new OptSpec(OptCount, 'c', "count"),
            new OptSpec(OptAfter, 'A', "after-context", OptKind.Value),
            new OptSpec(OptBefore, 'B', "before-context", OptKind.Value),
            new OptSpec(OptContext, 'C', "context", OptKind.Value),
            new OptSpec(OptColor, '\0', "color", OptKind.OptionalValue),
            new OptSpec(OptColor, '\0', "colour", OptKind.OptionalValue),
            new OptSpec(OptBinary, 'U', "binary"),
            new OptSpec(OptSpecSet.VersionId, 'V', "version"),
        },
        GrepValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        usageExitCode: 2,
        bundleDigitsId: OptContextNum,
        longOptionOrder: new[]
        {
            "basic-regexp", "extended-regexp", "fixed-regexp", "fixed-strings", "perl-regexp",
            "after-context", "before-context", "binary-files", "byte-offset", "context", "color", "colour",
            "count", "dereference-recursive", "devices", "directories", "exclude", "exclude-from",
            "exclude-dir", "file", "files-with-matches", "files-without-match", "group-separator", "help",
            "include", "ignore-case", "no-ignore-case", "initial-tab", "label", "line-buffered",
            "line-number", "line-regexp", "max-count", "no-filename", "no-group-separator", "no-messages",
            "null", "null-data", "only-matching", "quiet", "recursive", "regexp", "invert-match", "silent",
            "text", "binary", "unix-byte-offsets", "version", "with-filename", "word-regexp",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, GrepSpec);

    /// <summary>GNU <c>--color=WHEN</c> spellings (grep accepts all of these; ps-bash never colours).</summary>
    private static readonly string[] ColorWhenWords =
        { "always", "yes", "force", "never", "no", "none", "auto", "tty", "if-tty" };

    internal sealed class GrepArgs
    {
        public ParsedArgs Parsed = null!;
        public string? Error;
        public int ErrorExit = 2;

        public bool IgnoreCase, Invert, LineNumbers, Count, Quiet, Recursive, FilesWith, FilesWithout;
        public bool Word, OnlyMatching, ForceFileName, SuppressFileName, LineRegexp, NoMessages;
        public bool Extended, Fixed;

        /// <summary>Matcher letter (<c>G E F P</c>) or <c>'\0'</c>. GNU refuses two different matchers.</summary>
        public char Matcher;

        public int MaxMatches = int.MaxValue;
        public int After, Before;

        /// <summary>Any of -A/-B/-C/-NUM was given (even 0): GNU then prints "--" between groups.</summary>
        public bool ContextRequested;

        /// <summary>Every <c>-e PATTERN</c> / <c>-f FILE</c> in command-line order.</summary>
        public List<(bool IsFile, string Value)> PatternSources = new();

        public List<string> Include = new(), Exclude = new(), ExcludeDir = new(), ExcludeFromFiles = new();
        public List<string> Operands = new();

        public bool SawPatternFile => PatternSources.Exists(s => s.IsFile);

        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + interpret + validate, in GNU's order (first error wins). Semantics taken from GNU grep 3.11:
    /// <c>-A/-B</c> beat <c>-C</c>/<c>-NUM</c> regardless of order; <c>-l</c>/<c>-L</c> and <c>-h</c>/<c>-H</c>
    /// are last-wins; two DIFFERENT matchers among <c>-E -F -G -P</c> are
    /// "conflicting matchers specified"; a bad context length is "X: invalid context length argument" and a bad
    /// <c>-m</c> "invalid max count" (both exit 2) while a NEGATIVE <c>-m</c> means unlimited.
    /// </summary>
    internal static GrepArgs Plan(string[] args)
    {
        var g = new GrepArgs { Parsed = ScanArgs(args) };
        g.Operands = g.Parsed.Operands();
        if (g.Parsed.HasError) return g;
        if (g.Parsed.Has(OptSpecSet.HelpId) || g.Parsed.Has(OptSpecSet.VersionId)) return g;

        int after = -1, before = -1, both = -1;

        foreach (var tok in g.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            string? v = tok.Value;
            switch (tok.OptId)
            {
                case OptBasic: case OptExtended: case OptFixed: case OptPerl:
                {
                    char m = tok.OptId![0];
                    if (g.Matcher != '\0' && g.Matcher != m)
                    {
                        g.Error = "grep: conflicting matchers specified";
                        return g;
                    }
                    g.Matcher = m;
                    g.Extended = m is 'E' or 'P';   // -P runs on the .NET regex path, like -E
                    g.Fixed = m == 'F';
                    break;
                }
                case OptRegexp: g.PatternSources.Add((false, v!)); break;
                case OptFile: g.PatternSources.Add((true, v!)); break;
                case OptIgnoreCase: g.IgnoreCase = true; break;
                case OptNoIgnoreCase: g.IgnoreCase = false; break;
                case OptWord: g.Word = true; break;
                case OptLineRegexp: g.LineRegexp = true; break;
                case OptNoMessages: g.NoMessages = true; break;
                case OptInvert: g.Invert = true; break;
                case OptLineNumber: g.LineNumbers = true; break;
                case OptWithName: g.ForceFileName = true; g.SuppressFileName = false; break;
                case OptNoName: g.SuppressFileName = true; g.ForceFileName = false; break;
                case OptOnly: g.OnlyMatching = true; break;
                case OptQuiet: g.Quiet = true; break;
                case OptCount: g.Count = true; break;
                case OptRecursive: g.Recursive = true; break;
                case OptFilesWith: g.FilesWith = true; g.FilesWithout = false; break;
                case OptFilesWithout: g.FilesWithout = true; g.FilesWith = false; break;
                case OptInclude: g.Include.Add(v!); break;
                case OptExclude: g.Exclude.Add(v!); break;
                case OptExcludeDir: g.ExcludeDir.Add(v!); break;
                case OptExcludeFrom: g.ExcludeFromFiles.Add(v!); break;
                case OptMax:
                {
                    if (!TryParseMaxCount(v!, out int max))
                    {
                        g.Error = "grep: invalid max count";
                        return g;
                    }
                    g.MaxMatches = max;
                    break;
                }
                case OptAfter: case OptBefore: case OptContext: case OptContextNum:
                {
                    if (!TryParseContext(v!, out int n))
                    {
                        g.Error = $"grep: {v}: invalid context length argument";
                        return g;
                    }
                    if (tok.OptId == OptAfter) after = n;
                    else if (tok.OptId == OptBefore) before = n;
                    else both = n;
                    break;
                }
                case OptColor:
                    if (v is not null && Array.IndexOf(ColorWhenWords, v) < 0)
                    {
                        g.Error = $"grep: invalid argument '{v}' for '--color'";
                        return g;
                    }
                    break;
                // OptLineBuffered, OptBinary: accepted no-ops (nothing to flush / no CR stripping here).
            }
        }

        g.ContextRequested = after >= 0 || before >= 0 || both >= 0;
        g.After = after >= 0 ? after : Math.Max(both, 0);
        g.Before = before >= 0 ? before : Math.Max(both, 0);
        return g;
    }

    /// <summary>Context length: digits only (GNU rejects signs/garbage); overflow clamps.</summary>
    private static bool TryParseContext(string s, out int n)
    {
        n = 0;
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (c < '0' || c > '9') return false;
        n = BashRuntime.ParseCountClamped(s);
        return true;
    }

    /// <summary><c>-m NUM</c>: a negative value means "no limit" (GNU 3.11); non-numeric is an error.</summary>
    private static bool TryParseMaxCount(string s, out int max)
    {
        max = int.MaxValue;
        var t = s.Trim();
        bool neg = t.StartsWith('-');
        var digits = neg || t.StartsWith('+') ? t.Substring(1) : t;
        if (digits.Length == 0) return false;
        foreach (char c in digits)
            if (c < '0' || c > '9') return false;
        max = neg ? int.MaxValue : BashRuntime.ParseCountClamped(digits);
        return true;
    }

    /// <summary>
    /// Pipeline mode streams: each record is matched as it arrives (the cmdlet resolves its whole argv in
    /// <see cref="BeginProcessing"/>), so <c>ls | grep x</c> / <c>find | grep -m1 x</c> emit before the producer
    /// finishes and an early-satisfied grep (<c>-q</c>, <c>-m N</c>) stops the upstream. Retained state is
    /// bounded by the context window (-B ring + -A countdown), never the stream length.
    /// </summary>
    protected override void ProcessRecord()
    {
        if (!_pipelineMode || InputObject == null) return;
        if (_pDone)
        {
            FinishPipeline();
            if (!PsBashProxy) UpstreamStop.Throw(this);
            return;
        }

        string text = BashRuntime.GetBashText(InputObject);
        string trimmed = text.TrimEnd('\n');
        if (trimmed.Contains('\n'))
        {
            foreach (var subLine in trimmed.Split('\n'))
            {
                if (_pDone) break;
                FeedPipelineLine(subLine, InputObject, asNewObject: true);
            }
        }
        else
        {
            FeedPipelineLine(trimmed, InputObject, asNewObject: false);
        }

        if (_pDone)
        {
            FinishPipeline();
            if (!PsBashProxy) UpstreamStop.Throw(this);
        }
    }

    protected override void EndProcessing()
    {
        if (_pipelineMode) FinishPipeline();
        else _fileModeRun?.Invoke();
    }

    protected override void BeginProcessing()
    {
        // Direct PowerShell calls: a bare -d/-i/-v/-c/-w/-P/-o/-A/-B/-e binds a declared decoy parameter
        // (or a common parameter) instead of reaching Arguments. Re-inject them as the ordinary tokens
        // they stand for so the ordered parser sees them. Transpiled bash never gets here: the emitter
        // single-quotes every flag (OrderedArgCommands), so they arrive verbatim in Arguments.
        var args = BashRuntime.PrependDecoys(Arguments,
            (D.IsPresent, "-d"), (I.IsPresent, "-i"), (V.IsPresent, "-v"), (C.IsPresent, "-c"),
            (W.IsPresent, "-w"), (P.IsPresent, "-P"), (O.IsPresent, "-o"));
        if (A is not null || B is not null || E is { Length: > 0 })
        {
            var extra = new List<string>();
            if (A is { } av) { extra.Add("-A"); extra.Add(av.ToString()); }
            if (B is { } bv) { extra.Add("-B"); extra.Add(bv.ToString()); }
            if (E != null) foreach (var pat in E) { extra.Add("-e"); extra.Add(pat); }
            extra.AddRange(args);
            args = extra.ToArray();
        }

        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "grep", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "grep", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, plan.ErrorExit);
            return;
        }

        bool ignoreCase = plan.IgnoreCase;
        bool invertMatch = plan.Invert;
        bool showLineNumbers = plan.LineNumbers;
        bool countOnly = plan.Count;
        bool quietMode = plan.Quiet;
        bool recursive = plan.Recursive;
        bool filesOnly = plan.FilesWith;
        bool extendedRegex = plan.Extended;
        bool fixedString = plan.Fixed;
        bool wholeWord = plan.Word;
        bool outputMatchOnly = plan.OnlyMatching;
        bool forceFileName = plan.ForceFileName;
        bool suppressFileName = plan.SuppressFileName;
        int maxMatches = plan.MaxMatches;
        int afterContext = plan.After;
        int beforeContext = plan.Before;
        _contextRequested = plan.ContextRequested;
        bool filesWithoutMatch = plan.FilesWithout;
        bool lineRegexp = plan.LineRegexp;
        bool noMessages = plan.NoMessages;
        // Recursive filename filters (basename fnmatch), GNU grep --include/--exclude/--exclude-dir.
        var includeGlobs = plan.Include;
        var excludeGlobs = plan.Exclude;
        var excludeDirGlobs = plan.ExcludeDir;
        var operands = plan.Operands;

        // -e / -f in command-line order. A missing -f / --exclude-from file is fatal (GNU: exit 2),
        // whatever -s says.
        var patterns = new List<string>();
        foreach (var (isFile, value) in plan.PatternSources)
        {
            if (!isFile) { patterns.Add(value); continue; }
            if (!TryAddLinesFromFile(value, patterns)) { FileSystemHelpers.SetLastExitCode(this, 2); return; }
        }
        foreach (var exFile in plan.ExcludeFromFiles)
        {
            if (!TryAddLinesFromFile(exFile, excludeGlobs)) { FileSystemHelpers.SetLastExitCode(this, 2); return; }
        }
        // True once -f/--file was given: the pattern list is fully determined by the file(s), so the
        // first operand must NOT be reinterpreted as a pattern (an empty pattern file means "match
        // nothing", not "use file as pattern").
        bool sawPatternFile = plan.SawPatternFile;

        // Pattern collection: -e/-f patterns (already accumulated) or first operand.
        if (plan.PatternSources.Count == 0 && operands.Count > 0)
        {
            patterns.Add(operands[0]);
            operands.RemoveAt(0);
        }

        if (patterns.Count == 0)
        {
            // An empty -f pattern file means "match nothing" (exit 1), not a usage error.
            if (sawPatternFile)
            {
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
            FileSystemHelpers.WriteBashError(this, "grep: usage: grep [OPTION]... PATTERNS [FILE]...");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        // The pattern (or -e/-f patterns) is already removed, so operands now holds the file list.
        var fileOperands = operands;

        // Build regex list (OR logic across multiple patterns) via the shared ladder.
        if (!TryBuildRegexes(patterns, fixedString, extendedRegex, wholeWord, lineRegexp,
                ignoreCase, out var regexes, out var invalidRegex))
        {
            FileSystemHelpers.WriteBashError(this, $"grep: invalid regular expression: {invalidRegex}");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        // --- Pipeline mode ---
        if (fileOperands.Count == 0 && !recursive)
        {
            StartPipelineMode(regexes, invertMatch, showLineNumbers, countOnly,
                quietMode, outputMatchOnly, forceFileName, maxMatches,
                beforeContext, afterContext);
            return;
        }

        // --- File mode (incl. recursive) --- runs in EndProcessing (pipeline input is ignored).
        var recursiveFilter = BuildRecursiveFileFilter(includeGlobs, excludeGlobs, excludeDirGlobs);
        _fileModeRun = () => RunFileMode(regexes, fileOperands, recursive, invertMatch, showLineNumbers,
            countOnly, quietMode, filesOnly, filesWithoutMatch, noMessages, outputMatchOnly,
            forceFileName, suppressFileName, maxMatches, beforeContext, afterContext,
            recursiveFilter);
    }

    /// <summary>
    /// Read a file's lines into <paramref name="dest"/> — backing <c>-f</c> (pattern file) and
    /// <c>--exclude-from</c> (glob file). An unreadable file is fatal in GNU grep
    /// (<c>grep: F: No such file or directory</c>, exit 2): reports the error and returns false.
    /// </summary>
    private bool TryAddLinesFromFile(string rawPath, List<string> dest)
    {
        string path;
        try
        {
            path = SessionState.Path.GetUnresolvedProviderPathFromPSPath(
                FileSystemHelpers.NormalizeOperandPath(rawPath));
        }
        catch
        {
            path = rawPath;
        }
        try
        {
            foreach (var line in BashFileSystem.ReadLines(path)) dest.Add(line);
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"grep: {rawPath.Replace('\\', '/')}: No such file or directory");
            return false;
        }
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

    // ---- Streaming pipeline mode -------------------------------------------------------------------
    // State is set once in StartPipelineMode (BeginProcessing) and advanced per record in ProcessRecord.

    private Action? _fileModeRun;
    private bool _pipelineMode, _pDone, _pFinished;
    private List<Regex> _pRegexes = null!;
    private bool _pInvert, _pNumbers, _pCount, _pQuiet, _pOnly, _pForceName, _pContext;
    private int _pMax, _pBefore, _pAfter, _pLineNum, _pMatchCount;
    // Context window: -B ring of the most recent unprinted non-matching lines, -A countdown, the last
    // printed line index (group separator), and "the -m limit was hit, only the trailing window remains".
    private Queue<(int Index, string Text)>? _pRing;
    private int _pAfterLeft, _pLastEmitted = -2;
    private bool _pLimitReached;

    private void StartPipelineMode(
        List<Regex> regexes, bool invertMatch, bool showLineNumbers,
        bool countOnly, bool quietMode, bool outputMatchOnly, bool forceFileName,
        int maxMatches, int beforeContext, int afterContext)
    {
        _pipelineMode = true;
        _pRegexes = regexes;
        _pInvert = invertMatch;
        _pNumbers = showLineNumbers;
        _pCount = countOnly;
        _pQuiet = quietMode;
        _pOnly = outputMatchOnly;
        _pForceName = forceFileName;
        _pMax = maxMatches;
        _pBefore = beforeContext;
        _pAfter = afterContext;
        // -c / -q print no lines, so context is moot for them (they only count).
        _pContext = (_contextRequested || beforeContext > 0 || afterContext > 0) && !countOnly && !quietMode;
        if (_pContext && beforeContext > 0) _pRing = new Queue<(int, string)>(Math.Min(beforeContext, 1024));
        if (maxMatches <= 0) _pDone = true; // -m 0: select nothing (the first record stops the upstream)
    }

    private void FeedPipelineLine(string lineText, PSObject originalItem, bool asNewObject)
    {
        if (_pContext) { FeedContextLine(lineText); return; }

        _pLineNum++;
        Match? matchObject = MatchLine(_pRegexes, lineText, _pInvert, out bool isMatch);
        _ = matchObject;
        if (!isMatch) return;

        _pMatchCount++;
        if (_pMatchCount >= _pMax || _pQuiet) _pDone = true;
        if (_pQuiet || _pCount) return;

        string prefix = "";
        if (_pForceName) prefix = StdinLabel + ':';
        if (_pNumbers) prefix = prefix + _pLineNum + ":";

        if (_pOnly)
        {
            // -o: EVERY non-overlapping match on the line, each on its own output line; the prefix
            // (filename / line number) repeats on each, as GNU does.
            foreach (var mv in AllMatchValues(_pRegexes, lineText))
                WriteObject(BashRuntime.NewBashObject(prefix + mv));
            return;
        }

        if (prefix.Length > 0)
        {
            WriteObject(BashRuntime.NewBashObject(prefix + lineText));
        }
        else if (asNewObject)
        {
            // Multi-line split: a fresh text record (the upstream object spans several lines).
            WriteObject(BashRuntime.NewBashObject(lineText));
        }
        else
        {
            // Plain grep is a FILTER: the selected line IS the input record, so the ORIGINAL
            // object passes through (ls | grep .txt keeps PsBash.LsEntry). grep always
            // terminates its output line, so a stale missing-newline flag is stripped.
            WriteObject(BashRuntime.PassTerminated(originalItem));
        }
    }

    /// <summary>
    /// Context path (-A/-B/-C): streams with a -B ring and an -A countdown. A matched line prints as
    /// <c>NAME:NUM:text</c>, a context line <c>NAME-NUM-text</c>; non-adjacent groups are divided by
    /// <c>--</c>. Context and matches are fresh text records (GNU prints context as plain text).
    /// </summary>
    private void FeedContextLine(string lineText)
    {
        int li = _pLineNum++;
        MatchLine(_pRegexes, lineText, _pInvert, out bool isMatch);

        if (_pLimitReached)
        {
            // -m N reached: only the trailing -A window of the Nth match remains.
            if (_pAfterLeft > 0) { EmitContextLine(li, lineText, isMatch); _pAfterLeft--; }
            if (_pAfterLeft <= 0) _pDone = true;
            return;
        }

        if (isMatch)
        {
            _pMatchCount++;
            if (_pRing is { Count: > 0 })
            {
                foreach (var (ri, rt) in _pRing) EmitContextLine(ri, rt, false);
                _pRing.Clear();
            }
            EmitContextLine(li, lineText, true);
            _pAfterLeft = _pAfter;
            if (_pMatchCount >= _pMax)
            {
                _pLimitReached = true;
                if (_pAfterLeft <= 0) _pDone = true;
            }
        }
        else if (_pAfterLeft > 0)
        {
            EmitContextLine(li, lineText, false);
            _pAfterLeft--;
        }
        else if (_pRing is not null)
        {
            _pRing.Enqueue((li, lineText));
            if (_pRing.Count > _pBefore) _pRing.Dequeue();
        }
    }

    private void EmitContextLine(int li, string lineText, bool isMatchLine)
    {
        char sep = isMatchLine ? ':' : '-';
        string prefix = "";
        if (_pForceName) prefix = StdinLabel + sep;
        if (_pNumbers) prefix = prefix + (li + 1) + sep;

        // Non-adjacent groups are divided by the default group separator "--".
        if (li != _pLastEmitted + 1 && _pLastEmitted >= 0 && !_pOnly)
            WriteObject(BashRuntime.NewBashObject(GroupSeparator));
        _pLastEmitted = li;

        if (_pOnly)
        {
            if (isMatchLine)
                foreach (var mv in AllMatchValues(_pRegexes, lineText))
                    WriteObject(BashRuntime.NewBashObject(prefix + mv));
            return;
        }
        WriteObject(BashRuntime.NewBashObject(prefix + lineText));
    }

    /// <summary>Exit status (and the -c count), once, whether the stream ended or grep stopped it.</summary>
    private void FinishPipeline()
    {
        if (_pFinished) return;
        _pFinished = true;
        FileSystemHelpers.SetLastExitCode(this, _pMatchCount == 0 ? 1 : 0);
        if (_pCount && !_pQuiet)
            WriteObject(BashRuntime.NewBashObject(_pMatchCount.ToString()));
    }
    /// <summary>
    /// All non-overlapping match strings on <paramref name="lineText"/> in
    /// left-to-right order, for grep <c>-o</c>. Gathers matches from every
    /// pattern (multiple <c>-e</c>), orders by start index (longest wins on a
    /// tie), and skips any that overlap an already-emitted match — matching GNU
    /// grep, which advances past each reported match.
    /// </summary>
    private static IEnumerable<string> AllMatchValues(List<Regex> regexes, string lineText)
    {
        var found = new List<(int Index, int Length, string Value)>();
        foreach (var rx in regexes)
            foreach (Match m in rx.Matches(lineText))
                if (m.Length > 0)
                    found.Add((m.Index, m.Length, m.Value));

        found.Sort((a, b) => a.Index != b.Index ? a.Index - b.Index : b.Length - a.Length);

        int consumedTo = -1;
        foreach (var f in found)
        {
            if (f.Index <= consumedTo) continue; // overlaps a reported match
            yield return f.Value;
            consumedTo = f.Index + f.Length - 1;
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
                    // The null device (/dev/null, NUL) is an empty file, not a missing one.
                    if (File.Exists(fp) || Directory.Exists(fp) || FileSystemHelpers.IsNullDevice(fp))
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
                    _operandReadError = true;
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
        bool needsBuffer = _contextRequested || beforeContext > 0 || afterContext > 0 || maxMatches != int.MaxValue;

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
                    if (outputMatchOnly)
                    {
                        // -o: one output line per non-overlapping match (bash), not first only.
                        foreach (var mv in AllMatchValues(regexes, line))
                            EmitGrepLine(filePath, lineNum, line, mv, showFile, showLineNumbers);
                        continue;
                    }
                    EmitGrepLine(filePath, lineNum, line, line, showFile, showLineNumbers);
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

            var matchSet = new HashSet<int>(matchIndices);
            bool contextActive = _contextRequested || beforeContext > 0 || afterContext > 0;
            int prevEmitted = -2;
            foreach (var li in emitLines)
            {
                bool isMatchLine = matchSet.Contains(li);
                if (totalMatchCount > maxMatches && !isMatchLine) break;

                string line = lines[li];
                int lineNum = li + 1;
                // GNU: "--" divides non-adjacent groups, also across files.
                if (contextActive && !outputMatchOnly && li != prevEmitted + 1 && (prevEmitted >= 0 || _contextGroupEmitted))
                    WriteObject(BashRuntime.NewBashObject(GroupSeparator));
                prevEmitted = li;
                if (contextActive) _contextGroupEmitted = true;

                if (outputMatchOnly)
                {
                    // -o: emit every non-overlapping match (bash). Context lines
                    // (non-match) produce no -o output, matching GNU grep.
                    if (isMatchLine)
                        foreach (var mv in AllMatchValues(regexes, line))
                            EmitGrepLine(filePath, lineNum, line, mv, showFile, showLineNumbers);
                    continue;
                }
                EmitGrepLine(filePath, lineNum, line, line, showFile, showLineNumbers, isMatchLine ? ':' : '-');
            }
        }

        if (quietMode)
        {
            FileSystemHelpers.SetLastExitCode(this, _operandReadError ? 2 : 1);
            return;
        }

        FileSystemHelpers.SetLastExitCode(this, _operandReadError ? 2 : totalMatchCount == 0 ? 1 : 0);

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
    /// <summary>
    /// The grep regex-assembly ladder, shared by the cmdlet and the fused-pipeline
    /// streaming stage so the two can never drift: per pattern, fixed → escape,
    /// basic (BRE) → <see cref="EscapeBreMetas"/>, extended → verbatim; then the
    /// optional word (<c>\b…\b</c>) and whole-line (<c>^(?:…)$</c>) wraps;
    /// <c>-i</c> → <see cref="RegexOptions.IgnoreCase"/>. Returns false with
    /// <paramref name="invalidMessage"/> set (no error emission) on the first
    /// pattern that fails to compile — the caller decides how to report it.
    /// </summary>
    internal static bool TryBuildRegexes(
        IReadOnlyList<string> patterns, bool fixedString, bool extendedRegex,
        bool wholeWord, bool lineRegexp, bool ignoreCase,
        out List<Regex> regexes, out string? invalidMessage)
    {
        regexes = new List<Regex>();
        invalidMessage = null;
        var opts = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        foreach (var pat in patterns)
        {
            // POSIX classes first, before BRE escaping: the rewrite introduces regex
            // metacharacters (\s, \w, \p{P}) that must NOT then be escaped as literals.
            // -F is a literal search, so it keeps `[[:digit:]]` verbatim.
            string rp = fixedString ? Regex.Escape(pat)
                      : !extendedRegex ? EscapeBreMetas(BashRuntime.TranslatePosixClasses(pat))
                      : BashRuntime.TranslatePosixClasses(pat);
            if (wholeWord) rp = "\\b" + rp + "\\b";
            if (lineRegexp) rp = "^(?:" + rp + ")$";
            try { regexes.Add(new Regex(rp, opts)); }
            catch (ArgumentException ex) { invalidMessage = ex.Message; return false; }
        }
        return true;
    }

    internal static string EscapeBreMetas(string pat)
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
    internal static Match? MatchLine(List<Regex> regexes, string line, bool invertMatch, out bool isMatch)
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
        bool showFile, bool showLineNumbers, char sep = ':')
    {
        string bashText;
        if (showLineNumbers)
        {
            string prefix = showFile ? (filePath + sep) : "";
            bashText = prefix + lineNum + sep + outputText;
        }
        else if (showFile)
        {
            bashText = filePath + sep + outputText;
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
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
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
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
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
        _operandReadError = true;
        FileSystemHelpers.SetLastExitCode(this, 2);
    }
}
