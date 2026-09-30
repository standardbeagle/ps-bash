using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashDu</c> function
/// (REFACTOR-2 Phase 4 follow-on). Estimates disk usage of files and
/// directories, matching GNU coreutils <c>du</c>.
///
/// Oracle: the original psm1 <c>Invoke-BashDu</c>. Reproduces its branches
/// byte-for-byte — recursive directory enumeration via <see cref="System.IO.DirectoryInfo"/>,
/// per-directory size = sum of files directly inside it, bottom-up
/// accumulation so a directory's reported size includes all descendants,
/// 1024-byte block rounding via <c>Ceiling(bytes / 1024)</c>, human-readable
/// sizes via the oracle's <c>Format-BashSize</c> ladder
/// (<c>K</c>/<c>M</c>/<c>G</c>/<c>T</c>/<c>P</c>), depth-limited emission,
/// <c>-s</c> summary-only, <c>-a</c> include files, <c>-c</c> grand total.
///
/// Output: typed <c>PsBash.DuEntry</c> PSObjects with
/// <c>Size</c>/<c>SizeBytes</c>/<c>SizeHuman</c>/<c>Path</c>/<c>Depth</c>/<c>IsTotal</c>/<c>BashText</c>
/// — exact oracle shape. <c>BashText</c> is <c>"{size}\t{path}"</c>.
///
/// Common-parameter collisions (declared as explicit params, per the playbook
/// table — exact param-name match beats common-parameter prefix-match under
/// the PSCmdlet binder):
/// <list type="bullet">
/// <item><c>-d N</c> — prefix-collides with <c>-Debug</c>. Declared as
/// nullable int <see cref="D"/>. The joined form <c>-dN</c> stays in
/// <see cref="Arguments"/> and is recovered by the manual scan.</item>
/// <item><c>-a</c> — bare token prefix-matches the cmdlet's own
/// <see cref="Arguments"/> parameter (same hazard as <c>ls</c> / <c>split</c>
/// / <c>uname</c>). Declared as <see cref="SwitchParameter"/> <see cref="A"/>.</item>
/// <item><c>-c</c> — prefix-collides with <c>-Confirm</c>. Declared as
/// <see cref="SwitchParameter"/> <see cref="C"/>.</item>
/// <item><c>-s</c> / <c>-h</c> — no PS common-parameter prefix collision; both
/// stay in <see cref="Arguments"/> and are decoded by the manual scan.</item>
/// </list>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDu")]
[OutputType("PsBash.DuEntry")]
public sealed class InvokeBashDuCommand : PSCmdlet
{
    /// <summary>The bash <c>-d N</c> (max depth) value flag (decoy for direct calls; re-injected as <c>-d N</c>).</summary>
    [Parameter]
    public int? D { get; set; }

    /// <summary>The bash <c>-a</c> (include files) switch (decoy: bare <c>-a</c> prefix-matches <see cref="Arguments"/>).</summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    /// <summary>The bash <c>-c</c> (grand total) switch (decoy: prefix-collides with <c>-Confirm</c>).</summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Decoy for <c>-P</c> (no-dereference, valid-but-unsupported): bare <c>-P</c> prefix-collides with
    /// <c>-ProgressAction</c> and crashed the binder. Re-injected as <c>-P</c> so the classifier answers.
    /// </summary>
    [Parameter] public SwitchParameter P { get; set; }

    private const string OptAll = "all", OptTotal = "total", OptHuman = "human", OptSummarize = "summarize",
        OptMaxDepth = "max-depth", OptExclude = "exclude", OptKilo = "kilo", OptMega = "mega",
        OptBytes = "bytes", OptNoOp = "noop";

    /// <summary>GNU du 9.4 options ps-bash refuses (exit 2).</summary>
    private static readonly string[] DuValidButUnsupported =
    {
        "-B", "--block-size", "-D", "-H", "--dereference-args", "-L", "--dereference", "-P", "--no-dereference",
        "-S", "--separate-dirs", "-t", "--threshold", "-X", "--exclude-from", "-0", "--null",
        "--files0-from", "--inodes", "--si", "--time", "--time-style",
    };

