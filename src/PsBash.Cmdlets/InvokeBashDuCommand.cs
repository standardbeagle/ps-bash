using System.Management.Automation;

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
    /// <summary>The bash <c>-d N</c> (max depth) value flag.</summary>
    [Parameter]
    public int? D { get; set; }

    /// <summary>The bash <c>-a</c> (include files) switch.</summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    /// <summary>The bash <c>-c</c> (grand total) switch.</summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Decoy for the unsupported <c>-P</c> (no-dereference). Bare <c>-P</c> prefix-
    /// collides with <c>-ProgressAction</c> and crashed the binder. du silently
    /// swallows unknown short flags (oracle behavior), so this just prevents the
    /// crash — <c>du -P</c> is ignored, exactly like <c>-B</c>/<c>-l</c>.
    /// </summary>
    [Parameter] public SwitchParameter P { get; set; }

    // Valid GNU du flags not implemented by ps-bash. Implemented flags
    // (-h/-s/-a/-c/-d/--max-depth) are NOT in this set.
    // Note: short flags like -B/-l/-P are swallowed by the per-char bundle
    // decoder (oracle behavior) and never reach the operand list, so only
    // long forms are practically catchable by TryWriteOperandOptionError.
    private static readonly HashSet<string> DuValidButUnsupported =
        new(StringComparer.Ordinal)
        {
            "--apparent-size",
            "--block-size",
            "-B",
            "-l",
            "--inodes",
            "-P",
            "--no-dereference",
            "--time",
        };

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "du", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "du"))
            {
                WriteObject(line);
            }
            return;
        }

        bool humanReadable = false;
        bool summarize = false;
        bool allFiles = A.IsPresent;
        bool showTotal = C.IsPresent;
        int maxDepth = D ?? int.MaxValue;

        var operands = new List<string>();
        var excludePatterns = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // --exclude=GLOB / --exclude GLOB — prune matching files/dirs.
            if (arg.StartsWith("--exclude=", StringComparison.Ordinal))
            {
                excludePatterns.Add(arg.Substring("--exclude=".Length));
                continue;
            }
            if (arg == "--exclude" && (i + 1) < args.Length)
            {
                excludePatterns.Add(args[i + 1]);
                i++;
                continue;
            }

            // -d<digits> joined form (oracle: -cmatch '^-d(\d+)$')
            if (arg.Length > 2 && arg.StartsWith("-d", StringComparison.Ordinal)
                && AllDigits(arg, 2))
            {
                if (int.TryParse(arg.Substring(2), out var parsed))
                {
                    maxDepth = parsed;
                }
                continue;
            }

            // -d N separated form
            if (arg == "-d" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsed))
                {
                    maxDepth = parsed;
                }
                i++;
                continue;
            }

            // --max-depth=N / --max-depth N — GNU long form of -d.
            if (arg.StartsWith("--max-depth=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--max-depth=".Length), out var parsed))
                {
                    maxDepth = parsed;
                }
                continue;
            }
            if (arg == "--max-depth" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsed))
                {
                    maxDepth = parsed;
                }
                i++;
                continue;
            }

            // Per-char short-flag bundle (oracle's per-char dispatch)
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1
                && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                foreach (var ch in arg.Substring(1))
                {
                    switch (ch)
                    {
                        case 'h': humanReadable = true; break;
                        case 's': summarize = true; break;
                        case 'a': allFiles = true; break;
                        case 'c': showTotal = true; break;
                        // Oracle: default branch ignores unknown letters
                    }
                }
                continue;
            }

            operands.Add(arg);
        }

        if (FileSystemHelpers.TryWriteOperandOptionError(
                this, "du", operands, DuValidButUnsupported)) return;

        var excludeWild = excludePatterns
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
                long sizeKb = CeilingDiv(sizeBytes, 1024);
                string sizeHuman = FormatBashSize(sizeBytes);
                string displaySize = humanReadable ? sizeHuman : sizeKb.ToString();
                string displayPath = target.Replace('\\', '/');

                var fobj = new PSObject();
                fobj.TypeNames.Insert(0, "PsBash.DuEntry");
                fobj.Properties.Add(new PSNoteProperty("Size", sizeKb));
                fobj.Properties.Add(new PSNoteProperty("SizeBytes", sizeBytes));
                fobj.Properties.Add(new PSNoteProperty("SizeHuman", sizeHuman));
                fobj.Properties.Add(new PSNoteProperty("Path", displayPath));
                fobj.Properties.Add(new PSNoteProperty("Depth", 0));
                fobj.Properties.Add(new PSNoteProperty("IsTotal", false));
                fobj.Properties.Add(new PSNoteProperty("BashText", $"{displaySize}\t{displayPath}"));
                WriteObject(fobj);
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
                long sizeKb = CeilingDiv(sizeBytes, 1024);
                if (sizeKb == 0 && sizeBytes > 0) sizeKb = 1;
                string sizeHuman = FormatBashSize(sizeBytes);
                string displaySize = humanReadable ? sizeHuman : sizeKb.ToString();

                string displayPath = BuildDisplayPath(target, resolvedRoot, d.FullName);

                var obj = new PSObject();
                obj.TypeNames.Insert(0, "PsBash.DuEntry");
                obj.Properties.Add(new PSNoteProperty("Size", sizeKb));
                obj.Properties.Add(new PSNoteProperty("SizeBytes", sizeBytes));
                obj.Properties.Add(new PSNoteProperty("SizeHuman", sizeHuman));
                obj.Properties.Add(new PSNoteProperty("Path", displayPath));
                obj.Properties.Add(new PSNoteProperty("Depth", itemDepth));
                obj.Properties.Add(new PSNoteProperty("IsTotal", false));
                obj.Properties.Add(new PSNoteProperty("BashText", $"{displaySize}\t{displayPath}"));
                entries.Add(obj);
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
                    long sizeKb = CeilingDiv(sizeBytes, 1024);
                    if (sizeKb == 0 && sizeBytes > 0) sizeKb = 1;
                    string sizeHuman = FormatBashSize(sizeBytes);
                    string displaySize = humanReadable ? sizeHuman : sizeKb.ToString();
                    string displayPath = BuildDisplayPath(target, resolvedRoot, f.FullName);

                    var obj = new PSObject();
                    obj.TypeNames.Insert(0, "PsBash.DuEntry");
                    obj.Properties.Add(new PSNoteProperty("Size", sizeKb));
                    obj.Properties.Add(new PSNoteProperty("SizeBytes", sizeBytes));
                    obj.Properties.Add(new PSNoteProperty("SizeHuman", sizeHuman));
                    obj.Properties.Add(new PSNoteProperty("Path", displayPath));
                    obj.Properties.Add(new PSNoteProperty("Depth", fileDepth));
                    obj.Properties.Add(new PSNoteProperty("IsTotal", false));
                    obj.Properties.Add(new PSNoteProperty("BashText", $"{displaySize}\t{displayPath}"));
                    entries.Add(obj);
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
            long sizeKb = CeilingDiv(grandTotal, 1024);
            if (sizeKb == 0 && grandTotal > 0) sizeKb = 1;
            string sizeHuman = FormatBashSize(grandTotal);
            string displaySize = humanReadable ? sizeHuman : sizeKb.ToString();

            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.DuEntry");
            obj.Properties.Add(new PSNoteProperty("Size", sizeKb));
            obj.Properties.Add(new PSNoteProperty("SizeBytes", grandTotal));
            obj.Properties.Add(new PSNoteProperty("SizeHuman", sizeHuman));
            obj.Properties.Add(new PSNoteProperty("Path", "total"));
            obj.Properties.Add(new PSNoteProperty("Depth", 0));
            obj.Properties.Add(new PSNoteProperty("IsTotal", true));
            obj.Properties.Add(new PSNoteProperty("BashText", $"{displaySize}\ttotal"));
            WriteObject(obj);
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
