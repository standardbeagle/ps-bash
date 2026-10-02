using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>What <c>cp</c> does about an existing destination file (GNU: <c>-n</c>, <c>-i</c>, <c>--update=none|all</c>).</summary>
public enum CpExisting { Unspecified, Skip, Ask }

/// <summary>Which symbolic links <c>cp</c> follows (<c>-L</c>, <c>-H</c>, <c>-P</c>, <c>-d</c>, <c>-a</c>).</summary>
public enum CpDeref { Default, Never, CommandLine, Always }

/// <summary>Link instead of copy (<c>-l</c>, <c>-s</c>).</summary>
public enum CpLink { None, Hard, Symbolic }

/// <summary><c>--reflink[=WHEN]</c>.</summary>
public enum CpReflink { Auto, Always, Never }

/// <summary>
/// Every <c>cp</c> option resolved from the ordered scan, in command-line order, with GNU 9.4's rules
/// (oracle-checked): the LAST of <c>-i</c>/<c>-n</c>/<c>--update=none|all</c> decides what happens to an
/// existing file; <c>-u</c>/<c>--update[=older]</c> adds the "only when newer" test separately;
/// <c>--update=all</c> clears both; <c>-S</c> alone turns backups on; <c>-l</c> with <c>-s</c> is an error.
/// Pure — built from the scan, an environment lookup and nothing else.
/// </summary>
internal sealed class CpPlan
{
    public const string OptRecursive = "recursive", OptNoClobber = "no-clobber", OptForce = "force",
        OptVerbose = "verbose", OptPreserve = "preserve", OptUpdate = "update", OptArchive = "archive",
        OptPreserveList = "preserve-list", OptNoPreserve = "no-preserve", OptInteractive = "interactive",
        OptLink = "link", OptSymbolic = "symbolic-link", OptBackup = "backup", OptBackupControl = "backup-control",
        OptSuffix = "suffix", OptTargetDirectory = "target-directory", OptNoTargetDirectory = "no-target-directory",
        OptReflink = "reflink", OptDereference = "dereference", OptNoDereference = "no-dereference",
        OptDerefCommandLine = "dereference-command-line", OptNoDerefPreserveLinks = "no-deref-preserve-links",
        OptOneFileSystem = "one-file-system", OptSparse = "sparse", OptStripSlashes = "strip-trailing-slashes",
        OptAttributesOnly = "attributes-only", OptRemoveDestination = "remove-destination",
        OptParents = "parents", OptDebug = "debug";

    private const string TryHelp = "\nTry 'cp --help' for more information.";

    public bool Recursive { get; private set; }
    public bool Force { get; private set; }
    public bool Verbose { get; private set; }
    public bool Debug { get; private set; }
    public CpExisting Existing { get; private set; }
    public bool UpdateOlder { get; private set; }
    /// <summary>True when <c>-n</c> appeared (GNU 9.4 warns once per invocation).</summary>
    public bool NoClobberWarning { get; private set; }
    public CpLink Link { get; private set; }
    public CpDeref Deref { get; private set; }
    public CpReflink Reflink { get; private set; }
    public bool BackupEnabled { get; private set; }
    public BackupKind Backup { get; private set; } = BackupKind.None;
    public string BackupSuffix { get; private set; } = "~";
    public string? TargetDirectory { get; private set; }
    public bool NoTargetDirectory { get; private set; }
    public bool OneFileSystem { get; private set; }
    public bool StripSlashes { get; private set; }
    public bool Parents { get; private set; }
    public bool AttributesOnly { get; private set; }
    public bool RemoveDestination { get; private set; }

    private static readonly (string Name, string Value)[] UpdateWords = { ("all", "all"), ("none", "none"), ("older", "older") };
    private static readonly (string Name, CpReflink Value)[] ReflinkWords =
        { ("auto", CpReflink.Auto), ("always", CpReflink.Always), ("never", CpReflink.Never) };
    private static readonly (string Name, string Value)[] SparseWords = { ("never", "never"), ("auto", "auto"), ("always", "always") };

