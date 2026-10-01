using System.Formats.Tar;
using System.IO.Compression;
using System.Management.Automation;
using System.Text;
using PsBash.Cmdlets.Args;

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
    private const string OptCreate = "create", OptExtract = "extract", OptList = "list", OptVerbose = "verbose",
        OptGzip = "gzip", OptKeep = "keep", OptAuto = "auto", OptStdout = "stdout", OptFile = "file",
        OptDirectory = "directory", OptExclude = "exclude", OptStrip = "strip", OptNoOp = "noop";

    /// <summary>GNU tar 1.35 long options in argp table order (concatenated per-letter `--X` ambiguity lists
    /// from the oracle, so any abbreviation's candidate list is in GNU order). Every name here that is not
    /// implemented is a valid-but-unsupported option (exit 2): modes <c>-A -d -r -u --delete --test-label</c>,
    /// the bzip2/xz/lzma/lzip/lzop/zstd/compress filters (no managed .NET codec; gzip is the only one
    /// <c>System.IO.Compression</c> ships), <c>-h --dereference</c>, <c>-T -X --files-from --exclude-from</c>,
    /// incremental/multi-volume/sparse/xattr/ownership/transform/checkpoint families, and so on.</summary>
    private static readonly string[] TarLongNamesInGnuOrder =
    {
        "append", "atime-preserve", "acls", "auto-compress", "absolute-names", "after-date", "add-file", "anchored",
        "blocking-factor", "bzip2", "backup", "block-number",
        "create", "compare", "catenate", "concatenate", "check-device", "clamp-mtime", "compress", "checkpoint",
        "checkpoint-action", "check-links", "confirmation",
        "diff", "delete", "delay-directory-restore", "dereference", "directory",
        "extract", "exclude", "exclude-from", "exclude-caches", "exclude-caches-under", "exclude-caches-all",
        "exclude-tag", "exclude-ignore", "exclude-ignore-recursive", "exclude-tag-under", "exclude-tag-all",
        "exclude-vcs", "exclude-vcs-ignores", "exclude-backups",
        "file", "force-local", "format", "full-time", "files-from",
        "get", "group", "group-map", "gzip", "gunzip",
        "hole-detection", "hard-dereference", "help",
        "incremental", "ignore-failed-read", "ignore-command-error", "info-script", "ignore-zeros", "index-file",
        "interactive", "ignore-case",
        "keep-old-files", "keep-newer-files", "keep-directory-symlink",
        "list", "listed-incremental", "level", "label", "lzip", "lzma", "lzop",
        "mtime", "mode", "multi-volume",
        "no-seek", "no-check-device", "no-overwrite-dir", "no-ignore-command-error", "no-same-owner",
        "numeric-owner", "no-same-permissions", "no-delay-directory-restore", "no-xattrs", "no-selinux", "no-acls",
        "new-volume-script", "no-auto-compress", "newer", "newer-mtime", "no-quote-chars", "null", "no-null",
        "no-unquote", "no-verbatim-files-from", "no-recursion", "no-anchored", "no-ignore-case", "no-wildcards",
        "no-wildcards-match-slash",
        "occurrence", "overwrite", "overwrite-dir", "one-top-level", "owner", "owner-map", "old-archive",
        "one-file-system",
        "preserve-permissions", "preserve-order", "portability", "posix", "pax-option", "program-name",
        "quoting-style", "quote-chars",
        "remove-files", "recursive-unlink", "rmt-command", "rsh-command", "record-size", "read-full-records",
        "restrict", "recursion",
        "sparse", "sparse-version", "seek", "skip-old-files", "same-owner", "same-permissions", "same-order", "sort",
        "selinux", "starting-file", "suffix", "strip-components", "show-defaults", "show-snapshot-field-ranges",
        "show-omitted-dirs", "show-transformed-names", "show-stored-names",
        "test-label", "to-stdout", "to-command", "touch", "tape-length", "transform", "totals",
        "update", "unlink-first", "use-compress-program", "ungzip", "uncompress", "utc", "unquote", "usage",
        "verify", "volno-file", "verbose", "verbatim-files-from", "version",
        "warning", "wildcards", "wildcards-match-slash",
        "xattrs", "xattrs-include", "xattrs-exclude", "xz", "xform",
        "zstd",
    };

    private static readonly string[] TarImplementedLongNames =
    {
        "create", "extract", "get", "list", "verbose", "gzip", "gunzip", "ungzip", "keep-old-files", "auto-compress",
        "to-stdout", "touch", "preserve-permissions", "same-permissions", "overwrite", "wildcards", "no-wildcards",
        "no-same-owner", "same-owner", "no-same-permissions", "file", "directory", "exclude", "strip-components",
        "help", "version",
    };

    private static string[] BuildTarValidButUnsupported()
    {
        var implemented = new HashSet<string>(TarImplementedLongNames, StringComparer.Ordinal);
        var list = new List<string>();
        foreach (char c in "AdruGgnSTXUWsFLMbBiHVIjJZhKNPlRw") list.Add("-" + c);
        foreach (var name in TarLongNamesInGnuOrder)
            if (!implemented.Contains(name)) list.Add("--" + name);
        return list.ToArray();
    }

    /// <summary>GNU tar options ps-bash refuses (exit 2): see <see cref="TarLongNamesInGnuOrder"/>.</summary>
    private static readonly string[] TarValidButUnsupported = BuildTarValidButUnsupported();

    /// <summary>
    /// tar's option surface. Implemented: modes <c>-c -x -t</c>; <c>-f FILE</c>, <c>-C DIR</c> (position
    /// matters on create), <c>-v -z -k -a -O</c>, <c>--exclude=PAT</c>, <c>--strip-components=N</c>; accepted
    /// no-ops that match the existing behaviour: <c>-m/--touch -p/--preserve-permissions --same-permissions
    /// --overwrite --wildcards --no-wildcards --no-same-owner --same-owner --no-same-permissions</c>.
    /// Usage errors exit 64 (EX_USAGE, GNU tar); semantic errors (no/two modes, empty archive) exit 2.
    /// </summary>
    private static readonly OptSpecSet TarSpec = new(
        new[]
        {
            new OptSpec(OptCreate, 'c', "create"),
            new OptSpec(OptExtract, 'x', "extract"),
            new OptSpec(OptExtract, '\0', "get"),
            new OptSpec(OptList, 't', "list"),
            new OptSpec(OptVerbose, 'v', "verbose"),
            new OptSpec(OptGzip, 'z', "gzip"),
            new OptSpec(OptGzip, '\0', "gunzip"),
            new OptSpec(OptGzip, '\0', "ungzip"),
            new OptSpec(OptKeep, 'k', "keep-old-files"),
            new OptSpec(OptAuto, 'a', "auto-compress"),
            new OptSpec(OptStdout, 'O', "to-stdout"),
            new OptSpec(OptFile, 'f', "file", OptKind.Value),
            new OptSpec(OptDirectory, 'C', "directory", OptKind.Value),
            new OptSpec(OptExclude, '\0', "exclude", OptKind.Value),
            new OptSpec(OptStrip, '\0', "strip-components", OptKind.Value),
            // Accepted no-ops. Each GNU option keeps its OWN id (never a shared catch-all): ids that
            // share a long name set count as ONE option for abbreviation, but GNU tar reports
            // --no-same / --no-sa as ambiguous (--no-same-owner vs --no-same-permissions).
            new OptSpec(OptNoOp + ":touch", 'm', "touch"),
            new OptSpec(OptNoOp + ":preserve-permissions", 'p', "preserve-permissions"),
            new OptSpec(OptNoOp + ":preserve-permissions", '\0', "same-permissions"), // GNU alias of -p
            new OptSpec(OptNoOp + ":overwrite", '\0', "overwrite"),
            new OptSpec(OptNoOp + ":wildcards", '\0', "wildcards"),
            new OptSpec(OptNoOp + ":no-wildcards", '\0', "no-wildcards"),
            new OptSpec(OptNoOp + ":no-same-owner", '\0', "no-same-owner"),
            new OptSpec(OptNoOp + ":same-owner", '\0', "same-owner"),
            new OptSpec(OptNoOp + ":no-same-permissions", '\0', "no-same-permissions"),
        },
        TarValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        usageExitCode: 64,
        longOptionOrder: TarLongNamesInGnuOrder);

    /// <summary>Pure argv scan (unit-test seam); applies the old-style first-word rule first.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(NormalizeOldStyle(args), TarSpec);

    /// <summary>
    /// GNU tar's old-style option word: a FIRST argument that does not start with a dash is a bundle of
    /// option letters without the dash (<c>tar czf a.tgz dir</c>, <c>tar xvf a.tar</c>); the options that take a
    /// value (<c>b C f F g H I K L N T V X</c>) consume the FOLLOWING arguments in order
    /// (<c>tar cf a.tar -C d x</c>). Rewritten to ordinary dashed options; idempotent.
    /// </summary>
    internal static string[] NormalizeOldStyle(string[] args)
    {
        if (args.Length == 0 || args[0].Length == 0 || args[0][0] == '-') return args;
        var result = new List<string>(args.Length + 4);
        int next = 1;
        foreach (char ch in args[0])
        {
            result.Add("-" + ch);
            if ("bCfFgHIKLNTVX".IndexOf(ch) >= 0 && next < args.Length) result.Add(args[next++]);
        }
        for (; next < args.Length; next++) result.Add(args[next]);
        return result.ToArray();
    }

    internal sealed class TarArgs
    {
        public ParsedArgs Parsed = null!;
        public string? Mode;                // OptCreate / OptExtract / OptList
        public bool Verbose, Gzip, Keep, AutoCompress, ToStdout;
        public string? ArchiveFile;
        /// <summary>Accumulated -C/--directory (extract/list destination).</summary>
        public string? ChangeDir;
        public List<string> Excludes = new();
        public int StripComponents;
        /// <summary>Operands with the -C directory in effect where each appeared (create: -C is positional).</summary>
        public List<(string? Dir, string Path)> Sources = new();
        public List<string> Operands = new();
        public string? Error;
        public int ErrorExit = 2;
    }

    /// <summary>
    /// Scan + validate. Fixes over the old hand scan: usage errors exit 64 (they were 1/2) and unknown
    /// long options / a dangling -f were silently ignored or operands, abbreviations (<c>--dir</c>,
    /// <c>--strip=1</c>) work, two modes (<c>-cx</c>) and no mode are GNU's exit-2 errors, a bad
    /// <c>--strip-components</c> is "Invalid number of elements" (was 0), <c>-C</c> works on create and is
    /// positional (<c>tar cf a.tar -C d f</c>), old-style words (<c>tar czf ...</c>) parse.
    /// </summary>
    internal static TarArgs Plan(string[] args)
    {
        var p = new TarArgs { Parsed = ScanArgs(args) };
        p.Operands = p.Parsed.Operands();
        if (p.Parsed.HasError) return p;
        if (p.Parsed.Has(OptSpecSet.HelpId) || p.Parsed.Has(OptSpecSet.VersionId)) return p;

        var modes = new List<string>();
        string? dir = null;
        foreach (var tok in p.Parsed.Tokens)
        {
            if (tok.Kind == ArgTokKind.Operand) { p.Sources.Add((dir, tok.Raw)); continue; }
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptCreate or OptExtract or OptList:
                    if (!modes.Contains(tok.OptId!)) modes.Add(tok.OptId!);
                    break;
                case OptVerbose: p.Verbose = true; break;
                case OptGzip: p.Gzip = true; break;
                case OptKeep: p.Keep = true; break;
                case OptAuto: p.AutoCompress = true; break;
                case OptStdout: p.ToStdout = true; break;
                case OptFile: p.ArchiveFile = tok.Value; break;
                case OptExclude: p.Excludes.Add(tok.Value!); break;
                case OptDirectory:
                    dir = dir is null || Path.IsPathRooted(tok.Value!) ? tok.Value : Path.Combine(dir, tok.Value!);
                    break;
                case OptStrip:
                    {
                        string v = tok.Value!;
                        bool digits = v.Length > 0;
                        foreach (char ch in v) if (ch < '0' || ch > '9') { digits = false; break; }
                        if (!digits) { p.Error = $"tar: {v}: Invalid number of elements"; return p; }
                        p.StripComponents = int.TryParse(v, out int n) ? n : int.MaxValue;
                        break;
                    }
            }
        }
        p.ChangeDir = dir;

        if (modes.Count > 1)
        {
            p.Error = "tar: You may not specify more than one '-Acdtrux', '--delete' or  '--test-label' option";
            return p;
        }
        if (modes.Count == 0)
        {
            p.Error = "tar: You must specify one of the '-Acdtrux', '--delete' or '--test-label' options";
            return p;
        }
        p.Mode = modes[0];
        return p;
    }
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Decoy for <c>-c</c> (create): the bare token prefix-collides with <c>-Confirm</c>, and the
    /// case-insensitive binder also routes <c>-C</c> (change-dir) here in DIRECT calls. <see cref="ArgsWithDecoys"/>
    /// tells them apart; the transpiler single-quotes every flag so it never binds.</summary>
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

    /// <summary>
    /// Re-injects decoy-bound flags before the scan. <c>-v</c> and <c>-f FILE</c> are position-free. The
    /// <c>C</c> switch is either <c>-c</c> or <c>-C DIR</c> (the binder cannot tell): with no mode on the line
    /// it is <c>-c</c>; with a mode already given it is <c>-C</c> and its DIR is the first operand
    /// (`Invoke-BashTar -xf a.tar -C out` leaves <c>out</c> as the first operand).
    /// </summary>
    private string[] ArgsWithDecoys()
    {
        // A bound decoy means the original first argument was a dashed option the binder consumed, so what is
        // left in Arguments is NOT an old-style word (`Invoke-BashTar -c -f a.tar src` leaves `src` first).
        var raw = Arguments ?? Array.Empty<string>();
        if (!V.IsPresent && F is null && !C.IsPresent) raw = NormalizeOldStyle(raw);
        var pre = new List<string>();
        if (V.IsPresent) pre.Add("-v");
        if (F is not null) { pre.Add("-f"); pre.Add(F); }
        var rest = raw;
        if (C.IsPresent)
        {
            var probe = ArgParser.Parse(raw, TarSpec);
            bool hasMode = probe.Has(OptCreate) || probe.Has(OptExtract) || probe.Has(OptList);
            int operandIndex = -1;
            foreach (var t in probe.Tokens)
            {
                if (t.Kind == ArgTokKind.Operand) { operandIndex = t.ArgIndex; break; }
            }
            if (hasMode && operandIndex >= 0)
            {
                pre.Add("-C");
                pre.Add(raw[operandIndex]);
                rest = raw.Where((_, i) => i != operandIndex).ToArray();
            }
            else
            {
                pre.Add("-c");
            }
        }
        return pre.Count == 0 ? rest : pre.Concat(rest).ToArray();
    }

    protected override void EndProcessing()
    {
        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "tar", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "tar", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, plan.ErrorExit);
            return;
        }

        string? archiveFile = plan.ArchiveFile;
        string? changeDir = plan.ChangeDir;
        bool gzipFilter = plan.Gzip;

        if (!string.IsNullOrEmpty(archiveFile))
        {
            archiveFile = SessionState.Path.GetUnresolvedProviderPathFromPSPath(archiveFile);
        }
        if (!string.IsNullOrEmpty(changeDir))
        {
            changeDir = SessionState.Path.GetUnresolvedProviderPathFromPSPath(changeDir);
        }

        if (string.IsNullOrEmpty(archiveFile))
        {
            FileSystemHelpers.WriteBashError(this, "tar: you must specify -f archive");
            return;
        }

        // -a/--auto-compress: pick the filter from the archive extension. Only
        // gzip is available; a .bz2/.xz/.zst extension is the same .NET-codec gap.
        if (plan.AutoCompress)
        {
            if (archiveFile!.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                || archiveFile.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                gzipFilter = true;
            }
            else if (archiveFile.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase)
                     || archiveFile.EndsWith(".xz", StringComparison.OrdinalIgnoreCase)
                     || archiveFile.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)
                     || archiveFile.EndsWith(".Z", StringComparison.Ordinal))
            {
                FileSystemHelpers.WriteBashError(this,
                    "tar: auto-compress: this archive's compression format is recognized but not supported by ps-bash");
                FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
        }

        switch (plan.Mode)
        {
            case OptCreate:
                DoCreate(archiveFile!, plan.Sources, gzipFilter, plan.Verbose, plan.Excludes);
                break;
            case OptExtract:
                DoExtract(archiveFile!, gzipFilter, plan.Verbose, changeDir, plan.StripComponents, plan.ToStdout, plan.Keep);
                break;
            default:
                DoList(archiveFile!, gzipFilter);
                break;
        }
    }
    private void DoCreate(string archiveFile, List<(string? Dir, string Path)> sources, bool gzipFilter, bool verbose, List<string> excludePatterns)
    {
        if (sources.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "tar: Cowardly refusing to create an empty archive");
            FileSystemHelpers.SetLastExitCode(this, 2);
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

            foreach (var (srcDir, src) in sources)
            {
                string resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(src);
                if (!string.IsNullOrEmpty(srcDir) && !Path.IsPathRooted(src))
                {
                    // -C DIR before this operand: members are taken relative to DIR (GNU tar).
                    string dirResolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(srcDir);
                    resolved = Path.GetFullPath(Path.Combine(dirResolved, src));
                }
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
                    // Reparse-point-safe walk: a directory junction / symlink is archived as its
                    // own entry but never descended into, so tar -c can't pack the link TARGET's
                    // contents (an escape out of the source tree) or loop on a cyclic link.
                    writer.WriteEntry(resolved, root);
                    if (verbose) { WriteObject(BashRuntime.NewBashObject(root)); }
                    foreach (var childInfo in FileSystemHelpers.EnumerateNoFollow(new DirectoryInfo(resolved)))
                    {
                        string child = childInfo.FullName;
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
        int stripComponents = 0, bool toStdout = false, bool keepOldFiles = false)
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
                    using var sr = BashFileSystem.OpenRawReader(entry.DataStream, leaveOpen: true);
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
                // -k/--keep-old-files: never overwrite an existing file.
                if (keepOldFiles && File.Exists(targetPath))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"tar: {name}: Cannot open: File exists");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    continue;
                }
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
