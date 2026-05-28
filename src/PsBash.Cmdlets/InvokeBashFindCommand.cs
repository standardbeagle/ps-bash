using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashFind</c> function
/// (REFACTOR-2 Phase 3 follow-on). Walks a directory tree, applies bash
/// <c>find</c>-style predicates, and emits typed <c>PsBash.FindEntry</c>
/// PSObjects whose <c>BashText</c> is the printed path.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashFind</c> and its
/// helper-web dependency <c>Get-BashFileInfo</c> (which itself wraps
/// <c>Get-LsEntryFromFsi</c> — the same web Phase 1d ported to C# inside
/// <see cref="InvokeBashLsCommand"/>). The relevant slice
/// (<see cref="BuildFileInfo"/> below) duplicates the port from Phase 1d; that
/// duplication is intentional and minimal — extracting a shared helper into
/// <see cref="BashRuntime"/> would broaden the scope of this task. The psm1
/// <c>Get-BashFileInfo</c> is NOT removed; <c>Invoke-BashStat</c> (still a
/// psm1 function) continues to depend on it.
///
/// Supported predicates (the psm1 oracle's exact set):
/// <list type="bullet">
/// <item><c>-name PATTERN</c> — glob match on the bare file name.</item>
/// <item><c>-type f|d</c> — restrict to files or directories.</item>
/// <item><c>-size [+-]N[ckMG]</c> — block-count / byte-count predicate.</item>
/// <item><c>-maxdepth N</c> — relative-depth cap from the root.</item>
/// <item><c>-mtime [+-]N</c> — last-modified day predicate.</item>
/// <item><c>-empty</c> — empty file or empty directory.</item>
/// <item><c>-print0</c> / <c>--print0</c> — null-delimited single-object
/// emission instead of one <c>PsBash.FindEntry</c> per match.</item>
/// <item><c>-exec CMD ... {} \;|+</c> — per-file (<c>\;</c>) or batched
/// (<c>+</c>) external command invocation. Each <c>{}</c> token is replaced
/// with the matched path; batched form replaces a single <c>{}</c> with the
/// full collected list. Dispatched via
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string,object[])"/>
/// with a parameterized string body and the command name + each argument
/// passed as <c>$args[N]</c> — no ScriptBlock construction (AOT-safe), and
/// crucially no string concatenation of user-controlled values into the
/// script body (so a path containing <c>;</c>, <c>$(...)</c>, or scriptblock
/// chars cannot be re-parsed as PowerShell — Directive 12).</item>
/// </list>
///
/// Unsupported predicates emit a bash-style error and continue, exactly
/// matching the psm1 oracle's strict-mode warning behavior. Value-bearing
/// unsupported predicates consume their value; standalone ones do not.
///
/// Common-parameter / own-parameter audit (Phase 1c / 1d lesson — verified
/// against the live binder, not assumed): find's flag set is value-bearing
/// long-words plus <c>-empty</c> / <c>-print0</c> / <c>-exec</c>. None of
/// them prefix-collide with a PowerShell common parameter (no <c>-V</c>erbose
/// / <c>-D</c>ebug / <c>-W</c>arning* / <c>-E</c>rror* / <c>-I</c>nformation*
/// / <c>-O</c>ut* / <c>-P</c>rogress* / <c>-W</c>hatIf / <c>-C</c>onfirm
/// match):
/// <list type="bullet">
/// <item><c>-empty</c> — first letter <c>e</c> matches <c>-Error*</c>, but
/// <c>-empty</c> shares no further prefix with <c>-ErrorAction</c> /
/// <c>-ErrorVariable</c>, so PowerShell's prefix matcher rejects it as a
/// common-parameter binding and leaves it for <see cref="Arguments"/>.</item>
/// <item><c>-exec</c> — same analysis; no common parameter starts with
/// <c>exec</c>.</item>
/// <item><c>-name -type -size -maxdepth -mtime -print0</c> — no common
/// parameter shares their prefix.</item>
/// </list>
/// All predicates therefore stay in <see cref="Arguments"/> and are parsed
/// by <see cref="EndProcessing"/>'s manual switch loop, matching the psm1
/// oracle's parser byte-for-byte.
///
/// The <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c>; a
/// not-found / unreadable target delegates to the psm1 <c>Write-BashError</c>
/// — both via string-bodied <c>InvokeCommand.InvokeScript</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFind")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashFindCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "find"))
            {
                WriteObject(line);
            }
            return;
        }

        // Predicates still not implemented. (-iname/-path/-ipath/-regex/-iregex/-newer/-mindepth
        // and standalone -delete/-prune/-depth are now supported below.)
        var unsupportedValuePredicates = new HashSet<string>(StringComparer.Ordinal)
        {
            "-perm", "-user", "-group", "-printf", "-amin",
            "-atime", "-cmin", "-ctime", "-gid", "-uid", "-links",
            "-samefile", "-lname", "-ilname",
        };
        var unsupportedStandalonePredicates = new HashSet<string>(StringComparer.Ordinal)
        {
            "-print", "-follow", "-ls",
            "-mount", "-xdev", "-noleaf", "-daystart", "-warn", "-nowarn",
            "-not", "-or", "-o", "-and", "-a", "-true", "-false",
        };

        string searchPath = ".";
        string? namePattern = null;
        string? inamePattern = null;
        string? pathPattern = null;
        bool pathInsensitive = false;
        string? regexPattern = null;
        bool regexInsensitive = false;
        string? typeFilter = null;
        int maxDepth = int.MaxValue;
        int minDepth = 0;
        string? sizeExpr = null;
        string? mtimeExpr = null;
        string? newerFile = null;
        bool findEmpty = false;
        bool printNull = false;
        bool doDelete = false;
        bool doPrune = false;
        bool depthFirst = false;
        List<string>? execCmd = null;
        string? execTerminator = null;
        var operands = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-name":
                    if (++i < args.Length) namePattern = args[i];
                    i++;
                    continue;
                case "-iname":
                    if (++i < args.Length) inamePattern = args[i];
                    i++;
                    continue;
                case "-path":
                case "-wholename":
                    if (++i < args.Length) { pathPattern = args[i]; pathInsensitive = false; }
                    i++;
                    continue;
                case "-ipath":
                case "-iwholename":
                    if (++i < args.Length) { pathPattern = args[i]; pathInsensitive = true; }
                    i++;
                    continue;
                case "-regex":
                    if (++i < args.Length) { regexPattern = args[i]; regexInsensitive = false; }
                    i++;
                    continue;
                case "-iregex":
                    if (++i < args.Length) { regexPattern = args[i]; regexInsensitive = true; }
                    i++;
                    continue;
                case "-type":
                    if (++i < args.Length) typeFilter = args[i];
                    i++;
                    continue;
                case "-maxdepth":
                    if (++i < args.Length)
                    {
                        if (int.TryParse(args[i], out var md)) maxDepth = md;
                    }
                    i++;
                    continue;
                case "-mindepth":
                    if (++i < args.Length)
                    {
                        if (int.TryParse(args[i], out var mnd)) minDepth = mnd;
                    }
                    i++;
                    continue;
                case "-newer":
                    if (++i < args.Length) newerFile = args[i];
                    i++;
                    continue;
                case "-size":
                    if (++i < args.Length) sizeExpr = args[i];
                    i++;
                    continue;
                case "-mtime":
                    if (++i < args.Length) mtimeExpr = args[i];
                    i++;
                    continue;
                case "-empty":
                    findEmpty = true;
                    i++;
                    continue;
                case "-delete":
                    doDelete = true;
                    depthFirst = true; // find: -delete implies -depth (empty dirs before parents)
                    i++;
                    continue;
                case "-prune":
                    doPrune = true;
                    i++;
                    continue;
                case "-depth":
                    depthFirst = true;
                    i++;
                    continue;
                case "-print0":
                case "--print0":
                    printNull = true;
                    i++;
                    continue;
                case "-exec":
                    i++;
                    execCmd = new List<string>();
                    while (i < args.Length)
                    {
                        string ea = args[i];
                        if (ea == ";" || ea == "+")
                        {
                            execTerminator = ea;
                            i++;
                            break;
                        }
                        execCmd.Add(ea);
                        i++;
                    }
                    continue;
                default:
                    if (unsupportedValuePredicates.Contains(arg))
                    {
                        EmitError($"find: unsupported predicate '{arg}'", 1);
                        i += 2;
                        continue;
                    }
                    if (unsupportedStandalonePredicates.Contains(arg))
                    {
                        EmitError($"find: unsupported predicate '{arg}'", 1);
                        i++;
                        continue;
                    }
                    operands.Add(arg);
                    i++;
                    continue;
            }
        }

        if (operands.Count > 0)
        {
            searchPath = operands[0];
        }

        // Resolve root via Get-BashItem (psm1 oracle's error-message format,
        // including the bash "No such file or directory" mapping). Wrap with
        // inner 2>&1 so any Write-BashError ErrorRecord lands in the script's
        // success stream rather than buried in the sub-pipeline (otherwise
        // invisible to the cmdlet's caller's 2>&1 redirect).
        var rootResult = InvokeCommand.InvokeScript(
            "param($p) Get-BashItem -Path $p -Command 'find' 2>&1", searchPath);
        System.IO.FileSystemInfo? rootItem = null;
        foreach (var r in rootResult)
        {
            if (r?.BaseObject is System.IO.FileSystemInfo fsi)
            {
                rootItem = fsi;
                break;
            }
            if (r?.BaseObject is ErrorRecord innerEr)
            {
                FileSystemHelpers.WriteBashError(this, innerEr.ToString());
            }
        }
        if (rootItem == null)
        {
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
            return;
        }

        string resolvedRoot = rootItem.FullName;
        char[] sepChars = {
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar,
        };
        int rootDepth = resolvedRoot
            .TrimEnd(sepChars)
            .Split(new[] { '\\', '/' })
            .Length;

        // Collect items: root + recursive children, with maxdepth honored at
        // enumeration time so a large tree does not load into memory.
        var allItems = new List<System.IO.FileSystemInfo> { rootItem };
        if (rootItem is System.IO.DirectoryInfo rootDir)
        {
            try
            {
                var enumOpts = new System.IO.EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false,
                };
                if (maxDepth < int.MaxValue)
                {
                    enumOpts.RecurseSubdirectories = maxDepth > 0;
                    enumOpts.MaxRecursionDepth = Math.Max(0, maxDepth - 1);
                }
                else
                {
                    enumOpts.RecurseSubdirectories = true;
                }

                foreach (var fsi in rootDir.EnumerateFileSystemInfos("*", enumOpts))
                {
                    allItems.Add(fsi);
                }
            }
            catch
            {
                // best-effort, oracle swallows enumeration errors too.
            }
        }

        // Parse size expression: +1k, -500c, +1M, +1G — block (no suffix)
        // defaults to 512 bytes, matching POSIX find.
        char sizeOp = '\0';
        long sizeBytes = 0;
        if (sizeExpr != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                sizeExpr, @"^([+-])(\d+)([ckMG]?)$");
            if (m.Success)
            {
                sizeOp = m.Groups[1].Value[0];
                long n = long.Parse(m.Groups[2].Value);
                string suffix = m.Groups[3].Value;
                sizeBytes = suffix switch
                {
                    "c" => n,
                    "k" => n * 1024L,
                    "M" => n * 1048576L,
                    "G" => n * 1073741824L,
                    _ => n * 512L,
                };
            }
        }

        // Parse mtime expression: -7 (modified within the last 7 days),
        // +30 (older than 30 days).
        char mtimeOp = '\0';
        int mtimeDays = 0;
        if (mtimeExpr != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(mtimeExpr, @"^([+-])(\d+)$");
            if (m.Success)
            {
                mtimeOp = m.Groups[1].Value[0];
                mtimeDays = int.Parse(m.Groups[2].Value);
            }
        }

        // -newer FILE: keep entries modified strictly later than FILE's mtime.
        DateTime? newerThan = null;
        if (newerFile != null)
        {
            try
            {
                var curDir = SessionState.Path.CurrentFileSystemLocation.Path;
                var full = System.IO.Path.GetFullPath(newerFile, curDir);
                if (System.IO.File.Exists(full)) newerThan = System.IO.File.GetLastWriteTime(full);
                else if (System.IO.Directory.Exists(full)) newerThan = System.IO.Directory.GetLastWriteTime(full);
                else { EmitError($"find: '{newerFile}': No such file or directory", 1); return; }
            }
            catch
            {
                EmitError($"find: '{newerFile}': No such file or directory", 1);
                return;
            }
        }

        // -regex / -iregex: the pattern must match the WHOLE printed path.
        System.Text.RegularExpressions.Regex? rx = null;
        if (regexPattern != null)
        {
            var opts = regexInsensitive
                ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                : System.Text.RegularExpressions.RegexOptions.None;
            try { rx = new System.Text.RegularExpressions.Regex("^(?:" + regexPattern + ")$", opts); }
            catch { EmitError($"find: invalid regex: {regexPattern}", 1); return; }
        }

        var now = DateTime.Now;

        // Forward-slash relative display path anchored at searchPath (the psm1 oracle's form).
        string BuildDisplay(string itemPath)
        {
            string relativePath = itemPath.Substring(resolvedRoot.Length).Replace('\\', '/');
            if (relativePath.StartsWith("/", StringComparison.Ordinal))
                relativePath = relativePath.Substring(1);
            if (searchPath == ".")
                return relativePath.Length == 0 ? "." : $"./{relativePath}";
            string normalized = searchPath.Replace('\\', '/');
            return relativePath.Length == 0 ? normalized : $"{normalized}/{relativePath}";
        }

        var pathCmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // -prune needs parents evaluated before their children so a matched directory can
        // exclude its subtree. Sort shallow-first only then (keeps default output order otherwise).
        if (doPrune)
            allItems.Sort((a, b) => SegmentCount(a.FullName).CompareTo(SegmentCount(b.FullName)));

        var prunedRoots = new List<string>();
        var matches = new List<(System.IO.FileSystemInfo Item, string DisplayPath, bool IsDir, string ItemPath)>();

        foreach (var item in allItems)
        {
            string itemPath = item.FullName;
            int relativeDepth = SegmentCount(itemPath) - rootDepth;

            if (relativeDepth > maxDepth) continue;

            // Skip anything beneath a pruned directory (don't descend into it).
            if (prunedRoots.Count > 0)
            {
                bool underPruned = false;
                foreach (var root in prunedRoots)
                {
                    if (itemPath.StartsWith(root + System.IO.Path.DirectorySeparatorChar, pathCmp))
                    {
                        underPruned = true;
                        break;
                    }
                }
                if (underPruned) continue;
            }

            bool isDir = item is System.IO.DirectoryInfo;

            if (relativeDepth < minDepth) continue;

            if (typeFilter != null)
            {
                if (typeFilter == "f" && isDir) continue;
                if (typeFilter == "d" && !isDir) continue;
            }

            if (namePattern != null && !GlobMatch(item.Name, namePattern, ci: false)) continue;
            if (inamePattern != null && !GlobMatch(item.Name, inamePattern, ci: true)) continue;

            string displayPath = BuildDisplay(itemPath);

            if (pathPattern != null && !PathGlobMatch(displayPath, pathPattern, pathInsensitive)) continue;
            if (rx != null && !rx.IsMatch(displayPath)) continue;

            if (sizeOp != '\0')
            {
                long fileSize = isDir ? 0L : ((System.IO.FileInfo)item).Length;
                if (sizeOp == '+' && fileSize <= sizeBytes) continue;
                if (sizeOp == '-' && fileSize >= sizeBytes) continue;
            }

            if (mtimeOp != '\0')
            {
                double daysAgo = (now - item.LastWriteTime).TotalDays;
                if (mtimeOp == '-' && daysAgo >= mtimeDays) continue;
                if (mtimeOp == '+' && daysAgo <= mtimeDays) continue;
            }

            if (newerThan.HasValue && item.LastWriteTime <= newerThan.Value) continue;

            if (findEmpty)
            {
                if (isDir)
                {
                    try
                    {
                        if (((System.IO.DirectoryInfo)item)
                                .EnumerateFileSystemInfos().Any()) continue;
                    }
                    catch { continue; }
                }
                else
                {
                    if (((System.IO.FileInfo)item).Length > 0) continue;
                }
            }

            // A matched directory under -prune excludes its subtree from here on.
            if (doPrune && isDir)
                prunedRoots.Add(itemPath);

            matches.Add((item, displayPath, isDir, itemPath));
        }

        // -depth (and the implied -depth of -delete): process directory contents before the
        // directory itself, i.e. deepest paths first.
        if (depthFirst)
            matches.Sort((a, b) => SegmentCount(b.ItemPath).CompareTo(SegmentCount(a.ItemPath)));

        // ── dispatch ────────────────────────────────────────────────────────────
        // -delete is an action: remove matches and print nothing (deepest-first lets a
        // directory be removed after its matched contents are gone).
        if (doDelete)
        {
            foreach (var m in matches)
            {
                try
                {
                    if (m.IsDir) System.IO.Directory.Delete(m.ItemPath, recursive: false);
                    else System.IO.File.Delete(m.ItemPath);
                }
                catch (Exception ex)
                {
                    EmitError($"find: cannot delete '{m.DisplayPath}': {ex.Message}", 1);
                }
            }
            return;
        }

        if (execCmd != null)
        {
            var execCollectedPaths = new List<string>();
            foreach (var m in matches)
            {
                if (execTerminator == ";")
                {
                    var cmdArgs = new List<string>(execCmd.Count);
                    foreach (var token in execCmd)
                        cmdArgs.Add(token == "{}" ? m.DisplayPath : token);
                    InvokeExternalCommand(cmdArgs);
                }
                else
                {
                    execCollectedPaths.Add(m.DisplayPath);
                }
            }
            // -exec cmd {} +  — one invocation with the whole collected path set.
            if (execTerminator == "+" && execCollectedPaths.Count > 0)
            {
                var cmdArgs = new List<string>();
                foreach (var token in execCmd)
                {
                    if (token == "{}") cmdArgs.AddRange(execCollectedPaths);
                    else cmdArgs.Add(token);
                }
                InvokeExternalCommand(cmdArgs);
            }
            return;
        }

        if (printNull)
        {
            var nullDelimitedPaths = new StringBuilder();
            foreach (var m in matches)
            {
                nullDelimitedPaths.Append(m.DisplayPath);
                nullDelimitedPaths.Append('\0');
            }
            if (nullDelimitedPaths.Length > 0)
            {
                var obj = new PSObject();
                obj.TypeNames.Insert(0, "PsBash.TextOutput");
                obj.Properties.Add(new PSNoteProperty("BashText", nullDelimitedPaths.ToString()));
                obj.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
                WriteObject(obj);
            }
            return;
        }

        // Default action: emit a typed FindEntry per match.
        foreach (var m in matches)
        {
            var fileInfo = BuildFileInfo(m.Item);
            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.FindEntry");
            obj.Properties.Add(new PSNoteProperty("Path", m.DisplayPath));
            obj.Properties.Add(new PSNoteProperty("Name", m.Item.Name));
            obj.Properties.Add(new PSNoteProperty("FullPath", m.ItemPath));
            obj.Properties.Add(new PSNoteProperty("IsDirectory", m.IsDir));
            obj.Properties.Add(new PSNoteProperty("SizeBytes", fileInfo.SizeBytes));
            obj.Properties.Add(new PSNoteProperty("Permissions", fileInfo.Permissions));
            obj.Properties.Add(new PSNoteProperty("LinkCount", fileInfo.LinkCount));
            obj.Properties.Add(new PSNoteProperty("Owner", fileInfo.Owner));
            obj.Properties.Add(new PSNoteProperty("Group", fileInfo.Group));
            obj.Properties.Add(new PSNoteProperty("LastModified", m.Item.LastWriteTime));
            obj.Properties.Add(new PSNoteProperty("BashText", m.DisplayPath));
            WriteObject(obj);
        }
    }

    private static int SegmentCount(string path) =>
        path.TrimEnd('\\', '/').Split('\\', '/').Length;

    // ── -exec dispatcher ─────────────────────────────────────────────────────

    /// <summary>
    /// Invokes an external command whose name and arguments come from user
    /// input. The command name and each argument are passed as positional
    /// <c>$args</c> entries through a fixed, parameterless script body — never
    /// concatenated into the body — so a path or token containing <c>;</c>,
    /// <c>$(...)</c>, scriptblock chars, or backticks cannot be re-parsed as
    /// PowerShell syntax (qa-rubric Directive 12). The script body uses
    /// <c>&amp; $args[0] @rest</c> splatting, which treats every element as a
    /// single literal argument.
    /// </summary>
    private void InvokeExternalCommand(IReadOnlyList<string> cmdArgs)
    {
        if (cmdArgs.Count == 0) return;
        // Body uses positional $args splat. No interpolation of cmdArgs.
        const string body =
            "$rest = @(); if ($args.Count -gt 1) { $rest = $args[1..($args.Count-1)] }; " +
            "& $args[0] @rest";
        var boxed = new object[cmdArgs.Count];
        for (int k = 0; k < cmdArgs.Count; k++) boxed[k] = cmdArgs[k];
        try
        {
            foreach (var o in InvokeCommand.InvokeScript(body, boxed))
            {
                WriteObject(o);
            }
        }
        catch (Exception ex)
        {
            EmitError($"find: -exec failed: {ex.Message}", 1);
        }
    }

    // ── glob match (bash -name semantics) ────────────────────────────────────

    private static bool GlobMatch(string name, string pattern, bool ci)
    {
        // Bash -name uses fnmatch semantics: * matches any (including empty),
        // ? matches one char, [..] character class. PowerShell's -like
        // operator implements the same semantics for our purposes. -iname is
        // the case-insensitive variant.
        return WildcardPattern
            .Get(pattern, ci ? WildcardOptions.IgnoreCase : WildcardOptions.None)
            .IsMatch(name);
    }

    // -path / -ipath: the glob is matched against the whole printed path. Unlike -name, a '*'
    // is allowed to span '/' (fnmatch without FNM_PATHNAME), which WildcardPattern already does.
    private static bool PathGlobMatch(string path, string pattern, bool ci)
    {
        return WildcardPattern
            .Get(pattern, ci ? WildcardOptions.IgnoreCase : WildcardOptions.None)
            .IsMatch(path);
    }

    // ── file metadata (Get-BashFileInfo / Get-LsEntryFromFsi slice) ─────────
    // Duplicated from InvokeBashLsCommand.BuildEntryFromFsi by design — see
    // the class remarks. The psm1 oracle keeps Get-BashFileInfo because
    // Invoke-BashStat (still a psm1 function) also depends on it.

    private sealed class FindFileInfo
    {
        public long SizeBytes;
        public string Permissions = string.Empty;
        public int LinkCount = 1;
        public string Owner = string.Empty;
        public string Group = string.Empty;
    }

    private static FindFileInfo BuildFileInfo(System.IO.FileSystemInfo item)
    {
        var info = new FindFileInfo();
        var attrs = item.Attributes;
        bool isDir = item is System.IO.DirectoryInfo;
        bool isLink = (attrs & System.IO.FileAttributes.ReparsePoint) != 0;
        char typeChar = isDir ? 'd' : (isLink ? 'l' : '-');

        if (OperatingSystem.IsWindows())
        {
            bool readOnly = (attrs & System.IO.FileAttributes.ReadOnly) != 0;
            bool isExec = isDir || IsExecExtension(item.Extension);
            string r = "r";
            string w = readOnly ? "-" : "w";
            string x = isExec ? "x" : "-";
            info.Permissions = $"{typeChar}{r}{w}{x}{r}-{x}{r}-{x}";
            info.Owner = Environment.GetEnvironmentVariable("USERNAME") ?? string.Empty;
            info.Group = info.Owner;
        }
        else
        {
            int mode = (int)item.UnixFileMode;
            info.Permissions = $"{typeChar}{ConvertToPermissionString(mode)}";
            try
            {
                bool isMac = OperatingSystem.IsMacOS();
                var statArgs = isMac
                    ? new[] { "-f", "%Su %Sg", item.FullName }
                    : new[] { "-c", "%U %G", item.FullName };
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in statArgs) psi.ArgumentList.Add(a);
                // Bounded spawn + concurrent drain + kill-tree on timeout so a hung
                // /usr/bin/stat (run once per file in the walk) cannot wedge the host.
                string statOut = BashRuntime.RunChildProcess(psi).Stdout.Trim();
                if (statOut.Length > 0)
                {
                    var parts = statOut.Split(new[] { ' ' }, 2);
                    info.Owner = parts[0];
                    info.Group = parts.Length > 1 ? parts[1] : string.Empty;
                }
            }
            catch
            {
                // /usr/bin/stat unavailable — match the oracle's 2>$null swallow.
            }
        }

        info.SizeBytes = isDir ? 4096L : ((System.IO.FileInfo)item).Length;
        info.LinkCount = 1;
        return info;
    }

    private static bool IsExecExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return false;
        return ext.ToLowerInvariant() switch
        {
            ".exe" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".com" => true,
            _ => false,
        };
    }

    private static string ConvertToPermissionString(int mode)
    {
        var sb = new StringBuilder(9);
        int[] bits = { 256, 128, 64, 32, 16, 8, 4, 2, 1 };
        char[] chars = { 'r', 'w', 'x', 'r', 'w', 'x', 'r', 'w', 'x' };
        for (int k = 0; k < 9; k++)
        {
            sb.Append((mode & bits[k]) != 0 ? chars[k] : '-');
        }
        return sb.ToString();
    }

    // ── error sink ───────────────────────────────────────────────────────────

    private void EmitError(string message, int exitCode)
    {
        FileSystemHelpers.WriteBashError(this, message);
        FileSystemHelpers.SetLastExitCode(this, exitCode);
    }
}
