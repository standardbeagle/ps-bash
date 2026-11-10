using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>What <c>chmod</c> was asked to do: flags, the mode word and the file operands, or the usage error.</summary>
internal sealed record ChmodPlan(
    bool Recursive, bool Verbose, bool Changes, bool Quiet, bool Help, bool Version,
    string? Mode, IReadOnlyList<string> Files, string? Error);

/// <summary>
/// Binary cmdlet for <c>chmod</c> (GNU coreutils): <c>chmod [-R] [-v|-c] [-f] MODE FILE...</c>. MODE is octal or
/// symbolic (<c>u+x</c>, <c>go-w</c>, <c>a=r</c>, <c>+x</c>, also the dash-leading <c>-x</c> / <c>-rw</c>), compiled
/// per file by <see cref="FileModeSpec"/> against that file's current mode.
/// <para>Unix sets the permission bits. Windows has no mode bits: the one representable part is the owner-write
/// bit, kept as the read-only attribute (<c>chmod -w f</c> / <c>chmod 444 f</c> set it, <c>chmod +w f</c> /
/// <c>chmod 644 f</c> clear it); every other bit is accepted and has nothing to change, so <c>chmod +x script</c>
/// succeeds as it does in Git Bash. See docs/specs/intentional-differences.md.</para>
/// <para>Not implemented: <c>--reference=FILE</c>. <c>--preserve-root</c> / <c>--no-preserve-root</c> are accepted.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashChmod")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashChmodCommand : PSCmdlet
{
    // Decoys for DIRECT calls only (the transpiler quotes dash words): a bare -v / -w / -o would bind PowerShell's
    // -Verbose / -Warning* / -Out* common parameters. They are put back in front of the arguments.
    [Parameter] public SwitchParameter v { get; set; }
    [Parameter] public SwitchParameter w { get; set; }
    [Parameter] public SwitchParameter o { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Pure argv scan (unit-test seam). A dash-leading word made only of mode characters is the MODE.</summary>
    internal static ChmodPlan Plan(string[] args)
    {
        bool recursive = false, verbose = false, changes = false, quiet = false, help = false, version = false;
        string? mode = null;
        var files = new List<string>();
        bool optionsDone = false;
        foreach (var a in args)
        {
            if (!optionsDone && a == "--") { optionsDone = true; continue; }
            if (!optionsDone && a.StartsWith("--", StringComparison.Ordinal))
            {
                switch (a)
                {
                    case "--recursive": recursive = true; break;
                    case "--verbose": verbose = true; break;
                    case "--changes": changes = true; break;
                    case "--silent" or "--quiet": quiet = true; break;
                    case "--preserve-root" or "--no-preserve-root": break;
                    case "--help": help = true; break;
                    case "--version": version = true; break;
                    default:
                        if (a.StartsWith("--reference", StringComparison.Ordinal))
                            return Fail("chmod: --reference is not supported by ps-bash");
                        return Fail($"chmod: unrecognized option '{a}'\nTry 'chmod --help' for more information.");
                }
                continue;
            }
            if (!optionsDone && a.Length > 1 && a[0] == '-' && !IsDashMode(a))
            {
                foreach (char c in a.AsSpan(1))
                {
                    switch (c)
                    {
                        case 'R': recursive = true; break;
                        case 'v': verbose = true; break;
                        case 'c': changes = true; break;
                        case 'f': quiet = true; break;
                        default: return Fail($"chmod: invalid option -- '{c}'\nTry 'chmod --help' for more information.");
                    }
                }
                continue;
            }
            if (mode is null) mode = a; else files.Add(a);
        }

        if (help || version) return new ChmodPlan(recursive, verbose, changes, quiet, help, version, mode, files, null);
        if (mode is null) return Fail("chmod: missing operand\nTry 'chmod --help' for more information.");
        if (files.Count == 0) return Fail($"chmod: missing operand after '{mode}'\nTry 'chmod --help' for more information.");
        return new ChmodPlan(recursive, verbose, changes, quiet, false, false, mode, files, null);

        ChmodPlan Fail(string message) => new(recursive, verbose, changes, quiet, false, false, mode, files, message);
    }

    /// <summary><c>-x</c>, <c>-rw</c>, <c>-w,+x</c>: a dash-leading symbolic mode (GNU accepts it as the MODE operand).</summary>
    private static bool IsDashMode(string a)
    {
        for (int i = 1; i < a.Length; i++)
            if ("rwxXstugo,+-=".IndexOf(a[i]) < 0) return false;
        return true;
    }

    protected override void ProcessRecord()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (v.IsPresent, "-v"), (w.IsPresent, "-w"), (o.IsPresent, "-o"));
        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = Plan(args);
        if (plan.Error is { } error)
        {
            FileSystemHelpers.WriteBashError(this, error);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        if (plan.Version && FileSystemHelpers.TryHandleVersion(this, "chmod", args)) return;
        if (plan.Help)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "chmod")) WriteObject(line);
            return;
        }

        // A mode that cannot compile is a usage error before any file is touched.
        if (!FileModeSpec.TryParse(plan.Mode!, isDirectory: false, FileModeSpec.CurrentUmask(), baseMode: 0, out _))
        {
            FileSystemHelpers.WriteBashError(this, $"chmod: invalid mode: '{plan.Mode}'\nTry 'chmod --help' for more information.");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        bool hadError = false;
        foreach (var raw in plan.Files)
        {
            foreach (var op in FileSystemHelpers.ResolveOperands(this, raw))
            {
                if (!File.Exists(op.Path) && !Directory.Exists(op.Path))
                {
                    if (!plan.Quiet)
                        FileSystemHelpers.WriteBashError(this, $"chmod: cannot access '{op.Display}': No such file or directory");
                    hadError = true;
                    continue;
                }
                hadError |= !Apply(plan, op.Path, op.Display);
                if (plan.Recursive && Directory.Exists(op.Path) && !IsLink(op.Path))
                    hadError |= !ApplyTree(plan, op.Path, op.Display);
            }
        }
        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    private bool ApplyTree(ChmodPlan plan, string dir, string display)
    {
        bool ok = true;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).OrderBy(e => e, StringComparer.Ordinal).ToList(); }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            if (!plan.Quiet) FileSystemHelpers.WriteBashError(this, $"chmod: cannot read directory '{display}': {FileSystemHelpers.ReadErrorMessage(ex)}");
            return false;
        }
        foreach (var entry in entries)
        {
            string childDisplay = display.TrimEnd('/') + "/" + Path.GetFileName(entry);
            // GNU chmod -R never follows a symbolic link it meets during the walk (and does not change it).
            if (IsLink(entry)) continue;
            ok &= Apply(plan, entry, childDisplay);
            if (Directory.Exists(entry)) ok &= ApplyTree(plan, entry, childDisplay);
        }
        return ok;
    }

    private static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch { return false; }
    }

    /// <summary>Compile the mode against this entry's current bits and apply it; false when it could not be changed.</summary>
    private bool Apply(ChmodPlan plan, string path, string display)
    {
        try
        {
            bool isDir = Directory.Exists(path);
            int current = CurrentMode(path, isDir);
            FileModeSpec.TryParse(plan.Mode!, isDir, FileModeSpec.CurrentUmask(), current, out int wanted);
            if (OperatingSystem.IsWindows())
            {
                var attrs = File.GetAttributes(path);
                var next = FileModeSpec.OwnerCanWrite(wanted) ? attrs & ~FileAttributes.ReadOnly : attrs | FileAttributes.ReadOnly;
                if (next != attrs) File.SetAttributes(path, next);
            }
            else if (wanted != current)
            {
                File.SetUnixFileMode(path, (UnixFileMode)wanted);
            }

            if (plan.Verbose || plan.Changes)
            {
                // What is really on disk afterwards (on Windows only the owner-write bit can differ).
                int after = OperatingSystem.IsWindows() ? CurrentMode(path, isDir) : wanted;
                string shown = FileSystemHelpers.ToBashPath(display);
                if (after != current)
                    WriteObject(BashRuntime.NewBashObject($"mode of '{shown}' changed from {Describe(current)} to {Describe(after)}"));
                else if (plan.Verbose)
                    WriteObject(BashRuntime.NewBashObject($"mode of '{shown}' retained as {Describe(current)}"));
            }
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            if (!plan.Quiet)
            {
                string reason = ex is UnauthorizedAccessException ? "Operation not permitted" : FileSystemHelpers.ReadErrorMessage(ex);
                FileSystemHelpers.WriteBashError(this, $"chmod: changing permissions of '{display}': {reason}");
            }
            return false;
        }
    }

    /// <summary>The entry's permission bits; on Windows the stand-in 0644 / 0755 (0444 / 0555 when read-only).</summary>
    private static int CurrentMode(string path, bool isDir)
    {
        if (!OperatingSystem.IsWindows()) return (int)File.GetUnixFileMode(path) & 0xFFF;
        bool readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        int mode = isDir ? 0x1ED : 0x1A4; // 0755 : 0644
        return readOnly ? mode & ~0x92 : mode; // drop 0222
    }

    /// <summary><c>0755 (rwxr-xr-x)</c>, the form GNU prints with -v / -c.</summary>
    internal static string Describe(int mode)
    {
        Span<char> s = stackalloc char[9];
        const string Letters = "rwxrwxrwx";
        for (int i = 0; i < 9; i++) s[i] = (mode & (0x100 >> i)) != 0 ? Letters[i] : '-';
        if ((mode & 0x800) != 0) s[2] = s[2] == 'x' ? 's' : 'S';
        if ((mode & 0x400) != 0) s[5] = s[5] == 'x' ? 's' : 'S';
        if ((mode & 0x200) != 0) s[8] = s[8] == 'x' ? 't' : 'T';
        return Convert.ToString(mode & 0xFFF, 8).PadLeft(4, '0') + " (" + new string(s) + ")";
    }
}
