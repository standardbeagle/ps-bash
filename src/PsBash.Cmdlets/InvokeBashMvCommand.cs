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

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

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

        foreach (var a in args)
        {
            switch (a)
            {
                case "-n": noClobber = true; break;
                case "-f": /* no-op: File.Move(overwrite:true) already forces */ break;
                case "-v": verbose = true; break;
                default: operands.Add(a); break;
            }
        }

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

            string targetPath = destAbs;
            if (destIsExistingDir)
            {
                targetPath = Path.Combine(destAbs, Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }

            if (noClobber && (File.Exists(targetPath) || Directory.Exists(targetPath)))
            {
                continue;
            }

            try
            {
                if (srcIsDir)
                {
                    // Directory.Move doesn't take an overwrite param; do the
                    // remove-then-move dance only when target exists.
                    if (Directory.Exists(targetPath))
                    {
                        // Read-only-aware force delete (Windows .git packs / node_modules)
                        // — was a plain Directory.Delete that threw on read-only descendants.
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
