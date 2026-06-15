using System.Formats.Tar;
using System.IO.Compression;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTar</c> function
/// (REFACTOR-2 follow-on). Reproduces GNU/BSD <c>tar</c> across the
/// oracle's three modes byte-for-byte: <c>-c</c> create, <c>-x</c>
/// extract, and <c>-t</c> list, with optional <c>-z</c> gzip-compression
/// filter (also auto-detected from a <c>.tar.gz</c> / <c>.tgz</c> suffix on
/// extract/list per oracle), <c>-v</c> verbose name-per-line emission,
/// <c>-f FILE</c> archive path, <c>--directory=DIR</c> chdir-before-op,
/// and <c>--exclude=PATTERN</c> wildcard exclusion (per psm1 oracle: a
/// substring-match against the full path of each candidate entry).
///
/// Archive engine: <see cref="TarFile"/> is not used — the oracle drove the
/// lower-level <see cref="TarReader"/> / <see cref="TarWriter"/> pair to
/// keep streaming + exclude filtering exact, so we keep the same surface
/// here.
///
/// Flag binding (case-collision table):
/// <list type="bullet">
/// <item><c>-c</c> (create) prefix-collides with <c>-Confirm</c>: declared
/// as <see cref="SwitchParameter"/> literally named <c>C</c>. Because
/// PowerShell parameter binding is case-insensitive, <c>-C</c> (the bash
/// change-dir flag) also case-folds to this switch — it is therefore NOT
/// possible to express bash's bare <c>-C DIR</c> form on the cmdlet
/// binder. The long form <c>--directory=DIR</c> (and the separate
/// <c>--directory DIR</c>) ARE supported via the manual <c>Arguments</c>
/// scan. See the "Known gap" comment block below.</item>
/// <item><c>-v</c> (verbose) prefix-collides with <c>-Verbose</c>:
/// declared as <see cref="SwitchParameter"/> <c>V</c>.</item>
/// <item><c>-f FILE</c> (archive) is value-bearing; no PowerShell
/// common-parameter prefix collision but declared as <c>string? F</c>
/// for clean binder routing and to support the standard separated form
/// (<c>-f FILE</c>). The joined form (<c>-fFILE</c>) and bundled form
/// (e.g. <c>-cvf FILE</c>) are recovered from <see cref="Arguments"/> by
/// the manual scan, exactly matching the oracle's per-char dispatch.</item>
/// <item><c>-x</c>, <c>-t</c>, <c>-z</c> have no PowerShell common-parameter
/// prefix collision and stay in <see cref="Arguments"/>.</item>
/// </list>
///
/// <para><b>Known gap (-C DIR case collision):</b> Because the PowerShell
/// cmdlet binder is case-insensitive, the bash <c>-C DIR</c> change-directory
/// flag cannot be routed as a separate cmdlet parameter without colliding
/// with the <c>-c</c> create switch. Callers must use <c>--directory=DIR</c>
/// (or <c>--directory DIR</c>) instead. The psm1 oracle distinguished via
/// case-sensitive <c>-ceq</c> comparison, which the binder cannot
/// reproduce. This is the one residual flag-shape gap introduced by the
/// migration; the long form provides full coverage of the underlying
/// behavior.</para>
///
/// AOT safety: no <see cref="ScriptBlock"/> construction; <c>--help</c>
/// delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.
/// File-read / -write failures route through
/// <see cref="FileSystemHelpers.WriteBashError"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTar")]
[OutputType(typeof(string))]
public sealed class InvokeBashTarCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>The bash <c>-c</c> (create) switch — explicit because the
    /// bare token <c>-c</c> prefix-collides with <c>-Confirm</c>. Case-insensitive
    /// binder means <c>-C</c> (the bash change-dir flag) also binds to this
    /// switch — see the cmdlet docstring for the known-gap workaround.</summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    /// <summary>The bash <c>-v</c> (verbose) switch — explicit because the
    /// bare token <c>-v</c> prefix-collides with <c>-Verbose</c>.</summary>
    [Parameter]
    public SwitchParameter V { get; set; }

    /// <summary>The bash <c>-f FILE</c> archive path. No common-parameter
    /// prefix collision but declared for clean separated-form binding;
    /// joined / bundled forms are recovered from <see cref="Arguments"/>.</summary>
    [Parameter]
    public string? F { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "tar", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tar"))
            {
                WriteObject(line);
            }
            return;
        }

        // Defer the create flag — we'll decide AFTER the args loop whether
        // C.IsPresent meant -c (create) or -C DIR (chdir). The case-
        // insensitive PowerShell binder collapses them onto the same switch.
        bool cBoundByBinder = C.IsPresent;
        bool create = false;
        bool extract = false;
        bool listMode = false;
        bool gzipFilter = false;
        bool verbose = V.IsPresent;
        string? archiveFile = F;
        string? changeDir = null;
        var excludePatterns = new List<string>();
        var operands = new List<string>();
        bool sawExplicitCreate = false;
        int stripComponents = 0;
        bool toStdout = false;

        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];

            if (a == "--") { i++; while (i < args.Length) { operands.Add(args[i]); i++; } break; }
            if (a == "--create") { create = true; sawExplicitCreate = true; i++; continue; }
            if (a == "--extract" || a == "--get") { extract = true; i++; continue; }
            if (a == "--list") { listMode = true; i++; continue; }
            if (a == "--gzip" || a == "--gunzip") { gzipFilter = true; i++; continue; }
            if (a == "--verbose") { verbose = true; i++; continue; }

            if (a == "--file")
            {
                i++;
                if (i < args.Length) { archiveFile = args[i]; }
                i++;
                continue;
            }
            if (a.StartsWith("--file=", StringComparison.Ordinal))
            {
                archiveFile = a.Substring("--file=".Length);
                i++;
                continue;
            }
            if (a == "--directory")
            {
                i++;
                if (i < args.Length) { changeDir = args[i]; }
                i++;
                continue;
            }
            if (a.StartsWith("--directory=", StringComparison.Ordinal))
            {
                changeDir = a.Substring("--directory=".Length);
                i++;
                continue;
            }
            if (a.StartsWith("--exclude=", StringComparison.Ordinal))
            {
                excludePatterns.Add(a.Substring("--exclude=".Length));
                i++;
                continue;
            }
            if (a == "--exclude")
            {
                i++;
                if (i < args.Length) { excludePatterns.Add(args[i]); }
                i++;
                continue;
            }
            // --strip-components=N / --strip-components N: drop leading path
            // components on extract (ubiquitous for "extract into current dir").
            if (a.StartsWith("--strip-components=", StringComparison.Ordinal))
            {
                int.TryParse(a.Substring("--strip-components=".Length), out stripComponents);
                i++;
                continue;
            }
            if (a == "--strip-components")
            {
                i++;
                if (i < args.Length) int.TryParse(args[i], out stripComponents);
                i++;
                continue;
            }
            if (a == "--to-stdout") { toStdout = true; i++; continue; }

            // Bundled / joined short flags (oracle: `arg.Substring(1).ToCharArray()`
            // loop). `f` and `C` are value-bearing and consume the rest of the
            // token or the next argument; everything else is a boolean switch.
            if (a.Length > 1 && a[0] == '-' && !a.StartsWith("--", StringComparison.Ordinal))
            {
                string body = a.Substring(1);
                int j = 0;
                while (j < body.Length)
                {
                    char ch = body[j];
                    if (ch == 'c') { create = true; sawExplicitCreate = true; }
                    else if (ch == 'x') { extract = true; }
                    else if (ch == 't') { listMode = true; }
                    else if (ch == 'z') { gzipFilter = true; }
                    else if (ch == 'v') { verbose = true; }
                    else if (ch == 'p') { /* preserve perms — ignored, oracle parity */ }
                    else if (ch == 'O') { toStdout = true; }
                    else if (ch == 'f')
                    {
                        string rest = body.Substring(j + 1);
                        if (rest.Length > 0) { archiveFile = rest; }
                        else
                        {
                            i++;
                            if (i < args.Length) { archiveFile = args[i]; }
                        }
                        break;
                    }
                    else if (ch == 'C')
                    {
                        // Bash -C DIR / -CDIR change-dir. Note: a bare `-C`
                        // arriving here means the PSCmdlet binder did NOT
                        // consume it as the create switch (e.g. because it
                        // was bundled into a multi-char short flag like
                        // `-xC`). Standalone `-C` and `-c` are
                        // case-insensitively equivalent under the binder and
                        // are both captured by the `C` SwitchParameter
                        // declaration; callers must use --directory=DIR for
                        // change-dir. See cmdlet docstring.
                        string rest = body.Substring(j + 1);
                        if (rest.Length > 0) { changeDir = rest; }
                        else
                        {
                            i++;
                            if (i < args.Length) { changeDir = args[i]; }
                        }
                        break;
                    }
                    j++;
                }
                i++;
                continue;
            }

            operands.Add(a);
            i++;
        }

        if (!string.IsNullOrEmpty(archiveFile))
        {
            archiveFile = SessionState.Path.GetUnresolvedProviderPathFromPSPath(archiveFile);
        }
        if (!string.IsNullOrEmpty(changeDir))
        {
            changeDir = SessionState.Path.GetUnresolvedProviderPathFromPSPath(changeDir);
        }

        // Resolve the cBoundByBinder ambiguity: the PowerShell binder caught
        // either -c (create) or -C (chdir). If we already saw an explicit
        // lowercase -c in the args loop (--create or bundled), create is set
        // correctly. Otherwise the binder fired for either bare -c (the far
        // more common case — separated form `-c -f ARCHIVE SRC`) or for a
        // bare uppercase -C DIR. We disambiguate by looking at the other
        // action flags: if NO other action verb (-x / -t) is set, the binder
        // must have caught -c (create), since -C DIR alone is meaningless
        // without an action. Only when an action verb IS already set do we
        // treat cBoundByBinder as the chdir flag and consume the first
        // operand as the directory target.
        if (cBoundByBinder && !sawExplicitCreate)
        {
            if (!extract && !listMode)
            {
                // No other action verb — binder caught the create flag.
                create = true;
            }
            else if (string.IsNullOrEmpty(changeDir) && operands.Count > 0)
            {
                // Action verb already set; binder caught -C DIR. Pull the
                // chdir target from the first operand position. Matches
                // GNU tar's surface for `-C DIR` taking one value.
                changeDir = operands[0];
                operands.RemoveAt(0);
                try
                {
                    changeDir = SessionState.Path.GetUnresolvedProviderPathFromPSPath(changeDir);
                }
                catch { /* fall through with the raw token */ }
            }
        }
        else if (cBoundByBinder && sawExplicitCreate)
        {
            // Both -c and -C might be present. The bundled-flag handler
            // already set create; keep it. changeDir, if any, was already
            // captured via --directory= forms.
            create = true;
        }

        if (string.IsNullOrEmpty(archiveFile))
        {
            FileSystemHelpers.WriteBashError(this, "tar: you must specify -f archive");
            return;
        }

        if (create)
        {
            DoCreate(archiveFile!, operands, gzipFilter, verbose, excludePatterns);
        }
        else if (extract)
        {
            DoExtract(archiveFile!, gzipFilter, verbose, changeDir, stripComponents, toStdout);
        }
        else if (listMode)
        {
            DoList(archiveFile!, gzipFilter);
        }
        else
        {
            FileSystemHelpers.WriteBashError(this, "tar: you must specify -c, -x, or -t");
        }
    }

    private void DoCreate(string archiveFile, List<string> sources, bool gzipFilter, bool verbose, List<string> excludePatterns)
    {
        if (sources.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "tar: no files or directories specified");
            return;
        }

        FileStream? outStream = null;
        Stream? tarStream = null;
        TarWriter? writer = null;
        try
        {
            outStream = File.Open(archiveFile, FileMode.Create, FileAccess.Write, FileShare.None);
            tarStream = gzipFilter
                ? (Stream)new GZipStream(outStream, CompressionMode.Compress)
                : outStream;
            writer = new TarWriter(tarStream);

            // Compile each --exclude glob once. GNU tar matches the pattern
            // (fnmatch glob) against the member name; a match on any path
            // component prunes that component's whole subtree.
            var excludeRegexes = BuildExcludeRegexes(excludePatterns);

            foreach (string src in sources)
            {
                string resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(src);
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    FileSystemHelpers.WriteBashError(this, $"tar: {src}: Cannot stat: No such file or directory");
                    continue;
                }

                if (Directory.Exists(resolved))
                {
                    string root = Path.GetFileName(resolved);
                    string? baseDir = Path.GetDirectoryName(resolved);
                    if (baseDir == null) { baseDir = string.Empty; }
                    var enumOpts = new EnumerationOptions { RecurseSubdirectories = true };
                    string[] children = Directory.GetFileSystemEntries(resolved, "*", enumOpts);
                    writer.WriteEntry(resolved, root);
                    if (verbose) { WriteObject(BashRuntime.NewBashObject(root)); }
                    foreach (string child in children)
                    {
                        string relPath = child.Substring(baseDir.Length + 1).Replace('\\', '/');
                        if (IsExcluded(relPath, excludeRegexes)) { continue; }
                        if (verbose) { WriteObject(BashRuntime.NewBashObject(relPath)); }
                        writer.WriteEntry(child, relPath);
                    }
                }
                else
                {
                    string relPath = Path.GetFileName(resolved);
                    if (IsExcluded(relPath, excludeRegexes)) { continue; }
                    if (verbose) { WriteObject(BashRuntime.NewBashObject(relPath)); }
                    writer.WriteEntry(resolved, relPath);
                }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"tar: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
        finally
        {
            writer?.Dispose();
            if (gzipFilter) { tarStream?.Dispose(); }
            outStream?.Dispose();
        }
    }

    private void DoExtract(string archiveFile, bool gzipFilter, bool verbose, string? changeDir,
        int stripComponents = 0, bool toStdout = false)
    {
        if (!File.Exists(archiveFile))
        {
            FileSystemHelpers.WriteBashError(this, $"tar: {archiveFile}: Cannot open: No such file or directory");
            return;
        }
        bool isGz = gzipFilter
            || archiveFile.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || archiveFile.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
        string destDir = !string.IsNullOrEmpty(changeDir)
            ? changeDir!
            : SessionState.Path.CurrentLocation.ProviderPath;

        FileStream? inStream = null;
        Stream? tarStream = null;
        TarReader? reader = null;
        try
        {
            inStream = BashFileSystem.OpenRead(archiveFile);
            tarStream = isGz
                ? (Stream)new GZipStream(inStream, CompressionMode.Decompress)
                : inStream;
            reader = new TarReader(tarStream);

            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: true)) != null)
            {
                // -O / --to-stdout: emit a regular file's content instead of writing.
                if (toStdout)
                {
                    if (entry.DataStream == null) { continue; }
                    using var sr = new StreamReader(entry.DataStream);
                    foreach (var o in BashRuntime.EmitBashLines(sr.ReadToEnd())) WriteObject(o);
                    continue;
                }

                // --strip-components=N: drop the first N path segments of the name.
                string name = entry.Name;
                if (stripComponents > 0)
                {
                    var parts = name.Replace('\\', '/').Split('/');
                    if (parts.Length <= stripComponents) continue; // nothing left after strip
                    name = string.Join("/", parts.Skip(stripComponents));
                    if (name.Length == 0) continue;
                }

                // Guard against tar-slip / Zip-Slip: a malicious entry named
                // `../../x`, an absolute path, or a Windows drive/UNC path would
                // otherwise let Path.Join + File.Create write OUTSIDE destDir.
                // GNU tar strips a leading `/` and refuses to extract members that
                // resolve above the destination; mirror that — skip and warn.
                if (!TryResolveWithinDest(destDir, name, out string targetPath))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"tar: Skipping to next header: {name}: path escapes archive destination");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    continue;
                }
                if (verbose) { WriteObject(BashRuntime.NewBashObject(name)); }

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(targetPath);
                        continue;
                    case TarEntryType.SymbolicLink:
                        ExtractLink(destDir, targetPath, name, entry.LinkName, symbolic: true);
                        continue;
                    case TarEntryType.HardLink:
                        ExtractLink(destDir, targetPath, name, entry.LinkName, symbolic: false);
                        continue;
                }

                // Regular file.
                if (entry.DataStream == null) { continue; }
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                using var fs = File.Create(targetPath);
                entry.DataStream.CopyTo(fs);
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"tar: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
        finally
        {
            reader?.Dispose();
            if (isGz) { tarStream?.Dispose(); }
            inStream?.Dispose();
        }
    }

    /// <summary>
    /// Resolve a tar entry name against the extraction destination and confirm
    /// it stays inside it. Returns false for absolute paths, drive/UNC roots, or
    /// names that climb out via <c>..</c>. On success <paramref name="targetPath"/>
    /// is the fully-resolved, contained path to write.
    /// </summary>
    private static bool TryResolveWithinDest(string destDir, string name, out string targetPath)
    {
        targetPath = string.Empty;
        string rel = name.Replace('/', Path.DirectorySeparatorChar);

        // A rooted entry (leading separator, C:\..., \\server\...) must never
        // escape the destination — reject rather than letting Path.Join discard
        // destDir and honor the absolute path.
        if (Path.IsPathRooted(rel)) { return false; }

        string destFull = Path.GetFullPath(destDir);
        string candidate = Path.GetFullPath(Path.Combine(destFull, rel));
        if (!PathIsWithin(destFull, candidate)) { return false; }
        targetPath = candidate;
        return true;
    }

    /// <summary>True if <paramref name="candidateFull"/> equals or sits beneath
    /// <paramref name="destFull"/> (both already fully-resolved).</summary>
    private static bool PathIsWithin(string destFull, string candidateFull)
    {
        string prefix = destFull.EndsWith(Path.DirectorySeparatorChar)
            ? destFull
            : destFull + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidateFull.StartsWith(prefix, cmp)
            || string.Equals(candidateFull, destFull, cmp);
    }

    /// <summary>
    /// Extract a symbolic or hard link entry. The link is created only if its
    /// target stays within the destination — an escaping link (absolute, or
    /// climbing out via <c>..</c>) is the classic tar-slip pivot (extract a
    /// <c>link → /etc</c> then a regular file <c>link/passwd</c>), so it is
    /// refused. Symlinks that can't be created (e.g. Windows without the
    /// privilege) warn and continue rather than aborting the whole archive.
    /// </summary>
    private void ExtractLink(string destDir, string linkPath, string name, string? linkName, bool symbolic)
    {
        if (string.IsNullOrEmpty(linkName))
        {
            return;
        }

        string destFull = Path.GetFullPath(destDir);
        string linkDir = Path.GetDirectoryName(linkPath) ?? destFull;
        string relTarget = linkName.Replace('/', Path.DirectorySeparatorChar);

        // Resolve the target relative to the link's own directory (symlink) or
        // the destination root (hardlink names are archive-root-relative).
        string resolvedTarget = Path.IsPathRooted(relTarget)
            ? Path.GetFullPath(relTarget)
            : Path.GetFullPath(Path.Combine(symbolic ? linkDir : destFull, relTarget));

        if (Path.IsPathRooted(relTarget) || !PathIsWithin(destFull, resolvedTarget))
        {
            FileSystemHelpers.WriteBashError(this,
                $"tar: Skipping to next header: {name}: link target escapes archive destination");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        if (!string.IsNullOrEmpty(linkDir) && !Directory.Exists(linkDir))
        {
            Directory.CreateDirectory(linkDir);
        }
        if (File.Exists(linkPath) || Directory.Exists(linkPath))
        {
            FileSystemHelpers.DeleteFileForce(linkPath);
        }

        try
        {
            if (symbolic)
            {
                // Preserve the original (relative) link text, like GNU tar.
                File.CreateSymbolicLink(linkPath, linkName);
            }
            else
            {
                // No portable hardlink API — copy the already-extracted target's
                // bytes, which preserves the data (loses inode sharing).
                File.Copy(resolvedTarget, linkPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"tar: {name}: cannot create link: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    /// <summary>
    /// Compile each non-empty <c>--exclude</c> glob into an anchored regex.
    /// Empty patterns are dropped (an empty glob must match nothing, not
    /// everything). <c>*</c> → <c>.*</c> (matches across <c>/</c>, like GNU
    /// tar's default fnmatch), <c>?</c> → <c>.</c>, <c>[...]</c> preserved.
    /// </summary>
    private static List<System.Text.RegularExpressions.Regex> BuildExcludeRegexes(List<string> patterns)
    {
        var list = new List<System.Text.RegularExpressions.Regex>();
        foreach (string pat in patterns)
        {
            if (string.IsNullOrEmpty(pat)) { continue; }
            var sb = new StringBuilder("^");
            foreach (char c in pat)
            {
                switch (c)
                {
                    case '*': sb.Append(".*"); break;
                    case '?': sb.Append('.'); break;
                    case '[': sb.Append('['); break;
                    case ']': sb.Append(']'); break;
                    default: sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString())); break;
                }
            }
            sb.Append('$');
            list.Add(new System.Text.RegularExpressions.Regex(sb.ToString()));
        }
        return list;
    }

    /// <summary>
    /// A member is excluded if any compiled pattern matches the full relative
    /// path OR any single path component (so <c>--exclude=node_modules</c>
    /// prunes the whole <c>node_modules/…</c> subtree, matching GNU tar).
    /// </summary>
    private static bool IsExcluded(string relPath, List<System.Text.RegularExpressions.Regex> excludeRegexes)
    {
        if (excludeRegexes.Count == 0) { return false; }
        string norm = relPath.Replace('\\', '/');
        foreach (var rx in excludeRegexes)
        {
            if (rx.IsMatch(norm)) { return true; }
            foreach (string comp in norm.Split('/'))
            {
                if (comp.Length > 0 && rx.IsMatch(comp)) { return true; }
            }
        }
        return false;
    }

    private void DoList(string archiveFile, bool gzipFilter)
    {
        if (!File.Exists(archiveFile))
        {
            FileSystemHelpers.WriteBashError(this, $"tar: {archiveFile}: Cannot open: No such file or directory");
            return;
        }
        bool isGz = gzipFilter
            || archiveFile.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || archiveFile.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

        FileStream? inStream = null;
        Stream? tarStream = null;
        TarReader? reader = null;
        try
        {
            inStream = BashFileSystem.OpenRead(archiveFile);
            tarStream = isGz
                ? (Stream)new GZipStream(inStream, CompressionMode.Decompress)
                : inStream;
            reader = new TarReader(tarStream);

            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) != null)
            {
                string name = entry.Name;
                if (entry.EntryType == TarEntryType.Directory)
                {
                    name = name.TrimEnd('/') + "/";
                }
                string leaf = Path.GetFileName(name.TrimEnd('/'));
                var obj = new PSObject();
                obj.TypeNames.Insert(0, "PsBash.TarListOutput");
                obj.Properties.Add(new PSNoteProperty("BashText", name));
                obj.Properties.Add(new PSNoteProperty("Name", leaf));
                WriteObject(obj);
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"tar: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
        finally
        {
            reader?.Dispose();
            if (isGz) { tarStream?.Dispose(); }
            inStream?.Dispose();
        }
    }
}
