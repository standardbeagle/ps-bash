using System.Globalization;
using System.Management.Automation;
using System.Text;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashLs</c> function
/// (REFACTOR-2 Phase 1d — the final leaf of REFACTOR-2 Phase 1). Lists
/// directory contents / file entries, matching the bash <c>ls</c> command, and
/// emits typed <c>PsBash.LsEntry</c> PSObjects whose <c>BashText</c> is the
/// display line (the <c>PsBash.LsEntry</c> ps1xml view renders <c>BashText</c>).
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashLs</c> and its
/// helper web (<c>Get-LsEntryFromFsi</c>, <c>ConvertTo-PermissionString</c>,
/// <c>Format-BashSize</c>, <c>Format-BashDate</c>, <c>Format-LsLine</c>,
/// <c>Test-IsExecutable</c>). Those pure helpers are reimplemented here in C#.
///
/// Three-tier strategy, reproduced exactly from the psm1 oracle:
/// <list type="number">
/// <item><b>Tier 1 — custom providers.</b> The psm1 keeps a module-scoped
/// <c>$script:BashLsProviders</c> registry of user scriptblocks. A binary
/// cmdlet cannot reach <c>$script:</c>-scoped psm1 state, so Tier 1 (and
/// Tier 3) are delegated to the thin psm1 shim <c>Get-BashLsProviderEntries</c>
/// via a string-bodied <see cref="CommandInvocationIntrinsics.InvokeScript(string,object[])"/>
/// call — no ScriptBlock construction (AOT-safe). The shim returns raw
/// unsorted/unformatted <c>PsBash.LsEntry</c> objects; this cmdlet owns the
/// uniform sort + format pass for every tier.</item>
/// <item><b>Tier 2 — real filesystem.</b> The hot path: <see cref="System.IO"/>
/// streaming (no <c>Get-ChildItem</c>, no <c>Get-Acl</c>), fully reimplemented
/// in C# here. <c>-R</c> recurses depth first, one "dir:" section per directory (symlinks not followed).</item>
/// <item><b>Tier 3 — PS provider fallback.</b> Registry:, Cert:, custom
/// PSDrives — also delegated to the <c>Get-BashLsProviderEntries</c> shim,
/// which calls <c>Get-Item</c> / <c>Get-ChildItem</c> and
/// <c>Get-LsEntryFromPsItem</c>.</item>
/// </list>
///
/// Options are parsed by the shared ordered parser (<see cref="LsSpec"/>; see
/// <c>runtime-functions.md</c> "Shared argument parser"): ls is on
/// <c>PsEmitter.OrderedArgCommands</c>, so every dash word reaches <see cref="Arguments"/>
/// verbatim. Last-wins conflicts (<c>-tS</c> vs <c>-St</c>, <c>-pF</c>, <c>--color</c> forms) are
/// resolved by walking the tokens in order. A DIRECT PowerShell call still hits the binder, so the
/// colliding <c>-a</c>/<c>-A</c>, <c>-d</c>, <c>-p</c>, <c>-i</c> keep decoy switches
/// (<see cref="A"/>, <see cref="D"/>, <see cref="P"/>, <see cref="I"/>) that are re-injected.
////// The <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c>; a
/// not-found / unreadable target delegates to the psm1 <c>Write-BashError</c>
/// (<c>-ExitCode 2</c>, matching the oracle) — both via string-bodied
/// <c>InvokeCommand.InvokeScript</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashLs")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashLsCommand : PSCmdlet
{
    /// <summary>
    /// The bash <c>-a</c> / <c>-A</c> (show hidden) switch — declared
    /// explicitly because the bare tokens <c>-a</c> and <c>-A</c> prefix-match
    /// this cmdlet's own <c>-Arguments</c> parameter and would otherwise bind
    /// the next operand as the argument array. PowerShell parameter names are
    /// case-insensitive, so a single switch binds both <c>-a</c> and <c>-A</c>;
    /// that is behaviorally complete here because <see cref="System.IO"/>
    /// directory enumeration never yields <c>.</c> / <c>..</c>, so the psm1
    /// oracle's <c>-A</c>-excludes-dot-dirs distinction is a filesystem-path
    /// no-op. See the class remarks.
    /// </summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    /// <summary>
    /// The bash <c>-d</c> (list directories themselves) switch — declared
    /// explicitly because the bare token <c>-d</c> prefix-collides with the
    /// <c>-Debug</c> common parameter. An exact parameter-name match beats a
    /// common-parameter prefix match.
    /// </summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>
    /// The bash <c>-p</c> (append <c>/</c> to directories) switch — declared
    /// explicitly because the bare token <c>-p</c> prefix-collides with the
    /// <c>-ProgressAction</c> / <c>-PipelineVariable</c> common parameters.
    /// </summary>
    [Parameter]
    public SwitchParameter P { get; set; }

    /// <summary>
    /// The bash <c>-i</c> (inode) switch — a bare <c>-i</c> prefix-collides with
    /// <c>-InformationAction</c> / <c>-InformationVariable</c> and crashes the binder. Re-injected as <c>-i</c>.
    /// </summary>
    [Parameter]
    public SwitchParameter I { get; set; }

    /// <summary>
    /// The bash <c>-w COLS</c> (line width) value option — a bare <c>-w</c> is ambiguous between the
    /// <c>-WarningAction</c> / <c>-WarningVariable</c> common parameters and crashes the binder. Re-injected as <c>-w COLS</c>.
    /// </summary>
    [Parameter]
    public string? W { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static readonly string[] ExecExtensions =
        { ".exe", ".bat", ".cmd", ".ps1", ".sh", ".com" };

    // ---- option table (GNU coreutils 9.4 ls; oracle-checked) --------------------------------

    private const string OptAll = "all", OptAlmostAll = "almost-all", OptLong = "long", OptHuman = "human",
        OptRecursive = "recursive", OptSortSize = "sort-size", OptSortTime = "sort-time", OptReverse = "reverse",
        OptOnePerLine = "one", OptSlash = "slash", OptDirectory = "directory", OptClassifyShort = "classify-short",
        OptClassify = "classify", OptColor = "color", OptInode = "inode", OptBlocks = "blocks",
        OptGroupDirsFirst = "group-dirs-first", OptSort = "sort",
        OptVertical = "vertical", OptAcross = "across", OptCommas = "commas", OptFormat = "format",
        OptWidth = "width", OptTabsize = "tabsize";

    /// <summary>
    /// GNU options ps-bash refuses (exit 2): each changes the OUTPUT (columns, quoting, sort key,
    /// time format, owner columns...) in a way this cmdlet does not reproduce, so accepting and
    /// ignoring them would be a silent wrong answer. <c>-w</c> / <c>-T</c> / <c>-I</c> take a value
    /// but are refused before it is read. A <c>string[]</c> field so the collision guard sees them.
    /// </summary>
    private static readonly string[] LsUnsupported =
    {
        "-b", "-c", "-f", "-g", "-k", "-n", "-o", "-q", "-u", "-v",
        "-B", "-D", "-G", "-H", "-I", "-L", "-N", "-Q", "-U", "-X", "-Z",
        "--author", "--escape", "--block-size", "--ignore-backups", "--dired", "--file-type",
        "--full-time", "--no-group", "--si", "--dereference-command-line",
        "--dereference-command-line-symlink-to-dir", "--hide", "--hyperlink", "--indicator-style",
        "--ignore", "--kibibytes", "--dereference", "--numeric-uid-gid", "--literal",
        "--hide-control-chars", "--show-control-chars", "--quote-name", "--quoting-style", "--time",
        "--time-style", "--context", "--zero",
    };

    /// <summary>GNU's long_options[] order (what an ambiguous abbreviation lists), read from the oracle.</summary>
    private static readonly string[] LsLongOptionOrder =
    {
        "all", "almost-all", "author", "escape", "block-size", "ignore-backups", "classify", "color", "context",
        "directory", "dired", "dereference-command-line", "dereference-command-line-symlink-to-dir", "dereference",
        "full-time", "file-type", "format", "group-directories-first", "human-readable", "hide-control-chars",
        "hide", "hyperlink", "help", "inode", "ignore", "indicator-style", "kibibytes",
        "literal", "numeric-uid-gid", "no-group", "quote-name", "quoting-style", "reverse", "recursive",
        "size", "si", "show-control-chars", "sort", "tabsize", "time", "time-style", "width", "zero", "version",
    };

    private static readonly OptSpecSet LsSpec = new(
        new[]
        {
            new OptSpec(OptAll, 'a', "all"),
            new OptSpec(OptAlmostAll, 'A', "almost-all"),
            new OptSpec(OptLong, 'l', null),
            new OptSpec(OptHuman, 'h', "human-readable"),
            new OptSpec(OptRecursive, 'R', "recursive"),
            new OptSpec(OptSortSize, 'S', null),
            new OptSpec(OptSortTime, 't', null),
            new OptSpec(OptReverse, 'r', "reverse"),
            new OptSpec(OptOnePerLine, '1', null),
            new OptSpec(OptSlash, 'p', null),
            new OptSpec(OptDirectory, 'd', "directory"),
            new OptSpec(OptClassifyShort, 'F', null),
            new OptSpec(OptClassify, '\0', "classify", OptKind.OptionalValue),
            new OptSpec(OptColor, '\0', "color", OptKind.OptionalValue),
            new OptSpec(OptInode, 'i', "inode"),
            new OptSpec(OptBlocks, 's', "size"),
            new OptSpec(OptGroupDirsFirst, '\0', "group-directories-first"),
            new OptSpec(OptSort, '\0', "sort", OptKind.Value),
            new OptSpec(OptVertical, 'C', null),
            new OptSpec(OptAcross, 'x', null),
            new OptSpec(OptCommas, 'm', null),
            new OptSpec(OptFormat, '\0', "format", OptKind.Value),
            new OptSpec(OptWidth, 'w', "width", OptKind.Value),
            new OptSpec(OptTabsize, 'T', "tabsize", OptKind.Value),
        },
        LsUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        usageExitCode: 2,
        longOptionOrder: LsLongOptionOrder);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, LsSpec);

    private const string WhenValidBlock =
        "  - 'always', 'yes', 'force'\n  - 'never', 'no', 'none'\n  - 'auto', 'tty', 'if-tty'";

    private static readonly (string, int)[] WhenTable =
    {
        ("always", 1), ("yes", 1), ("force", 1), ("never", 0), ("no", 0), ("none", 0),
        ("auto", 2), ("tty", 2), ("if-tty", 2),
    };

    internal enum LsFormat { OnePerLine, Long, Vertical, Across, Commas }

    private const string FormatValidBlock =
        "  - 'verbose', 'long'\n  - 'commas'\n  - 'horizontal', 'across'\n  - 'vertical'\n  - 'single-column'";

    private static readonly (string, LsFormat)[] FormatTable =
    {
        ("verbose", LsFormat.Long), ("long", LsFormat.Long), ("commas", LsFormat.Commas),
        ("horizontal", LsFormat.Across), ("across", LsFormat.Across), ("vertical", LsFormat.Vertical),
        ("single-column", LsFormat.OnePerLine),
    };

    /// <summary>A GNU <c>-w</c>/<c>-T</c> operand: plain decimal digits (0 allowed), nothing else.</summary>
    private static bool TryParseCount(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text) if (c is < '0' or > '9') return false;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            value = long.MaxValue;    // beyond long: GNU saturates to "no practical limit"
        return true;
    }

    /// <summary>
    /// The line length when no <c>-w</c>: <c>$COLUMNS</c> when it is a number (0 = unlimited), else 80 (output
    /// here is never a terminal, so GNU's ioctl is not consulted). An invalid value is warned about and ignored.
    /// </summary>
    private long EnvLineWidth()
    {
        string? raw = Environment.GetEnvironmentVariable("COLUMNS");
        if (string.IsNullOrEmpty(raw)) return 80;
        if (TryParseCount(raw, out long v)) return v;
        FileSystemHelpers.WriteBashError(this,
            $"ls: ignoring invalid width in environment variable COLUMNS: '{raw}'");
        return 80;
    }

    /// <summary>The tab size when no <c>-T</c>: <c>$TABSIZE</c> when numeric, else 8.</summary>
    private int EnvTabSize()
    {
        string? raw = Environment.GetEnvironmentVariable("TABSIZE");
        if (string.IsNullOrEmpty(raw)) return 8;
        if (TryParseCount(raw, out long v)) return (int)Math.Min(v, int.MaxValue);
        FileSystemHelpers.WriteBashError(this,
            $"ls: ignoring invalid tab size in environment variable TABSIZE: '{raw}'");
        return 8;
    }

    private static readonly (string, int)[] SortTable =
    {
        ("none", 0), ("time", 1), ("size", 2), ("extension", 3), ("version", 4), ("width", 5),
    };

    protected override void EndProcessing()
    {
        // A decoy-bound flag (direct PowerShell call: `Invoke-BashLs -a`, `-d`, `-p`, `-i`) is
        // re-injected ahead of everything; the transpiler single-quotes every dash word for ls
        // (PsEmitter.OrderedArgCommands) so they arrive in Arguments, in order.
        var args = BashRuntime.PrependDecoys(Arguments, (A.IsPresent, "-a"), (D.IsPresent, "-d"),
            (P.IsPresent, "-p"), (I.IsPresent, "-i"));
        if (W is not null) args = new[] { "-w", W }.Concat(args).ToArray();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "ls", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "ls"))
            {
                WriteObject(line);
            }
            return;
        }

        // ONE scan: bundles, last-wins conflicts, abbreviations and GNU's exit statuses (usage
        // error 2, invalid WHEN / --sort word 1). Unknown dash words used to fall through as
        // "operands" and surface as `ls: cannot access '-x'`.
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "ls", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "ls", parsed)) return;

        // Sort key, indicator and colour are "last option wins" in GNU, so walk the tokens in order.
        int sortKey = 0; // 0 = name, 1 = time, 2 = size
        int indicatorStyle = 0; // 0 = none, 1 = slash (-p), 2 = classify (-F)
        bool colorOn = false;
        // Output format is "last option wins" too, except that -1 never cancels -l (GNU ls.c).
        LsFormat format = LsFormat.OnePerLine;
        long? lineWidth = null;     // -w / --width (0 = unlimited)
        int? tabSizeOpt = null;     // -T / --tabsize
        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            switch (t.OptId)
            {
                case OptLong: format = LsFormat.Long; break;
                case OptOnePerLine: if (format != LsFormat.Long) format = LsFormat.OnePerLine; break;
                case OptVertical: format = LsFormat.Vertical; break;
                case OptAcross: format = LsFormat.Across; break;
                case OptCommas: format = LsFormat.Commas; break;
                case OptFormat:
                    if (!GnuArgMatch.TryMatch("ls", "format", t.Value ?? "", FormatTable, FormatValidBlock,
                            out LsFormat fv, out var ferr))
                    {
                        FileSystemHelpers.WriteBashError(this, ferr!);
                        FileSystemHelpers.SetLastExitCode(this, 1);
                        return;
                    }
                    format = fv;
                    break;
                case OptWidth:
                    if (!TryParseCount(t.Value, out long wv))
                    {
                        WriteBashError($"ls: invalid line width: '{t.Value}'", 2);
                        return;
                    }
                    lineWidth = wv;
                    break;
                case OptTabsize:
                    if (!TryParseCount(t.Value, out long tv))
                    {
                        WriteBashError($"ls: invalid tab size: '{t.Value}'", 2);
                        return;
                    }
                    tabSizeOpt = (int)Math.Min(tv, int.MaxValue);
                    break;
                case OptSortSize: sortKey = 2; break;
                case OptSortTime: sortKey = 1; break;
                case OptSlash: indicatorStyle = 1; break;
                case OptClassifyShort: indicatorStyle = 2; break;
                case OptClassify:
                    if (t.Value is null) { indicatorStyle = 2; break; }
                    if (!GnuArgMatch.TryMatch("ls", "classify", t.Value, WhenTable, WhenValidBlock, out int cw, out var cerr))
                    {
                        FileSystemHelpers.WriteBashError(this, cerr!);
                        FileSystemHelpers.SetLastExitCode(this, 1);
                        return;
                    }
                    indicatorStyle = cw == 1 ? 2 : 0; // auto = "only on a terminal": output here is never one
                    break;
                case OptColor:
                    if (t.Value is null) { colorOn = true; break; }
                    if (!GnuArgMatch.TryMatch("ls", "color", t.Value, WhenTable, WhenValidBlock, out int w, out var werr))
                    {
                        FileSystemHelpers.WriteBashError(this, werr!);
                        FileSystemHelpers.SetLastExitCode(this, 1);
                        return;
                    }
                    colorOn = w == 1;
                    break;
                case OptSort:
                    if (!GnuArgMatch.TryMatch("ls", "sort", t.Value ?? "", SortTable,
                            "  - 'none'\n  - 'time'\n  - 'size'\n  - 'extension'\n  - 'version'\n  - 'width'",
                            out int sw, out var serr))
                    {
                        FileSystemHelpers.WriteBashError(this, serr!);
                        FileSystemHelpers.SetLastExitCode(this, 1);
                        return;
                    }
                    if (sw is 1 or 2) { sortKey = sw; break; }
                    FileSystemHelpers.WriteBashError(this,
                        $"ls: option '--sort={t.Value}' is recognized but not supported by ps-bash");
                    FileSystemHelpers.SetLastExitCode(this, ArgError.UnsupportedExitCode);
                    return;
            }
        }

        bool longMode = format == LsFormat.Long;
        bool columnar = format is LsFormat.Vertical or LsFormat.Across or LsFormat.Commas;
        long lineLength = 80;
        int tabSize = 8;
        if (columnar)
        {
            lineLength = lineWidth ?? EnvLineWidth();
            tabSize = tabSizeOpt ?? EnvTabSize();
        }
        bool showAll = parsed.Has(OptAll);          // -a: also "." and ".."
        bool showHidden = showAll || parsed.Has(OptAlmostAll);
        bool humanSizes = parsed.Has(OptHuman);
        bool recursive = parsed.Has(OptRecursive);
        bool sortBySize = sortKey == 2;
        bool sortByTime = sortKey == 1;
        bool reverseSort = parsed.Has(OptReverse);
        bool dirOnly = parsed.Has(OptDirectory);
        bool classifyF = indicatorStyle == 2;
        bool classifyP = indicatorStyle == 1;
        bool groupDirsFirst = parsed.Has(OptGroupDirsFirst);
        bool showInode = parsed.Has(OptInode);
        bool showBlocks = parsed.Has(OptBlocks);
        // -1 changes nothing in this one-entry-per-line listing.

        bool classify = classifyF || classifyP || longMode;
        bool colorize = colorOn;
        var operandList = parsed.Operands();
        var operands = operandList.Count > 0
            ? operandList
            : new List<string> { "." };
        var targets = ResolveGlob(operands);

        // GNU layout: the non-directory operands (and -d operands) first as one block, then every
        // directory operand as its own section. A section gets a "dir:" header when more than one
        // operand was given or -R is on, sections are separated by a blank line, and -l / -s
        // sections start with a "total N" line.
        var fileBlock = new List<PSObject>();
        var dirTargets = new List<(string Path, string Display, DateTime Mtime)>();
        bool hadError = false;

        foreach (var (target, typedOperand) in targets)
        {
            string? resolvedPath = null;
            try
            {
                resolvedPath = Path.GetFullPath(target);
            }
            catch
            {
                // Not a valid filesystem path — fall through to the provider
                // shim (Tier 1 / Tier 3).
            }

            // Tier 2: real filesystem — System.IO streaming.
            if (resolvedPath != null && Directory.Exists(resolvedPath))
            {
                if (dirOnly)
                {
                    fileBlock.Add(WithOperandDisplayName(BuildEntryFromFsi(new DirectoryInfo(resolvedPath)), typedOperand));
                }
                else
                {
                    dirTargets.Add((resolvedPath, typedOperand ?? RelativeDisplay(resolvedPath),
                        Directory.GetLastWriteTime(resolvedPath)));
                }
                continue;
            }

            if (resolvedPath != null && File.Exists(resolvedPath))
            {
                fileBlock.Add(WithOperandDisplayName(BuildEntryFromFsi(new FileInfo(resolvedPath)), typedOperand));
                continue;
            }

            // Tier 1 + Tier 3: custom providers and PS-provider fallback. The
            // psm1 shim owns the $script:BashLsProviders registry and the
            // Get-Item / Get-ChildItem path; it returns raw LsEntry objects (or
            // nothing, having already emitted the bash-style "cannot access"
            // error and set $global:LASTEXITCODE = 2).
            // Run the psm1 shim with an inner 2>&1 so any Write-BashError
            // ErrorRecord lands in the script's success stream rather than
            // being buried in the sub-pipeline (invisible to the cmdlet's
            // own callers and 2>&1 redirects).
            var shimResults = InvokeCommand.InvokeScript(
                "param($t,$hidden,$rec,$d) " +
                "Get-BashLsProviderEntries -Target $t -ShowHidden:$hidden " +
                "-Recursive:$rec -DirOnly:$d 2>&1",
                target,
                showHidden,
                recursive,
                dirOnly);

            bool shimFailed = false;
            foreach (var item in shimResults)
            {
                if (item == null)
                {
                    continue;
                }
                // Inner ErrorRecord (from psm1 Write-BashError) — re-emit via
                // the EAP-override-safe WriteBashError so the outer cmdlet's
                // error stream carries it.
                var baseObj = (item is PSObject po2) ? po2.BaseObject : item;
                if (baseObj is ErrorRecord)
                {
                    // Not found anywhere. Name the operand as typed (GNU: `ls: cannot access
                    // 'nosuch'`), through THIS cmdlet's error stream so `2>/dev/null` discards it.
                    FileSystemHelpers.WriteBashError(this,
                        $"ls: cannot access '{typedOperand ?? target}': No such file or directory");
                    shimFailed = true;
                    continue;
                }
                fileBlock.Add(item as PSObject ?? PSObject.AsPSObject(item));
            }

            // An empty provider container is not an error; only a not-found target is.
            if (shimFailed) hadError = true;
        }

        // Sort — bash default is case-insensitive alphabetical with dirs and
        // files interleaved; -S sorts by size, -t by mtime (ties by name); -r reverses.
        List<PSObject> Sort(List<PSObject> entries)
        {
            IEnumerable<PSObject> sorted;
            if (sortBySize)
            {
                sorted = entries.OrderByDescending(e => GetLong(e, "SizeBytes"))
                    .ThenBy(e => GetDisplayName(e), StringComparer.OrdinalIgnoreCase);
                if (reverseSort) sorted = sorted.Reverse();
            }
            else if (sortByTime)
            {
                sorted = entries.OrderByDescending(e => GetDate(e, "LastModified"))
                    .ThenBy(e => GetDisplayName(e), StringComparer.OrdinalIgnoreCase);
                if (reverseSort) sorted = sorted.Reverse();
            }
            else
            {
                var byName = entries.OrderBy(e => GetDisplayName(e), StringComparer.OrdinalIgnoreCase);
                sorted = reverseSort ? byName.Reverse() : byName;
            }

            // --group-directories-first: stable re-order so directories precede files
            // (LINQ OrderBy is stable, preserving the within-group sort above).
            if (groupDirsFirst)
            {
                sorted = sorted.OrderByDescending(e => GetBool(e, "IsDirectory"));
            }
            return sorted.ToList();
        }

        // Format and emit.
        const string reset = "[0m";
        const string bold = "[1m";
        const string blue = "[34m";
        const string cyan = "[36m";
        const string green = "[32m";

        bool anyBlockWritten = false;

        // One block of already-sorted entries: optional header, optional "total N", the entries.
        void EmitBlock(string? header, List<PSObject> sorted, bool withTotal)
        {
            if (anyBlockWritten) WriteObject(BashRuntime.TextRecord(string.Empty, false));
            anyBlockWritten = true;
            if (header is not null) WriteObject(BashRuntime.TextRecord(header + ":", false));

            var blocks = showBlocks || (withTotal && longMode)
                ? sorted.Select(BlocksK).ToList()
                : null;
            if (withTotal && (longMode || showBlocks) && blocks is not null)
            {
                long sumK = 0;
                foreach (var b in blocks) sumK += b;
                string totalText = humanSizes
                    ? FormatBashSize(sumK * 1024)
                    : sumK.ToString(CultureInfo.InvariantCulture);
                WriteObject(BashRuntime.TextRecord("total " + totalText, false));
            }

            // `-l` columns are aligned to the widest entry of THIS block (LsLongFormat), so they are laid
            // out once for the whole block before the per-entry prefix/indicator work below.
            string[]? longLines = null;
            if (longMode)
            {
                var rows = new List<LsLongRow>(sorted.Count);
                foreach (var e in sorted) rows.Add(LongRow(e, humanSizes));
                longLines = LsLongFormat.Align(rows);
            }

            string[]? inodes = showInode ? sorted.Select(InodeText).ToArray() : null;
            string[]? blockText = showBlocks && blocks is not null
                ? blocks.Select(b => humanSizes
                    ? FormatBashSize(b * 1024)
                    : b.ToString(CultureInfo.InvariantCulture)).ToArray()
                : null;
            int inodeWidth = inodes is null ? 0 : inodes.Max(s => s.Length);
            int blockWidth = blockText is null ? 0 : blockText.Max(s => s.Length);

            var cells = columnar ? new List<LsCell>(sorted.Count) : null;

            for (int idx = 0; idx < sorted.Count; idx++)
            {
                var entry = sorted[idx];
                bool isDir = GetBool(entry, "IsDirectory");
                bool isSymlink = GetBool(entry, "IsSymlink");

                string indicator = string.Empty;
                if (classifyF)
                {
                    if (isDir)
                    {
                        indicator = "/";
                    }
                    else if (isSymlink)
                    {
                        indicator = "@";
                    }
                    else if (IsExecutable(entry))
                    {
                        indicator = "*";
                    }
                }
                else if (classifyP)
                {
                    if (isDir)
                    {
                        indicator = "/";
                    }
                }

                string prefix = string.Empty;
                // -m prints the inode / block columns unpadded (GNU: width 0 under with_commas).
                int padInode = format == LsFormat.Commas ? 0 : inodeWidth;
                int padBlock = format == LsFormat.Commas ? 0 : blockWidth;
                if (inodes is not null) prefix += inodes[idx].PadLeft(padInode) + " ";
                if (blockText is not null) prefix += blockText[idx].PadLeft(padBlock) + " ";

                if (columnar)
                {
                    string rawName = GetDisplayName(entry);
                    string shown = rawName;
                    if (colorize)
                    {
                        if (isDir) shown = $"{blue}{bold}{rawName}{reset}";
                        else if (isSymlink) shown = $"{cyan}{rawName}{reset}";
                        else if (IsExecutable(entry)) shown = $"{green}{rawName}{reset}";
                    }
                    cells!.Add(new LsCell(prefix + shown + indicator,
                        TextWidth.Of(prefix) + TextWidth.Of(rawName) + indicator.Length));
                    continue;
                }

                string bashText;
                if (longMode)
                {
                    string line = longLines![idx];
                    if (classify)
                    {
                        line += indicator;
                    }
                    bashText = prefix + line + "\n";
                }
                else
                {
                    string name = GetDisplayName(entry);
                    if (colorize)
                    {
                        if (isDir)
                        {
                            name = $"{blue}{bold}{name}{reset}";
                        }
                        else if (isSymlink)
                        {
                            name = $"{cyan}{name}{reset}";
                        }
                        else if (IsExecutable(entry))
                        {
                            name = $"{green}{name}{reset}";
                        }
                    }
                    bashText = $"{prefix}{name}{indicator}\n";
                }

                // Match the psm1 Set-BashDisplayProperty normalization (strip one
                // trailing \n) and write the typed object through.
                var prop = entry.Properties["BashText"];
                if (prop != null)
                {
                    prop.Value = BashRuntime.NormalizeBashText(bashText);
                }
                else
                {
                    entry.Properties.Add(new PSNoteProperty(
                        "BashText", BashRuntime.NormalizeBashText(bashText)));
                }
                WriteObject(entry);
            }

            if (cells is not null)
            {
                // Multi-column rows hold several entries each, so they are plain text records (the LsEntry
                // objects of the one-per-line and -l views cannot carry a shared line).
                var rowsText = format == LsFormat.Commas
                    ? LsColumns.RenderCommas(cells, lineLength)
                    : LsColumns.Render(cells, lineLength, format == LsFormat.Across, tabSize);
                foreach (var row in rowsText) WriteObject(BashRuntime.TextRecord(row, false));
            }
        }

        bool printHeaders = recursive || targets.Count > 1;

        // One directory section, then (with -R) each subdirectory in listing order, depth first.
        void ListDirectory(string path, string display)
        {
            var entries = new List<PSObject>();
            try
            {
                var dirInfo = new DirectoryInfo(path);
                if (showAll)
                {
                    entries.Add(DotEntry(dirInfo, "."));
                    entries.Add(DotEntry(dirInfo.Parent ?? dirInfo, ".."));
                }
                foreach (var fsi in dirInfo.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
                {
                    if (!showHidden)
                    {
                        if (fsi.Name.Length > 0 && fsi.Name[0] == '.')
                        {
                            continue;
                        }
                        if (IsWindows()
                            && (fsi.Attributes & FileAttributes.Hidden) != 0)
                        {
                            continue;
                        }
                    }
                    entries.Add(BuildEntryFromFsi(fsi));
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                WriteBashError(
                    $"ls: cannot open directory '{display}': {ex.Message}", 2);
                hadError = true;
                return;
            }

            var sorted = Sort(entries);
            EmitBlock(printHeaders ? display : null, sorted, withTotal: true);

            if (!recursive) return;
            foreach (var e in sorted)
            {
                if (!GetBool(e, "IsDirectory") || GetBool(e, "IsSymlink")) continue;
                string name = GetString(e, "Name");
                if (name is "." or "..") continue;
                ListDirectory(GetString(e, "FullPath"), JoinDisplay(display, name));
            }
        }

        if (fileBlock.Count > 0)
        {
            EmitBlock(null, Sort(fileBlock), withTotal: false);
        }

        IEnumerable<(string Path, string Display, DateTime Mtime)> orderedDirs;
        if (sortBySize)
        {
            orderedDirs = dirTargets.OrderBy(t => t.Display, StringComparer.OrdinalIgnoreCase);
            if (reverseSort) orderedDirs = orderedDirs.Reverse();
        }
        else if (sortByTime)
        {
            orderedDirs = dirTargets.OrderByDescending(t => t.Mtime)
                .ThenBy(t => t.Display, StringComparer.OrdinalIgnoreCase);
            if (reverseSort) orderedDirs = orderedDirs.Reverse();
        }
        else
        {
            var byName = dirTargets.OrderBy(t => t.Display, StringComparer.OrdinalIgnoreCase);
            orderedDirs = reverseSort ? byName.Reverse() : byName;
        }

        foreach (var d in orderedDirs)
        {
            ListDirectory(d.Path, d.Display);
        }

        if (hadError)
        {
            SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
        }
    }

    /// <summary>"." / ".." entries for <c>-a</c>: the directory itself or its parent, named as GNU names them.</summary>
    private static PSObject DotEntry(DirectoryInfo dir, string name)
    {
        var e = BuildEntryFromFsi(dir);
        e.Properties["Name"].Value = name;
        return e;
    }

    /// <summary>A directory operand that came out of a glob: show it relative to the working directory.</summary>
    private static string RelativeDisplay(string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(Environment.CurrentDirectory, fullPath);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                return rel.Replace('\\', '/');
        }
        catch { /* fall through */ }
        return fullPath;
    }

    /// <summary>GNU builds a child's header as "parent/child" without doubling a trailing slash.</summary>
    internal static string JoinDisplay(string parent, string child) =>
        parent.EndsWith('/') || parent.EndsWith('\\') ? parent + child : parent + "/" + child;

    /// <summary>
    /// Allocated size in 1 KiB units, the way <c>ls -s</c> / the <c>total</c> line count it:
    /// whole 4 KiB allocation units (a 5000-byte file is 8, an empty file 0, a directory 4).
    /// The true figure is filesystem-specific (st_blocks); 4 KiB is what ext4/NTFS give in practice.
    /// </summary>
    internal static long BlocksK(PSObject entry)
    {
        if (GetBool(entry, "IsSymlink")) return 0;
        if (GetBool(entry, "IsDirectory")) return 4;
        long size = GetLong(entry, "SizeBytes");
        return (size + 4095) / 4096 * 4;
    }

    private static string InodeText(PSObject entry)
    {
        var path = GetString(entry, "FullPath");
        return FileIdentity.TryGetInode(path, out var inode)
            ? inode.ToString(CultureInfo.InvariantCulture)
            : "?";
    }

    /// <summary>
    /// bash prints a FILE / <c>-d</c> operand exactly as typed (<c>ls -d .</c> -> <c>.</c>,
    /// <c>ls ./a.txt</c> -> <c>./a.txt</c>). The typed entry keeps its real <c>Name</c>; only the
    /// rendered text uses the operand, carried in <c>DisplayName</c>. Directory LISTINGS never
    /// set it (their entries print their own names).
    /// </summary>
    private static PSObject WithOperandDisplayName(PSObject entry, string? operand)
    {
        if (operand is not null)
            entry.Properties.Add(new PSNoteProperty("DisplayName", operand));
        return entry;
    }

    private static string GetDisplayName(PSObject entry)
    {
        string d = GetString(entry, "DisplayName");
        return d.Length > 0 ? d : GetString(entry, "Name");
    }

    /// <summary>
    /// Reimplements the psm1 <c>Get-LsEntryFromFsi</c>: builds a
    /// <c>PsBash.LsEntry</c> PSObject from a real
    /// <see cref="FileSystemInfo"/> using attribute-derived permissions on
    /// Windows (no <c>Get-Acl</c>) and the Unix file mode plus a <c>stat</c>
    /// shell-out for owner/group on POSIX — exactly the oracle's behavior.
    /// </summary>
    private static PSObject BuildEntryFromFsi(FileSystemInfo item)
    {
        var attrs = item.Attributes;
        bool isDir = item is DirectoryInfo;
        bool isLink = (attrs & FileAttributes.ReparsePoint) != 0;
        char typeChar = isDir ? 'd' : (isLink ? 'l' : '-');

        string perm;
        string owner;
        string group;
        int linkCount = 1;

        if (IsWindows())
        {
            bool readOnly = (attrs & FileAttributes.ReadOnly) != 0;
            bool isExec = isDir || IsExecExtension(item.Extension);
            string r = "r";
            string w = readOnly ? "-" : "w";
            string x = isExec ? "x" : "-";
            perm = $"{typeChar}{r}{w}{x}{r}-{x}{r}-{x}";
            owner = Environment.GetEnvironmentVariable("USERNAME") ?? string.Empty;
            group = owner;
            // NTFS hard links: read lazily for `-l` only (LongRow), a per-entry handle open is not free.
            linkCount = 1;
        }
        else
        {
            int mode = (int)item.UnixFileMode;
            perm = $"{typeChar}{ConvertToPermissionString(mode)}";
            owner = string.Empty;
            group = string.Empty;
            try
            {
                bool isMac = OperatingSystem.IsMacOS();
                var statArgs = isMac
                    ? new[] { "-f", "%l %Su %Sg", item.FullName }
                    : new[] { "-c", "%h %U %G", item.FullName };
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in statArgs)
                {
                    psi.ArgumentList.Add(a);
                }
                // Bounded spawn + concurrent drain + kill-tree on timeout so a hung
                // /usr/bin/stat (run once per file) cannot wedge the host runspace.
                string statOut = BashRuntime.RunChildProcess(psi).Stdout.Trim();
                if (statOut.Length > 0)
                {
                    // "LINKS OWNER GROUP": the group takes the remainder (it may contain spaces).
                    var parts = statOut.Split(new[] { ' ' }, 3);
                    if (parts.Length == 3 && int.TryParse(parts[0], out int links))
                    {
                        linkCount = links;
                        owner = parts[1];
                        group = parts[2];
                    }
                }
            }
            catch
            {
                // stat unavailable — leave owner/group empty, matching the
                // oracle's `2>$null` swallow.
            }
        }

        // FileInfo.Length throws (FileNotFoundException) on a dangling symlink —
        // the target is gone. bash `ls -l` still lists such an entry (with the
        // link's own size), so fall back to 0 rather than crashing the listing.
        long sizeBytes;
        if (isDir)
        {
            sizeBytes = 4096L;
        }
        else
        {
            try { sizeBytes = ((FileInfo)item).Length; }
            catch (System.IO.FileNotFoundException) { sizeBytes = 0L; }
            catch (System.IO.DirectoryNotFoundException) { sizeBytes = 0L; }
        }

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.LsEntry");
        obj.Properties.Add(new PSNoteProperty("Name", item.Name));
        obj.Properties.Add(new PSNoteProperty("FullPath", item.FullName));
        obj.Properties.Add(new PSNoteProperty("IsDirectory", isDir));
        obj.Properties.Add(new PSNoteProperty("IsSymlink", isLink));
        obj.Properties.Add(new PSNoteProperty("SizeBytes", sizeBytes));
        obj.Properties.Add(new PSNoteProperty("Permissions", perm));
        obj.Properties.Add(new PSNoteProperty("LinkCount", linkCount));
        obj.Properties.Add(new PSNoteProperty("Owner", owner));
        obj.Properties.Add(new PSNoteProperty("Group", group));
        obj.Properties.Add(new PSNoteProperty("LastModified", item.LastWriteTime));
        obj.Properties.Add(new PSNoteProperty("BashText", string.Empty));
        return obj;
    }

    /// <summary>
    /// Reimplements the psm1 <c>ConvertTo-PermissionString</c>: maps the low 9
    /// bits of a Unix mode to an <c>rwxrwxrwx</c> string.
    /// </summary>
    private static string ConvertToPermissionString(int mode)
    {
        var sb = new StringBuilder(9);
        int[] bits = { 256, 128, 64, 32, 16, 8, 4, 2, 1 };
        char[] chars = { 'r', 'w', 'x', 'r', 'w', 'x', 'r', 'w', 'x' };
        for (int i = 0; i < 9; i++)
        {
            sb.Append((mode & bits[i]) != 0 ? chars[i] : '-');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reimplements the psm1 <c>Format-BashSize</c>: bytes under 1024 print
    /// raw; otherwise scale by 1024 and print 1 decimal under 10, else a
    /// ceiling-rounded integer, with a K/M/G/T/P unit suffix.
    /// </summary>
    private static string FormatBashSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture);
        }

        string[] units = { "K", "M", "G", "T", "P" };
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
            return string.Format(
                CultureInfo.InvariantCulture, "{0}{1}", rounded, units[unitIdx]);
        }

        double r1 = Math.Ceiling(value * 10) / 10;
        return string.Format(
            CultureInfo.InvariantCulture, "{0:F1}{1}", r1, units[unitIdx]);
    }

    /// <summary>
    /// The <c>-l</c> columns of one entry as text. The size is the plain byte count (or the <c>-h</c> form);
    /// padding is NOT done here — <see cref="LsLongFormat.Align"/> pads to the widest entry of the block.
    /// On Windows the NTFS hard-link count is read here (only <c>-l</c> pays for the handle open).
    /// </summary>
    private static LsLongRow LongRow(PSObject entry, bool humanReadable)
    {
        long sizeBytes = GetLong(entry, "SizeBytes");
        string size = humanReadable
            ? FormatBashSize(sizeBytes)
            : sizeBytes.ToString(CultureInfo.InvariantCulture);
        int links = GetInt(entry, "LinkCount");
        if (IsWindows() && !GetBool(entry, "IsSymlink")
            && FileIdentity.TryGetLinkCount(GetString(entry, "FullPath"), out uint ntfsLinks))
            {
                links = (int)ntfsLinks;
                if (entry.Properties["LinkCount"] is { } lp) lp.Value = links;
            }

        return new LsLongRow(
            GetString(entry, "Permissions"),
            links.ToString(CultureInfo.InvariantCulture),
            GetString(entry, "Owner"),
            GetString(entry, "Group"),
            size,
            LsLongFormat.Date(GetDate(entry, "LastModified"), DateTime.Now),
            GetDisplayName(entry));
    }
    /// <summary>
    /// Reimplements the psm1 <c>Test-IsExecutable</c>: a directory or symlink is
    /// never executable; on Windows the extension decides; on POSIX any of the
    /// three <c>x</c> permission bits decides.
    /// </summary>
    private static bool IsExecutable(PSObject entry)
    {
        if (GetBool(entry, "IsDirectory"))
        {
            return false;
        }
        if (GetBool(entry, "IsSymlink"))
        {
            return false;
        }

        if (IsWindows())
        {
            string name = GetString(entry, "Name");
            int dot = name.LastIndexOf('.');
            string ext = dot >= 0 ? name.Substring(dot).ToLowerInvariant() : string.Empty;
            return IsExecExtension(ext);
        }

        string perm = GetString(entry, "Permissions");
        if (perm.Length >= 4 && perm[3] == 'x')
        {
            return true;
        }
        if (perm.Length >= 7 && perm[6] == 'x')
        {
            return true;
        }
        if (perm.Length >= 10 && perm[9] == 'x')
        {
            return true;
        }
        return false;
    }

    private static bool IsExecExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }
        string lower = ext.ToLowerInvariant();
        foreach (var e in ExecExtensions)
        {
            if (lower == e)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsWindows()
        => OperatingSystem.IsWindows();

    /// <summary>
    /// Reimplements the psm1 <c>Resolve-BashGlob</c> slice in C# (see
    /// <see cref="InvokeBashCatCommand"/> for the rationale): <c>*</c>/<c>?</c>
    /// patterns expand against the current location and pass through literally
    /// when nothing matches; literal paths resolve against the shell's
    /// <c>$PWD</c> via the path provider.
    /// </summary>
    private List<(string Target, string? Display)> ResolveGlob(IReadOnlyList<string> paths)
    {
        var result = new List<(string, string?)>();
        foreach (var p in paths)
        {
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
            {
                var matched = new List<string>();
                try
                {
                    foreach (var resolved in SessionState.Path
                                 .GetResolvedProviderPathFromPSPath(p, out _))
                    {
                        matched.Add(resolved);
                    }
                }
                catch
                {
                    // No matches — literal passthrough.
                }

                if (matched.Count == 0)
                {
                    result.Add((p, null));
                }
                else
                {
                    foreach (var m in matched) result.Add((m, null));
                }
            }
            else
            {
                result.Add((SessionState.Path.GetUnresolvedProviderPathFromPSPath(p), p));
            }
        }
        return result;
    }

    private void WriteBashError(string message, int exitCode)
    {
        FileSystemHelpers.WriteBashError(this, message);
        FileSystemHelpers.SetLastExitCode(this, exitCode);
    }

    // --- Typed-property accessors for the PsBash.LsEntry PSObject ---

    private static string GetString(PSObject o, string name)
        => o.Properties[name]?.Value?.ToString() ?? string.Empty;

    private static bool GetBool(PSObject o, string name)
    {
        var v = o.Properties[name]?.Value;
        return v is bool b && b;
    }

    private static long GetLong(PSObject o, string name)
    {
        var v = o.Properties[name]?.Value;
        if (v == null)
        {
            return 0L;
        }
        try
        {
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0L;
        }
    }

    private static int GetInt(PSObject o, string name)
    {
        var v = o.Properties[name]?.Value;
        if (v == null)
        {
            return 0;
        }
        try
        {
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime GetDate(PSObject o, string name)
    {
        var v = o.Properties[name]?.Value;
        if (v is DateTime dt)
        {
            return dt;
        }
        if (v != null && DateTime.TryParse(
                v.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        return DateTime.MinValue;
    }
}
