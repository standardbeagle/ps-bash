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

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

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
        // Short flags not implemented.
        "-g", "-i", "-R", "-z", "-o", "-m", "-S", "-T",
        // Long forms (none are implemented by this cmdlet).
        "--reverse", "--numeric-sort", "--unique", "--ignore-case",
        "--dictionary-order", "--ignore-leading-blanks", "--general-numeric-sort",
        "--ignore-nonprinting", "--month-sort", "--human-numeric-sort",
        "--random-sort", "--version-sort", "--stable", "--zero-terminated",
        "--check", "--key", "--field-separator", "--output", "--merge",
        "--buffer-size", "--temporary-directory", "--parallel", "--sort",
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
        var rawArgs = Arguments ?? Array.Empty<string>();

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
        bool unique = false;
        bool foldCase = false;
        bool humanNumeric = false;
        bool versionSort = V.IsPresent;
        bool monthSort = false;
        bool checkOnly = C.IsPresent;
        bool blankIgnore = false;
        bool dictOrder = D.IsPresent;
        string? delimiter = null;
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

            if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1
                && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                foreach (char ch in arg.Substring(1))
                {
                    switch (ch)
                    {
                        case 'r': reverse = true; break;
                        case 'n': numeric = true; break;
                        case 'u': unique = true; break;
                        case 'f': foldCase = true; break;
                        case 'h': humanNumeric = true; break;
                        case 'V': versionSort = true; break;
                        case 'M': monthSort = true; break;
                        case 'c': checkOnly = true; break;
                        case 'b': blankIgnore = true; break;
                        case 'd': dictOrder = true; break;
                        case 's': /* stable — always stable in our sort */ break;
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

        // Collect items.
        var items = new List<object>();
        bool hadError = false;

        if (_pipeline.Count > 0)
        {
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        items.Add(subLine);
                    }
                }
                else
                {
                    items.Add(item);
                }
            }
        }

        foreach (var raw in operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadLines(filePath))
                    {
                        items.Add(BashRuntime.NewBashObject(line));
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
        bool gFold = foldCase;
        bool gHuman = humanNumeric;
        bool gMonth = monthSort;
        bool gBlank = blankIgnore;
        bool gDict = dictOrder;
        string? gDelim = delimiter;

        int Compare(object a, object b)
        {
            if (keySpecs.Count > 0)
            {
                foreach (var spec in keySpecs)
                {
                    string aKey = ExtractKeyText(a, spec, gDelim, gBlank);
                    string bKey = ExtractKeyText(b, spec, gDelim, gBlank);
                    if (gDict)
                    {
                        aKey = Regex.Replace(aKey, @"[^a-zA-Z0-9\s]", "");
                        bKey = Regex.Replace(bKey, @"[^a-zA-Z0-9\s]", "");
                    }
                    int cmp = 0;
                    if (gHuman)
                    {
                        double aH = ConvertFromHumanNumeric(aKey);
                        double bH = ConvertFromHumanNumeric(bKey);
                        cmp = aH < bH ? -1 : (aH > bH ? 1 : 0);
                    }
                    else if (spec.Numeric || gNumeric)
                    {
                        double aN = ParseNumericPrefix(aKey);
                        double bN = ParseNumericPrefix(bKey);
                        cmp = aN < bN ? -1 : (aN > bN ? 1 : 0);
                    }
                    else if (gMonth)
                    {
                        int aM = ConvertFromMonthName(aKey);
                        int bM = ConvertFromMonthName(bKey);
                        cmp = aM - bM;
                        if (cmp < 0) cmp = -1;
                        else if (cmp > 0) cmp = 1;
                    }
                    else if (gFold)
                    {
                        cmp = string.Compare(aKey, bKey, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        cmp = string.CompareOrdinal(aKey, bKey);
                    }
                    if (spec.Reverse || gReverse) cmp = -cmp;
                    if (cmp != 0) return Math.Sign(cmp);
                }
                return 0;
            }

            string aText = GetFullText(a, gBlank);
            string bText = GetFullText(b, gBlank);
            if (gDict)
            {
                aText = Regex.Replace(aText, @"[^a-zA-Z0-9\s]", "");
                bText = Regex.Replace(bText, @"[^a-zA-Z0-9\s]", "");
            }
            int cmp2 = 0;
            if (gHuman)
            {
                // Prefer the typed SizeBytes property when present (LsEntry
                // objects from ls -lh): the full ls line doesn't parse as a
                // bare human-readable number, so falling back to text-only
                // comparison would mis-sort the pipeline by leading char.
                double aH = ExtractSizeBytes(a) ?? ConvertFromHumanNumeric(aText);
                double bH = ExtractSizeBytes(b) ?? ConvertFromHumanNumeric(bText);
                cmp2 = aH < bH ? -1 : (aH > bH ? 1 : 0);
            }
            else if (gNumeric)
            {
                double aN = 0; double bN = 0;
                double.TryParse(aText, NumberStyles.Float, CultureInfo.InvariantCulture, out aN);
                double.TryParse(bText, NumberStyles.Float, CultureInfo.InvariantCulture, out bN);
                cmp2 = aN < bN ? -1 : (aN > bN ? 1 : 0);
            }
            else if (gMonth)
            {
                int aM = ConvertFromMonthName(aText);
                int bM = ConvertFromMonthName(bText);
                cmp2 = aM - bM;
                if (cmp2 < 0) cmp2 = -1;
                else if (cmp2 > 0) cmp2 = 1;
            }
            else if (gFold)
            {
                cmp2 = string.Compare(aText, bText, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                cmp2 = string.CompareOrdinal(aText, bText);
            }
            if (gReverse) cmp2 = -cmp2;
            return Math.Sign(cmp2);
        }

        // Check-only mode: walk pairs, exit 1 on first out-of-order pair.
        if (checkOnly)
        {
            for (int idx = 1; idx < items.Count; idx++)
            {
                if (Compare(items[idx - 1], items[idx]) > 0)
                {
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
            }
            FileSystemHelpers.SetLastExitCode(this, 0);
            return;
        }

        // Build index list for stable sort tracking (LINQ OrderBy is stable but
        // we want explicit control; List.Sort is not stable so we attach index).
        var indexed = new List<(int Index, object Item)>(items.Count);
        for (int idx = 0; idx < items.Count; idx++)
        {
            indexed.Add((idx, items[idx]));
        }

        if (versionSort)
        {
            indexed.Sort((a, b) =>
            {
                string aText = GetFullText(a.Item, gBlank);
                string bText = GetFullText(b.Item, gBlank);
                int c = CompareVersion(aText, bText);
                if (gReverse) c = -c;
                if (c != 0) return c;
                return a.Index - b.Index;
            });
        }
        else
        {
            indexed.Sort((a, b) =>
            {
                int c = Compare(a.Item, b.Item);
                if (c != 0) return c;
                return a.Index - b.Index;
            });
        }

        IEnumerable<(int Index, object Item)> sorted = indexed;

        if (unique)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var deduped = new List<(int Index, object Item)>();
            foreach (var entry in indexed)
            {
                string t = GetFullText(entry.Item, gBlank);
                string key = foldCase ? t.ToLowerInvariant() : t;
                if (seen.Add(key)) deduped.Add(entry);
            }
            sorted = deduped;
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
        if (blankIgnore) text = Regex.Replace(text, @"^\s+", "");
        return text;
    }

    private static string ExtractKeyText(object item, KeySpec spec, string? delimiter, bool gBlank)
    {
        string text = BashRuntime.GetBashText(item);
        text = text.TrimEnd('\n');
        string sep = delimiter != null ? Regex.Escape(delimiter) : @"\s+";
        var parts = Regex.Split(text, sep);
        int startIdx = spec.StartField - 1;
        if (startIdx < 0) startIdx = 0;
        if (startIdx >= parts.Length) return "";
        int endIdx = spec.EndField > 0 ? spec.EndField - 1 : parts.Length - 1;
        if (endIdx >= parts.Length) endIdx = parts.Length - 1;

        var fields = new List<string>();
        for (int fi = startIdx; fi <= endIdx; fi++)
        {
            string fieldText = parts[fi];
            if (fi == startIdx && spec.StartChar > 0)
            {
                int skip = spec.StartChar - 1;
                fieldText = skip < fieldText.Length ? fieldText.Substring(skip) : "";
            }
            if (fi == endIdx && spec.EndChar > 0)
            {
                if (spec.EndChar < fieldText.Length)
                    fieldText = fieldText.Substring(0, spec.EndChar);
            }
            fields.Add(fieldText);
        }
        string key = string.Join(" ", fields);
        if (spec.BlankIgnore || gBlank)
        {
            key = Regex.Replace(key, @"^\s+", "");
        }
        return key;
    }

    private static double ParseNumericPrefix(string s)
    {
        var m = Regex.Match(s, @"^[+-]?\d+(?:\.\d+)?");
        string numStr = m.Success ? m.Value : "0";
        double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
        return v;
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
        var m = Regex.Match(s, @"^(\d+)(?:\.(\d+))?([nrRbB]*)?$");
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
        var m = Regex.Match(trimmed, @"^([0-9]*\.?[0-9]+)\s*([KMGTP])$");
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

    private static int CompareVersion(string left, string right)
    {
        var leftParts = Regex.Split(left, @"[.\-]");
        var rightParts = Regex.Split(right, @"[.\-]");
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
