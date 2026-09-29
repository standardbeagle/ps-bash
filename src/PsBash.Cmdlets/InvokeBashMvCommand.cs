using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashMv</c> (REFACTOR-2).
/// Moves each source operand to the destination, matching GNU coreutils
/// <c>mv</c> semantics. Supports <c>-v</c> (verbose), <c>-n</c>
/// (no-clobber), <c>-f</c> (force — accepted as a no-op since the
/// underlying <see cref="File.Move(string, string, bool)"/> overwrite call
/// already replaces an existing target).
///
/// Behavioral parity oracle: the psm1 function. Branches preserved:
/// <list type="bullet">
/// <item>Fewer than 2 operands → "missing file operand" error.</item>
/// <item>Last operand is the destination; sources are glob-expanded.</item>
/// <item>Destination is an existing directory → move source into it,
/// preserving the source's basename.</item>
/// <item>With <c>-n</c>, skip if target already exists.</item>
/// <item>Validated before any mutation (<see cref="TransferValidation"/>): same
/// file, directory into itself, several sources onto a non-directory, and
/// type clashes are refused; a directory replaces only an EMPTY directory.</item>
/// <item>Verbose mode emits <c>'src' -> 'dest'\n</c> per move.</item>
/// </list>
/// <para>
/// <b>One colliding flag</b> declared explicitly: <c>-v</c> vs
/// <c>-Verbose</c>. <c>-n</c> / <c>-f</c> stay in <c>Arguments</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMv")]
[OutputType(typeof(string))]
public sealed class InvokeBashMvCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-i</c> (interactive). Bare <c>-i</c>
    /// prefix-collides with <c>-InformationAction</c>/<c>-InformationVariable</c> and
    /// crashed the binder before the classifier. Re-injected below so it fires exit 2.
    /// </summary>
    [Parameter] public SwitchParameter I { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Valid GNU <c>mv</c> flags ps-bash does not implement. See
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/> /
    /// <see cref="InvokeBashCpCommand"/> for the classification contract.</summary>
    private static readonly HashSet<string> MvValidButUnsupported = new(StringComparer.Ordinal)
    {
        "-i", "--interactive", "-b", "--backup", "-S", "--suffix",
        "-u", "--update", "-t", "--target-directory",
        "-T", "--no-target-directory", "--strip-trailing-slashes",
        "-Z", "--context",
    };

    protected override void ProcessRecord()
    {
        // Re-inject the decoy-bound -i so the classifier still emits exit 2.
        var args = BashRuntime.PrependDecoys(Arguments, (I.IsPresent, "-i"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "mv", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "mv"))
            {
                WriteObject(line);
            }
            return;
        }

        bool noClobber = false;
        bool verbose = v.IsPresent;
        var operands = new List<string>();
        bool pastDoubleDash = false;
        int preDashCount = -1;

        foreach (var a in args)
        {
            if (pastDoubleDash) { operands.Add(a); continue; }
            switch (a)
            {
                case "--": pastDoubleDash = true; preDashCount = operands.Count; break;
                case "-n": case "--no-clobber": noClobber = true; break;
                case "-f": case "--force": /* no-op: File.Move(overwrite:true) already forces */ break;
                case "-v": case "--verbose": verbose = true; break;
                default: operands.Add(a); break;
            }
        }

        var mvToClassify = preDashCount < 0 ? operands : operands.GetRange(0, preDashCount);
        if (FileSystemHelpers.TryWriteOperandOptionError(this, "mv", mvToClassify, MvValidButUnsupported))
            return;

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "mv: missing file operand");
            return;
        }

        var destRaw = operands[^1];
        var sourceOperands = operands.GetRange(0, operands.Count - 1);

        var sources = new List<string>();
        foreach (var s in sourceOperands)
        {
            foreach (var expanded in FileSystemHelpers.ResolveOperandPaths(this, s))
            {
                sources.Add(expanded);
            }
        }

        bool hadError = false;
        var destAbs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(destRaw);
        bool destIsExistingDir = Directory.Exists(destAbs);

        // Validate the operand shape BEFORE any mutation (several sources need a directory).
        var shapeError = TransferValidation.CheckOperandShape("mv", sources.Count, destRaw, destAbs);
        if (shapeError != null)
        {
            FileSystemHelpers.WriteBashError(this, shapeError);
            return;
        }

        foreach (var src in sources)
        {
            bool srcIsFile = File.Exists(src);
            bool srcIsDir = !srcIsFile && Directory.Exists(src);

            if (!srcIsFile && !srcIsDir)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"mv: cannot stat '{src}': No such file or directory");
                hadError = true;
                continue;
            }

            var targetPath = TransferValidation.ResolveTarget(src, destAbs, destIsExistingDir);

            // Identity first (same file / dir into itself): must run before ANY delete, since the
            // resolved target can BE the source (`mv p/src p`).
            var identityError = TransferValidation.CheckIdentity("mv", src, srcIsDir, targetPath);
            if (identityError != null)
            {
                FileSystemHelpers.WriteBashError(this, identityError);
                hadError = true;
                continue;
            }

            if (noClobber && (File.Exists(targetPath) || Directory.Exists(targetPath)))
            {
                continue;
            }

            bool caseOnlyRename = TransferValidation.IsCaseOnlyRename(src, targetPath);
            var occupancyError = caseOnlyRename
                ? null
                : TransferValidation.CheckOccupancy("mv", src, srcIsDir, targetPath, replaceEmptyDirOnly: true);
            if (occupancyError != null)
            {
                FileSystemHelpers.WriteBashError(this, occupancyError);
                hadError = true;
                continue;
            }

            try
            {
                if (srcIsDir)
                {
                    // Directory.Move doesn't take an overwrite param. CheckOccupancy already proved
                    // an existing target is an EMPTY directory (GNU lets that be replaced), so this
                    // delete can never destroy content. Force variant: the empty dir may be read-only.
                    if (!caseOnlyRename && Directory.Exists(targetPath))
                    {
                        FileSystemHelpers.DeleteDirectoryForce(targetPath);
                    }
                    Directory.Move(src, targetPath);
                }
                else
                {
                    File.Move(src, targetPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"mv: cannot move '{src}' to '{targetPath}': {ex.Message}");
                hadError = true;
                continue;
            }

            if (verbose)
            {
                WriteObject(BashRuntime.NewBashObject(
                    $"'{FileSystemHelpers.ToBashPath(src)}' -> '{FileSystemHelpers.ToBashPath(targetPath)}'\n"));
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
