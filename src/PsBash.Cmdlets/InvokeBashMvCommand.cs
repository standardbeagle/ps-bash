using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>What <c>mv</c> does about an existing destination (GNU: the LAST of <c>-f</c>, <c>-n</c>,
/// <c>-i</c>, <c>--update=none|all</c> decides).</summary>
public enum MvExisting
{
    /// <summary>Replace without asking (the default, <c>-f</c>, <c>--update=all</c>).</summary>
    Unspecified,

    /// <summary><c>-n</c>: keep it and SAY so (<c>mv: not replacing 'b'</c>, exit 1).</summary>
    NoClobber,

    /// <summary><c>--update=none</c>: keep it silently, exit 0.</summary>
    UpdateNone,

    /// <summary><c>-i</c>: ask first.</summary>
    Ask,
}

/// <summary>
/// Binary cmdlet <c>Invoke-BashMv</c>: GNU coreutils 9.4 <c>mv</c>. Moves each source operand to the
/// destination. Branches (oracle: GNU 9.4):
/// <list type="bullet">
/// <item>Operand shapes: <c>SOURCE DEST</c>, <c>SOURCE... DIRECTORY</c>, <c>-t DIR SOURCE...</c>,
/// <c>-T SOURCE DEST</c>.</item>
/// <item>Overwrite policy: the last of <c>-f</c>/<c>-n</c>/<c>-i</c>/<c>--update=none|all</c> wins; <c>-u</c> /
/// <c>--update[=older]</c> adds "only when the source is newer" (equal times keep the destination).
/// <c>-i</c> asks <c>mv: overwrite 'b'? </c> on stderr, reading the command's stdin like <c>rm -i</c>; a declined
/// prompt exits 1.</item>
/// <item>Backups (<see cref="BackupControl"/>, shared with cp/ln): <c>-b</c>, <c>--backup[=CONTROL]</c>,
/// <c>-S</c>/<c>--suffix</c>, <c>VERSION_CONTROL</c>, <c>SIMPLE_BACKUP_SUFFIX</c>.</item>
/// <item>Validated before any mutation (<see cref="TransferValidation"/>): same file, directory into itself,
/// several sources onto a non-directory, type clashes; a directory replaces only an EMPTY directory.</item>
/// <item>Verbose output is GNU's <c>renamed 'src' -&gt; 'dest'</c> (plus <c> (backup: 'dest~')</c>).</item>
/// </list>
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec <see cref="MvSpec"/>). The
/// transpiler single-quotes every dash-leading word for mv (<c>PsEmitter.OrderedArgCommands</c>) so flags arrive
/// in <c>Arguments</c> in order; the <c>v</c>/<c>I</c> decoy switches exist ONLY for direct calls and are
/// re-injected first.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMv")]
[OutputType(typeof(string))]
public sealed class InvokeBashMvCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    /// <summary>Decoy for <c>-i</c> (interactive): bare <c>-i</c> prefix-collides with
    /// <c>-InformationAction</c>/<c>-InformationVariable</c> and crashed the binder before the cmdlet ran.</summary>
    [Parameter] public SwitchParameter I { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>The command's stdin, read only for an <c>-i</c> answer; the command runs once, in <see cref="EndProcessing"/>.</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _stdin = new();

    /// <summary>Valid GNU <c>mv</c> options ps-bash refuses: <c>-Z</c>/<c>--context</c> need SELinux.
    /// (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static string sets.)</summary>
    private static readonly string[] MvValidButUnsupported =
        { "-Z", "--context", "--strip-trailing-slashes", "--no-copy", "--debug" };

    private const string OptNoClobber = "no-clobber", OptForce = "force", OptVerbose = "verbose",
        OptInteractive = "interactive", OptBackup = "backup", OptBackupControl = "backup-control",
        OptSuffix = "suffix", OptUpdate = "update", OptTargetDirectory = "target-directory",
        OptNoTargetDirectory = "no-target-directory";

    private const string TryHelp = "\nTry 'mv --help' for more information.";

    /// <summary>GNU mv long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] MvLongOptionOrder =
    {
        "backup", "context", "debug", "force", "interactive", "no-clobber", "no-copy", "no-target-directory",
        "strip-trailing-slashes", "suffix", "target-directory", "update", "verbose", "version",
    };

    /// <summary>mv's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet MvSpec = new(
        new[]
        {
            new OptSpec(OptNoClobber, 'n', "no-clobber"),
            new OptSpec(OptForce, 'f', "force"),
            new OptSpec(OptVerbose, 'v', "verbose"),
            new OptSpec(OptInteractive, 'i', "interactive"),
            new OptSpec(OptBackup, 'b', null),
            new OptSpec(OptBackupControl, '\0', "backup", OptKind.OptionalValue),
            new OptSpec(OptSuffix, 'S', "suffix", OptKind.Value),
            new OptSpec(OptUpdate, 'u', null),
            new OptSpec(OptUpdate, '\0', "update", OptKind.OptionalValue),
            new OptSpec(OptTargetDirectory, 't', "target-directory", OptKind.Value),
            new OptSpec(OptNoTargetDirectory, 'T', "no-target-directory"),
        },
        validButUnsupported: MvValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: MvLongOptionOrder);

    private static readonly (string Name, string Value)[] UpdateWords = { ("all", "all"), ("none", "none"), ("older", "older") };

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, MvSpec);

    /// <summary>The resolved overwrite policy and backup settings of one command line (pure; unit-test seam).</summary>
    internal sealed record MvPlan(MvExisting Existing, bool UpdateOlder, bool BackupEnabled, BackupKind Backup,
        string BackupSuffix, bool Verbose, string? TargetDirectory, bool NoTargetDirectory);

    internal static bool TryPlan(ParsedArgs parsed, Func<string, string?> getenv, out MvPlan plan, out string? error)
    {
        error = null;
        plan = default!;
        var existing = MvExisting.Unspecified;
        bool updateOlder = false, backup = false, verbose = false;
        string? control = null, suffix = null, tdir = null;
        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            switch (t.OptId)
            {
                case OptForce: existing = MvExisting.Unspecified; break;
                case OptNoClobber: existing = MvExisting.NoClobber; break;
                case OptInteractive: existing = MvExisting.Ask; break;
                case OptVerbose: verbose = true; break;
                case OptBackup: backup = true; break;
                case OptBackupControl: backup = true; control = t.Value; break;
                case OptSuffix: backup = true; suffix = t.Value; break;
                case OptTargetDirectory: tdir = t.Value; break;
                case OptUpdate:
                    if (t.Value is null) { updateOlder = true; break; }
                    if (!GnuArgMatch.TryMatch("mv", "update", t.Value, UpdateWords,
                            "  - 'all'\n  - 'none'\n  - 'older'", out var word, out var updateError))
                    {
                        error = updateError;
                        return false;
                    }
                    if (word == "older") updateOlder = true;
                    else if (word == "none") { existing = MvExisting.UpdateNone; updateOlder = false; }
                    else { existing = MvExisting.Unspecified; updateOlder = false; }
                    break;
            }
        }

        bool noT = parsed.Has(OptNoTargetDirectory);
        if (tdir is not null && noT)
        {
            error = "mv: cannot combine --target-directory (-t) and --no-target-directory (-T)";
            return false;
        }

        var kind = BackupKind.None;
        var simple = "~";
        if (backup)
        {
            if (!BackupControl.TryResolve("mv", control, getenv, out kind, out var backupError))
            {
                error = backupError;
                return false;
            }
            simple = BackupControl.Suffix(suffix, getenv);
        }
        plan = new MvPlan(existing, updateOlder, backup, kind, simple, verbose, tdir, noT);
        return true;
    }

    protected override void ProcessRecord()
    {
        if (InputObject != null) _stdin.Add(InputObject);
    }

    protected override void EndProcessing() => Execute();

    private void Execute()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word for mv
        // (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT call
        // (`Invoke-BashMv -v a b`, Pester) binds the decoys instead. Prepending is safe: a decoy can only
        // have been bound before any `--`.
        var args = BashRuntime.PrependDecoys(Arguments, (v.IsPresent, "-v"), (I.IsPresent, "-i"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "mv", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "mv"))
                WriteObject(line);
            return;
        }

        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "mv", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "mv", parsed)) return;
        if (!TryPlan(parsed, BashVariableStore.Get, out var plan, out var planError))
        {
            FileSystemHelpers.WriteBashError(this, planError!);
            return;
        }

        var operands = parsed.Operands();

        List<string> sourceOperands;
        string destRaw;
        bool forceDirectory = false;
        if (plan.TargetDirectory is { } tdir)
        {
            if (operands.Count == 0)
            {
                FileSystemHelpers.WriteBashError(this, "mv: missing file operand" + TryHelp);
                return;
            }
            var tabs = FileSystemHelpers.ProviderPath(this, tdir);
            if (!Directory.Exists(tabs))
            {
                FileSystemHelpers.WriteBashError(this, File.Exists(tabs)
                    ? $"mv: target directory '{tdir}': Not a directory"
                    : $"mv: target directory '{tdir}': No such file or directory");
                return;
            }
            sourceOperands = operands;
            destRaw = tdir;
            forceDirectory = true;
        }
        else
        {
            if (operands.Count == 0)
            {
                FileSystemHelpers.WriteBashError(this, "mv: missing file operand" + TryHelp);
                return;
            }
            if (operands.Count == 1)
            {
                FileSystemHelpers.WriteBashError(this, $"mv: missing destination file operand after '{operands[0]}'" + TryHelp);
                return;
            }
            if (plan.NoTargetDirectory && operands.Count > 2)
            {
                FileSystemHelpers.WriteBashError(this, $"mv: extra operand '{operands[2]}'" + TryHelp);
                return;
            }
            destRaw = operands[^1];
            sourceOperands = operands.GetRange(0, operands.Count - 1);
        }

        var sources = new List<FileSystemHelpers.OperandPath>();
        foreach (var s in sourceOperands)
        {
            foreach (var expanded in FileSystemHelpers.ResolveOperands(this, s))
                sources.Add(expanded);
        }

        bool hadError = false;
        var destAbs = FileSystemHelpers.ProviderPath(this, destRaw);
        bool destIsExistingDir = forceDirectory || (!plan.NoTargetDirectory && Directory.Exists(destAbs));

        // Validate the operand shape BEFORE any mutation (several sources need a directory).
        if (!forceDirectory)
        {
            var shapeError = TransferValidation.CheckOperandShape("mv", sources.Count, destRaw, destAbs);
            if (shapeError != null)
            {
                FileSystemHelpers.WriteBashError(this, shapeError);
                return;
            }
        }

        // Answers to -i prompts come from this command's stdin (the pipeline); none at all is EOF = "no".
        var stdin = new StdinLineSource(this, _stdin);

        foreach (var operand in sources)
        {
            var src = operand.Path;
            var srcDisplay = operand.Display;
            bool srcIsFile = File.Exists(src);
            bool srcIsDir = !srcIsFile && Directory.Exists(src);

            if (!srcIsFile && !srcIsDir && !FileSystemHelpers.IsReparsePoint(src))
            {
                FileSystemHelpers.WriteBashError(this,
                    $"mv: cannot stat '{srcDisplay}': No such file or directory");
                hadError = true;
                continue;
            }
            if (!srcIsFile && !srcIsDir) srcIsFile = true; // a dangling symbolic link moves as a file

            var targetPath = TransferValidation.ResolveTarget(src, destAbs, destIsExistingDir);
            // Diagnostics name the destination as typed (GNU): `mv f d/` reports 'd/f'.
            var targetDisplay = destIsExistingDir ? FileSystemHelpers.JoinDisplay(destRaw, srcDisplay) : destRaw;
            bool targetExists = File.Exists(targetPath) || Directory.Exists(targetPath) || FileSystemHelpers.IsReparsePoint(targetPath);

            // -n: an existing destination is NOT replaced, and GNU 9.4 says so and exits 1
            // (`mv: not replacing 'b'`) — before the same-file check (`mv -n a a` also says this).
            if (plan.Existing == MvExisting.NoClobber && targetExists)
            {
                FileSystemHelpers.WriteBashError(this, $"mv: not replacing '{targetDisplay}'");
                hadError = true;
                continue;
            }
            // --update=none: the same skip, silent, exit 0.
            if (plan.Existing == MvExisting.UpdateNone && targetExists) continue;

            // Identity first (same file / dir into itself): must run before ANY delete, since the
            // resolved target can BE the source (`mv p/src p`).
            var identityError = TransferValidation.CheckIdentity("mv", src, srcIsDir, targetPath, srcDisplay, targetDisplay);
            if (identityError != null)
            {
                FileSystemHelpers.WriteBashError(this, identityError);
                hadError = true;
                continue;
            }

            bool caseOnlyRename = TransferValidation.IsCaseOnlyRename(src, targetPath);
            var occupancyError = caseOnlyRename
                ? null
                : TransferValidation.CheckOccupancy("mv", src, srcIsDir, targetPath, replaceEmptyDirOnly: true,
                    srcDisplay, targetDisplay);
            if (occupancyError != null)
            {
                FileSystemHelpers.WriteBashError(this, occupancyError);
                hadError = true;
                continue;
            }

            var targetParent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
            {
                FileSystemHelpers.WriteBashError(this,
                    $"mv: cannot move '{srcDisplay}' to '{targetDisplay}': No such file or directory");
                hadError = true;
                continue;
            }

            // --update[=older]: keep a destination that is not older than the source.
            if (targetExists && !caseOnlyRename && plan.UpdateOlder && !srcIsDir && File.Exists(targetPath)
                && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(targetPath))
                continue;

            if (targetExists && !caseOnlyRename && plan.Existing == MvExisting.Ask)
            {
                FileSystemHelpers.WriteStderr(this, $"mv: overwrite '{targetDisplay}'? ");
                if (!StdinLineSource.IsYes(stdin.ReadLine()))
                {
                    hadError = true;   // GNU: a declined prompt is exit 1
                    continue;
                }
            }

            try
            {
                string note = "";
                if (targetExists && !caseOnlyRename && plan.BackupEnabled)
                {
                    var suffix = BackupControl.MakeBackup(targetPath, plan.Backup, plan.BackupSuffix);
                    if (suffix is not null) note = $" (backup: '{FileSystemHelpers.ToBashPath(targetDisplay)}{suffix}')";
                }

                if (srcIsDir)
                {
                    // Directory.Move doesn't take an overwrite param. CheckOccupancy already proved
                    // an existing target is an EMPTY directory (GNU lets that be replaced), so this
                    // delete can never destroy content. Force variant: the empty dir may be read-only.
                    if (!caseOnlyRename && Directory.Exists(targetPath))
                        FileSystemHelpers.DeleteDirectoryForce(targetPath);
                    Directory.Move(src, targetPath);
                }
                else
                {
                    File.Move(src, targetPath, overwrite: true);
                }

                if (plan.Verbose)
                {
                    WriteObject(BashRuntime.NewBashObject(
                        $"renamed '{FileSystemHelpers.ToBashPath(srcDisplay)}' -> '{FileSystemHelpers.ToBashPath(targetDisplay)}'{note}\n"));
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"mv: cannot move '{srcDisplay}' to '{targetDisplay}': {ex.Message}");
                hadError = true;
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
