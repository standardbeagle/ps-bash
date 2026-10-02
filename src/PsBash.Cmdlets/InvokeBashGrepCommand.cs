using System.Linq;
using System.Management.Automation;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashGrep</c> function
/// (REFACTOR-2 Phase 4 follow-on). Searches input lines (pipeline or files)
/// for a pattern, reproducing GNU <c>grep</c> 3.11.
///
/// Option parsing is the shared ORDERED parser (<see cref="ArgParser"/>, GNU grep 3.11 option table —
/// see <see cref="Plan"/>): bundles (<c>-ie PAT</c>, <c>-ePAT</c>, <c>-1n</c>), repeated <c>-e</c> /
/// <c>-f</c> / <c>--include</c> / <c>--exclude</c> / <c>--exclude-dir</c>, <c>-A/-B/-C N</c> and the
/// <c>-NUM</c> shorthand, <c>--color[=WHEN]</c>, unique-prefix long options, usage errors exit 2.
/// The first operand is the PATTERN unless <c>-e</c>/<c>-f</c> was given.
///
/// <para><b>One engine.</b> Stdin and every file go through <see cref="GrepScanner"/> (a push state machine:
/// context ring, binary rules, <c>-o</c>, colours, <c>-b -T -Z -z</c>), so output decorations cannot drift
/// between the pipeline and file paths. <see cref="GrepPcre"/> maps <c>-P</c> patterns onto .NET.</para>
///
/// <para><b>Direct PowerShell calls.</b> The transpiler single-quotes every flag
/// (<c>PsEmitter.OrderedArgCommands</c>) so the whole argv reaches <see cref="Arguments"/> verbatim and in
/// order. Typed directly at PowerShell the binder still intercepts colliding bare flags, so the decoys
/// <see cref="I"/> <see cref="V"/> <see cref="C"/> <see cref="W"/> <see cref="P"/> <see cref="O"/>
/// <see cref="D"/> <see cref="A"/> <see cref="B"/> <see cref="E"/> remain and are re-injected as ordinary
/// tokens before parsing; the psm1 <c>Invoke-BashGrep</c> proxy hands every argument over as a literal
/// string so a repeated <c>-e</c> or a bundle like <c>-ve</c> never reaches the binder.</para>
/// Output is a typed <c>PsBash.GrepMatch</c> PSObject per file match with
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
    /// parameters and never reaches <see cref="Arguments"/>. Routed through the PCRE translator
    /// (<see cref="GrepPcre"/>); the bundled (<c>-iP</c>) and long (<c>--perl-regexp</c>)
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
    /// Decoy for <c>-d ACTION</c> (--directories) / <c>-D ACTION</c> (--devices). Bare <c>-d</c> silently
    /// bound <c>-Debug</c> and <c>-D</c> likewise. A single switch catches both (case-insensitive binder);
    /// the ACTION word stays in <see cref="Arguments"/>, and which spelling was typed is recovered from the
    /// command's own pipeline segment (<see cref="CapitalDTyped"/>).
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

    /// <summary>
    /// An input operand could not be read (missing / unreadable / a directory). GNU grep then exits 2
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
        OptContext = "context", OptContextNum = "context-num", OptColor = "color", OptBinary = "binary",
        OptNullData = "null-data", OptNull = "null", OptText = "text", OptBinaryFiles = "binary-files",
        OptBinaryWithoutMatch = "binary-without-match", OptByteOffset = "byte-offset",
        OptDirectories = "directories", OptDevices = "devices", OptInitialTab = "initial-tab",
        OptUnixOffsets = "unix-byte-offsets", OptLabel = "label", OptGroupSep = "group-separator",
        OptNoGroupSep = "no-group-separator";

    /// <summary>
    /// GNU grep options that are valid but not implemented by ps-bash. Refused loudly (exit 2) rather than
    /// silently dropped. Written as typed; the <c>=VALUE</c> suffix of a long option is not part of the name.
    /// None remain: <c>-z -Z -a -b -d -D -T -u -I --label --group-separator --binary-files</c> are implemented.
    /// </summary>
    private static readonly string[] GrepValidButUnsupported = Array.Empty<string>();

    /// <summary>
    /// grep's option surface (GNU grep 3.11). <c>-y</c> is the obsolete <c>-i</c>; <c>-V</c> is
    /// <c>--version</c>; <c>--color</c>/<c>--colour</c> take an optional attached WHEN;
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
            new OptSpec(OptNullData, 'z', "null-data"),
            new OptSpec(OptNull, 'Z', "null"),
            new OptSpec(OptText, 'a', "text"),
            new OptSpec(OptBinaryFiles, '\0', "binary-files", OptKind.Value),
            new OptSpec(OptBinaryWithoutMatch, 'I', null),
            new OptSpec(OptByteOffset, 'b', "byte-offset"),
            new OptSpec(OptDirectories, 'd', "directories", OptKind.Value),
            new OptSpec(OptDevices, 'D', "devices", OptKind.Value),
            new OptSpec(OptInitialTab, 'T', "initial-tab"),
            new OptSpec(OptUnixOffsets, 'u', "unix-byte-offsets"),
            new OptSpec(OptLabel, '\0', "label", OptKind.Value),
            new OptSpec(OptGroupSep, '\0', "group-separator", OptKind.Value),
            new OptSpec(OptNoGroupSep, '\0', "no-group-separator"),
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

    /// <summary>GNU <c>--color=WHEN</c> spellings: always|yes|force, never|no|none, auto|tty|if-tty.</summary>
    private static bool TryParseColorWhen(string? v, out GrepColorMode mode)
    {
        mode = GrepColorMode.Auto;
        if (v is null) return true;
        switch (v.ToLowerInvariant())
        {
            case "always": case "yes": case "force": mode = GrepColorMode.Always; return true;
            case "never": case "no": case "none": mode = GrepColorMode.Never; return true;
            case "auto": case "tty": case "if-tty": mode = GrepColorMode.Auto; return true;
            default: return false;
        }
    }

    internal sealed class GrepArgs
    {
        public ParsedArgs Parsed = null!;
        public string? Error;
        public int ErrorExit = 2;

        public bool IgnoreCase, Invert, LineNumbers, Count, Quiet, Recursive, FilesWith, FilesWithout;
        public bool Word, OnlyMatching, ForceFileName, SuppressFileName, LineRegexp, NoMessages;
        public bool Extended, Fixed, Perl;

        /// <summary>Matcher letter (<c>G E F P</c>) or <c>'\0'</c>. GNU refuses two different matchers.</summary>
        public char Matcher;

        public int MaxMatches = int.MaxValue;
        public int After, Before;

        /// <summary>Any of -A/-B/-C/-NUM was given (even 0): GNU then prints "--" between groups.</summary>
        public bool ContextRequested;

        // ---- text/binary handling and output decoration
        public bool NullData, NullAfterName, ByteOffset, InitialTab, UnixOffsetsWarning;
        public GrepBinaryMode Binary = GrepBinaryMode.Binary;
        public GrepDirectories Directories = GrepDirectories.Read;
        public GrepDevices Devices = GrepDevices.Read;
        public GrepColorMode Color = GrepColorMode.Never;
        public string? Label;

        /// <summary>The group separator (<c>--</c> default); <c>null</c> after <c>--no-group-separator</c>.</summary>
        public string? GroupSeparator = "--";

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
    /// <c>-m</c> "invalid max count" (both exit 2) while a NEGATIVE <c>-m</c> means unlimited. <c>-a</c> / <c>-I</c> /
    /// <c>--binary-files</c> and <c>-r</c> / <c>-d</c> are last-wins.
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
                    g.Perl = m == 'P';
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
                case OptRecursive: g.Directories = GrepDirectories.Recurse; break;
                case OptFilesWith: g.FilesWith = true; g.FilesWithout = false; break;
                case OptFilesWithout: g.FilesWithout = true; g.FilesWith = false; break;
                case OptInclude: g.Include.Add(v!); break;
                case OptExclude: g.Exclude.Add(v!); break;
                case OptExcludeDir: g.ExcludeDir.Add(v!); break;
                case OptExcludeFrom: g.ExcludeFromFiles.Add(v!); break;
                case OptNullData: g.NullData = true; break;
                case OptNull: g.NullAfterName = true; break;
                case OptText: g.Binary = GrepBinaryMode.Text; break;
                case OptBinaryWithoutMatch: g.Binary = GrepBinaryMode.WithoutMatch; break;
                case OptBinaryFiles:
                    switch (v)
                    {
                        case "binary": g.Binary = GrepBinaryMode.Binary; break;
                        case "text": g.Binary = GrepBinaryMode.Text; break;
                        case "without-match": g.Binary = GrepBinaryMode.WithoutMatch; break;
                        default: g.Error = "grep: unknown binary-files type"; return g;
                    }
                    break;
                case OptByteOffset: g.ByteOffset = true; break;
                case OptUnixOffsets: g.UnixOffsetsWarning = true; break;
                case OptInitialTab: g.InitialTab = true; break;
                case OptLabel: g.Label = v; break;
                case OptGroupSep: g.GroupSeparator = v; break;
                case OptNoGroupSep: g.GroupSeparator = null; break;
                case OptDirectories:
                    switch (v)
                    {
                        case "read": g.Directories = GrepDirectories.Read; break;
                        case "skip": g.Directories = GrepDirectories.Skip; break;
                        case "recurse": g.Directories = GrepDirectories.Recurse; break;
                        default:
                            g.Error = $"grep: invalid argument '{v}' for '--directories'\n"
                                + "Valid arguments are:\n  - 'read'\n  - 'recurse'\n  - 'skip'\n"
                                + "Usage: grep [OPTION]... PATTERNS [FILE]...\n"
                                + "Try 'grep --help' for more information.";
                            return g;
                    }
                    break;
                case OptDevices:
                    switch (v)
                    {
                        case "read": g.Devices = GrepDevices.Read; break;
                        case "skip": g.Devices = GrepDevices.Skip; break;
                        default: g.Error = "grep: unknown devices method"; return g;
                    }
                    break;
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
                    if (!TryParseColorWhen(v, out var cm))
                    {
                        g.Error = $"grep: invalid argument '{v}' for '--color'";
                        return g;
                    }
                    g.Color = cm;
                    break;
                // OptLineBuffered, OptBinary: accepted no-ops (nothing to flush / no CR stripping here).
            }
        }

        g.Recursive = g.Directories == GrepDirectories.Recurse;
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

    // ---- processing state ----------------------------------------------------------------------

    private GrepScanner? _scanner;
    private GrepStdinFeeder? _feeder;
    private GrepArgs _plan = null!;
    private Action? _fileModeRun;
    private bool _pipelineMode, _pDone, _pFinished, _collectStdin;
    private readonly List<(string Text, bool Unterminated)> _stdinBuffer = new();

    /// <summary>
    /// Pipeline mode streams: each record is matched as it arrives (the cmdlet resolves its whole argv in
    /// <see cref="BeginProcessing"/>), so <c>ls | grep x</c> / <c>find | grep -m1 x</c> emit before the producer
    /// finishes and an early-satisfied grep (<c>-q</c>, <c>-m N</c>) stops the upstream. Retained state is
    /// bounded by the context window (-B ring + -A countdown), never the stream length.
    /// </summary>
    protected override void ProcessRecord()
    {
        if (_collectStdin)
        {
            // File mode with a `-` operand: the pipeline is that file; ProcessRecord only collects it.
            if (InputObject != null)
                _stdinBuffer.Add((BashRuntime.GetBashText(InputObject), BashRuntime.IsUnterminated(InputObject)));
            return;
        }
        if (!_pipelineMode || InputObject == null) return;
        if (_pDone)
        {
            FinishPipeline();
            if (!PsBashProxy) UpstreamStop.Throw(this);
            return;
        }

        bool more = _feeder!.Add(BashRuntime.GetBashText(InputObject), BashRuntime.IsUnterminated(InputObject), InputObject);
        if (!more || _scanner!.SourceDone || _scanner.QuitAll) _pDone = true;

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

    /// <summary>
    /// Was the capital <c>-D</c> typed (devices) rather than <c>-d</c> (directories)? The binder's decoy switch
    /// cannot tell; the command's own pipeline segment can.
    /// </summary>
    private bool CapitalDTyped()
    {
        string seg = BashRuntime.CurrentPipelineSegment(MyInvocation);
        return Regex.IsMatch(seg, @"(?<![\w-])-D(?![\w-])");
    }

    protected override void BeginProcessing()
    {
        // Direct PowerShell calls: a bare -d/-i/-v/-c/-w/-P/-o/-A/-B/-e binds a declared decoy parameter
        // (or a common parameter) instead of reaching Arguments. Re-inject them as the ordinary tokens
        // they stand for so the ordered parser sees them. Transpiled bash never gets here: the emitter
        // single-quotes every flag (OrderedArgCommands), so they arrive verbatim in Arguments.
        var args = BashRuntime.PrependDecoys(Arguments,
            (D.IsPresent, CapitalDTyped() ? "-D" : "-d"), (I.IsPresent, "-i"), (V.IsPresent, "-v"), (C.IsPresent, "-c"),
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
        _plan = plan;

        if (plan.UnixOffsetsWarning)
            FileSystemHelpers.WriteStderr(this, "grep: warning: --unix-byte-offsets (-u) is obsolete");

        var operands = plan.Operands;

        // -e / -f in command-line order. A missing -f / --exclude-from file is fatal (GNU: exit 2),
        // whatever -s says.
        var patterns = new List<string>();
        var excludeGlobs = plan.Exclude;
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

        if (plan.Perl && patterns.Count > 1)
        {
            FileSystemHelpers.WriteBashError(this, "grep: the -P option only supports a single pattern");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        // Build regex list (OR logic across multiple patterns) via the shared ladder.
        if (!TryBuildRegexes(patterns, plan.Fixed, plan.Extended, plan.Word, plan.LineRegexp,
                plan.IgnoreCase, out var regexes, out var invalidRegex, plan.Perl, plan.NullData))
        {
            FileSystemHelpers.WriteBashError(this, plan.Perl
                ? $"grep: {invalidRegex}"
                : $"grep: invalid regular expression: {invalidRegex}");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        var style = GrepStyle.Create(plan.Color, BashVariableStore.Get,
            msg => FileSystemHelpers.WriteStderr(this, msg));
        var opts = new GrepOptions
        {
            Invert = plan.Invert, LineNumbers = plan.LineNumbers, Count = plan.Count, Quiet = plan.Quiet,
            FilesWith = plan.FilesWith, FilesWithout = plan.FilesWithout, OnlyMatching = plan.OnlyMatching,
            NullData = plan.NullData, NullAfterName = plan.NullAfterName, ByteOffset = plan.ByteOffset,
            InitialTab = plan.InitialTab, ContextRequested = plan.ContextRequested,
            Max = plan.MaxMatches, After = plan.After, Before = plan.Before, Binary = plan.Binary,
            GroupSeparator = plan.GroupSeparator, Style = style,
        };
        _scanner = new GrepScanner(opts, regexes, EmitScanned, msg => FileSystemHelpers.WriteStderr(this, msg));

        // The pattern (or -e/-f patterns) is already removed, so operands now holds the file list.
        // --- Pipeline mode ---
        if (operands.Count == 0 && !plan.Recursive)
        {
            _pipelineMode = true;
            _scanner.Begin(plan.Label ?? StdinLabel, plan.ForceFileName, startBinary: false, sizeHint: -1);
            _feeder = new GrepStdinFeeder(_scanner, plan.NullData);
            _pDone = _scanner.SourceDone;
            return;
        }

        // --- File mode (incl. recursive) --- runs in EndProcessing.
        _collectStdin = operands.Contains("-");
        var walkFilter = BuildRecursiveFileFilter(plan.Include, excludeGlobs, plan.ExcludeDir);
        _fileModeRun = () => RunFileMode(operands, walkFilter);
    }

    /// <summary>The scanner's output sink: a line / count / name becomes one pipeline record.</summary>
    private void EmitScanned(string text, bool exact, string? label, int lineNo, string? line, object? original)
    {
        if (exact) WriteObject(BashRuntime.TextRecord(text, unterminated: true));
        else if (original != null) WriteObject(BashRuntime.PassTerminated(original));
        else if (label != null && line != null) WriteObject(BuildGrepMatch(label, lineNo, line, text));
        else WriteObject(BashRuntime.NewBashObject(text));
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

    /// <summary>Exit status (and the -c count), once, whether the stream ended or grep stopped it.</summary>
    private void FinishPipeline()
    {
        if (_pFinished) return;
        _pFinished = true;
        _feeder!.Flush();
        _scanner!.End();
        FileSystemHelpers.SetLastExitCode(this, _scanner.AnyMatch ? 0 : 1);
    }

    // ---- file mode -------------------------------------------------------------------------------

    private void RunFileMode(List<string> operands, Func<string, bool>? walkFilter)
    {
        var plan = _plan;
        var scanner = _scanner!;
        bool noIgnore = BashFileSystem.DefaultFilteringDisabled();
        var opList = operands.Count > 0 ? operands : new List<string> { "." };
        bool nameForOperands = plan.ForceFileName || (!plan.SuppressFileName && opList.Count > 1);
        bool nameInWalk = plan.ForceFileName || !plan.SuppressFileName;

        foreach (var raw in opList)
        {
            if (scanner.QuitAll) break;
            if (raw == "-")
            {
                SearchStdinOperand(nameForOperands);
                continue;
            }

            bool existed = false;
            foreach (var fp in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                if (scanner.QuitAll) break;
                if (Directory.Exists(fp))
                {
                    existed = true;
                    if (plan.Directories == GrepDirectories.Recurse)
                    {
                        // grep -r searches dot-files too — only the prune set (.git, bin, obj, node_modules, …)
                        // is skipped; PSBASH_SEARCH_NO_IGNORE turns the prune set off (GNU parity).
                        IEnumerable<string> files = BashFileSystem.EnumerateSearchFiles(
                            fp, includeIgnored: noIgnore, includeHidden: true);
                        // --include / --exclude / --exclude-dir apply to the recursive directory walk
                        // (an explicitly-named file is always searched, per grep semantics).
                        if (walkFilter != null) files = files.Where(walkFilter);
                        foreach (var f in files)
                        {
                            if (scanner.QuitAll) break;
                            SearchFile(f, nameInWalk, walked: true, noIgnore);
                        }
                    }
                    else if (plan.Directories == GrepDirectories.Read)
                    {
                        if (!plan.NoMessages)
                            FileSystemHelpers.WriteBashError(this, $"grep: {raw.Replace('\\', '/')}: Is a directory");
                        _operandReadError = true;
                    }
                    continue;
                }
                if (File.Exists(fp) || FileSystemHelpers.IsNullDevice(fp))
                {
                    existed = true;
                    if (plan.Devices == GrepDevices.Skip && IsDeviceFile(fp)) continue;
                    SearchFile(fp, nameForOperands, walked: false, noIgnore);
                }
            }
            if (!existed)
            {
                // -s / --no-messages suppresses the diagnostic but still sets the failure code.
                if (!plan.NoMessages)
                    FileSystemHelpers.WriteBashError(this, $"grep: {raw.Replace('\\', '/')}: No such file or directory");
                _operandReadError = true;
            }
        }

        int exit = scanner.QuitAll ? 0 : _operandReadError ? 2 : scanner.AnyMatch ? 0 : 1;
        FileSystemHelpers.SetLastExitCode(this, exit);
    }

    private static bool IsDeviceFile(string path)
    {
        if (FileSystemHelpers.IsNullDevice(path)) return true;
        try { return (new FileInfo(path).Attributes & FileAttributes.Device) != 0; }
        catch { return false; }
    }

    /// <summary>The <c>-</c> operand: the pipeline records collected in <see cref="ProcessRecord"/>.</summary>
    private void SearchStdinOperand(bool showName)
    {
        var scanner = _scanner!;
        scanner.Begin(_plan.Label ?? StdinLabel, showName, startBinary: false, sizeHint: -1);
        var feeder = new GrepStdinFeeder(scanner, _plan.NullData);
        foreach (var (text, unterminated) in _stdinBuffer)
            if (!feeder.Add(text, unterminated, null)) break;
        if (!scanner.SourceDone) feeder.Flush();
        scanner.End();
    }

    /// <summary>
    /// Search one file. The binary probe (a NUL in the first 96 KiB) decides how a match is reported; files
    /// found by the recursive WALK that are binary stay skipped silently (ps-bash's default recursive prune —
    /// <c>PSBASH_SEARCH_NO_IGNORE</c> or <c>-a</c> searches them).
    /// </summary>
    private void SearchFile(string path, bool showName, bool walked, bool noIgnore)
    {
        var scanner = _scanner!;
        var plan = _plan;
        bool failed = false;
        try
        {
            bool binary = false;
            if (plan.Binary != GrepBinaryMode.Text && !plan.NullData)
            {
                binary = walked ? BashFileSystem.IsBinary(path) : GrepIo.ProbeNul(path);
                if (walked && binary && !noIgnore) return;
            }

            long size = -1;
            if (plan.InitialTab)
            {
                try { size = FileSystemHelpers.IsNullDevice(path) ? 0 : new FileInfo(path).Length; } catch { size = -1; }
            }

            scanner.Begin(path, showName, binary, size, typed: true);
            foreach (var (text, term) in GrepIo.ReadRecords(path, plan.NullData, exact: plan.ByteOffset))
                if (!scanner.Feed(text, term, null)) break;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            EmitGrepReadError(path, ex);
            failed = true;
        }
        if (!failed) scanner.End();
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
    /// The grep regex-assembly ladder, shared by the cmdlet and the fused-pipeline
    /// streaming stage so the two can never drift: per pattern, fixed → escape,
    /// basic (BRE) → <see cref="EscapeBreMetas"/>, extended → verbatim, perl → <see cref="GrepPcre"/>; then the
    /// optional word (<c>\b…\b</c>) and whole-line (<c>^(?:…)$</c>) wraps;
    /// <c>-i</c> → <see cref="RegexOptions.IgnoreCase"/>; <c>-z</c> → <see cref="RegexOptions.Singleline"/> for
    /// BRE/ERE (a record spans lines and <c>.</c> matches the newline; PCRE keeps its own dot). Returns false with
    /// <paramref name="invalidMessage"/> set (no error emission) on the first
    /// pattern that fails to compile — the caller decides how to report it.
    /// </summary>
    internal static bool TryBuildRegexes(
        IReadOnlyList<string> patterns, bool fixedString, bool extendedRegex,
        bool wholeWord, bool lineRegexp, bool ignoreCase,
        out List<Regex> regexes, out string? invalidMessage,
        bool perl = false, bool nullData = false)
    {
        regexes = new List<Regex>();
        invalidMessage = null;
        var opts = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        if (nullData && !perl && !fixedString) opts |= RegexOptions.Singleline;
        foreach (var pat in patterns)
        {
            string rp;
            if (perl)
            {
                if (!GrepPcre.TryTranslate(BashRuntime.TranslatePosixClasses(pat), out rp, out var perr))
                {
                    invalidMessage = perr;
                    return false;
                }
                if (wholeWord) rp = @"(?<!\w)(?:" + rp + @")(?!\w)";
                if (lineRegexp) rp = "^(?:" + rp + ")$";
            }
            else
            {
                // POSIX classes first, before BRE escaping: the rewrite introduces regex
                // metacharacters (\s, \w, \p{P}) that must NOT then be escaped as literals.
                // -F is a literal search, so it keeps `[[:digit:]]` verbatim.
                rp = fixedString ? Regex.Escape(pat)
                   : !extendedRegex ? EscapeBreMetas(BashRuntime.TranslatePosixClasses(pat))
                   : BashRuntime.TranslatePosixClasses(pat);
                if (wholeWord) rp = "\\b" + rp + "\\b";
                if (lineRegexp) rp = "^(?:" + rp + ")$";
            }
            try { regexes.Add(new Regex(rp, opts)); }
            catch (ArgumentException ex) { invalidMessage = ex.Message; return false; }
        }
        return true;
    }

    /// <summary>
    /// BRE → .NET escape: escape ( ) { } | + ? when not already preceded by a
    /// backslash. Mirrors the oracle's <c>-replace</c> chain with
    /// <c>(?&lt;!\\)\(</c> etc.
    /// </summary>
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
    /// invert applied to <paramref name="isMatch"/>. Shared with the fused-pipeline core so match
    /// semantics stay identical.
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