    /// <summary>Resolves <paramref name="parsed"/>; on failure <paramref name="error"/> is the complete diagnostic (with the Try line).</summary>
    public static bool TryBuild(ParsedArgs parsed, Func<string, string?> getenv, out CpPlan plan, out string? error)
    {
        plan = new CpPlan();
        error = null;
        string? backupControl = null;
        string? suffixOption = null;
        bool hard = false, symbolic = false;

        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            switch (t.OptId)
            {
                case OptRecursive: plan.Recursive = true; break;
                case OptArchive: plan.Recursive = true; plan.Deref = CpDeref.Never; break;
                case OptForce: plan.Force = true; break;
                case OptVerbose: plan.Verbose = true; break;
                case OptDebug: plan.Debug = true; break;
                case OptInteractive: plan.Existing = CpExisting.Ask; break;
                case OptNoClobber:
                    plan.Existing = CpExisting.Skip;
                    plan.NoClobberWarning = true;
                    break;
                case OptUpdate:
                    if (t.Value is null) { plan.UpdateOlder = true; break; }
                    if (!GnuArgMatch.TryMatch("cp", "update", t.Value, UpdateWords,
                            "  - 'all'\n  - 'none'\n  - 'older'", out var word, out var updateError))
                    {
                        error = updateError;
                        return false;
                    }
                    if (word == "older") plan.UpdateOlder = true;
                    else if (word == "none") { plan.Existing = CpExisting.Skip; plan.UpdateOlder = false; }
                    else { plan.Existing = CpExisting.Unspecified; plan.UpdateOlder = false; }
                    break;
                case OptLink: hard = true; break;
                case OptSymbolic: symbolic = true; break;
                case OptBackup: plan.BackupEnabled = true; break;
                case OptBackupControl: plan.BackupEnabled = true; backupControl = t.Value; break;
                case OptSuffix: plan.BackupEnabled = true; suffixOption = t.Value; break;
                case OptTargetDirectory: plan.TargetDirectory = t.Value; break;
                case OptNoTargetDirectory: plan.NoTargetDirectory = true; break;
                case OptReflink:
                    if (t.Value is null) { plan.Reflink = CpReflink.Always; break; }
                    if (!GnuArgMatch.TryMatch("cp", "reflink", t.Value, ReflinkWords,
                            "  - 'auto'\n  - 'always'\n  - 'never'", out var rl, out var reflinkError))
                    {
                        error = reflinkError;
                        return false;
                    }
                    plan.Reflink = rl;
                    break;
                case OptSparse:
                    if (!GnuArgMatch.TryMatch("cp", "sparse", t.Value ?? "", SparseWords,
                            "  - 'never'\n  - 'auto'\n  - 'always'", out _, out var sparseError))
                    {
                        error = sparseError;
                        return false;
                    }
                    break; // sparse detection tunes how bytes are written, never what they are
                case OptDereference: plan.Deref = CpDeref.Always; break;
                case OptDerefCommandLine: plan.Deref = CpDeref.CommandLine; break;
                case OptNoDereference:
                case OptNoDerefPreserveLinks: plan.Deref = CpDeref.Never; break;
                case OptOneFileSystem: plan.OneFileSystem = true; break;
                case OptStripSlashes: plan.StripSlashes = true; break;
                case OptAttributesOnly: plan.AttributesOnly = true; break;
                case OptRemoveDestination: plan.RemoveDestination = true; break;
                case OptParents: plan.Parents = true; break;
            }
        }

        if (hard && symbolic)
        {
            error = "cp: cannot make both hard and symbolic links" + TryHelp;
            return false;
        }
        plan.Link = hard ? CpLink.Hard : symbolic ? CpLink.Symbolic : CpLink.None;

        if (plan.TargetDirectory is not null && plan.NoTargetDirectory)
        {
            error = "cp: cannot combine --target-directory (-t) and --no-target-directory (-T)";
            return false;
        }

        if (plan.BackupEnabled)
        {
            if (!BackupControl.TryResolve("cp", backupControl, getenv, out var kind, out var backupError))
            {
                error = backupError;
                return false;
            }
            plan.Backup = kind;
            plan.BackupSuffix = BackupControl.Suffix(suffixOption, getenv);
        }
        return true;
    }
}
