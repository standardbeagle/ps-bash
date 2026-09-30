using System.Management.Automation;
using PsBash.Cmdlets.Args;

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
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec
/// <see cref="MvSpec"/>). The transpiler single-quotes every dash-leading word for mv
/// (<c>PsEmitter.OrderedArgCommands</c>) so flags arrive in <c>Arguments</c> in order; the
/// <c>v</c>/<c>I</c> decoy switches exist ONLY for direct calls and are re-injected first.
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
    // (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static
    // string sets to find short flags the binder could eat.)
    private static readonly string[] MvValidButUnsupported =
    {
        "-i", "--interactive", "-b", "--backup", "-S", "--suffix",
        "-u", "--update", "-t", "--target-directory",
        "-T", "--no-target-directory", "--strip-trailing-slashes",
        "-Z", "--context", "--no-copy", "--debug",
    };

    private const string OptNoClobber = "no-clobber", OptForce = "force", OptVerbose = "verbose";

    /// <summary>GNU mv long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] MvLongOptionOrder = { "no-clobber", "no-copy", "no-target-directory", "strip-trailing-slashes", "suffix", "verbose", "version" };

    /// <summary>mv's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet MvSpec = new(
        new[]
        {
            new OptSpec(OptNoClobber, 'n', "no-clobber"),
            new OptSpec(OptForce, 'f', "force"),
            new OptSpec(OptVerbose, 'v', "verbose"),
        },
        validButUnsupported: MvValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: MvLongOptionOrder);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, MvSpec);

    protected override void ProcessRecord()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word
        // for mv (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT
        // call (`Invoke-BashMv -v a b`, Pester) binds the decoys instead. Prepending is safe:
        // a decoy can only have been bound before any `--`.
        var args = BashRuntime.PrependDecoys(Arguments, (v.IsPresent, "-v"), (I.IsPresent, "-i"));

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

        // Shared ordered parser: bundles (-fv, which the old switch never de-bundled), `--`,
        // long options with abbreviation, and the unsupported/unknown classifier in one scan.
        // -f/--force is accepted and is a no-op (File.Move(overwrite:true) already forces).
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "mv", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "mv", parsed)) return;

        // GNU mv: the LAST of -f / -n / -i decides (`mv -n -f a b` replaces, `mv -f -n a b` does not).
        bool noClobber = false;
        foreach (var tok in parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            if (tok.OptId == OptNoClobber) noClobber = true;
            else if (tok.OptId == OptForce) noClobber = false;
        }
        bool verbose = parsed.Has(OptVerbose);
        var operands = parsed.Operands();

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "mv: missing file operand");
            return;
        }

        var destRaw = operands[^1];
        var sourceOperands = operands.GetRange(0, operands.Count - 1);

        var sources = new List<FileSystemHelpers.OperandPath>();
        foreach (var s in sourceOperands)
        {
            foreach (var expanded in FileSystemHelpers.ResolveOperands(this, s))
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

        foreach (var operand in sources)
        {
            var src = operand.Path;
            var srcDisplay = operand.Display;
            bool srcIsFile = File.Exists(src);
            bool srcIsDir = !srcIsFile && Directory.Exists(src);

            if (!srcIsFile && !srcIsDir)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"mv: cannot stat '{srcDisplay}': No such file or directory");
                hadError = true;
                continue;
            }

            var targetPath = TransferValidation.ResolveTarget(src, destAbs, destIsExistingDir);
            // Diagnostics name the destination as typed (GNU): `mv f d/` reports 'd/f'.
            var targetDisplay = destIsExistingDir ? FileSystemHelpers.JoinDisplay(destRaw, srcDisplay) : destRaw;

            // -n: an existing destination is NOT replaced, and GNU 9.4 says so and exits 1
            // (`mv: not replacing 'b'`) — before the same-file check (`mv -n a a` also says this).
            if (noClobber && (File.Exists(targetPath) || Directory.Exists(targetPath)))
            {
                FileSystemHelpers.WriteBashError(this, $"mv: not replacing '{targetDisplay}'");
                hadError = true;
                continue;
            }

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
                    $"mv: cannot move '{srcDisplay}' to '{targetDisplay}': {ex.Message}");
                hadError = true;
                continue;
            }

            if (verbose)
            {
                WriteObject(BashRuntime.NewBashObject(
                    $"'{FileSystemHelpers.ToBashPath(srcDisplay)}' -> '{FileSystemHelpers.ToBashPath(targetDisplay)}'\n"));
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
