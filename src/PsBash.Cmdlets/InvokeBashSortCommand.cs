using System.Management.Automation;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for GNU coreutils <c>sort</c>.
///
/// <para><b>Arguments</b> go through the shared ordered parser (<see cref="SortSpec"/>; <c>sort</c> is
/// on <c>PsEmitter.OrderedArgCommands</c>, so every flag arrives in <c>Arguments</c> verbatim and in
/// order). <see cref="Plan"/> resolves the whole argv into a <see cref="SortPlan"/> (keys with their
/// effective options, global flags) and validates it with GNU's rules and messages. <b>Usage and
/// validation errors exit 2</b> (GNU <c>SORT_FAILURE</c>); <c>-c</c> disorder exits 1; an invalid
/// <c>--sort</c>/<c>--check</c> argument exits 1 (as GNU's argmatch). Ps-bash refuses (exit 2) the
/// real options it cannot honour: <c>-R</c>/<c>--random-sort</c>, <c>--random-source</c>,
/// <c>-z</c>/<c>--zero-terminated</c> (NUL-terminated records), <c>--debug</c>,
/// <c>--files0-from</c>.</para>
///
/// <para><b>Accepted and ignored</b> (they tune HOW GNU sorts, never WHAT it outputs):
/// <c>-S</c>/<c>--buffer-size</c> (validated), <c>-T</c>/<c>--temporary-directory</c> (this sort never
/// spills to disk), <c>--parallel=N</c> (validated), <c>--batch-size=N</c> (validated),
/// <c>--compress-program</c>. <c>-m</c> is implemented as a real k-way merge of the already-sorted
/// inputs (an unsorted input is merged as-is, exactly as GNU).</para>
///
/// <para>The comparison engine is <see cref="SortEngine"/>, shared with the fused <c>SortStage</c>
/// core. Output preserves the original pipeline objects (sort is a FILTER; see runtime-functions.md
/// "Pipeline record kinds"); in file mode it emits typed text objects. An unreadable input file is
/// fatal like GNU (<c>sort: cannot read: F: ...</c>, exit 2, no output).</para>
///
/// <para><b>Direct PowerShell calls</b>: <c>-c</c>, <c>-d</c>, <c>-V</c>, <c>-i</c> and <c>-o</c>
/// prefix-collide with common parameters, so they are declared (<see cref="C"/>, <see cref="D"/>,
/// <see cref="V"/>, <see cref="I"/>, <see cref="O"/>) and re-injected before parsing (<c>-o</c> as a
/// <c>-o FILE</c> pair).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSort")]
[OutputType(typeof(string))]
public sealed class InvokeBashSortCommand : PSCmdlet
{
    private const string OptReverse = "reverse", OptNumeric = "numeric", OptGeneral = "general",
        OptHuman = "human", OptMonth = "month", OptVersion = "version-sort", OptFold = "fold",
        OptBlank = "blank", OptDict = "dict", OptNonPrint = "nonprint", OptUnique = "unique",
        OptStable = "stable", OptMerge = "merge", OptCheck = "check", OptCheckLong = "check-long",
        OptCheckQuiet = "check-quiet", OptKey = "key", OptTab = "tab", OptOutput = "output",
        OptSort = "sort", OptIgnoredSize = "ignored-size", OptIgnoredTmp = "ignored-tmp",
        OptParallel = "parallel", OptBatch = "batch-size", OptCompress = "compress-program",
        OptFiles0 = "files0-from", OptRandomSource = "random-source";

    /// <summary>Valid GNU <c>sort</c> options ps-bash refuses (exit 2).</summary>
    private static readonly string[] SortValidButUnsupported =
        { "-R", "--random-sort", "-z", "--zero-terminated", "--debug" };

