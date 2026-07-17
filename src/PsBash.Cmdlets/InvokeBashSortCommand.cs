using System.Globalization;
using System.Management.Automation;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashSort</c> function
/// (REFACTOR-2 follow-on). Sorts input lines with support for the GNU
/// coreutils <c>sort</c> flag surface byte-for-byte against the psm1 oracle:
/// <c>-r</c> reverse, <c>-n</c> numeric, <c>-u</c> unique, <c>-f</c> fold-case,
/// <c>-h</c> human-numeric (10K, 1.5M), <c>-V</c> version (natural alphanumeric),
/// <c>-M</c> month-name, <c>-c</c> check-sorted, <c>-b</c> blank-ignore,
/// <c>-d</c> dictionary-order, <c>-s</c> stable, <c>-t SEP</c> field separator,
/// <c>-k POS[,POS]</c> key spec with per-key <c>n/r/b</c> modifiers.
///
/// File + pipeline dual mode. Pipeline mode preserves the original typed
/// pipeline objects (LsEntry / CatLine / ...) — they are sorted by their
/// <c>BashText</c> derived sort key and re-emitted in sorted order. File mode
/// streams input lines with CRLF normalization and StreamReader.ReadLine
/// semantics.
///
/// Flag binding: three short flags prefix-collide with PowerShell common
/// parameters and are declared as explicit <see cref="SwitchParameter"/>s
/// (an exact param-name match beats a common-parameter prefix match):
/// <list type="bullet">
/// <item><c>-c</c> (check) vs <c>-Confirm</c> — declared as <c>C</c>.</item>
/// <item><c>-d</c> (dict order) vs <c>-Debug</c> — declared as <c>D</c>.</item>
/// <item><c>-V</c> (version) vs <c>-Verbose</c> — declared as <c>V</c>.</item>
/// </list>
/// All other short flags (<c>-r -n -u -f -h -M -b -s</c>) have no PS
/// common-parameter prefix collision and stay in <see cref="Arguments"/>; the
/// value-bearing <c>-t</c> / <c>-k</c> also stay in <see cref="Arguments"/> and
/// are parsed (separated and joined forms) by the manual scan. Bundled forms
/// like <c>-rn</c>, <c>-un</c>, <c>-rb</c> survive the binder by landing in
/// <see cref="Arguments"/> intact and are recovered post-parse against the
/// oracle's per-char dispatch.
///
/// Directive 12: a <c>-k VALUE</c> value containing <c>$(throw 'pwn')</c> is
/// never re-parsed as PowerShell — the value is fed only to the oracle's
/// regex parser (<c>^(\d+)(?:\.(\d+))?([nrRbB]*)?$</c>) which simply does not
/// match, and the spec falls through to all-zero fields, matching the oracle
/// byte-for-byte (no exception, no eval, no output object).
///
/// Output preserves the original pipeline objects when present; in file mode,
/// emits typed <c>PsBash.TextOutput</c> objects via
/// <see cref="BashRuntime.NewBashObject(string)"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSort")]
[OutputType(typeof(string))]
public sealed class InvokeBashSortCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter]
    public SwitchParameter D { get; set; }

    [Parameter]
    public SwitchParameter V { get; set; }

    /// <summary>Decoy for the valid-but-unsupported <c>-i</c> (--ignore-nonprinting).
    /// Bare <c>-i</c> prefix-collides with <c>-InformationAction</c>/<c>-InformationVariable</c>
    /// and crashed the binder; re-injected below so the scan emits exit 2.</summary>
    [Parameter]
    public SwitchParameter I { get; set; }

    /// <summary>
    /// Bash <c>-o FILE</c> (output file). A bare <c>-o</c> is an ambiguous
    /// prefix of the common parameters <c>-OutVariable</c> / <c>-OutBuffer</c>,
    /// so it must be declared with an exact single-letter name to bind. The
    /// joined form <c>-oFILE</c> and <c>--output=FILE</c> arrive via
    /// <see cref="Arguments"/> and are recovered by the manual scan.
    /// </summary>
    [Parameter]
    public string? O { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    // Comparator/key-extraction patterns are invariant but were evaluated per
    // comparison (O(n log n)) or per key. Hoisted to compiled static fields so
    // a sort never relocks the shared Regex cache or risks cache eviction.
    private static readonly Regex s_dictStrip = new(@"[^a-zA-Z0-9\s]", RegexOptions.Compiled);
    private static readonly Regex s_leadingBlank = new(@"^\s+", RegexOptions.Compiled);
    // GNU `-n` ignores a field's leading blanks: the prefix scan allows
    // optional leading whitespace (the captured value is fed to double.Parse
    // with NumberStyles.Float, which also tolerates leading white). Without the
    // `^\s*` a numeric key like "\t9" (a tab-led field under the GNU field
    // model) failed to match and collapsed to 0 — the -k-with-leading-blank bug.
    private static readonly Regex s_numericPrefix = new(@"^\s*[+-]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex s_humanNumeric = new(@"^([0-9]*\.?[0-9]+)\s*([KMGTP])$", RegexOptions.Compiled);
    private static readonly Regex s_versionSplit = new(@"[.\-]", RegexOptions.Compiled);
    private static readonly Regex s_keySpecPos = new(@"^(\d+)(?:\.(\d+))?([nrRbB]*)?$", RegexOptions.Compiled);

    /// <summary>
    /// Valid GNU <c>sort</c> options ps-bash does not implement (this cmdlet
    /// parses only short flags). Hitting one yields a specific "recognized but
    /// not supported" message via <see cref="FileSystemHelpers.WriteOptionError"/>
    /// instead of the old misleading "No such file or directory" (the token used
    /// to fall through to the file-operand list). Anything option-looking NOT in
    /// this set is reported as unrecognized/invalid (bash parity). Includes the
    /// long forms of even the *supported* short flags, since the long spellings
    /// are genuinely unparsed here. Representative — see the flag-catalog rollout.
    /// </summary>
    private static readonly HashSet<string> ValidButUnsupported = new(StringComparer.Ordinal)
    {
        // Short flags not implemented. (-g is now supported.)
        "-i", "-R", "-z", "-m", "-S", "-T",
        // Long forms not implemented. The aliases of supported short flags
        // (--reverse, --numeric-sort, --unique, --ignore-case, --dictionary-order,
        // --ignore-leading-blanks, --general-numeric-sort, --month-sort,
        // --human-numeric-sort, --version-sort, --stable, --check, --key,
        // --field-separator, --sort) are now parsed and are NOT listed here.
        "--ignore-nonprinting", "--random-sort", "--zero-terminated", "--merge",
        "--buffer-size", "--temporary-directory", "--parallel",
        "--debug", "--batch-size", "--compress-program", "--files0-from",
        "--random-source",
    };

    private sealed class KeySpec
    {
        public int StartField;
        public int StartChar;
        public int EndField;
        public int EndChar;
        public bool Numeric;
        public bool Reverse;
        public bool BlankIgnore;
    }

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        // Re-inject the decoy-bound -i so the scan emits the exit-2 "recognized but
        // not supported" message (bare -i crashed the binder otherwise).
        var rawArgs = BashRuntime.PrependDecoys(Arguments, (I.IsPresent, "-i"));

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

        bool reverse = false;
        bool numeric = false;
        bool generalNumeric = false;
        bool unique = false;
        bool foldCase = false;
        bool humanNumeric = false;
        bool versionSort = V.IsPresent;
        bool monthSort = false;
        bool checkOnly = C.IsPresent;
        bool blankIgnore = false;
        bool dictOrder = D.IsPresent;
        bool stable = false;
        string? delimiter = null;
        string? outputFile = O;
        var keySpecs = new List<KeySpec>();
        var operands = new List<string>();
        bool pastDoubleDash = false;

        int i = 0;
        while (i < rawArgs.Length)
        {
            var arg = rawArgs[i];

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

            // -o FILE (output) — separate, joined, and long forms.
            if (arg == "-o" && i + 1 < rawArgs.Length)
            {
                outputFile = rawArgs[++i];
                i++;
                continue;
            }
            if (arg.Length > 2 && arg.StartsWith("-o", StringComparison.Ordinal))
            {
                outputFile = arg.Substring(2);
                i++;
                continue;
            }
            if (arg.StartsWith("--output=", StringComparison.Ordinal))
            {
                outputFile = arg.Substring("--output=".Length);
                i++;
                continue;
            }
            if (arg == "--output" && i + 1 < rawArgs.Length)
            {
                outputFile = rawArgs[++i];
                i++;
                continue;
            }

            // -t with joined value (e.g. -t:)
            if (arg.Length > 2 && arg.StartsWith("-t", StringComparison.Ordinal))
            {
                delimiter = arg.Substring(2);
                i++;
                continue;
            }

            // -k with joined value (e.g. -k2 / -k2,2 / -k2.3,4.1n)
            // Oracle pattern: ^-k(\d[^,\s]*(?:,\d[^,\s]*)?)$
            if (arg.Length > 2 && arg.StartsWith("-k", StringComparison.Ordinal)
                && char.IsDigit(arg[2]))
            {
                var spec = ParseKeySpec(arg.Substring(2));
                if (spec != null) keySpecs.Add(spec);
                i++;
                continue;
            }

            // -t as separate arg
            if (arg == "-t")
            {
                i++;
                if (i < rawArgs.Length) { delimiter = rawArgs[i]; }
                i++;
                continue;
            }

            // -k as separate arg
            if (arg == "-k")
            {
                i++;
                if (i < rawArgs.Length)
                {
                    var spec = ParseKeySpec(rawArgs[i]);
                    if (spec != null) keySpecs.Add(spec);
                }
                i++;
                continue;
            }

            // Long-form flags. The boolean ones are exact aliases of the short
            // flags above; the value-bearing ones (--key / --field-separator)
            // mirror -k / -t. GNU sort accepts both spellings interchangeably.
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                // --field-separator=SEP / --field-separator SEP  (alias of -t)
                if (arg.StartsWith("--field-separator=", StringComparison.Ordinal))
                { delimiter = arg.Substring("--field-separator=".Length); i++; continue; }
                if (arg == "--field-separator")
                { i++; if (i < rawArgs.Length) delimiter = rawArgs[i]; i++; continue; }
                // --key=SPEC / --key SPEC  (alias of -k)
                if (arg.StartsWith("--key=", StringComparison.Ordinal))
                { var s = ParseKeySpec(arg.Substring("--key=".Length)); if (s != null) keySpecs.Add(s); i++; continue; }
                if (arg == "--key")
                { i++; if (i < rawArgs.Length) { var s = ParseKeySpec(rawArgs[i]); if (s != null) keySpecs.Add(s); } i++; continue; }
                // --sort=WORD  (GNU mode selector)
                if (arg.StartsWith("--sort=", StringComparison.Ordinal))
                {
                    switch (arg.Substring("--sort=".Length))
                    {
                        case "general-numeric": generalNumeric = true; break;
                        case "numeric": numeric = true; break;
                        case "human-numeric": humanNumeric = true; break;
                        case "month": monthSort = true; break;
                        case "version": versionSort = true; break;
                        // "general" / unknown WORD: leave default (lexical).
                    }
                    i++;
                    continue;
                }
                bool matchedLong = true;
                switch (arg)
                {
                    case "--reverse": reverse = true; break;
                    case "--numeric-sort": numeric = true; break;
                    case "--general-numeric-sort": generalNumeric = true; break;
                    case "--unique": unique = true; break;
                    case "--ignore-case": foldCase = true; break;
                    case "--human-numeric-sort": humanNumeric = true; break;
                    case "--version-sort": versionSort = true; break;
                    case "--month-sort": monthSort = true; break;
                    case "--check": checkOnly = true; break;
                    case "--ignore-leading-blanks": blankIgnore = true; break;
                    case "--dictionary-order": dictOrder = true; break;
                    case "--stable": stable = true; break;
                    default: matchedLong = false; break;
                }
                if (matchedLong) { i++; continue; }
            }

            if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1
                && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                foreach (char ch in arg.Substring(1))
                {
                    switch (ch)
                    {
                        case 'r': reverse = true; break;
                        case 'n': numeric = true; break;
                        case 'g': generalNumeric = true; break;
                        case 'u': unique = true; break;
                        case 'f': foldCase = true; break;
                        case 'h': humanNumeric = true; break;
                        case 'V': versionSort = true; break;
                        case 'M': monthSort = true; break;
                        case 'c': checkOnly = true; break;
                        case 'b': blankIgnore = true; break;
                        case 'd': dictOrder = true; break;
                        case 's': stable = true; break;
                        default:
                            // Unknown short flag: valid-but-unsupported sort
                            // option → specific refusal; else bash-parity
                            // "invalid option". getopt stops at the first
                            // offending char.
                            FileSystemHelpers.WriteOptionError(this, "sort", "-" + ch, ValidButUnsupported);
                            return;
                    }
                }
                i++;
                continue;
            }

            // Any remaining option-looking token (a long flag this cmdlet does
            // not parse, e.g. --reverse / --bogus) is NOT a file operand.
            // Classify: valid-but-unsupported → specific refusal; otherwise
            // bash-parity "unrecognized option". A lone "-" (stdin) is not
            // option-like and falls through to operands.
            if (FileSystemHelpers.IsOptionLike(arg))
            {
                FileSystemHelpers.WriteOptionError(this, "sort", arg, ValidButUnsupported);
                return;
            }

            operands.Add(arg);
            i++;
        }

        // GNU sort rejects an empty field separator (`-t ''`) with exit 2 rather
        // than mis-splitting at every position via an empty-pattern regex.
        if (delimiter is { Length: 0 })
        {
            FileSystemHelpers.WriteBashError(this, "sort: empty tab");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        // Collect items.
        var items = new List<object>();
        // Parallel to items: the "sort -c" disorder message needs a GNU-style
        // "<file>:<line>" locator per item — "-" for pipeline/stdin input, the
        // operand path (with a per-file 1-based line counter) for file input.
        var itemLabels = new List<string>();
        var itemLineNo = new List<int>();
        bool hadError = false;

        if (_pipeline.Count > 0)
        {
            int pipelineLine = 0;
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        items.Add(subLine);
                        itemLabels.Add("-");
                        itemLineNo.Add(++pipelineLine);
                    }
                }
                else
                {
                    items.Add(item);
                    itemLabels.Add("-");
                    itemLineNo.Add(++pipelineLine);
                }
            }
        }

        foreach (var raw in operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                try
                {
                    int fileLine = 0;
                    string label = filePath.Replace('\\', '/');
                    foreach (var line in BashFileSystem.ReadLines(filePath))
                    {
                        items.Add(BashRuntime.NewBashObject(line));
                        itemLabels.Add(label);
                        itemLineNo.Add(++fileLine);
                    }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    WriteReadError(filePath, ex);
                    hadError = true;
                }
            }
        }

        // Capture closure state for the comparator.
        bool gReverse = reverse;
        bool gNumeric = numeric;
        bool gGeneral = generalNumeric;
        bool gFold = foldCase;
        bool gHuman = humanNumeric;
        bool gMonth = monthSort;
        bool gBlank = blankIgnore;
        bool gDict = dictOrder;
        bool gStable = stable;
        string? gDelim = delimiter;

        // Decorate-sort-undecorate: a comparison sort calls the comparator
        // O(n log n) times, but every sort key is a pure function of its item.
        // Extracting keys per comparison meant ~2·n·log n key derivations (each
        // doing GetBashText + regex splits); precomputing each item's key ONCE
        // into parallel arrays (indexed by original position) drops that to n.
        // The arrays carry exactly the values the comparator used to recompute,
        // so results are byte-identical — this is memoization, not new logic.
        int n = items.Count;
        bool hasKeySpecs = keySpecs.Count > 0;
        var dRaw = new string[n];                                   // GetFullText (version sort / unique)
        var dGlobalText = hasKeySpecs ? null : new string[n];       // global cmp text (post dict-strip)
        var dGlobalNum = (!hasKeySpecs && (gHuman || gNumeric || gGeneral)) ? new double[n] : null;
        var dGlobalMonth = (!hasKeySpecs && gMonth) ? new int[n] : null;
        // Per-key-spec decorated values: text (post dict-strip) + numeric.
        var dKeyText = hasKeySpecs ? new string[n][] : null;
        var dKeyNum = hasKeySpecs ? new double[n][] : null;
        var dKeyMonth = hasKeySpecs ? new int[n][] : null;
        // Version-sort decoration honors -k: one version-part array per key spec
        // (or a single full-line entry when no -k is given). This is what makes
        // `sort -V -k1,1` compare the keyed field, not the whole line (GNU parity).
        var dVerKeyParts = versionSort ? new string[n][][] : null;
        for (int di = 0; di < n; di++)
        {
            object it = items[di];
            string raw = GetFullText(it, gBlank);
            dRaw[di] = raw;
            if (hasKeySpecs)
            {
                int ks = keySpecs.Count;
                var kt = new string[ks];
                var kn = new double[ks];
                var km = new int[ks];
                for (int s = 0; s < ks; s++)
                {
                    var spec = keySpecs[s];
                    string key = ExtractKeyText(it, spec, gDelim, gBlank);
                    if (gDict) key = s_dictStrip.Replace(key, "");
                    kt[s] = key;
                    if (gHuman) kn[s] = ConvertFromHumanNumeric(key);
                    else if (gGeneral) kn[s] = ParseGeneralNumeric(key);
                    else if (spec.Numeric || gNumeric) kn[s] = ParseNumericPrefix(key);
                    else if (gMonth) km[s] = ConvertFromMonthName(key);
                }
                dKeyText![di] = kt;
                dKeyNum![di] = kn;
                dKeyMonth![di] = km;
                if (versionSort)
                {
                    var vp = new string[ks][];
                    for (int s = 0; s < ks; s++) vp[s] = s_versionSplit.Split(kt[s]);
                    dVerKeyParts![di] = vp;
                }
            }
            else
            {
                if (versionSort) dVerKeyParts![di] = new[] { s_versionSplit.Split(raw) };
                string text = gDict ? s_dictStrip.Replace(raw, "") : raw;
                dGlobalText![di] = text;
                if (gHuman) dGlobalNum![di] = ExtractSizeBytes(it) ?? ConvertFromHumanNumeric(text);
                else if (gGeneral) dGlobalNum![di] = ParseGeneralNumeric(text);
                else if (gNumeric)
                {
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
                    dGlobalNum![di] = v;
                }
                else if (gMonth) dGlobalMonth![di] = ConvertFromMonthName(text);
            }
        }

        int Compare(int ai, int bi)
        {
            if (hasKeySpecs)
            {
                for (int s = 0; s < keySpecs.Count; s++)
                {
                    var spec = keySpecs[s];
                    int cmp;
                    if (gHuman || gGeneral)
                    {
                        double aH = dKeyNum![ai][s], bH = dKeyNum![bi][s];
                        cmp = aH < bH ? -1 : (aH > bH ? 1 : 0);
                    }
                    else if (spec.Numeric || gNumeric)
                    {
                        double aN = dKeyNum![ai][s], bN = dKeyNum![bi][s];
                        cmp = aN < bN ? -1 : (aN > bN ? 1 : 0);
                    }
                    else if (gMonth)
                    {
                        cmp = Math.Sign(dKeyMonth![ai][s] - dKeyMonth![bi][s]);
                    }
                    else if (gFold)
                    {
                        cmp = string.Compare(dKeyText![ai][s], dKeyText![bi][s], StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        cmp = string.CompareOrdinal(dKeyText![ai][s], dKeyText![bi][s]);
                    }
                    if (spec.Reverse || gReverse) cmp = -cmp;
                    if (cmp != 0) return Math.Sign(cmp);
                }
                return 0;
            }

            int cmp2;
            if (gHuman || gGeneral)
            {
                // SizeBytes-aware value precomputed per item (LsEntry from ls -lh),
                // or general-numeric (-g) full double parse.
                double aH = dGlobalNum![ai], bH = dGlobalNum![bi];
                cmp2 = aH < bH ? -1 : (aH > bH ? 1 : 0);
            }
            else if (gNumeric)
            {
                double aN = dGlobalNum![ai], bN = dGlobalNum![bi];
                cmp2 = aN < bN ? -1 : (aN > bN ? 1 : 0);
            }
            else if (gMonth)
            {
                cmp2 = Math.Sign(dGlobalMonth![ai] - dGlobalMonth![bi]);
            }
            else if (gFold)
            {
                cmp2 = string.Compare(dGlobalText![ai], dGlobalText![bi], StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                cmp2 = string.CompareOrdinal(dGlobalText![ai], dGlobalText![bi]);
            }
            if (gReverse) cmp2 = -cmp2;
            return Math.Sign(cmp2);
        }

        // Check-only mode: walk pairs, exit 1 on first out-of-order pair. GNU
        // sort -c reports "sort: <file>:<line>: disorder: <content>" to stderr
        // on the first offending line (1-based line of the disordered line).
        if (checkOnly)
        {
            for (int idx = 1; idx < items.Count; idx++)
            {
                if (Compare(idx - 1, idx) > 0)
                {
                    string content = BashRuntime.GetBashText(items[idx]).TrimEnd('\n');
                    FileSystemHelpers.WriteBashError(
                        this, $"sort: {itemLabels[idx]}:{itemLineNo[idx]}: disorder: {content}");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
            }
            FileSystemHelpers.SetLastExitCode(this, 0);
            return;
        }

        // GNU last-resort tie-break: when the keyed/global comparison is equal,
        // sort compares the ENTIRE original line bytewise (reversed by a GLOBAL
        // -r). This is NOT applied in -c check mode (GNU -c treats equal keys as
        // in order) and is suppressed by -s (stable: equal keys keep input
        // order). Without it, ps-bash fell back to input order for every tie,
        // diverging from GNU whenever equal-key lines differed in their tails
        // (e.g. `Apple` vs `apple`, both field2=5). The original-line index is
        // the absolute last tie-break, preserving stability for identical lines.
        int LastResort(int ai, int bi)
        {
            if (gStable) return 0;
            int c = string.CompareOrdinal(dRaw[ai], dRaw[bi]);
            if (gReverse) c = -c;
            return Math.Sign(c);
        }

        // Build index list for stable sort tracking (List.Sort is not stable so
        // we attach the original index as the absolute final tie-break).
        var indexed = new List<(int Index, object Item)>(items.Count);
        for (int idx = 0; idx < items.Count; idx++)
        {
            indexed.Add((idx, items[idx]));
        }

        if (versionSort)
        {
            indexed.Sort((a, b) =>
            {
                var pa = dVerKeyParts![a.Index];
                var pb = dVerKeyParts![b.Index];
                int c = 0;
                for (int s = 0; s < pa.Length; s++)
                {
                    int cc = CompareVersionParts(pa[s], pb[s]);
                    bool rev = hasKeySpecs ? (keySpecs[s].Reverse || gReverse) : gReverse;
                    if (rev) cc = -cc;
                    if (cc != 0) { c = cc; break; }
                }
                if (c != 0) return c;
                c = LastResort(a.Index, b.Index);
                if (c != 0) return c;
                return a.Index - b.Index;
            });
        }
        else
        {
            indexed.Sort((a, b) =>
            {
                int c = Compare(a.Index, b.Index);
                if (c != 0) return c;
                c = LastResort(a.Index, b.Index);
                if (c != 0) return c;
                return a.Index - b.Index;
            });
        }

        IEnumerable<(int Index, object Item)> sorted = indexed;

        if (unique)
        {
            // GNU `sort -u` emits only the FIRST line of each run of lines that
            // COMPARE EQUAL under the active key/ordering — it does NOT dedup by
            // the whole line. The whole-line LastResort/stable tiebreak is
            // deliberately EXCLUDED from the equality test, so two lines equal on
            // the key but differing in the rest of the line collapse to one (e.g.
            // `1 a`/`1 b` under `-k1,1`, or `01`/`1` under `-n`). The list is
            // already sorted, so equal-key lines are contiguous — keep the first
            // of each run. With no `-k`/`-n`/... the "key" is the full line, so
            // plain `-u` still dedups by full line exactly as before.
            int KeyCompare(int ai, int bi)
            {
                if (versionSort)
                {
                    var pa = dVerKeyParts![ai];
                    var pb = dVerKeyParts![bi];
                    for (int s = 0; s < pa.Length; s++)
                    {
                        int cc = CompareVersionParts(pa[s], pb[s]);
                        bool rev = hasKeySpecs ? (keySpecs[s].Reverse || gReverse) : gReverse;
                        if (rev) cc = -cc;
                        if (cc != 0) return cc;
                    }
                    return 0;
                }
                return Compare(ai, bi);
            }

            var deduped = new List<(int Index, object Item)>();
            bool havePrev = false;
            int prevIndex = -1;
            foreach (var entry in indexed)
            {
                if (!havePrev || KeyCompare(prevIndex, entry.Index) != 0)
                {
                    deduped.Add(entry);
                    prevIndex = entry.Index;
                    havePrev = true;
                }
            }
            sorted = deduped;
        }

        // -o FILE: write the sorted lines to a file (newline-terminated) instead
        // of the pipeline. GNU sort -o is a stdout redirect that also allows
        // in-place sort (`sort -o f f`); we materialize then write so an
        // input==output file is read fully before being overwritten.
        if (outputFile != null)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var entry in sorted)
            {
                sb.Append(BashRuntime.GetBashText(entry.Item).TrimEnd('\n')).Append('\n');
            }
            try
            {
                var outPath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(outputFile);
                File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this, $"sort: {outputFile.Replace('\\', '/')}: {ex.Message}");
                FileSystemHelpers.SetLastExitCode(this, 1);
            }
            return;
        }

        foreach (var entry in sorted)
        {
            // Preserve original objects when they're PSObjects; bare strings get
            // wrapped into the default PsBash.TextOutput shape.
            if (entry.Item is PSObject ps)
            {
                WriteObject(ps);
            }
            else if (entry.Item is string s)
            {
                WriteObject(BashRuntime.NewBashObject(s));
            }
            else
            {
                WriteObject(entry.Item);
            }
        }

        if (hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    // ----- helpers (oracle parity, reimplemented in C#) -----

    private static string GetFullText(object item, bool blankIgnore)
    {
        string text = BashRuntime.GetBashText(item);
        text = text.TrimEnd('\n');
        if (blankIgnore) text = s_leadingBlank.Replace(text, "");
        return text;
    }

    private static string ExtractKeyText(object item, KeySpec spec, string? delimiter, bool gBlank)
    {
        string text = BashRuntime.GetBashText(item);
        text = text.TrimEnd('\n');

        // Field splitting. GNU sort has two field models:
        //   * With -t CHAR: split on the exact char; empty fields are real and
        //     the separator delimits but is NOT part of a field. A multi-field
        //     key re-joins the selected fields with the delimiter (GNU includes
        //     the separator between fields of a -k range).
        //   * Default (no -t): a field is any leading blanks followed by a
        //     maximal run of non-blanks. The field separator is the zero-width
        //     point between a non-blank and a following blank, so the leading
        //     blanks of a field belong to the field that FOLLOWS them — they are
        //     part of the key and are significant for lexical comparison (unless
        //     -b). A naive `\s+` split instead emitted an empty leading field for
        //     a line with leading blanks ("  x\t9" -> ["", "x", "9"]), so -k2
        //     selected "x" not "9": the -k misalignment bug. Each field carries
        //     its own leading separator blanks, so a multi-field key is the raw
        //     substring — concatenate the selected fields with no extra separator.
        string[] parts;
        string joinSep;
        if (delimiter != null)
        {
            parts = Regex.Split(text, Regex.Escape(delimiter));
            joinSep = delimiter;
        }
        else
        {
            parts = SplitWhitespaceFields(text);
            joinSep = "";
        }

        int startIdx = spec.StartField - 1;
        if (startIdx < 0) startIdx = 0;
        if (startIdx >= parts.Length) return "";
        int endIdx = spec.EndField > 0 ? spec.EndField - 1 : parts.Length - 1;
        if (endIdx >= parts.Length) endIdx = parts.Length - 1;
        if (endIdx < startIdx) endIdx = startIdx;

        string key;
        if (startIdx == endIdx)
        {
            // Single-field key — the common case, no join allocation.
            key = ApplyCharOffsets(parts[startIdx], spec.StartChar, spec.EndChar);
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            for (int fi = startIdx; fi <= endIdx; fi++)
            {
                int startChar = fi == startIdx ? spec.StartChar : 0;
                int endChar = fi == endIdx ? spec.EndChar : 0;
                if (fi > startIdx) sb.Append(joinSep);
                sb.Append(ApplyCharOffsets(parts[fi], startChar, endChar));
            }
            key = sb.ToString();
        }

        if (spec.BlankIgnore || gBlank)
        {
            key = StripLeadingBlanks(key);
        }
        return key;
    }

    /// <summary>
    /// Applies a key position's 1-based start/end character offsets within a
    /// single field (0 = no offset). Matches the original per-field slicing.
    /// </summary>
    private static string ApplyCharOffsets(string fieldText, int startChar, int endChar)
    {
        if (startChar > 0)
        {
            int skip = startChar - 1;
            fieldText = skip < fieldText.Length ? fieldText.Substring(skip) : "";
        }
        if (endChar > 0 && endChar < fieldText.Length)
        {
            fieldText = fieldText.Substring(0, endChar);
        }
        return fieldText;
    }

    /// <summary>
    /// GNU default field split (no -t): each field is its leading blanks plus a
    /// maximal run of non-blanks. A field boundary is the point where a blank
    /// (space/tab) follows a non-blank, so the run of blanks separating two
    /// fields is attached to the FOLLOWING field. Manual single-pass scan (no
    /// regex) — this is the per-line, per-key hot path.
    /// </summary>
    private static string[] SplitWhitespaceFields(string text)
    {
        if (text.Length == 0) return new[] { string.Empty };
        var fields = new List<string>();
        int start = 0;
        for (int i = 1; i < text.Length; i++)
        {
            bool cBlank = text[i] == ' ' || text[i] == '\t';
            bool pBlank = text[i - 1] == ' ' || text[i - 1] == '\t';
            if (cBlank && !pBlank)
            {
                fields.Add(text.Substring(start, i - start));
                start = i;
            }
        }
        fields.Add(text.Substring(start));
        return fields.ToArray();
    }

    private static string StripLeadingBlanks(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return i == 0 ? s : s.Substring(i);
    }

    private static double ParseNumericPrefix(string s)
    {
        var m = s_numericPrefix.Match(s);
        string numStr = m.Success ? m.Value : "0";
        double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
        return v;
    }

    /// <summary>
    /// GNU <c>-g</c> / <c>--general-numeric-sort</c>: parse the whole (trimmed)
    /// field as a general floating-point number — unlike <c>-n</c> this honors
    /// scientific notation (<c>1e3</c>) and a leading sign on the full value.
    /// An unparseable field sorts as 0 (GNU treats non-numbers as 0).
    /// </summary>
    private static double ParseGeneralNumeric(string s)
    {
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : 0.0;
    }

    private static KeySpec? ParseKeySpec(string spec)
    {
        var result = new KeySpec();
        var parts = spec.Split(new[] { ',' }, 2);
        var start = ParseKeySpecPos(parts[0]);
        result.StartField = start.field;
        result.StartChar = start.charOffset;
        result.Numeric = start.numeric;
        result.Reverse = start.reverse;
        result.BlankIgnore = start.blankIgnore;
        if (parts.Length >= 2)
        {
            var end = ParseKeySpecPos(parts[1]);
            result.EndField = end.field;
            result.EndChar = end.charOffset;
            if (end.numeric) result.Numeric = true;
            if (end.reverse) result.Reverse = true;
            if (end.blankIgnore) result.BlankIgnore = true;
        }
        return result;
    }

    private static (int field, int charOffset, bool numeric, bool reverse, bool blankIgnore)
        ParseKeySpecPos(string s)
    {
        // Oracle regex: ^(\d+)(?:\.(\d+))?([nrRbB]*)?$
        var m = s_keySpecPos.Match(s);
        int field = 0, charOffset = 0;
        bool numeric = false, reverse = false, blankIgnore = false;
        if (m.Success)
        {
            int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out field);
            if (m.Groups[2].Success && m.Groups[2].Value.Length > 0)
            {
                int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out charOffset);
            }
            if (m.Groups[3].Success)
            {
                foreach (char c in m.Groups[3].Value)
                {
                    switch (c)
                    {
                        case 'n': numeric = true; break;
                        case 'r': reverse = true; break;
                        case 'R': reverse = true; break;
                        case 'b': blankIgnore = true; break;
                        case 'B': blankIgnore = true; break;
                    }
                }
            }
        }
        return (field, charOffset, numeric, reverse, blankIgnore);
    }

    /// <summary>
    /// Extracts a SizeBytes-typed property from a pipeline object (LsEntry
    /// from ls -lh has SizeBytes:long). Returns null when the object lacks
    /// such a property so the caller falls back to text-based parsing.
    /// </summary>
    private static double? ExtractSizeBytes(object item)
    {
        PSObject? pso = item as PSObject ?? (item != null ? PSObject.AsPSObject(item) : null);
        var prop = pso?.Properties["SizeBytes"];
        if (prop?.Value == null) return null;
        try
        {
            return Convert.ToDouble(prop.Value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static double ConvertFromHumanNumeric(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return 0.0;
        var m = s_humanNumeric.Match(trimmed);
        if (m.Success)
        {
            double num = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            double mult = m.Groups[2].Value switch
            {
                "K" => 1024.0,
                "M" => 1048576.0,
                "G" => 1073741824.0,
                "T" => 1099511627776.0,
                "P" => 1125899906842624.0,
                _ => 1.0,
            };
            return num * mult;
        }
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        return 0.0;
    }

    private static int ConvertFromMonthName(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.Length < 3) return 0;
        return trimmed.Substring(0, 3) switch
        {
            "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4,
            "may" => 5, "jun" => 6, "jul" => 7, "aug" => 8,
            "sep" => 9, "oct" => 10, "nov" => 11, "dec" => 12,
            _ => 0,
        };
    }

    private static int CompareVersionParts(string[] leftParts, string[] rightParts)
    {
        int max = Math.Max(leftParts.Length, rightParts.Length);
        for (int i = 0; i < max; i++)
        {
            string lp = i < leftParts.Length ? leftParts[i] : "0";
            string rp = i < rightParts.Length ? rightParts[i] : "0";
            bool lIsNum = int.TryParse(lp, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ln);
            bool rIsNum = int.TryParse(rp, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rn);
            if (lIsNum && rIsNum)
            {
                if (ln != rn) return ln - rn;
            }
            else
            {
                int cmp = string.CompareOrdinal(lp, rp);
                if (cmp != 0) return cmp;
            }
        }
        return 0;
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"sort: {normalized}: {msg}");
    }
}
