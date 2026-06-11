using System.Management.Automation;

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

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private int _dirCount;
    private int _fileCount;
    private string _resolvedRoot = string.Empty;
    private int _maxDepth = int.MaxValue;
    private string? _excludePattern;
    private bool _showAll;
    private bool _dirsOnly;
    private bool _dirsFirst;
    private bool _noReport;
    private bool _fullPath;
    private string _normTarget = ".";

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "tree", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tree"))
            {
                WriteObject(line);
            }
            return;
        }

        _showAll = A.IsPresent;
        _dirsOnly = D.IsPresent;
        _excludePattern = I;
        _dirsFirst = false;
        _maxDepth = int.MaxValue;
        _dirCount = 0;
        _fileCount = 0;

        var operands = new List<string>();

        // Manual scan, matching the oracle's flag parsing order: -L (joined or
        // separated), -I (separated), --dirsfirst, then short-bundle decode of
        // -a / -d, then operand.
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // -LN joined form
            if (arg.Length > 2 && arg.StartsWith("-L", StringComparison.Ordinal))
            {
                var rest = arg.Substring(2);
                if (int.TryParse(rest, out var depth))
                {
                    _maxDepth = depth;
                    continue;
                }
            }
            if (arg == "-L" && (i + 1) < args.Length)
            {
                if (int.TryParse(args[i + 1], out var depth))
                {
                    _maxDepth = depth;
                }
                i++;
                continue;
            }
            if (arg == "-I" && (i + 1) < args.Length)
            {
                _excludePattern = args[i + 1];
                i++;
                continue;
            }
            if (arg == "--dirsfirst")
            {
                _dirsFirst = true;
                continue;
            }
            if (arg == "--noreport")
            {
                _noReport = true;
                continue;
            }
            // -f: print the full (target-relative) path for each entry. No
            // common-parameter collision; handled before the bundle decoder.
            if (arg == "-f")
            {
                _fullPath = true;
                continue;
            }

            // Short-bundle decoder for -a / -d that arrived via the catch-all
            // (e.g. PowerShell may forward "-ad" as a single token, or the
            // user may write -a / -d explicitly when binder didn't claim).
            if (arg.Length > 1 && arg[0] == '-' && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                bool recognized = true;
                foreach (var ch in arg.Substring(1))
                {
                    switch (ch)
                    {
                        case 'a': _showAll = true; break;
                        case 'd': _dirsOnly = true; break;
                        default: recognized = false; break;
                    }
                }
                if (recognized)
                {
                    continue;
                }
            }

            operands.Add(arg);
        }

        if (operands.Count == 0)
        {
            operands.Add(".");
        }

        string target = operands[0];
        _normTarget = target.Replace('\\', '/').TrimEnd('/');
        if (_normTarget.Length == 0) _normTarget = "/";

        string resolved;
        string rootName;
        try
        {
            resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(target);
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

        if (Directory.Exists(resolved))
        {
            WriteTreeLevel(resolved, currentDepth: 1, prefix: "");
        }

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
            if (_excludePattern != null && WildcardMatch(it.Name, _excludePattern)) continue;
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
