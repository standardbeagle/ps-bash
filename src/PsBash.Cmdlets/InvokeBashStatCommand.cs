using System.Globalization;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashStat</c> function
/// (REFACTOR-2 Phase 4 follow-on). Reports file metadata in the GNU coreutils
/// <c>stat</c> shape: a default multi-line block, <c>-t</c> terse one-line
/// form, or <c>-c FORMAT</c> / <c>--printf FORMAT</c> caller-driven format.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashStat</c>. The
/// per-operand metadata slice (<c>Get-BashFileInfo</c>) is reimplemented in
/// C# inside this cmdlet (<see cref="BuildFileInfo"/>) — duplicating the
/// Phase 1d / find port. The duplication is intentional and minimal: the
/// psm1 <c>Get-BashFileInfo</c> is kept in place because other psm1 helpers
/// still depend on it; consolidating into <see cref="BashRuntime"/> would
/// broaden this task's scope.
///
/// Flag surface (the psm1 oracle's exact set):
/// <list type="bullet">
/// <item><c>-c FORMAT</c> — caller-driven format string; output is the
/// formatted text + trailing newline.</item>
/// <item><c>--printf=FORMAT</c> — like <c>-c</c> but escape sequences in the
/// format are expanded (<c>\n</c> / <c>\t</c> / <c>\\</c>) and no trailing
/// newline is appended.</item>
/// <item><c>-t</c> — terse one-line format (14 space-separated fields).</item>
/// <item>No flag — default multi-line "File: ..." block.</item>
/// </list>
///
/// Format spec characters (case-sensitive, matching the oracle):
/// <c>%n</c> name, <c>%N</c> full path, <c>%s</c> size, <c>%a</c> octal perms,
/// <c>%A</c> permission string, <c>%U</c> user, <c>%G</c> group, <c>%i</c>
/// inode, <c>%b</c> blocks, <c>%d</c> device, <c>%Y</c> mtime epoch,
/// <c>%h</c> hardlink count, <c>%%</c> literal percent. Unknown specs are
/// preserved as literal text (oracle: the percent and the spec char are kept
/// in the output).
///
/// Flag-collision audit (per the playbook table):
/// <list type="bullet">
/// <item><c>-c FORMAT</c> — bare <c>-c</c> prefix-collides with <c>-Confirm</c>
/// under the cmdlet binder. Declared as the value-bearing <see cref="C"/>
/// parameter (literal single-letter name beats common-parameter prefix
/// match). The psm1 oracle's only short-form pattern is the separated
/// <c>-c FORMAT</c>; a joined <c>-cFORMAT</c> token would land in
/// <see cref="Arguments"/> and is recovered post-parse to match the oracle's
/// <c>-eq '-c'</c> dispatch (the oracle did not match a joined form, so we
/// don't synthesize one; tokens starting with <c>-c</c> that aren't bare
/// <c>-c</c> are passed through unchanged).</item>
/// <item><c>-t</c> — no PowerShell common-parameter prefix collision; stays
/// in <see cref="Arguments"/>.</item>
/// <item><c>--printf=FORMAT</c> — long form, no collision; stays in
/// <see cref="Arguments"/> and is recovered by the manual <c>^--printf=(.+)$</c>
/// regex.</item>
/// </list>
///
/// AOT safety: no <see cref="ScriptBlock"/> construction; <c>--help</c> and
/// the not-found <c>Get-BashItem</c> shim route through parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.
/// Missing operands route through
/// <see cref="FileSystemHelpers.WriteBashError"/> and set
/// <c>$global:LASTEXITCODE = 1</c>.
///
/// Directive 12: format strings are walked character-by-character — a
/// <c>%n$(throw 'pwn')</c> token reaches the spec switch, emits the literal
/// <c>$(throw 'pwn')</c> tail unmodified, and is never re-parsed as
/// PowerShell. The format-string value never feeds an <c>InvokeScript</c> body.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashStat")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashStatCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// The bash <c>-c FORMAT</c> (caller-driven format) value flag — declared
    /// explicitly because the bare token <c>-c</c> prefix-collides with the
    /// <c>-Confirm</c> common parameter. The parameter is literally named
    /// <c>C</c> so the binder routes by exact name (which beats a common-
    /// parameter prefix match; an <c>[Alias("c")]</c> on a longer name would
    /// not be sufficient).
    /// </summary>
    [Parameter]
    public string? C { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "stat"))
            {
                WriteObject(line);
            }
            return;
        }

        string? formatString = C;
        string? printfString = null;
        bool terseMode = false;
        var operands = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a == "-c" && i + 1 < args.Length)
            {
                formatString = args[i + 1];
                i += 2;
                continue;
            }
            if (a.StartsWith("--printf=", StringComparison.Ordinal))
            {
                printfString = a.Substring("--printf=".Length);
                i++;
                continue;
            }
            if (a == "-t")
            {
                terseMode = true;
                i++;
                continue;
            }
            operands.Add(a);
            i++;
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "stat: missing operand");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        bool hadError = false;

        foreach (var target in operands)
        {
            // Route resolution through psm1 Get-BashItem to preserve the
            // bash-style "stat: cannot stat 'PATH': No such file or directory"
            // error format. Wrap with inner 2>&1 so any Write-BashError
            // ErrorRecord surfaces in the script's success stream and we
            // can re-emit it via the cmdlet's own error stream.
            var itemResult = InvokeCommand.InvokeScript(
                "param($p) Get-BashItem -Path $p -Command 'stat' -Verb 'cannot stat' 2>&1",
                target);
            System.IO.FileSystemInfo? item = null;
            foreach (var r in itemResult)
            {
                if (r?.BaseObject is System.IO.FileSystemInfo fsi)
                {
                    item = fsi;
                    break;
                }
                if (r?.BaseObject is ErrorRecord innerEr)
                {
                    FileSystemHelpers.WriteBashError(this, innerEr.ToString());
                }
            }
            if (item == null)
            {
                hadError = true;
                continue;
            }

            var entry = BuildStatEntry(item);

            // Format output per oracle dispatch order.
            if (printfString != null)
            {
                string text = FormatStatString(entry, printfString);
                text = BashRuntime.ExpandEscapeSequences(text);
                entry.BashText = text;
            }
            else if (formatString != null)
            {
                string text = FormatStatString(entry, formatString);
                entry.BashText = text + "\n";
            }
            else if (terseMode)
            {
                entry.BashText = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11} {12} {13}\n",
                    entry.Name,
                    entry.SizeBytes,
                    entry.Blocks,
                    entry.OctalPerms,
                    entry.Owner,
                    entry.Group,
                    entry.Device,
                    entry.Inode,
                    entry.LinkCount,
                    "0",
                    "0",
                    entry.AtimeEpoch,
                    entry.MtimeEpoch,
                    "0");
            }
            else
            {
                string typeDesc = entry.IsDirectory ? "directory" : "regular file";
                var sb = new StringBuilder();
                sb.Append("  File: ").Append(entry.Name).Append('\n');
                sb.Append("  Size: ").Append(entry.SizeBytes)
                    .Append("\tBlocks: ").Append(entry.Blocks)
                    .Append("\tIO Block: 4096\t").Append(typeDesc).Append('\n');
                sb.Append("Device: ").Append(entry.Device)
                    .Append("\tInode: ").Append(entry.Inode)
                    .Append("\tLinks: ").Append(entry.LinkCount).Append('\n');
                sb.Append("Access: (").Append(entry.OctalPerms).Append('/').Append(entry.Permissions)
                    .Append(")\tUid: (").Append(entry.Owner)
                    .Append(")\tGid: (").Append(entry.Group).Append(")\n");
                sb.Append("Modify: ").Append(entry.LastModified.ToString(
                    "yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture));
                entry.BashText = sb.ToString() + "\n";
            }

            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.StatEntry");
            obj.Properties.Add(new PSNoteProperty("Name", entry.Name));
            obj.Properties.Add(new PSNoteProperty("FullPath", entry.FullPath));
            obj.Properties.Add(new PSNoteProperty("IsDirectory", entry.IsDirectory));
            obj.Properties.Add(new PSNoteProperty("SizeBytes", entry.SizeBytes));
            obj.Properties.Add(new PSNoteProperty("Permissions", entry.Permissions));
            obj.Properties.Add(new PSNoteProperty("OctalPerms", entry.OctalPerms));
            obj.Properties.Add(new PSNoteProperty("LinkCount", entry.LinkCount));
            obj.Properties.Add(new PSNoteProperty("Owner", entry.Owner));
            obj.Properties.Add(new PSNoteProperty("Group", entry.Group));
            obj.Properties.Add(new PSNoteProperty("Inode", entry.Inode));
            obj.Properties.Add(new PSNoteProperty("Blocks", entry.Blocks));
            obj.Properties.Add(new PSNoteProperty("Device", entry.Device));
            obj.Properties.Add(new PSNoteProperty("LastModified", entry.LastModified));
            obj.Properties.Add(new PSNoteProperty("MtimeEpoch", entry.MtimeEpoch));
            obj.Properties.Add(new PSNoteProperty("AccessTime", entry.AccessTime));
            obj.Properties.Add(new PSNoteProperty("AtimeEpoch", entry.AtimeEpoch));
            obj.Properties.Add(new PSNoteProperty("BashText", entry.BashText));
            WriteObject(obj);
        }

        if (hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    // ── format engine ────────────────────────────────────────────────────────

    /// <summary>
    /// Reproduces the psm1 <c>Format-StatString</c> helper byte-for-byte:
    /// per-char walk over the format with a case-sensitive switch on
    /// <c>%X</c> spec chars. Unknown specs preserve the percent and the spec
    /// char as literal text (oracle: the default branch appends
    /// <c>$FormatStr[$i]</c> and advances by one, so the next iteration's
    /// match-or-not handles the rest naturally).
    /// </summary>
    private static string FormatStatString(StatEntry entry, string fmt)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < fmt.Length)
        {
            if (fmt[i] == '%' && i + 1 < fmt.Length)
            {
                char spec = fmt[i + 1];
                switch (spec)
                {
                    case 's': sb.Append(entry.SizeBytes); i += 2; continue;
                    case 'a': sb.Append(entry.OctalPerms); i += 2; continue;
                    case 'A': sb.Append(entry.Permissions); i += 2; continue;
                    case 'n': sb.Append(entry.Name); i += 2; continue;
                    case 'N': sb.Append(entry.FullPath); i += 2; continue;
                    case 'U': sb.Append(entry.Owner); i += 2; continue;
                    case 'G': sb.Append(entry.Group); i += 2; continue;
                    case 'i': sb.Append(entry.Inode); i += 2; continue;
                    case 'b': sb.Append(entry.Blocks); i += 2; continue;
                    case 'd': sb.Append(entry.Device); i += 2; continue;
                    case 'Y': sb.Append(entry.MtimeEpoch); i += 2; continue;
                    case 'h': sb.Append(entry.LinkCount); i += 2; continue;
                    case '%': sb.Append('%'); i += 2; continue;
                    default:
                        sb.Append(fmt[i]);
                        i++;
                        continue;
                }
            }
            sb.Append(fmt[i]);
            i++;
        }
        return sb.ToString();
    }

    // ── stat entry assembly ──────────────────────────────────────────────────

    private sealed class StatEntry
    {
        public string Name = string.Empty;
        public string FullPath = string.Empty;
        public bool IsDirectory;
        public long SizeBytes;
        public string Permissions = string.Empty;
        public string OctalPerms = string.Empty;
        public int LinkCount = 1;
        public string Owner = string.Empty;
        public string Group = string.Empty;
        public long Inode;
        public long Blocks;
        public long Device;
        public DateTime LastModified;
        public long MtimeEpoch;
        public DateTime AccessTime;
        public long AtimeEpoch;
        public string BashText = string.Empty;
    }

    private static StatEntry BuildStatEntry(System.IO.FileSystemInfo item)
    {
        var entry = new StatEntry
        {
            Name = item.Name,
            FullPath = item.FullName,
            IsDirectory = item is System.IO.DirectoryInfo,
            LastModified = item.LastWriteTime,
            AccessTime = item.LastAccessTime,
        };

        entry.SizeBytes = entry.IsDirectory ? 4096L : ((System.IO.FileInfo)item).Length;
        entry.Blocks = (long)Math.Ceiling(entry.SizeBytes / 512.0);

        // Cross-platform inode + device synthesis (matches the psm1 oracle's
        // fallback for Windows; non-Windows the oracle shells out to
        // /usr/bin/stat, which we replicate so the FullPath, %i and %d fields
        // match GNU stat on Linux / BSD stat on macOS).
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                bool isMac = OperatingSystem.IsMacOS();
                var statArgs = isMac
                    ? new[] { "-f", "%i %b %d", item.FullName }
                    : new[] { "-c", "%i %b %d", item.FullName };
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in statArgs) psi.ArgumentList.Add(a);
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    string statOut = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    if (statOut.Length > 0)
                    {
                        var parts = statOut.Split(new[] { ' ', '\t' },
                            StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 1) long.TryParse(parts[0], out entry.Inode);
                        if (parts.Length >= 2) long.TryParse(parts[1], out entry.Blocks);
                        if (parts.Length >= 3) long.TryParse(parts[2], out entry.Device);
                    }
                }
            }
            catch
            {
                // /usr/bin/stat unavailable — keep the synthesized values.
            }
        }
        else if (item.FullName.Length > 0 && char.IsLetter(item.FullName[0]))
        {
            // Windows: synthesize device from drive letter (A=0, B=1, ...).
            char drive = char.ToUpperInvariant(item.FullName[0]);
            entry.Device = drive - 'A';
        }

        // Permission string + mode → octal.
        var info = BuildFileInfo(item);
        entry.Permissions = info.Permissions;
        entry.LinkCount = info.LinkCount;
        entry.Owner = info.Owner;
        entry.Group = info.Group;
        entry.OctalPerms = ComputeOctalPerms(item, info.Permissions);

        entry.MtimeEpoch = new DateTimeOffset(
            DateTime.SpecifyKind(entry.LastModified, DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(entry.LastModified))
            .ToUnixTimeSeconds();
        entry.AtimeEpoch = new DateTimeOffset(
            DateTime.SpecifyKind(entry.AccessTime, DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(entry.AccessTime))
            .ToUnixTimeSeconds();

        return entry;
    }

    private static string ComputeOctalPerms(System.IO.FileSystemInfo item, string permissionString)
    {
        int mode = 0;
        if (!OperatingSystem.IsWindows())
        {
            mode = (int)item.UnixFileMode;
        }
        else
        {
            // psm1 oracle's "approximate from permission string" branch: walk
            // the 9 permission chars after the leading type char, set the
            // corresponding rwx-by-group bit on each non-'-' entry.
            string perm = permissionString.Length > 0
                ? permissionString.Substring(1)
                : new string('-', 9);
            int[] rBits = { 256, 32, 4 };
            int[] wBits = { 128, 16, 2 };
            int[] xBits = { 64, 8, 1 };
            for (int ci = 0; ci < 9 && ci < perm.Length; ci++)
            {
                char ch = perm[ci];
                if (ch == '-') continue;
                int groupIdx = ci / 3;
                int typeIdx = ci % 3;
                int bit = typeIdx switch
                {
                    0 => rBits[groupIdx],
                    1 => wBits[groupIdx],
                    _ => xBits[groupIdx],
                };
                mode |= bit;
            }
        }
        return Convert.ToString(mode & 0x1FF, 8).PadLeft(4, '0');
    }

    // ── Get-BashFileInfo slice (find / ls share the same web) ────────────────

    private sealed class FileInfoSlice
    {
        public string Permissions = string.Empty;
        public int LinkCount = 1;
        public string Owner = string.Empty;
        public string Group = string.Empty;
    }

    private static FileInfoSlice BuildFileInfo(System.IO.FileSystemInfo item)
    {
        var info = new FileInfoSlice();
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
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    string statOut = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    if (statOut.Length > 0)
                    {
                        var parts = statOut.Split(new[] { ' ' }, 2);
                        info.Owner = parts[0];
                        info.Group = parts.Length > 1 ? parts[1] : string.Empty;
                    }
                }
            }
            catch
            {
                // /usr/bin/stat unavailable — match the oracle's 2>$null swallow.
            }
        }

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
}
