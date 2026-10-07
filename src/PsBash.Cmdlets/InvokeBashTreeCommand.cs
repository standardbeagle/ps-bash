using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTree</c> function
/// (REFACTOR-2 follow-on). Recursively prints a directory tree using
/// box-drawing prefix characters (<c>├── │   └──</c>), with an optional
/// summary line counting directories and files.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashTree</c>
/// function. This cmdlet reproduces its exact branches:
/// <list type="bullet">
/// <item><c>-L N</c> / <c>-LN</c> — maximum recursion depth.</item>
/// <item><c>-I PATTERN</c> — glob exclude pattern, <c>-notlike</c> match
/// on entry names.</item>
/// <item><c>-a</c> — include dotfiles. (Default: hide dotfiles.)</item>
/// <item><c>-d</c> — directories only.</item>
/// <item><c>--dirsfirst</c> — sort directories before files (then
/// alphabetical).</item>
/// <item>Default sort: alphabetical by name.</item>
/// <item>Summary line: <c>"{N} directories, {M} files"</c>, or
/// <c>"{N} directories"</c> under <c>-d</c>.</item>
/// </list>
///
/// Common-parameter collisions (declared as explicit parameters so the
/// bare-token form binds via exact-name match rather than the
/// common-parameter prefix match):
/// <list type="bullet">
/// <item><c>-d</c> prefix-collides with <c>-Debug</c>; declared as
/// <see cref="D"/> <see cref="SwitchParameter"/>.</item>
/// <item><c>-a</c> prefix-matches the cmdlet's own <see cref="Arguments"/>
/// parameter; declared as <see cref="A"/> <see cref="SwitchParameter"/>.</item>
/// <item><c>-I PATTERN</c> prefix-collides with <c>-InformationAction</c>
/// / <c>-InformationVariable</c>; declared as <see cref="I"/>
/// nullable string parameter.</item>
/// <item><c>-L</c> and <c>--dirsfirst</c> have no common-parameter prefix
/// collision and stay in <see cref="Arguments"/>.</item>
/// </list>
///
/// Output: one typed <c>PsBash.TreeEntry</c> PSObject per line including
/// the root and the summary (matching the psm1 oracle byte-for-byte). The
/// summary's <c>BashText</c> reflects the dir/file totals.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTree")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashTreeCommand : PSCmdlet
{
    /// <summary>The bash <c>-d</c> (directories only) switch.</summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>The bash <c>-a</c> (all, include dotfiles) switch.</summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    /// <summary>The bash <c>-I PATTERN</c> exclude-pattern value flag.</summary>
    [Parameter]
    public string? I { get; set; }

    /// <summary>Decoy for the unsupported <c>-C</c> (force color). Bare <c>-C</c>
    /// silently bound <c>-Confirm</c>; re-injected below so the classifier fires.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    /// <summary>Decoy for the unsupported <c>-p</c> (print permissions). Bare <c>-p</c>
    /// prefix-collides with <c>-ProgressAction</c> and crashed the binder.</summary>
    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private const string OptAll = "all", OptDirsOnly = "dirs-only", OptLevel = "level", OptIgnore = "ignore",
        OptFullPath = "full-path", OptDirsFirst = "dirsfirst", OptNoReport = "noreport", OptNoOp = "noop";

    /// <summary>tree(1) 2.1 options ps-bash refuses (exit 2). tree is NOT getopt_long: long options
    /// are exact names only, so there is no abbreviation.</summary>
    private static readonly string[] TreeValidButUnsupported =
    {
        "-l", "-x", "-P", "-R", "-q", "-N", "-Q", "-p", "-u", "-g", "-s", "-h", "-D", "-F", "-v", "-t", "-c",
        "-U", "-r", "-i", "-A", "-S", "-C", "-X", "-J", "-H", "-T", "-o",
        "--prune", "--sort", "--charset", "--fromfile", "--fromtabfile", "--gitignore", "--gitfile",
        "--ignore-case", "--matchdirs", "--metafirst", "--info", "--filelimit", "--si", "--du", "--timefmt",
        "--inodes", "--device", "--filesfirst", "--nolinks", "--hintro", "--houtro",
    };

    /// <summary>
    /// tree's option surface. Implemented: -a -d -f -L N -I PAT (repeatable, <c>|</c> alternatives)
    /// --dirsfirst --noreport; <c>-n</c> (no colour) is an accepted no-op since colour is never on.
    /// </summary>
    private static readonly OptSpecSet TreeSpec = new(
        new[]
        {
            new OptSpec(OptAll, 'a', null),
            new OptSpec(OptDirsOnly, 'd', null),
            new OptSpec(OptFullPath, 'f', null),
            new OptSpec(OptLevel, 'L', null, OptKind.Value),
            new OptSpec(OptIgnore, 'I', null, OptKind.Value),
            new OptSpec(OptNoOp, 'n', null),
            new OptSpec(OptDirsFirst, '\0', "dirsfirst"),
            new OptSpec(OptNoReport, '\0', "noreport"),
        },
        TreeValidButUnsupported,
        allowAbbrev: false,
        gnuInfoOptions: true);

    private int _dirCount;
    private int _fileCount;
    private int _rootsWritten;
    private string _resolvedRoot = string.Empty;
    private int _maxDepth = int.MaxValue;
    private List<string> _excludePatterns = new();
    private bool _showAll;
    private bool _dirsOnly;
    private bool _dirsFirst;
    private bool _noReport;
    private bool _fullPath;
    private string _normTarget = ".";

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, TreeSpec);

    internal sealed class TreeArgs
    {
        public ParsedArgs Parsed = null!;
        public bool ShowAll, DirsOnly, FullPath, DirsFirst, NoReport;
        public int MaxDepth = int.MaxValue;
        public List<string> Excludes = new();
        public List<string> Operands = new();
        public string? Error;
    }

    /// <summary>
    /// Scan + validate. Fixes over the old hand scan: an unknown option or a dangling <c>-L</c>/<c>-I</c>
    /// was a directory operand / silently ignored, <c>-L abc</c> / <c>-L 0</c> were ignored (tree: "Invalid
    /// level, must be greater than 0.", exit 1), <c>-I</c> could not be joined (<c>-I*.o</c>) or repeated
    /// or use <c>|</c> alternatives, <c>--</c> was not honoured, every operand past the first was dropped.
    /// </summary>
    internal static TreeArgs Plan(string[] args)
    {
        var p = new TreeArgs { Parsed = ScanArgs(args) };
        p.Operands = p.Parsed.Operands();
        if (p.Parsed.HasError) return p;
        if (p.Parsed.Has(OptSpecSet.HelpId) || p.Parsed.Has(OptSpecSet.VersionId)) return p;

        foreach (var tok in p.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptAll: p.ShowAll = true; break;
                case OptDirsOnly: p.DirsOnly = true; break;
                case OptFullPath: p.FullPath = true; break;
                case OptDirsFirst: p.DirsFirst = true; break;
                case OptNoReport: p.NoReport = true; break;
                case OptIgnore: p.Excludes.AddRange(tok.Value!.Split('|')); break;
                case OptLevel:
                    {
                        string v = tok.Value!;
                        bool digits = v.Length > 0;
                        foreach (char ch in v) if (ch < '0' || ch > '9') { digits = false; break; }
                        int level = digits ? (int.TryParse(v, out int n) ? n : int.MaxValue) : 0;
                        if (level < 1) { p.Error = "tree: Invalid level, must be greater than 0."; return p; }
                        p.MaxDepth = level;
                        break;
                    }
            }
        }
        return p;
    }

    protected override void EndProcessing()
    {
        // Decoy-bound flags (bare -d/-a/-C/-p/-I never reach Arguments) are re-injected; the
        // transpiler single-quotes every flag so only DIRECT calls bind them.
        var raw = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (D.IsPresent) pre.Add("-d");
        if (A.IsPresent) pre.Add("-a");
        if (C.IsPresent) pre.Add("-C");
        if (P.IsPresent) pre.Add("-p");
        if (I is not null) { pre.Add("-I"); pre.Add(I); }
        var args = pre.Count == 0 ? raw : pre.Concat(raw).ToArray();

        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "tree", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "tree", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        _showAll = plan.ShowAll;
        _dirsOnly = plan.DirsOnly;
        _excludePatterns = plan.Excludes;
        _dirsFirst = plan.DirsFirst;
        _noReport = plan.NoReport;
        _fullPath = plan.FullPath;
        _maxDepth = plan.MaxDepth;
        _dirCount = 0;
        _fileCount = 0;
        _rootsWritten = 0;

        var operands = plan.Operands;
        if (operands.Count == 0)
        {
            operands.Add(".");
        }

        foreach (var target in operands)
        {
            WriteRoot(target);
        }

        // Nothing walked (every operand missing): no report, only the errors (and exit 1).
        if (_rootsWritten > 0) WriteSummary();
    }

    private void WriteSummary()
    {
        // Summary line — suppressed by --noreport.
        if (_noReport) return;

        string dirLabel = _dirCount == 1 ? "directory" : "directories";
        string fileLabel = _fileCount == 1 ? "file" : "files";
        string summaryText = _dirsOnly
            ? $"{_dirCount} {dirLabel}"
            : $"{_dirCount} {dirLabel}, {_fileCount} {fileLabel}";

        var summaryObj = new PSObject();
        summaryObj.TypeNames.Insert(0, "PsBash.TreeEntry");
        summaryObj.Properties.Add(new PSNoteProperty("Name", ""));
        summaryObj.Properties.Add(new PSNoteProperty("Path", ""));
        summaryObj.Properties.Add(new PSNoteProperty("Depth", 0));
        summaryObj.Properties.Add(new PSNoteProperty("IsDirectory", false));
        summaryObj.Properties.Add(new PSNoteProperty("TreePrefix", ""));
        summaryObj.Properties.Add(new PSNoteProperty("BashText", summaryText));
        WriteObject(summaryObj);
    }

    private void WriteRoot(string target)
    {
        _normTarget = target.Replace('\\', '/').TrimEnd('/');
        if (_normTarget.Length == 0) _normTarget = "/";

        string resolved;
        string rootName;
        try
        {
            resolved = FileSystemHelpers.ProviderPath(this, target);
            if (Directory.Exists(resolved))
            {
                var dirInfo = new DirectoryInfo(resolved);
                rootName = dirInfo.Name;
            }
            else if (File.Exists(resolved))
            {
                var fi = new FileInfo(resolved);
                rootName = fi.Name;
            }
            else
            {
                string normalized = target.Replace('\\', '/');
                FileSystemHelpers.WriteBashError(
                    this,
                    $"tree: cannot access '{normalized}': No such file or directory");
                return;
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            string normalized = target.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(
                this,
                $"tree: cannot access '{normalized}': {ex.Message}");
            return;
        }

        _resolvedRoot = resolved;

        // Root entry — single object with no tree-prefix.
        var rootObj = new PSObject();
        rootObj.TypeNames.Insert(0, "PsBash.TreeEntry");
        rootObj.Properties.Add(new PSNoteProperty("Name", rootName));
        rootObj.Properties.Add(new PSNoteProperty("Path", target.Replace('\\', '/')));
        rootObj.Properties.Add(new PSNoteProperty("Depth", 0));
        rootObj.Properties.Add(new PSNoteProperty("IsDirectory", true));
        rootObj.Properties.Add(new PSNoteProperty("TreePrefix", ""));
        rootObj.Properties.Add(new PSNoteProperty("BashText", _fullPath ? _normTarget : rootName));
        WriteObject(rootObj);
        _rootsWritten++;

        if (Directory.Exists(resolved))
        {
            WriteTreeLevel(resolved, currentDepth: 1, prefix: "");
        }
    }

    private bool IsExcluded(string name)
    {
        foreach (var pat in _excludePatterns)
        {
            if (WildcardMatch(name, pat)) return true;
        }
        return false;
    }

    private void WriteTreeLevel(string dirPath, int currentDepth, string prefix)
    {
        if (currentDepth > _maxDepth)
        {
            return;
        }

        FileSystemInfo[] items;
        try
        {
            var dirInfo = new DirectoryInfo(dirPath);
            items = dirInfo.GetFileSystemInfos();
        }
        catch
        {
            return;
        }

        // Filter dotfiles unless -a.
        var filtered = new List<FileSystemInfo>(items.Length);
        foreach (var it in items)
        {
            if (!_showAll && it.Name.StartsWith(".", StringComparison.Ordinal)) continue;
            if (IsExcluded(it.Name)) continue;
            if (_dirsOnly && it is not DirectoryInfo) continue;
            filtered.Add(it);
        }

        // Sort: dirsfirst (dirs first, then files), then alphabetical by name.
        // Use OrdinalIgnoreCase to track PowerShell's default Sort-Object Name
        // behaviour closely enough for the failure-surface matrix here.
        if (_dirsFirst)
        {
            filtered.Sort((a, b) =>
            {
                int aDir = a is DirectoryInfo ? 0 : 1;
                int bDir = b is DirectoryInfo ? 0 : 1;
                if (aDir != bDir) return aDir - bDir;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        else
        {
            filtered.Sort((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        for (int idx = 0; idx < filtered.Count; idx++)
        {
            var item = filtered[idx];
            bool isLast = idx == filtered.Count - 1;

            // U+2514 LIGHT UP AND RIGHT, U+2500 LIGHT HORIZONTAL
            // U+251C LIGHT VERTICAL AND RIGHT
            // U+2502 LIGHT VERTICAL
            string connector = isLast ? "└── " : "├── ";
            string childPrefix = isLast ? (prefix + "    ") : (prefix + "│   ");

            bool isDir = item is DirectoryInfo;
            if (isDir) _dirCount++;
            else _fileCount++;

            string fullName = item.FullName;
            string relativePath;
            if (fullName.Length >= _resolvedRoot.Length &&
                fullName.StartsWith(_resolvedRoot, StringComparison.Ordinal))
            {
                relativePath = fullName.Substring(_resolvedRoot.Length).Replace('\\', '/');
            }
            else
            {
                relativePath = item.Name;
            }
            if (relativePath.StartsWith("/", StringComparison.Ordinal))
            {
                relativePath = relativePath.Substring(1);
            }

            string treePrefix = prefix + connector;
            // -f: show the target-relative path instead of the bare name.
            string display = _fullPath ? $"{_normTarget}/{relativePath}" : item.Name;
            string bashText = prefix + connector + display;

            var entryObj = new PSObject();
            entryObj.TypeNames.Insert(0, "PsBash.TreeEntry");
            entryObj.Properties.Add(new PSNoteProperty("Name", item.Name));
            entryObj.Properties.Add(new PSNoteProperty("Path", relativePath));
            entryObj.Properties.Add(new PSNoteProperty("Depth", currentDepth));
            entryObj.Properties.Add(new PSNoteProperty("IsDirectory", isDir));
            entryObj.Properties.Add(new PSNoteProperty("TreePrefix", treePrefix));
            entryObj.Properties.Add(new PSNoteProperty("BashText", bashText));
            WriteObject(entryObj);

            if (isDir)
            {
                WriteTreeLevel(item.FullName, currentDepth + 1, childPrefix);
            }
        }
    }

    /// <summary>
    /// Approximates PowerShell's <c>-like</c> wildcard operator against a
    /// glob pattern (supports <c>*</c> and <c>?</c>). Used for the
    /// <c>-I PATTERN</c> exclusion. Directive 12: the pattern is treated as a
    /// glob and never re-parsed as PowerShell — a literal <c>$(throw)</c>
    /// arrives here as raw text and is only ever fed to a wildcard
    /// matcher, so no injection is possible.
    /// </summary>
    private static bool WildcardMatch(string name, string pattern)
    {
        var wp = new WildcardPattern(pattern, WildcardOptions.IgnoreCase);
        return wp.IsMatch(name);
    }
}
