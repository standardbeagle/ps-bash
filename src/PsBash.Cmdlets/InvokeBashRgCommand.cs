using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for <c>rg</c>: an internal ripgrep-14.1-flavoured search engine (see <see cref="RgSearcher"/> for
/// the search + print side, <see cref="RgWalker"/> for the directory walk, <see cref="RgTypes"/> /
/// <see cref="RgColors"/> / <see cref="RgInput"/> / <see cref="PcreToDotNet"/> for the pieces), with an opt-in
/// passthrough to the real ripgrep binary.
///
/// <para><b>Native passthrough</b>, enabled by setting <c>PSBASH_RG_NATIVE</c> to a truthy value (and a real
/// <c>rg</c> on PATH, and no pipeline input): the argv is forwarded to the binary VERBATIM and in order, before any
/// validation here, and its stdout is emitted line by line. It is no longer needed to reach any ripgrep flag — the
/// internal engine implements them (the options it still refuses are listed in <see cref="RgValidButUnsupported"/>) — it
/// remains the way to get ripgrep's own gitignore handling, PCRE2 engine and the rest of its filtering exactly.</para>
///
/// <para>Option parsing is the shared ORDERED parser (<see cref="ArgParser"/>, ripgrep 14.1 rules: no long option
/// abbreviation, attached short values, options after operands; see <see cref="Plan"/>). The transpiler single-quotes
/// every flag (<c>PsEmitter.OrderedArgCommands</c>), so the whole argv reaches <see cref="Arguments"/> verbatim and in
/// order. Typed directly at PowerShell the binder still intercepts colliding bare flags, so the decoys
/// <see cref="I"/> <see cref="V"/> <see cref="C"/> <see cref="W"/> <see cref="O"/> <see cref="A"/> <see cref="B"/>
/// <see cref="E"/> remain and are re-injected as ordinary tokens before parsing.</para>
///
/// Output: file-mode lines are typed <c>PsBash.RgMatch</c> PSObjects (<c>FileName</c>, <c>LineNumber</c>, <c>Line</c>,
/// <c>BashText</c>); a plain unprefixed pipeline match passes the original object through (rg is a filter there);
/// everything else is a text record. Exit status is ripgrep's: 0 a line matched, 1 none, 2 an error.
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
        OptHeading = "heading", OptNoHeading = "no-heading", OptNoop = "noop",
        OptType = "type", OptTypeNot = "type-not", OptTypeAdd = "type-add", OptTypeClear = "type-clear",
        OptTypeList = "type-list", OptSearchZip = "search-zip", OptMultiline = "multiline",
        OptDotAll = "multiline-dotall", OptPcre2 = "pcre2", OptNoPcre2 = "no-pcre2", OptReplace = "replace",
        OptFile = "file", OptMaxCount = "max-count", OptMaxDepth = "max-depth", OptFollow = "follow",
        OptNull = "null", OptEncoding = "encoding", OptText = "text", OptPretty = "pretty",
        OptByteOffset = "byte-offset", OptQuiet = "quiet", OptWithFilename = "with-filename",
        OptNoFilename = "no-filename", OptFiles = "files", OptStats = "stats", OptVimgrep = "vimgrep",
        OptJson = "json", OptSort = "sort", OptSortr = "sortr", OptPassthru = "passthru", OptColumn = "column",
        OptTrim = "trim", OptIglob = "iglob", OptFilesWithout = "files-without", OptCountMatches = "count-matches",
        OptColors = "colors";

    /// <summary>
    /// ripgrep options the internal engine does not run (refused, exit 2, instead of being silently dropped). With
    /// <c>PSBASH_RG_NATIVE</c> and a real <c>rg</c> on PATH the native binary receives the argv verbatim and runs
    /// them all.
    /// </summary>
    private static readonly string[] RgValidButUnsupported =
    {
        "--debug", "--trace", "--pre", "--pre-glob", "--binary", "--max-filesize", "--ignore-file",
        "--no-ignore-dot", "--no-ignore-global", "--no-ignore-parent", "--no-ignore-files", "--null-data",
        "--no-ignore-exclude", "--no-ignore-messages", "--no-require-git", "--one-file-system", "--crlf",
        "--engine", "--auto-hybrid-regex", "--hostname-bin", "--hyperlink-format", "--field-match-separator",
        "--field-context-separator", "--context-separator", "--no-context-separator", "--path-separator",
        "--regex-size-limit", "--dfa-size-limit", "--no-unicode", "--unicode", "--sort-files",
        "--include-zero", "--max-columns", "--max-columns-preview", "-M", "--no-encoding",
        "--ignore-file-case-insensitive", "--no-ignore-file-case-insensitive",
    };

    /// <summary>
    /// rg's option surface (ripgrep 14.1; lexopt-style: NO long-option abbreviation, attached short
    /// values <c>-A2 -g*.rs -epat</c>, counted <c>-uu</c>, options after operands). Usage errors exit 2.
    /// <c>--color WHEN</c> takes a REQUIRED value (<c>--color never</c> / <c>--color=never</c>);
    /// <c>--no-heading --no-messages --no-config --mmap --no-mmap -j --line-buffered --block-buffered</c> are accepted no-ops.
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
            new OptSpec(OptCountMatches, '\0', "count-matches"),
            new OptSpec(OptFilesWith, 'l', "files-with-matches"),
            new OptSpec(OptFilesWithout, '\0', "files-without-match"),
            new OptSpec(OptLineNumber, 'n', "line-number"),
            new OptSpec(OptNoLineNumber, 'N', "no-line-number"),
            new OptSpec(OptOnly, 'o', "only-matching"),
            new OptSpec(OptInvert, 'v', "invert-match"),
            new OptSpec(OptFixed, 'F', "fixed-strings"),
            new OptSpec(OptUnrestricted, 'u', "unrestricted"),
            new OptSpec(OptHidden, '.', "hidden"),
            new OptSpec(OptNoIgnore, '\0', "no-ignore"),
            new OptSpec(OptNoIgnore, '\0', "no-ignore-vcs"),
            new OptSpec(OptGlob, 'g', "glob", OptKind.Value),
            new OptSpec(OptIglob, '\0', "iglob", OptKind.Value),
            new OptSpec(OptAfter, 'A', "after-context", OptKind.Value),
            new OptSpec(OptBefore, 'B', "before-context", OptKind.Value),
            new OptSpec(OptContext, 'C', "context", OptKind.Value),
            new OptSpec(OptRegexp, 'e', "regexp", OptKind.Value),
            new OptSpec(OptFile, 'f', "file", OptKind.Value),
            new OptSpec(OptColor, '\0', "color", OptKind.Value),
            new OptSpec(OptColors, '\0', "colors", OptKind.Value),
            new OptSpec(OptHeading, '\0', "heading"),
            new OptSpec(OptNoHeading, '\0', "no-heading"),
            new OptSpec(OptType, 't', "type", OptKind.Value),
            new OptSpec(OptTypeNot, 'T', "type-not", OptKind.Value),
            new OptSpec(OptTypeAdd, '\0', "type-add", OptKind.Value),
            new OptSpec(OptTypeClear, '\0', "type-clear", OptKind.Value),
            new OptSpec(OptTypeList, '\0', "type-list"),
            new OptSpec("threads", 'j', "threads", OptKind.Value),
            new OptSpec(OptSearchZip, 'z', "search-zip"),
            new OptSpec(OptMultiline, 'U', "multiline"),
            new OptSpec(OptDotAll, '\0', "multiline-dotall"),
            new OptSpec(OptPcre2, 'P', "pcre2"),
            new OptSpec(OptNoPcre2, '\0', "no-pcre2"),
            new OptSpec(OptReplace, 'r', "replace", OptKind.Value),
            new OptSpec(OptMaxCount, 'm', "max-count", OptKind.Value),
            new OptSpec(OptMaxDepth, 'd', "max-depth", OptKind.Value),
            new OptSpec(OptMaxDepth, '\0', "maxdepth", OptKind.Value),
            new OptSpec(OptFollow, 'L', "follow"),
            new OptSpec(OptNull, '0', "null"),
            new OptSpec(OptEncoding, 'E', "encoding", OptKind.Value),
            new OptSpec(OptText, 'a', "text"),
            new OptSpec(OptPretty, 'p', "pretty"),
            new OptSpec(OptByteOffset, 'b', "byte-offset"),
            new OptSpec(OptQuiet, 'q', "quiet"),
            new OptSpec(OptWithFilename, 'H', "with-filename"),
            new OptSpec(OptNoFilename, 'I', "no-filename"),
            new OptSpec(OptFiles, '\0', "files"),
            new OptSpec(OptStats, '\0', "stats"),
            new OptSpec(OptVimgrep, '\0', "vimgrep"),
            new OptSpec(OptJson, '\0', "json"),
            new OptSpec(OptSort, '\0', "sort", OptKind.Value),
            new OptSpec(OptSortr, '\0', "sortr", OptKind.Value),
            new OptSpec(OptPassthru, '\0', "passthru"),
            new OptSpec(OptPassthru, '\0', "passthrough"),
            new OptSpec(OptColumn, '\0', "column"),
            new OptSpec(OptTrim, '\0', "trim"),
            new OptSpec(OptNoop, '\0', "no-messages"),
            new OptSpec(OptNoop, '\0', "no-config"),
            new OptSpec(OptNoop, '\0', "mmap"),
            new OptSpec(OptNoop, '\0', "no-mmap"),
            new OptSpec("line-buffered", '\0', "line-buffered"),
            new OptSpec("block-buffered", '\0', "block-buffered"),
            new OptSpec(OptSpecSet.HelpId, 'h', "help"),
            new OptSpec(OptSpecSet.VersionId, 'V', "version"),
        },
        RgValidButUnsupported,
        allowAbbrev: false,
        usageExitCode: 2);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, RgSpec);

    private static readonly string[] ColorWhenWords = { "never", "auto", "always", "ansi" };
    private static readonly string[] SortWords = { "none", "path", "modified", "accessed", "created" };

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
        public List<string> Globs = new(), Patterns = new(), Operands = new(), IGlobs = new();

        // ---- the batch-8 options ----
        public List<string> PatternFiles = new(), ColorSpecs = new();
        public string? Replace;
        public int MaxCount = int.MaxValue;
        public int MaxDepth = -1;
        public bool Quiet, Null, Column, Vimgrep, Json, Stats, Passthru, Trim, Follow, Text, SearchZip, Pcre, Multiline, DotAll;
        public bool ByteOffset, FilesList, FilesWithout, CountMatches, TypeList;
        public bool? WithFilename;
        public string? SortKey;
        public bool SortReverse;
        public string? ColorWhen;
        public string? EncodingLabel;
        public RgTypes.TypeSet Types = new();

        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    private static bool TryParseUnsigned(string s, out int n)
    {
        n = 0;
        if (s.Length == 0) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        n = BashRuntime.ParseCountClamped(s);
        return true;
    }

    /// <summary>
    /// Scan + interpret. <c>-e PATTERN</c> (repeatable, OR) replaces the first-operand pattern; the last of
    /// <c>-i -s -S</c> wins; <c>-A/-B</c> beat <c>-C</c> in any order; <c>-uu</c> also includes hidden files;
    /// a non-numeric context length / <c>-m</c> / <c>--max-depth</c>, an unknown <c>--color</c> WHEN, <c>--sort</c> key,
    /// <c>--colors</c> spec, <c>-E</c> label or file type is a usage error (exit 2). <c>--column</c>/<c>--vimgrep</c>/<c>-p</c>
    /// turn line numbers on at their position (a later <c>-N</c> wins, as in ripgrep).
    /// </summary>
    internal static RgArgs Plan(string[] args)
    {
        var r = new RgArgs { Parsed = ScanArgs(args) };
        r.Operands = r.Parsed.Operands();
        if (r.Parsed.HasError) return r;
        if (r.Parsed.Has(OptSpecSet.HelpId) || r.Parsed.Has(OptSpecSet.VersionId)) return r;

        var selections = new List<(bool Negate, string Name)>();
        var colorSpecs = r.ColorSpecs;
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
                case OptCountMatches: r.CountMatches = true; break;
                case OptFilesWith: r.FilesOnly = true; break;
                case OptFilesWithout: r.FilesWithout = true; break;
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
                case OptIglob: r.IGlobs.Add(v!); break;
                case OptRegexp: r.Patterns.Add(v!); break;
                case OptFile: r.PatternFiles.Add(v!); break;
                case OptColors: colorSpecs.Add(v!); break;
                case OptType: selections.Add((false, v!)); break;
                case OptTypeNot: selections.Add((true, v!)); break;
                case OptTypeAdd:
                    if (r.Types.Add(v!) is { } addErr) { r.Error = $"rg: error parsing flag --type-add: {addErr}"; return r; }
                    break;
                case OptTypeClear: r.Types.Clear(v!); break;
                case OptTypeList: r.TypeList = true; break;
                case OptSearchZip: r.SearchZip = true; break;
                case OptMultiline: r.Multiline = true; break;
                case OptDotAll: r.DotAll = true; r.Multiline = true; break;
                case OptPcre2: r.Pcre = true; break;
                case OptNoPcre2: r.Pcre = false; break;
                case OptReplace: r.Replace = v; break;
                case OptFollow: r.Follow = true; break;
                case OptNull: r.Null = true; break;
                case OptText: r.Text = true; break;
                case OptByteOffset: r.ByteOffset = true; break;
                case OptQuiet: r.Quiet = true; break;
                case OptWithFilename: r.WithFilename = true; break;
                case OptNoFilename: r.WithFilename = false; break;
                case OptFiles: r.FilesList = true; break;
                case OptStats: r.Stats = true; break;
                case OptJson: r.Json = true; break;
                case OptTrim: r.Trim = true; break;
                case OptPassthru: r.Passthru = true; break;
                case OptColumn: r.Column = true; r.LineNumberMode = true; break;
                case OptVimgrep: r.Vimgrep = true; r.Column = true; r.LineNumberMode = true; break;
                case OptPretty: r.ColorWhen = "always"; r.HeadingMode = true; r.LineNumberMode = true; break;
                case OptEncoding:
                    if (!RgInput.TryResolveEncoding(v!, out _, out _, out var encErr))
                    {
                        r.Error = $"rg: error parsing flag -E: {encErr}";
                        return r;
                    }
                    r.EncodingLabel = v;
                    break;
                case OptSort or OptSortr:
                    if (Array.IndexOf(SortWords, v) < 0)
                    {
                        r.Error = $"rg: error parsing flag --{tok.OptId}: choice '{v}' is unrecognized";
                        return r;
                    }
                    r.SortKey = v;
                    r.SortReverse = tok.OptId == OptSortr;
                    break;
                case OptMaxCount:
                    if (!TryParseUnsigned(v!, out int mc))
                    {
                        r.Error = "rg: error parsing flag -m: value is not a valid number: invalid digit found in string";
                        return r;
                    }
                    r.MaxCount = mc;
                    break;
                case OptMaxDepth:
                    if (!TryParseUnsigned(v!, out int md))
                    {
                        r.Error = "rg: error parsing flag --max-depth: value is not a valid number: invalid digit found in string";
                        return r;
                    }
                    r.MaxDepth = md;
                    break;
                case OptColor:
                    if (Array.IndexOf(ColorWhenWords, v) < 0)
                    {
                        r.Error = $"rg: error parsing flag --color: choice '{v}' is unrecognized";
                        return r;
                    }
                    r.ColorWhen = v;
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

        // Types: definitions first (above), then selections, so `-t foo --type-add foo:*.x` and `--type-clear` order never matters.
        foreach (var (negate, name) in selections)
        {
            bool ok = negate ? r.Types.Negate(name) : r.Types.Select(name);
            if (!ok) { r.Error = $"rg: unrecognized file type: {name}"; return r; }
        }
        var probe = new RgColors();
        if (probe.Apply(colorSpecs) is { } colorErr)
        {
            r.Error = $"rg: error parsing flag --colors: {colorErr}";
            return r;
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

        if (plan.TypeList)
        {
            foreach (var l in plan.Types.ListLines()) WriteObject(BashRuntime.NewBashObject(l));
            return;
        }

        var total = Stopwatch.StartNew();
        try { Run(plan, total); }
        catch (Exception ex) when (FileSystemHelpers.IsPipelineStop(ex)) { throw; }
    }

    private void Sink(RgEmit e)
    {
        if (e.Original is PSObject po) WriteObject(BashRuntime.PassTerminated(po));
        else if (e.File is not null && e.Line is not null) WriteObject(BuildRgMatch(e.File, e.LineNumber, e.Line, e.Text));
        else if (e.Unterminated) WriteObject(BashRuntime.TextRecord(e.Text, true));
        else WriteObject(BashRuntime.NewBashObject(e.Text));
    }

    private bool ColorEnabled(RgArgs plan)
    {
        switch (plan.ColorWhen)
        {
            case "always" or "ansi": return true;
            case "never": return false;
        }
        var o = Environment.GetEnvironmentVariable("PSBASH_RG_COLOR")?.Trim();
        if (o is { Length: > 0 }) return BashRuntime.IsHostConfigTruthy("PSBASH_RG_COLOR");
        // "auto": a real terminal only (the launcher's PTY hand-off). PSBASH_RG_TTY alone is a LAYOUT override.
        return BashRuntime.IsHostConfigTruthy("PSBASH_PTY_ATTACHED");
    }

    /// <summary>Where the rest of the work happens: patterns, regex, inputs, search, stats, exit status.</summary>
    private void Run(RgArgs plan, Stopwatch total)
    {
        var operands = plan.Operands;

        // ---- patterns: -e, -f, or the first operand (not with --files) ----
        var patterns = new List<string>(plan.Patterns);
        bool sawPatternFile = false;
        foreach (var pf in plan.PatternFiles)
        {
            sawPatternFile = true;
            if (!TryReadPatternFile(pf, patterns)) { FileSystemHelpers.SetLastExitCode(this, 2); return; }
        }
        if (!plan.FilesList && patterns.Count == 0 && !sawPatternFile)
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

        // ---- terminal-dependent defaults (see StdoutIsTerminal) ----
        bool tty = StdoutIsTerminal();
        bool pipelineMode = _pipeline.Count > 0 && operands.Count == 0 && !plan.FilesList;
        var colors = ColorEnabled(plan) ? new RgColors() : null;
        colors?.Apply(plan.ColorSpecs);

        var inputs = new List<(string Abs, string Display, bool Explicit)>();
        bool pathError = false;
        bool multipleFiles = false;
        RgSource? stdinSource = null;

        // ---- inputs ----
        var types = plan.Types;
        var walkOpts = new RgWalker.Options
        {
            IncludeIgnored = plan.NoIgnore || BashFileSystem.DefaultFilteringDisabled(),
            IncludeHidden = plan.Hidden,
            Follow = plan.Follow,
            MaxDepth = plan.MaxDepth,
        };
        walkOpts.Accept = BuildAccept(plan, types);

        if (pipelineMode)
        {
            stdinSource = BuildStdinSource(plan);
        }
        else
        {
            var targets = operands.Count > 0 ? operands : new List<string> { "" };
            int fileTargets = 0;
            bool anyDir = false;
            var found = new List<(string Abs, string Display, bool Explicit)>();
            foreach (var target in targets)
            {
                bool implicitRoot = target.Length == 0;
                string abs;
                try
                {
                    abs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(
                        implicitRoot ? "." : FileSystemHelpers.NormalizeOperandPath(target));
                }
                catch
                {
                    FileSystemHelpers.WriteBashError(this, $"rg: {target}: No such file or directory (os error 2)");
                    pathError = true;
                    continue;
                }
                if (Directory.Exists(abs))
                {
                    anyDir = true;
                    foreach (var f in RgWalker.Walk(abs, implicitRoot ? "" : target, walkOpts)) found.Add((f.Abs, f.Display, false));
                }
                else if (File.Exists(abs) || FileSystemHelpers.IsNullDevice(abs))
                {
                    fileTargets++;
                    found.Add((abs, target, true));
                }
                else
                {
                    FileSystemHelpers.WriteBashError(this, $"rg: {target}: No such file or directory (os error 2)");
                    pathError = true;
                }
            }
            multipleFiles = anyDir || fileTargets > 1;
            inputs = found;
            if (plan.SortKey is { } sk && sk != "none") inputs = SortInputs(inputs, sk, plan.SortReverse);
        }

        // ---- --files: list, no search ----
        if (plan.FilesList)
        {
            int printed = 0;
            foreach (var inp in inputs)
            {
                printed++;
                if (plan.Null) WriteObject(BashRuntime.TextRecord(inp.Display + "\0", true));
                else WriteObject(BashRuntime.NewBashObject(inp.Display));
            }
            FileSystemHelpers.SetLastExitCode(this, pathError ? 2 : printed == 0 ? 1 : 0);
            return;
        }

        // ---- regex ----
        if (!TryBuildRegex(plan, patterns, out var regex, out var regexError))
        {
            FileSystemHelpers.WriteBashError(this, regexError!);
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        // ---- settings ----
        bool vimgrep = plan.Vimgrep;
        bool lineNumbers = pipelineMode ? plan.LineNumberMode == true : plan.LineNumberMode ?? tty;
        bool heading = !vimgrep && (plan.HeadingMode ?? tty);
        bool showPath = pipelineMode
            ? plan.WithFilename == true
            : plan.WithFilename ?? (multipleFiles || vimgrep);
        bool json = plan.Json && !plan.CountOnly && !plan.CountMatches && !plan.FilesOnly && !plan.FilesWithout;
        var settings = new RgSettings
        {
            Regex = regex!,
            Invert = plan.Invert,
            Multiline = plan.Multiline,
            OnlyMatching = plan.OnlyMatching,
            Quiet = plan.Quiet,
            Json = json,
            Passthru = plan.Passthru,
            Trim = plan.Trim,
            Null = plan.Null,
            ByteOffset = plan.ByteOffset,
            Column = plan.Column,
            Vimgrep = vimgrep,
            CountOnly = plan.CountOnly,
            CountMatches = plan.CountMatches,
            FilesWith = plan.FilesOnly,
            FilesWithout = plan.FilesWithout,
            LineNumbers = lineNumbers,
            UseHeading = heading && showPath,
            ShowPath = showPath,
            Stats = plan.Stats,
            Replace = plan.Replace,
            Before = plan.Passthru ? int.MaxValue / 2 : plan.Before,
            After = plan.Passthru ? int.MaxValue / 2 : plan.After,
            MaxCount = plan.MaxCount,
            Colors = colors,
        };

        var stats = new RgStats();
        var searcher = new RgSearcher(settings, Sink, stats);
        var searching = new Stopwatch();
        bool anyMatch = false;
        bool neverMatch = patterns.Count == 0;   // an empty -f file matches nothing
        var read = new RgReadOptions
        {
            Text = plan.Text,
            SearchZip = plan.SearchZip,
            Exact = plan.ByteOffset || json,
        };
        if (plan.EncodingLabel is { } label && RgInput.TryResolveEncoding(label, out var enc, out var none, out _))
        {
            read.Encoding = enc;
            read.NoDecode = none;
        }

        if (!neverMatch)
        {
            if (stdinSource is not null)
            {
                searching.Start();
                anyMatch = searcher.SearchOne(stdinSource);
                searching.Stop();
            }
            else
            {
                foreach (var inp in inputs)
                {
                    RgSource? src;
                    try { src = RgInput.Read(inp.Abs, inp.Display, inp.Explicit, read); }
                    catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
                    {
                        FileSystemHelpers.WriteBashError(this, $"rg: {inp.Display}: {ex.Message}");
                        pathError = true;
                        continue;
                    }
                    if (src is null) continue;
                    searching.Start();
                    bool m = searcher.SearchOne(src);
                    searching.Stop();
                    if (m) { anyMatch = true; if (plan.Quiet) break; }
                }
            }
        }

        if (json) searcher.WriteJsonSummary(total, searching.Elapsed);
        else if (plan.Stats) WriteStats(stats, searching.Elapsed, total.Elapsed);

        int exit = plan.Quiet && anyMatch ? 0 : pathError ? 2 : anyMatch ? 0 : 1;
        FileSystemHelpers.SetLastExitCode(this, exit);
    }

    private void WriteStats(RgStats s, TimeSpan searching, TimeSpan total)
    {
        WriteObject(BashRuntime.NewBashObject(""));
        foreach (var l in new[]
                 {
                     $"{s.Matches} matches", $"{s.MatchedLines} matched lines", $"{s.FilesWithMatch} files contained matches",
                     $"{s.Searches} files searched", $"{s.BytesPrinted} bytes printed", $"{s.BytesSearched} bytes searched",
                     $"{searching.TotalSeconds:F6} seconds spent searching", $"{total.TotalSeconds:F6} seconds",
                 })
            WriteObject(BashRuntime.NewBashObject(l));
    }

    /// <summary>
    /// The walked-file filter: the repeatable <c>-g</c>/<c>--iglob</c> filename globs (a file must match one positive glob when
    /// any were given and none of the <c>!negated</c> ones; a positive glob match also OVERRIDES the <c>-t</c>/<c>-T</c> type
    /// selection, as ripgrep's overrides do), then the type selection. Explicitly named files bypass both.
    /// </summary>
    private static Func<string, bool>? BuildAccept(RgArgs plan, RgTypes.TypeSet types)
    {
        if (plan.Globs.Count == 0 && plan.IGlobs.Count == 0 && !types.Active) return null;
        var include = new List<WildcardPattern>();
        var exclude = new List<WildcardPattern>();
        foreach (var g in plan.Globs.Concat(plan.IGlobs))
        {
            if (g.StartsWith('!') && g.Length > 1) exclude.Add(WildcardPattern.Get(g.Substring(1), WildcardOptions.IgnoreCase));
            else include.Add(WildcardPattern.Get(g, WildcardOptions.IgnoreCase));
        }
        return path =>
        {
            string name = Path.GetFileName(path);
            foreach (var g in exclude) if (g.IsMatch(name)) return false;
            if (include.Count > 0)
            {
                foreach (var g in include) if (g.IsMatch(name)) return true;
                return false;
            }
            return types.Accepts(name);
        };
    }

    private static List<(string Abs, string Display, bool Explicit)> SortInputs(
        List<(string Abs, string Display, bool Explicit)> inputs, string key, bool reverse)
    {
        IEnumerable<(string Abs, string Display, bool Explicit)> sorted = key switch
        {
            "path" => inputs.OrderBy(i => i.Display.Replace('\\', '/'), StringComparer.Ordinal),
            "modified" => inputs.OrderBy(i => SafeTime(() => File.GetLastWriteTimeUtc(i.Abs))),
            "accessed" => inputs.OrderBy(i => SafeTime(() => File.GetLastAccessTimeUtc(i.Abs))),
            "created" => inputs.OrderBy(i => SafeTime(() => File.GetCreationTimeUtc(i.Abs))),
            _ => inputs,
        };
        var list = sorted.ToList();
        if (reverse) list.Reverse();
        return list;
    }

    private static DateTime SafeTime(Func<DateTime> f)
    {
        try { return f(); } catch { return DateTime.MinValue; }
    }

    private RgSource BuildStdinSource(RgArgs plan)
    {
        var src = new RgSource { Display = "<stdin>", IsStdin = true, Items = new List<object?>() };
        foreach (var item in _pipeline)
        {
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var sub in trimmed.Split('\n')) { src.Lines.Add(sub); src.Items.Add(null); src.ByteSize += RawBytes_Count(sub) + 1; }
            }
            else
            {
                src.Lines.Add(trimmed);
                src.Items.Add(item);
                src.ByteSize += RawBytes_Count(trimmed) + 1;
            }
        }
        return src;
    }

    private static int RawBytes_Count(string s) => PsBash.Core.RawBytes.GetByteCount(s);

    /// <summary>Read <c>-f FILE</c> (one pattern per line; <c>-</c> = the pipeline). An unreadable file is fatal (exit 2).</summary>
    private bool TryReadPatternFile(string raw, List<string> dest)
    {
        if (raw == "-")
        {
            foreach (var item in _pipeline)
                foreach (var l in BashRuntime.GetBashText(item).TrimEnd('\n').Split('\n')) dest.Add(l.TrimEnd('\r'));
            return true;
        }
        string path;
        try { path = SessionState.Path.GetUnresolvedProviderPathFromPSPath(FileSystemHelpers.NormalizeOperandPath(raw)); }
        catch { path = raw; }
        try
        {
            foreach (var l in BashFileSystem.ReadLines(path)) dest.Add(l.TrimEnd('\r'));
            return true;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            FileSystemHelpers.WriteBashError(this, $"rg: {raw}: No such file or directory (os error 2)");
            return false;
        }
    }

    /// <summary>
    /// Build the single combined regex: patterns OR-ed, <c>-F</c> escaped, <c>-w</c> as <c>\b(?:…)\b</c>, <c>-x</c> as
    /// <c>^(?:…)$</c>, smart-case, POSIX classes translated, PCRE2 (<c>-P</c>) and Rust-isms mapped through
    /// <see cref="PcreToDotNet"/>. A literal <c>\n</c> in a pattern needs <c>-U</c>, as in ripgrep.
    /// </summary>
    private static bool TryBuildRegex(RgArgs plan, List<string> patterns, out Regex? regex, out string? error)
    {
        regex = null;
        error = null;
        if (patterns.Count == 0) { regex = new Regex("(?!)"); return true; }

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
            if (plan.Fixed) pattern = Regex.Escape(pattern);
            else
            {
                if (!plan.Multiline && HasLiteralNewlineEscape(pattern))
                {
                    error = "rg: the literal \"\\n\" is not allowed in a regex\n\nConsider enabling multiline mode with the --multiline flag (or -U for short).\nWhen multiline mode is enabled, new line characters can be matched.";
                    return false;
                }
                pattern = BashRuntime.TranslatePosixClasses(pattern);
                if (!PcreToDotNet.TryTranslate(pattern, plan.Pcre, out var translated, out var terr))
                {
                    error = plan.Pcre ? $"rg: PCRE2: error compiling pattern: {terr}" : $"rg: regex parse error: {terr}";
                    return false;
                }
                pattern = translated;
            }
            if (plan.WordRegexp) pattern = "\\b(?:" + pattern + ")\\b";
            if (plan.LineRegexp) pattern = "^(?:" + pattern + ")$";
            parts.Add(pattern);
        }
        string combined = parts.Count == 1 ? parts[0] : string.Join("|", parts.Select(p => "(?:" + p + ")"));

        var opts = RegexOptions.None;
        if (effectiveIgnore) opts |= RegexOptions.IgnoreCase;
        if (plan.Multiline) opts |= RegexOptions.Multiline;
        if (plan.DotAll) opts |= RegexOptions.Singleline;
        try
        {
            regex = new Regex(combined, opts);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = plan.Pcre ? $"rg: PCRE2: error compiling pattern: {ex.Message}" : $"rg: regex parse error: {ex.Message}";
            return false;
        }
    }

    /// <summary>An unescaped backslash-n in the pattern text (an even run of backslashes before the <c>n</c> is a literal backslash).</summary>
    private static bool HasLiteralNewlineEscape(string p)
    {
        for (int i = 0; i + 1 < p.Length; i++)
        {
            if (p[i] != '\\') continue;
            if (p[i + 1] == 'n') return true;
            i++;   // skip the escaped character
        }
        return false;
    }

    /// <summary>
    /// Is this output headed for a terminal? ripgrep keys its defaults (line numbers, headings) on
    /// it. The host process's own stdout is always a pipe/IPC frame, so the signal is the launcher's
    /// hand-off: <c>PSBASH_PTY_ATTACHED=1</c> (interactive shell under a PTY). <c>PSBASH_RG_TTY</c>
    /// (<c>1</c>/<c>0</c>) overrides the LAYOUT defaults (tests, wrappers that know better); colour has its own
    /// override <c>PSBASH_RG_COLOR</c> and otherwise follows only the real PTY signal. Known limit: an interactive
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
            // (BashRuntime.RunChildProcess). Native rg here never reads stdin (only reached when
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
}