    /// <summary>
    /// du's option surface (GNU coreutils 9.4; long names in du.c <c>long_options[]</c> table order so an
    /// ambiguous abbreviation lists candidates like GNU: <c>--s</c> = '--si' '--separate-dirs' '--summarize').
    /// <c>-k</c> (1K blocks, the default), <c>-x</c>, <c>-l</c> and <c>--apparent-size</c> are accepted
    /// no-ops: ps-bash always reports apparent size, counts every hard link, and does not cross mounts.
    /// </summary>
    private static readonly OptSpecSet DuSpec = new(
        new[]
        {
            new OptSpec(OptAll, 'a', "all"),
            new OptSpec(OptNoOp, '\0', "apparent-size"),
            new OptSpec(OptBytes, 'b', "bytes"),
            new OptSpec(OptNoOp, 'l', "count-links"),
            new OptSpec(OptExclude, '\0', "exclude", OptKind.Value),
            new OptSpec(OptHuman, 'h', "human-readable"),
            new OptSpec(OptMaxDepth, 'd', "max-depth", OptKind.Value),
            new OptSpec(OptNoOp, 'x', "one-file-system"),
            new OptSpec(OptSummarize, 's', "summarize"),
            new OptSpec(OptTotal, 'c', "total"),
            new OptSpec(OptKilo, 'k', null),
            new OptSpec(OptMega, 'm', null),
        },
        DuValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: new[]
        {
            "all", "apparent-size", "block-size", "bytes", "count-links", "dereference", "dereference-args",
            "exclude", "exclude-from", "files0-from", "human-readable", "inodes", "si", "max-depth", "null",
            "no-dereference", "one-file-system", "separate-dirs", "summarize", "total", "threshold", "time",
            "time-style",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, DuSpec);

    internal enum DuSizeMode { Kilo, Mega, Bytes, Human }

    internal sealed class DuArgs
    {
        public ParsedArgs Parsed = null!;
        public bool All, Total, Summarize;
        public int MaxDepth = int.MaxValue;
        public DuSizeMode Mode = DuSizeMode.Kilo;
        public List<string> Excludes = new();
        public List<string> Operands = new();
        public string? Error;
    }

    /// <summary>
    /// Scan + validate in GNU's order. Fixes over the old hand scan: unknown options, a dangling
    /// <c>-d</c>, and a non-numeric depth were swallowed (the per-character bundle decoder ignored
    /// every unknown letter: <c>du -z d</c> ran), <c>--</c> is honoured, options may follow operands,
    /// abbreviations (<c>--max=1</c>), <c>-k -m -b</c> size units (the last of -h/-k/-m/-b wins),
    /// <c>-a</c> with <c>-s</c> and <c>-s</c> with a non-zero <c>-d</c> are GNU's usage errors.
    /// </summary>
    internal static DuArgs Plan(string[] args)
    {
        var p = new DuArgs { Parsed = ScanArgs(args) };
        p.Operands = p.Parsed.Operands();
        if (p.Parsed.HasError) return p;
        if (p.Parsed.Has(OptSpecSet.HelpId) || p.Parsed.Has(OptSpecSet.VersionId)) return p;

        int? depthSpecified = null;
        foreach (var tok in p.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptAll: p.All = true; break;
                case OptTotal: p.Total = true; break;
                case OptSummarize: p.Summarize = true; break;
                case OptHuman: p.Mode = DuSizeMode.Human; break;
                case OptKilo: p.Mode = DuSizeMode.Kilo; break;
                case OptMega: p.Mode = DuSizeMode.Mega; break;
                case OptBytes: p.Mode = DuSizeMode.Bytes; break;
                case OptExclude: p.Excludes.Add(tok.Value!); break;
                case OptMaxDepth:
                    {
                        if (!TryParseDepth(tok.Value!, out int depth))
                        { p.Error = $"du: invalid maximum depth '{tok.Value}'"; return p; }
                        p.MaxDepth = depth;
                        depthSpecified = depth;
                        break;
                    }
            }
        }

        if (p.All && p.Summarize) { p.Error = "du: cannot both summarize and show all entries"; return p; }
        if (p.Summarize && depthSpecified is int d && d != 0)
        { p.Error = $"du: warning: summarizing conflicts with --max-depth={d}"; return p; }
        if (p.Summarize) p.MaxDepth = 0;
        return p;
    }

