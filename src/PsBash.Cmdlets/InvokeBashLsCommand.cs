using System.Globalization;
using System.Management.Automation;
using System.Text;

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
/// in C# here. <c>-R</c> uses <see cref="SearchOption.AllDirectories"/>.</item>
/// <item><b>Tier 3 — PS provider fallback.</b> Registry:, Cert:, custom
/// PSDrives — also delegated to the <c>Get-BashLsProviderEntries</c> shim,
/// which calls <c>Get-Item</c> / <c>Get-ChildItem</c> and
/// <c>Get-LsEntryFromPsItem</c>.</item>
/// </list>
///
/// Common-parameter / own-parameter audit (Phase 1c lesson — the "no
/// collisions" claim must be VERIFIED against a live runspace, not assumed —
/// the original audit here was wrong and the parity tests caught it):
/// ls's short flags are <c>-l -a -A -h -R -S -t -r -1 -p -d -F -i -s</c>.
/// Three collide and are declared as explicit <see cref="SwitchParameter"/>s:
/// <list type="bullet">
/// <item><c>-a</c> / <c>-A</c> prefix-match this cmdlet's own
/// <see cref="Arguments"/> parameter (<c>-A</c> → <c>-Arguments</c>), which
/// would bind the next operand as the argument array. A single switch
/// (<see cref="A"/>) binds both — PowerShell parameter names are
/// case-insensitive — and that is behaviorally complete because
/// <see cref="System.IO"/> directory enumeration never yields <c>.</c> /
/// <c>..</c>, so the oracle's <c>-A</c>-excludes-dot-dirs nuance is a
/// filesystem-path no-op.</item>
/// <item><c>-d</c> prefix-collides with the <c>-Debug</c> common parameter
/// (<see cref="D"/>).</item>
/// <item><c>-p</c> prefix-collides with <c>-ProgressAction</c> /
/// <c>-PipelineVariable</c> (<see cref="P"/>).</item>
/// </list>
/// An exact / explicit parameter match beats a prefix match. The remaining
/// flags (<c>-l -h -R -S -t -r -1 -F -i -s</c>, <c>--color</c>) do not
/// prefix-collide and stay in <see cref="Arguments"/>, parsed by
/// <see cref="BashRuntime.ConvertFromBashArgs"/>. Bundled forms (e.g.
/// <c>-la</c>, <c>-ad</c>) are recovered post-parse, as in the cat / wc
/// cmdlets. <c>-1</c> / <c>-i</c> / <c>-s</c> are accepted but ignored,
/// matching the psm1 oracle.
///
/// The <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c>; a
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

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static readonly string[] ExecExtensions =
        { ".exe", ".bat", ".cmd", ".ps1", ".sh", ".com" };

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

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

        // Common-parameter / own-parameter audit (Phase 1c lesson — the
        // "no collisions" claim must be VERIFIED, not assumed):
        //   -a / -A  prefix-match this cmdlet's own -Arguments parameter,
        //   -d       prefix-collides with the -Debug common parameter,
        //   -p       prefix-collides with -ProgressAction / -PipelineVariable.
        // Those three are declared as explicit SwitchParameters (A / D / P) —
        // an exact / explicit parameter match beats a prefix match. The rest
        // (-l -h -R -S -t -r -1 -F -i -s, --color) do not prefix-collide and
        // stay in Arguments, parsed by ConvertFromBashArgs.
        var flagDefs = BashRuntime.NewFlagDefs(new[]
        {
            "-l", "long listing",
            "-h", "human readable sizes",
            "-R", "recursive",
            "-S", "sort by size",
            "-t", "sort by time",
            "-r", "reverse sort",
            "-1", "one per line",
            "-F", "classify (append */=>@| type indicators)",
            "--color", "colorize output",
            "-i", "show inode number",
            "-s", "show allocated size in blocks",
        });
        var parsed = BashRuntime.ConvertFromBashArgs(args, flagDefs);

        bool longMode = parsed.Flags["-l"];
        bool showHidden = A.IsPresent;
        bool humanSizes = parsed.Flags["-h"];
        bool recursive = parsed.Flags["-R"];
        bool sortBySize = parsed.Flags["-S"];
        bool sortByTime = parsed.Flags["-t"];
        bool reverseSort = parsed.Flags["-r"];
        bool dirOnly = D.IsPresent;
        bool classifyF = parsed.Flags["-F"];
        bool classifyP = P.IsPresent;

        // Bundled-flag recovery: a bundle like -la or -ad reaches Arguments
        // intact (the explicit A/D/P switches only bind a bare -a/-A/-d/-p).
        // ConvertFromBashArgs turns an unrecognized bundle char into an
        // operand, so -a/-d/-p inside a bundle of otherwise-known ls flags
        // would be lost. Detect that case and restore them, matching the psm1
        // oracle's ConvertFrom-BashArgs which split bundled short flags.
        const string knownBundleChars = "lhRStr1FisaAdp";
        for (int bi = 0; bi < parsed.Operands.Count; bi++)
        {
            var op = parsed.Operands[bi];
            if (op.Length > 1 && op[0] == '-' && op[1] != '-'
                && op.Skip(1).All(c => knownBundleChars.IndexOf(c) >= 0))
            {
                if (op.IndexOf('l') >= 0) longMode = true;
                if (op.IndexOf('h') >= 0) humanSizes = true;
                if (op.IndexOf('R') >= 0) recursive = true;
                if (op.IndexOf('S') >= 0) sortBySize = true;
                if (op.IndexOf('t') >= 0) sortByTime = true;
                if (op.IndexOf('r') >= 0) reverseSort = true;
                if (op.IndexOf('F') >= 0) classifyF = true;
                if (op.IndexOf('a') >= 0 || op.IndexOf('A') >= 0) showHidden = true;
                if (op.IndexOf('d') >= 0) dirOnly = true;
                if (op.IndexOf('p') >= 0) classifyP = true;
                // -1 / -i / -s are accepted-but-ignored (parity with the psm1
                // oracle, which parsed but did not act on -1 / -i / -s).
                parsed.Operands.RemoveAt(bi);
                bi--;
            }
        }

        bool classify = classifyF || classifyP || longMode;
        bool colorize = parsed.Flags["--color"];

        var operands = parsed.Operands.Count > 0
            ? parsed.Operands
            : new List<string> { "." };
        var targets = ResolveGlob(operands);

        var allEntries = new List<PSObject>();
        bool hadError = false;

        foreach (var target in targets)
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
                    allEntries.Add(BuildEntryFromFsi(new DirectoryInfo(resolvedPath)));
                }
                else
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(resolvedPath);
                        var searchOpt = recursive
                            ? SearchOption.AllDirectories
                            : SearchOption.TopDirectoryOnly;
                        foreach (var fsi in dirInfo.EnumerateFileSystemInfos("*", searchOpt))
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
                            allEntries.Add(BuildEntryFromFsi(fsi));
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteBashError(
                            $"ls: cannot open directory '{target}': {ex.Message}", 2);
                        hadError = true;
                    }
                }
                continue;
            }

            if (resolvedPath != null && File.Exists(resolvedPath))
            {
                allEntries.Add(BuildEntryFromFsi(new FileInfo(resolvedPath)));
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

            bool anyFromShim = false;
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
                if (baseObj is ErrorRecord innerEr)
                {
                    FileSystemHelpers.WriteBashError(this, innerEr.ToString());
                    continue;
                }
                anyFromShim = true;
                allEntries.Add(item as PSObject ?? PSObject.AsPSObject(item));
            }

            if (!anyFromShim)
            {
                // The shim emits its own bash-style error and sets the exit
                // code; mirror the oracle's $hadError flag for the final code.
                hadError = true;
            }
        }

        // Sort — bash default is case-insensitive alphabetical with dirs and
        // files interleaved; -S sorts by size, -t by mtime; -r reverses.
        IEnumerable<PSObject> sorted;
        if (sortBySize)
        {
            sorted = reverseSort
                ? allEntries.OrderBy(e => GetLong(e, "SizeBytes"))
                : allEntries.OrderByDescending(e => GetLong(e, "SizeBytes"));
        }
        else if (sortByTime)
        {
            sorted = reverseSort
                ? allEntries.OrderBy(e => GetDate(e, "LastModified"))
                : allEntries.OrderByDescending(e => GetDate(e, "LastModified"));
        }
        else
        {
            var byName = allEntries.OrderBy(
                e => GetString(e, "Name"), StringComparer.OrdinalIgnoreCase);
            sorted = reverseSort
                ? byName.Reverse()
                : byName;
        }

        // Format and emit.
        const string reset = "[0m";
        const string bold = "[1m";
        const string blue = "[34m";
        const string cyan = "[36m";
        const string green = "[32m";

        foreach (var entry in sorted)
        {
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

            string bashText;
            if (longMode)
            {
                string line = FormatLsLine(entry, humanSizes);
                if (classify)
                {
                    line += indicator;
                }
                bashText = line + "\n";
            }
            else
            {
                string name = GetString(entry, "Name");
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
                bashText = $"{name}{indicator}\n";
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

        if (hadError)
        {
            SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
        }
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
                    ? new[] { "-f", "%Su %Sg", item.FullName }
                    : new[] { "-c", "%U %G", item.FullName };
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
                    var parts = statOut.Split(new[] { ' ' }, 2);
                    owner = parts[0];
                    group = parts.Length > 1 ? parts[1] : string.Empty;
                }
            }
            catch
            {
                // stat unavailable — leave owner/group empty, matching the
                // oracle's `2>$null` swallow.
            }
        }

        long sizeBytes = isDir ? 4096L : ((FileInfo)item).Length;

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.LsEntry");
        obj.Properties.Add(new PSNoteProperty("Name", item.Name));
        obj.Properties.Add(new PSNoteProperty("FullPath", item.FullName));
        obj.Properties.Add(new PSNoteProperty("IsDirectory", isDir));
        obj.Properties.Add(new PSNoteProperty("IsSymlink", isLink));
        obj.Properties.Add(new PSNoteProperty("SizeBytes", sizeBytes));
        obj.Properties.Add(new PSNoteProperty("Permissions", perm));
        obj.Properties.Add(new PSNoteProperty("LinkCount", 1));
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
    /// Reimplements the psm1 <c>Format-BashDate</c>: a date within the last six
    /// months (and not in the future) prints <c>MMM dd HH:mm</c>; anything else
    /// prints <c>MMM dd  yyyy</c>. Month name is invariant-culture.
    /// </summary>
    private static string FormatBashDate(DateTime date)
    {
        DateTime now = DateTime.Now;
        DateTime sixMonthsAgo = now.AddMonths(-6);

        string month = date.ToString("MMM", CultureInfo.InvariantCulture);
        string day = date.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2);

        if (date < sixMonthsAgo || date > now)
        {
            return $"{month} {day}  {date.Year}";
        }
        string time = date.ToString("HH:mm", CultureInfo.InvariantCulture);
        return $"{month} {day} {time}";
    }

    /// <summary>
    /// Reimplements the psm1 <c>Format-LsLine</c>: the <c>-l</c> long-format
    /// line — permissions, link count, owner, group, size (8-wide, or 4-wide
    /// human), date, name.
    /// </summary>
    private static string FormatLsLine(PSObject entry, bool humanReadable)
    {
        long sizeBytes = GetLong(entry, "SizeBytes");
        string size = humanReadable
            ? FormatBashSize(sizeBytes).PadLeft(4)
            : sizeBytes.ToString(CultureInfo.InvariantCulture).PadLeft(8);
        string date = FormatBashDate(GetDate(entry, "LastModified"));

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3} {4} {5} {6}",
            GetString(entry, "Permissions"),
            GetInt(entry, "LinkCount"),
            GetString(entry, "Owner"),
            GetString(entry, "Group"),
            size,
            date,
            GetString(entry, "Name"));
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
    private List<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        var result = new List<string>();
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
                    result.Add(p);
                }
                else
                {
                    result.AddRange(matched);
                }
            }
            else
            {
                result.Add(SessionState.Path.GetUnresolvedProviderPathFromPSPath(p));
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