    /// <summary>
    /// sort's option surface (GNU coreutils 9.4). Ambiguity lists follow GNU's <c>long_options[]</c>
    /// table order (<c>--r</c> = random-sort, random-source, reverse; <c>--ver</c> = version-sort, version).
    /// </summary>
    private static readonly OptSpecSet SortSpec = new(
        new[]
        {
            new OptSpec(OptReverse, 'r', "reverse"),
            new OptSpec(OptNumeric, 'n', "numeric-sort"),
            new OptSpec(OptGeneral, 'g', "general-numeric-sort"),
            new OptSpec(OptHuman, 'h', "human-numeric-sort"),
            new OptSpec(OptMonth, 'M', "month-sort"),
            new OptSpec(OptVersion, 'V', "version-sort"),
            new OptSpec(OptFold, 'f', "ignore-case"),
            new OptSpec(OptBlank, 'b', "ignore-leading-blanks"),
            new OptSpec(OptDict, 'd', "dictionary-order"),
            new OptSpec(OptNonPrint, 'i', "ignore-nonprinting"),
            new OptSpec(OptUnique, 'u', "unique"),
            new OptSpec(OptStable, 's', "stable"),
            new OptSpec(OptMerge, 'm', "merge"),
            new OptSpec(OptCheck, 'c', null),
            new OptSpec(OptCheckLong, '\0', "check", OptKind.OptionalValue),
            new OptSpec(OptCheckQuiet, 'C', null),
            new OptSpec(OptKey, 'k', "key", OptKind.Value),
            new OptSpec(OptTab, 't', "field-separator", OptKind.Value),
            new OptSpec(OptOutput, 'o', "output", OptKind.Value),
            new OptSpec(OptSort, '\0', "sort", OptKind.Value),
            new OptSpec(OptIgnoredSize, 'S', "buffer-size", OptKind.Value),
            new OptSpec(OptIgnoredTmp, 'T', "temporary-directory", OptKind.Value),
            new OptSpec(OptParallel, '\0', "parallel", OptKind.Value),
            new OptSpec(OptBatch, '\0', "batch-size", OptKind.Value),
            new OptSpec(OptCompress, '\0', "compress-program", OptKind.Value),
            new OptSpec(OptFiles0, '\0', "files0-from", OptKind.Value),
            new OptSpec(OptRandomSource, '\0', "random-source", OptKind.Value),
        },
        SortValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        usageExitCode: 2,
        longOptionOrder: new[]
        {
            "ignore-leading-blanks", "check", "compress-program", "debug", "dictionary-order",
            "ignore-case", "files0-from", "general-numeric-sort", "ignore-nonprinting", "key", "merge",
            "month-sort", "numeric-sort", "human-numeric-sort", "version-sort", "random-sort",
            "random-source", "reverse", "sort", "stable", "batch-size", "buffer-size",
            "field-separator", "temporary-directory", "unique", "zero-terminated", "parallel", "output",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, SortSpec);

    internal sealed class SortArgs
    {
        public ParsedArgs Parsed = null!;
        public SortPlan Plan = new();
        public List<string> Operands = new();
        public string? Output;
        /// <summary>0 = sort, 1 = <c>-c</c> (diagnose first disorder), 2 = <c>-C</c> (silent).</summary>
        public int CheckMode;
        public string? Error;
        public int ErrorExit = 2;

        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + interpret + validate, in GNU's order (first error wins). Fixes over the old hand scan:
    /// per-key modifiers <c>b d f g h i M n r V</c> (old: only <c>n r b</c>; anything else silently
    /// became field 0), GNU key-option inheritance (a key with its own ordering option no longer
    /// inherits the global <c>-r</c>), <c>-k F.C,F.C</c> offsets, incompatible-option errors, key and
    /// tab validation (multi-character <c>-t</c> is an error), <c>-i</c>, <c>-C</c>, <c>-m</c>,
    /// <c>--check[=..]</c>, <c>--sort=WORD</c> abbreviations, long-option abbreviations, bundles
    /// such as <c>-rk2</c>, options after operands.
    /// </summary>
    internal static SortArgs Plan(string[] args)
    {
        var s = new SortArgs { Parsed = ScanArgs(args) };
        s.Operands = s.Parsed.Operands();
        if (s.Parsed.HasError) return s;
        if (s.Parsed.Has(OptSpecSet.HelpId) || s.Parsed.Has(OptSpecSet.VersionId)) return s;

        var g = new SortKeyOpts();
        var keys = new List<SortKey>();
        var plan = s.Plan;
        string? output = null;
        bool sawCheck = false, sawQuiet = false;

        foreach (var tok in s.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            string? v = tok.Value;
            switch (tok.OptId)
            {
                case OptReverse: g.Reverse = true; break;
                case OptNumeric: g.Numeric = true; break;
                case OptGeneral: g.General = true; break;
                case OptHuman: g.Human = true; break;
                case OptMonth: g.Month = true; break;
                case OptVersion: g.Version = true; break;
                case OptFold: g.Fold = true; break;
                case OptBlank: g.BlankStart = g.BlankEnd = true; break;
                case OptDict: g.Dict = true; break;
                case OptNonPrint: g.NonPrint = true; break;
                case OptUnique: plan.Unique = true; break;
                case OptStable: plan.Stable = true; break;
                case OptMerge: plan.Merge = true; break;
                case OptCheck: sawCheck = true; break;
                case OptCheckQuiet: sawQuiet = true; break;
                case OptCheckLong:
                    {
                        string? mode = v is null ? "diagnose-first" : MatchArg(v, "quiet", "silent", "diagnose-first");
                        if (mode is null)
                        {
                            return Fail(s, $"sort: invalid argument '{v}' for '--check'\nValid arguments are:\n  - 'quiet', 'silent'\n  - 'diagnose-first'", 1);
                        }
                        if (mode == "diagnose-first") sawCheck = true; else sawQuiet = true;
                        break;
                    }
                case OptKey:
                    {
                        if (!TryParseKey(v!, out var key, out string? kerr)) return Fail(s, kerr!, 2);
                        keys.Add(key!);
                        break;
                    }
                case OptTab:
                    {
                        if (v!.Length == 0) return Fail(s, "sort: empty tab", 2);
                        char c;
                        if (v == "\\0") c = '\0';
                        else if (v.Length == 1) c = v[0];
                        else return Fail(s, $"sort: multi-character tab '{v}'", 2);
                        if (plan.Delimiter is { } prev && prev != c) return Fail(s, "sort: incompatible tabs", 2);
                        plan.Delimiter = c;
                        break;
                    }
                case OptOutput:
                    if (output is not null && output != v) return Fail(s, "sort: multiple output files specified", 2);
                    output = v;
                    break;
                case OptSort:
                    {
                        string? word = MatchArg(v!, "general-numeric", "human-numeric", "month", "numeric", "random", "version");
                        if (word is null)
                        {
                            return Fail(s, $"sort: invalid argument '{v}' for '--sort'\nValid arguments are:\n  - 'general-numeric'\n  - 'human-numeric'\n  - 'month'\n  - 'numeric'\n  - 'random'\n  - 'version'", 1);
                        }
                        switch (word)
                        {
                            case "general-numeric": g.General = true; break;
                            case "human-numeric": g.Human = true; break;
                            case "month": g.Month = true; break;
                            case "numeric": g.Numeric = true; break;
                            case "version": g.Version = true; break;
                            default: return Fail(s, "sort: option '--sort=random' is recognized but not supported by ps-bash", 2);
                        }
                        break;
                    }
                case OptIgnoredSize:
                    if (!IsValidSize(v!, out string? serr)) return Fail(s, serr!, 2);
                    break;
                case OptParallel:
                    if (!IsDigits(v!)) return Fail(s, $"sort: invalid --parallel argument '{v}'", 2);
                    if (v!.Trim('0').Length == 0) return Fail(s, "sort: number in parallel must be nonzero", 2);
                    break;
                case OptBatch:
                    if (!IsDigits(v!)) return Fail(s, $"sort: invalid --batch-size argument '{v}'", 2);
                    if (v!.TrimStart('0').Length == 0 || (v.Length == 1 && v[0] < '2'))
                        return Fail(s, "sort: minimum --batch-size argument is '2'", 2);
                    break;
                case OptFiles0:
                    return Fail(s, "sort: option '--files0-from' is recognized but not supported by ps-bash", 2);
                case OptRandomSource:
                    return Fail(s, "sort: option '--random-source' is recognized but not supported by ps-bash", 2);
                // -T / --compress-program: accepted, no effect on the output.
            }
        }

        if (sawCheck && sawQuiet) return Fail(s, "sort: options '-cC' are incompatible", 2);
        s.CheckMode = sawQuiet ? 2 : sawCheck ? 1 : 0;
        if (s.CheckMode != 0 && s.Operands.Count > 1)
            return Fail(s, $"sort: extra operand '{s.Operands[1]}' not allowed with -c", 2);

        // Effective key options: GNU gives a key with no ordering option (and no 'r') every global one.
        plan.Reverse = g.Reverse;
        plan.HasExplicitKeys = keys.Count > 0;
        if (keys.Count == 0)
            keys.Add(new SortKey { Whole = true, Opts = g.Clone() });
        else
        {
            foreach (var k in keys)
            {
                if (!k.Opts.HasOrdering && !k.Opts.Reverse) k.Opts = g.Clone();
            }
        }
        foreach (var k in keys)
        {
            if (k.Opts.IncompatibleLetters() is { } letters)
                return Fail(s, $"sort: options '-{letters}' are incompatible", 2);
        }
        plan.Keys = keys;
        s.Output = output;
        return s;
    }

    private static SortArgs Fail(SortArgs s, string message, int exit)
    {
        s.Error = message;
        s.ErrorExit = exit;
        return s;
    }

    // getopt_long / argmatch: a unique prefix of one of the valid arguments (an exact match wins).
    private static string? MatchArg(string value, params string[] valid)
    {
        if (value.Length == 0) return null;
        string? hit = null;
        foreach (var w in valid)
        {
            if (w == value) return w;
            if (w.StartsWith(value, StringComparison.Ordinal))
            {
                if (hit is not null) return null;
                hit = w;
            }
        }
        return hit;
    }

    private static bool IsDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    // -S SIZE: digits (optionally fractional) and one of the GNU multiplier suffixes.
    private static bool IsValidSize(string v, out string? error)
    {
        error = null;
        int i = 0;
        while (i < v.Length && (char.IsAsciiDigit(v[i]) || v[i] == '.')) i++;
        if (i == 0) { error = $"sort: invalid -S argument '{v}'"; return false; }
        if (i == v.Length) return true;
        if (i == v.Length - 1 && "%bkKmMgGtTPEZYRQ".IndexOf(v[i]) >= 0) return true;
        error = $"sort: invalid suffix in -S argument '{v}'";
        return false;
    }

    private static readonly Regex s_keyPos = new(@"^\d*", RegexOptions.Compiled);

    /// <summary>
    /// GNU <c>-k F[.C][OPTS][,F[.C][OPTS]]</c> (OPTS from <c>bdfghiMnRrV</c>). Messages and their order
    /// follow GNU's <c>parse_field_count</c> / <c>badfieldspec</c>; <c>R</c> is refused (random sort).
    /// </summary>
    internal static bool TryParseKey(string spec, out SortKey? key, out string? error)
    {
        key = null;
        error = null;
        var k = new SortKey();
        string s = spec;

        if (!ParseCount(ref s, "invalid number at field start", out int sword)) { error = CountError(s, "invalid number at field start"); return false; }
        if (sword == 0) { error = Bad(spec, "field number is zero"); return false; }
        k.StartField = sword;
        if (s.StartsWith('.'))
        {
            s = s.Substring(1);
            if (!ParseCount(ref s, "", out int schar)) { error = CountError(s, "invalid number after '.'"); return false; }
            if (schar == 0) { error = Bad(spec, "character offset is zero"); return false; }
            k.StartChar = schar;
        }
        if (!SetOrdering(ref s, k.Opts, start: true, out string? r1)) { error = r1; return false; }
        if (s.StartsWith(','))
        {
            s = s.Substring(1);
            if (!ParseCount(ref s, "", out int eword)) { error = CountError(s, "invalid number after ','"); return false; }
            if (eword == 0) { error = Bad(spec, "field number is zero"); return false; }
            k.EndField = eword;
            if (s.StartsWith('.'))
            {
                s = s.Substring(1);
                if (!ParseCount(ref s, "", out int echar)) { error = CountError(s, "invalid number after '.'"); return false; }
                k.EndChar = echar; // 0 = end of the field
            }
            if (!SetOrdering(ref s, k.Opts, start: false, out string? r2)) { error = r2; return false; }
        }
        if (s.Length > 0) { error = Bad(spec, "stray character in field spec"); return false; }
        key = k;
        return true;
    }

    private static string Bad(string spec, string msg) => $"sort: {msg}: invalid field specification '{spec}'";

    private static string CountError(string rest, string msg) => $"sort: {msg}: invalid count at start of '{rest}'";

    // Digits at the front of s (saturating to int.MaxValue); false when there are none (s untouched).
    private static bool ParseCount(ref string s, string _, out int value)
    {
        value = 0;
        int n = s_keyPos.Match(s).Length;
        if (n == 0) return false;
        value = int.TryParse(s.AsSpan(0, n), out int v) ? v : int.MaxValue;
        s = s.Substring(n);
        return true;
    }

    private static bool SetOrdering(ref string s, SortKeyOpts o, bool start, out string? error)
    {
        error = null;
        int i = 0;
        for (; i < s.Length; i++)
        {
            switch (s[i])
            {
                case 'b': if (start) o.BlankStart = true; else o.BlankEnd = true; break;
                case 'd': o.Dict = true; break;
                case 'f': o.Fold = true; break;
                case 'g': o.General = true; break;
                case 'h': o.Human = true; break;
                case 'i': o.NonPrint = true; break;
                case 'M': o.Month = true; break;
                case 'n': o.Numeric = true; break;
                case 'r': o.Reverse = true; break;
                case 'V': o.Version = true; break;
                case 'R':
                    error = "sort: key option 'R' (random sort) is recognized but not supported by ps-bash";
                    return false;
                default:
                    s = s.Substring(i);
                    return true;
            }
        }
        s = string.Empty;
        return true;
    }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter]
    public SwitchParameter D { get; set; }

    [Parameter]
    public SwitchParameter V { get; set; }

    /// <summary>Decoy for <c>-i</c> (--ignore-nonprinting): bare <c>-i</c> prefix-collides with
    /// <c>-InformationAction</c>/<c>-InformationVariable</c>.</summary>
    [Parameter]
    public SwitchParameter I { get; set; }

    /// <summary>Decoy for <c>-o FILE</c>: a bare <c>-o</c> is an ambiguous prefix of
    /// <c>-OutVariable</c>/<c>-OutBuffer</c>. Re-injected as the pair <c>-o FILE</c>.</summary>
    [Parameter]
    public string? O { get; set; }

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
        var rawArgs = BashRuntime.PrependDecoys(Arguments,
            (C.IsPresent, "-c"), (D.IsPresent, "-d"), (V.IsPresent, "-V"), (I.IsPresent, "-i"));
        if (O is not null) rawArgs = new[] { "-o", O }.Concat(rawArgs).ToArray();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "sort", rawArgs)) return;
        if (Array.IndexOf(rawArgs, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "sort"))
            {
                WriteObject(line);
            }
            return;
        }

        var plan = Plan(rawArgs);
        if (FileSystemHelpers.TryWriteParseError(this, "sort", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "sort", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, plan.ErrorExit);
            return;
        }

        // ---- gather input: items (original objects), compare texts, per-item locators, source runs ----
        var items = new List<object>();
        var texts = new List<string>();
        var labels = new List<string>();
        var lineNos = new List<int>();
        var sourceStarts = new List<int>();

        void AddPipeline()
        {
            sourceStarts.Add(items.Count);
            int line = 0;
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var sub in trimmed.Split('\n'))
                    {
                        items.Add(sub); texts.Add(sub); labels.Add("-"); lineNos.Add(++line);
                    }
                }
                else
                {
                    items.Add(item); texts.Add(trimmed); labels.Add("-"); lineNos.Add(++line);
                }
            }
        }

        if (plan.Operands.Count == 0)
        {
            AddPipeline();
        }
        else
        {
            bool stdinUsed = false;
            foreach (var raw in plan.Operands)
            {
                if (raw == "-")
                {
                    if (!stdinUsed) { AddPipeline(); stdinUsed = true; }
                    continue;
                }
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    sourceStarts.Add(items.Count);
                    try
                    {
                        int fileLine = 0;
                        string label = filePath.Replace('\\', '/');
                        foreach (var line in BashFileSystem.ReadLines(filePath))
                        {
                            items.Add(BashRuntime.NewBashObject(line));
                            texts.Add(line);
                            labels.Add(label);
                            lineNos.Add(++fileLine);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        // GNU: an unreadable input is fatal — nothing is written.
                        WriteReadError(filePath, ex);
                        FileSystemHelpers.SetLastExitCode(this, 2);
                        return;
                    }
                }
            }
        }

        var engine = new SortEngine(plan.Plan);
        engine.Prepare(texts, i => ExtractSizeBytes(items[i]));

        if (plan.CheckMode != 0)
        {
            int bad = engine.FirstDisorder();
            if (bad < 0) { FileSystemHelpers.SetLastExitCode(this, 0); return; }
            if (plan.CheckMode == 1)
            {
                FileSystemHelpers.WriteBashError(this, $"sort: {labels[bad]}:{lineNos[bad]}: disorder: {texts[bad]}");
            }
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        var order = engine.Order(sourceStarts);

        // -o FILE: write the sorted lines (newline-terminated) instead of the pipeline. Everything is read
        // first, so `sort -o f f` sorts in place.
        if (plan.Output != null)
        {
            var sb = new System.Text.StringBuilder();
            foreach (int idx in order) sb.Append(texts[idx]).Append('\n');
            try
            {
                var outPath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(plan.Output);
                File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                string reason = ex is DirectoryNotFoundException or FileNotFoundException
                    ? "No such file or directory" : ex.Message;
                FileSystemHelpers.WriteBashError(this, $"sort: open failed: {plan.Output.Replace('\\', '/')}: {reason}");
                FileSystemHelpers.SetLastExitCode(this, 2);
            }
            return;
        }

        foreach (int idx in order)
        {
            // sort is a FILTER: original objects pass through. sort terminates every line, so
            // PassTerminated strips a stale missing-newline flag (the flagged record may sort
            // anywhere). Bare strings get wrapped into the default PsBash.TextOutput shape.
            var item = items[idx];
            if (item is PSObject ps) WriteObject(BashRuntime.PassTerminated(ps));
            else if (item is string str) WriteObject(BashRuntime.NewBashObject(str));
            else WriteObject(item);
        }
    }

    /// <summary>
    /// Extracts a SizeBytes-typed property from a pipeline object (LsEntry from ls -lh has
    /// SizeBytes:long). Returns null when the object lacks one so the engine parses the text.
    /// </summary>
    private static double? ExtractSizeBytes(object item)
    {
        PSObject? pso = item as PSObject ?? (item != null ? PSObject.AsPSObject(item) : null);
        var prop = pso?.Properties["SizeBytes"];
        if (prop?.Value == null) return null;
        try
        {
            return Convert.ToDouble(prop.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"sort: cannot read: {normalized}: {msg}");
    }
}