    // Decimal digits (an optional leading '-' is GNU's "unlimited": strtoul wraps it to ULONG_MAX); overflow clamps.
    private static bool TryParseDepth(string s, out int depth)
    {
        depth = int.MaxValue;
        if (s.Length == 0) return false;
        bool neg = s[0] == '-';
        int i = neg || s[0] == '+' ? 1 : 0;
        if (i >= s.Length) return false;
        for (int k = i; k < s.Length; k++)
            if (s[k] < '0' || s[k] > '9') return false;
        if (neg) return true; // unlimited
        depth = int.TryParse(s.AsSpan(i), out int v) ? v : int.MaxValue;
        return true;
    }

    private string[] ArgsWithDecoys()
    {
        var raw = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (A.IsPresent) pre.Add("-a");
        if (C.IsPresent) pre.Add("-c");
        if (P.IsPresent) pre.Add("-P");
        if (D is int d) { pre.Add("-d"); pre.Add(d.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return pre.Count == 0 ? raw : pre.Concat(raw).ToArray();
    }

    /// <summary>Size number for the mode: KiB (default), MiB, or bytes; all round up like GNU.</summary>
    private static long SizeUnits(long bytes, DuSizeMode mode) => mode switch
    {
        DuSizeMode.Mega => CeilingDiv(bytes, 1048576),
        DuSizeMode.Bytes => bytes,
        _ => CeilingDiv(bytes, 1024),
    };

    private PSObject NewEntry(long sizeBytes, string path, int depth, bool isTotal, DuSizeMode mode)
    {
        long units = SizeUnits(sizeBytes, mode);
        string sizeHuman = FormatBashSize(sizeBytes);
        string displaySize = mode == DuSizeMode.Human ? sizeHuman : units.ToString();
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.DuEntry");
        obj.Properties.Add(new PSNoteProperty("Size", units));
        obj.Properties.Add(new PSNoteProperty("SizeBytes", sizeBytes));
        obj.Properties.Add(new PSNoteProperty("SizeHuman", sizeHuman));
        obj.Properties.Add(new PSNoteProperty("Path", path));
        obj.Properties.Add(new PSNoteProperty("Depth", depth));
        obj.Properties.Add(new PSNoteProperty("IsTotal", isTotal));
        obj.Properties.Add(new PSNoteProperty("BashText", $"{displaySize}\t{path}"));
        return obj;
    }

    protected override void EndProcessing()
    {
        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "du", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "du", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        bool allFiles = plan.All;
        bool showTotal = plan.Total;
        bool summarize = plan.Summarize;
        int maxDepth = plan.MaxDepth;
        var mode = plan.Mode;
        var operands = plan.Operands;

        var excludeWild = plan.Excludes
            .Select(p => WildcardPattern.Get(p, WildcardOptions.None))
            .ToList();
        if (operands.Count == 0)
        {
            operands.Add(".");
        }

        long grandTotal = 0;

        foreach (var target in operands)
        {
            // Oracle: Get-BashItem -Path $target -Command 'du'
            // Resolve to FileSystemInfo; null on miss (writes bash error).
            FileSystemInfo? rootItem;
            try
            {
                string resolved = SessionState.Path
                    .GetUnresolvedProviderPathFromPSPath(target);
                if (Directory.Exists(resolved))
                {
                    rootItem = new DirectoryInfo(resolved);
                }
                else if (File.Exists(resolved))
                {
                    rootItem = new FileInfo(resolved);
                }
                else
                {
                    string norm = target.Replace('\\', '/');
                    FileSystemHelpers.WriteBashError(
                        this,
                        $"du: cannot access '{norm}': No such file or directory");
                    continue;
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                string norm = target.Replace('\\', '/');
                FileSystemHelpers.WriteBashError(
                    this, $"du: cannot access '{norm}': {ex.Message}");
                continue;
            }

            string resolvedRoot = rootItem.FullName;

            if (rootItem is FileInfo fi)
            {
                long sizeBytes = fi.Length;
                grandTotal += sizeBytes;
                WriteObject(NewEntry(sizeBytes, target.Replace('\\', '/'), 0, false, mode));

                continue;
            }

            var rootDir = (DirectoryInfo)rootItem;
            // Oracle: rootDepth = count of segments of root path (after trimming trailing sep).
            char[] seps = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            int rootDepth = resolvedRoot.TrimEnd(seps)
                .Split(new[] { '\\', '/' }).Length;

            // Collect root + all subdirectories (-Force -Recurse equivalent)
            var allDirs = new List<DirectoryInfo> { rootDir };
            try
            {
                foreach (var sub in rootDir.EnumerateDirectories(
                             "*", SearchOption.AllDirectories))
                {
                    // --exclude prunes a matching directory (and its subtree:
                    // any descendant carries the excluded segment in its rel path).
                    if (IsSegmentExcluded(sub.FullName, resolvedRoot, excludeWild)) continue;
                    allDirs.Add(sub);
                }
            }
            catch { /* matches -ErrorAction SilentlyContinue */ }

            // Per-directory file size sum (files directly inside)
            var dirSizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in allDirs)
            {
                long total = 0;
                try
                {
                    foreach (var f in d.EnumerateFiles())
                    {
                        if (IsSegmentExcluded(f.FullName, resolvedRoot, excludeWild)) continue;
                        total += f.Length;
                    }
                }
                catch { }
                dirSizes[d.FullName] = total;
            }

            // Bottom-up accumulation: deepest-first sort by full-name length
            var accumSizes = new Dictionary<string, long>(StringComparer.Ordinal);
            var sortedDirs = allDirs
                .OrderByDescending(d => d.FullName.Length)
                .ToList();
            foreach (var d in sortedDirs)
            {
                long total = dirSizes[d.FullName];
                try
                {
                    foreach (var sd in d.EnumerateDirectories())
                    {
                        if (accumSizes.TryGetValue(sd.FullName, out var sub))
                        {
                            total += sub;
                        }
                    }
                }
                catch { }
                accumSizes[d.FullName] = total;
            }

            // Build directory entries
            var entries = new List<PSObject>();
            foreach (var d in allDirs)
            {
                int itemDepth = d.FullName.Split(new[] { '\\', '/' }).Length - rootDepth;
                if (itemDepth > maxDepth) continue;
                if (summarize && !string.Equals(d.FullName, resolvedRoot, StringComparison.Ordinal)) continue;

                long sizeBytes = accumSizes[d.FullName];
                string displayPath = BuildDisplayPath(target, resolvedRoot, d.FullName);
                entries.Add(NewEntry(sizeBytes, displayPath, itemDepth, false, mode));
            }

            // Individual file entries with -a. Enumerate lazily rather than
            // materializing every file in the tree into a List first — the
            // per-file entries already accumulate into `entries` (which the
            // trailing Sort-Object needs), so the intermediate FileInfo list
            // was a redundant full copy of the whole subtree.
            if (allFiles)
            {
                // Manual enumeration so an IO error mid-walk stops silently
                // (the oracle's AddRange-in-try swallowed it) while the per-file
                // body below is NOT wrapped in that catch.
                IEnumerator<FileInfo>? fileEnum = null;
                try { fileEnum = rootDir.EnumerateFiles("*", SearchOption.AllDirectories).GetEnumerator(); }
                catch { fileEnum = null; }

                while (fileEnum != null)
                {
                    FileInfo f;
                    try
                    {
                        if (!fileEnum.MoveNext()) break;
                        f = fileEnum.Current;
                    }
                    catch
                    {
                        break;
                    }

                    if (IsSegmentExcluded(f.FullName, resolvedRoot, excludeWild)) continue;
                    int fileDepth = f.FullName.Split(new[] { '\\', '/' }).Length - rootDepth;
                    if (fileDepth > maxDepth) continue;
                    if (summarize) continue;

                    long sizeBytes = f.Length;
                    string displayPath = BuildDisplayPath(target, resolvedRoot, f.FullName);
                    entries.Add(NewEntry(sizeBytes, displayPath, fileDepth, false, mode));
                }
                fileEnum?.Dispose();
            }

            // Sort by Path (oracle: Sort-Object { $_.Path })
            foreach (var e in entries.OrderBy(o => (string)o.Properties["Path"].Value, StringComparer.Ordinal))
            {
                WriteObject(e);
            }

            if (accumSizes.TryGetValue(resolvedRoot, out var rootBytes))
            {
                grandTotal += rootBytes;
            }
        }

        if (showTotal)
        {
            WriteObject(NewEntry(grandTotal, "total", 0, true, mode));
        }
    }

    /// <summary>
    /// True when any path segment of <paramref name="fullName"/> below
    /// <paramref name="root"/> matches a <c>--exclude</c> glob. Checking every
    /// segment (not just the basename) means an excluded directory prunes its
    /// whole subtree, matching GNU <c>du --exclude</c>.
    /// </summary>
    private static bool IsSegmentExcluded(string fullName, string root, List<WildcardPattern> pats)
    {
        if (pats.Count == 0) return false;
        // Runs once per directory and once per file in the walk, so avoid the
        // per-call string[] that String.Split allocates: scan segment boundaries
        // manually below root and materialize a segment only to test it (the
        // WildcardPattern API takes a string), short-circuiting on first match.
        int i = (fullName.Length > root.Length
                 && fullName.StartsWith(root, StringComparison.Ordinal))
            ? root.Length : 0;
        int n = fullName.Length;
        while (i < n)
        {
            while (i < n && (fullName[i] == '\\' || fullName[i] == '/')) i++;
            int segStart = i;
            while (i < n && fullName[i] != '\\' && fullName[i] != '/') i++;
            if (i > segStart)
            {
                string seg = fullName.Substring(segStart, i - segStart);
                foreach (var w in pats)
                {
                    if (w.IsMatch(seg)) return true;
                }
            }
        }
        return false;
    }

    private static bool AllDigits(string s, int start)
    {
        if (start >= s.Length) return false;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
        }
        return true;
    }

    private static long CeilingDiv(long bytes, long divisor)
    {
        if (bytes <= 0) return 0;
        return (bytes + divisor - 1) / divisor;
    }

    /// <summary>
    /// Builds the display path for an entry: <c>{target-as-bashpath}/{relative-from-root-as-bashpath}</c>.
    /// When entry == root, just emits the normalized target. Matches the
    /// oracle's <c>$relativePath = $dir.FullName.Substring($resolvedRoot.Length) -replace '\\','/'</c>
    /// + leading-slash strip + join.
    /// </summary>
    private static string BuildDisplayPath(string target, string resolvedRoot, string entryFullName)
    {
        string normalizedTarget = target.Replace('\\', '/');
        if (string.Equals(entryFullName, resolvedRoot, StringComparison.Ordinal))
        {
            return normalizedTarget;
        }

        string rel;
        if (entryFullName.Length >= resolvedRoot.Length
            && entryFullName.StartsWith(resolvedRoot, StringComparison.Ordinal))
        {
            rel = entryFullName.Substring(resolvedRoot.Length);
        }
        else
        {
            rel = entryFullName;
        }
        rel = rel.Replace('\\', '/');
        if (rel.StartsWith("/", StringComparison.Ordinal))
        {
            rel = rel.Substring(1);
        }
        return rel.Length == 0 ? normalizedTarget : $"{normalizedTarget}/{rel}";
    }

    /// <summary>
    /// Reproduces the psm1 <c>Format-BashSize</c> ladder byte-for-byte: under
    /// 1024 → bare byte count; otherwise scale by 1024 through
    /// <c>K M G T P</c>, returning <c>"{N}{unit}"</c> when scaled >= 10 (using
    /// <see cref="Math.Ceiling(double)"/>) or <c>"{N.N}{unit}"</c> otherwise.
    /// </summary>
    internal static string FormatBashSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString();
        }

        var units = new[] { 'K', 'M', 'G', 'T', 'P' };
        double value = bytes;
        int unitIdx = -1;
        while (value >= 1024 && unitIdx < units.Length - 1)
        {
            value /= 1024;
            unitIdx++;
        }

        if (value >= 10)
        {
            double rounded = Math.Ceiling(value);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}{1}", rounded, units[unitIdx]);
        }
        double r1 = Math.Ceiling(value * 10) / 10.0;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:F1}{1}", r1, units[unitIdx]);
    }
}
