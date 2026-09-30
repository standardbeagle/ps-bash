using System.Linq;
using System.Management.Automation;
using System.Runtime.InteropServices;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRm</c> (REFACTOR-2).
/// Removes each operand, matching GNU coreutils <c>rm</c> semantics with the
/// project's added safety guards (path-root protection, Windows
/// reserved-device-name refusal). Supports <c>-r</c> / <c>-R</c> (recursive),
/// <c>-f</c> (force — suppresses missing-file errors and the missing-operand
/// error), <c>-v</c> (verbose).
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved:
/// <list type="bullet">
/// <item>No operands and <c>-f</c> not set → "missing operand" error.
/// With <c>-f</c>, silent.</item>
/// <item>Windows reserved device name in the leaf → refuse with "Windows
/// reserved device name" error and $LASTEXITCODE=1. Preserves the psm1
/// oracle's CON / PRN / AUX / NUL / COM1-9 / LPT1-9 list.</item>
/// <item>Path equals a drive root or user-profile root → refuse with
/// "refusing to remove: protected path".</item>
/// <item>Target missing and <c>-f</c> not set → "No such file or directory"
/// error and $LASTEXITCODE=1. With <c>-f</c>, silent skip.</item>
/// <item>Target is a directory and <c>-r</c> not set → "Is a directory"
/// error and $LASTEXITCODE=1.</item>
/// <item>Verbose mode emits <c>removed '&lt;path&gt;'\n</c> per file; with
/// <c>-rv</c> over a directory, lists each child first, then the directory.</item>
/// </list>
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec
/// <c>RmSpec</c>). The transpiler single-quotes every dash-leading word for rm
/// (<c>PsEmitter.OrderedArgCommands</c>) so flags arrive in <c>Arguments</c> in order; the
/// <c>v</c>/<c>I</c>/<c>D</c> decoy switches exist ONLY for direct calls and are re-injected first.
/// The path-root / reserved-device safety guards below are untouched by the migration.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRm")]
[OutputType(typeof(string))]
public sealed class InvokeBashRmCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-i</c>/<c>-I</c> (interactive). Bare
    /// <c>-i</c> prefix-collides with <c>-InformationAction</c>/<c>-InformationVariable</c>
    /// and crashed the binder — so "rm -i fires even under -f" (the spec) was impossible.
    /// Re-injected below so the classifier fires exit 2.
    /// </summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-d</c> (remove empty dirs). Bare <c>-d</c>
    /// silently bound <c>-Debug</c>, so the classifier never fired.
    /// </summary>
    [Parameter] public SwitchParameter D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Valid GNU <c>rm</c> flags ps-bash does not implement. See
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/> /
    /// <see cref="InvokeBashCpCommand"/> for the classification contract.</summary>
    // (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static
    // string sets to find short flags the binder could eat.)
    private static readonly string[] RmValidButUnsupported =
    {
        "-i", "-I", "--interactive", "-d", "--dir",
        "--one-file-system", "--no-preserve-root", "--preserve-root",
    };

    private const string OptRecursive = "recursive", OptForce = "force", OptVerbose = "verbose";

    /// <summary>rm's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet RmSpec = new(
        new[]
        {
            new OptSpec(OptRecursive, 'r', "recursive"),
            new OptSpec(OptRecursive, 'R', null),
            new OptSpec(OptForce, 'f', "force"),
            new OptSpec(OptVerbose, 'v', "verbose"),
        },
        validButUnsupported: RmValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, RmSpec);

    private static readonly HashSet<string> WinReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    protected override void ProcessRecord()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word
        // for rm (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT
        // call (`Invoke-BashRm -v f`, Pester) binds the decoys instead (bare -v/-i/-d never reach
        // Arguments). Prepending is safe: a decoy can only have been bound before any `--`.
        var args = BashRuntime.PrependDecoys(Arguments, (v.IsPresent, "-v"), (I.IsPresent, "-i"), (D.IsPresent, "-d"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "rm", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "rm"))
            {
                WriteObject(line);
            }
            return;
        }

        // Shared ordered parser: bundles in any order/repeat (-rf, -rvf, -vv), `--`, long options
        // with unique-prefix abbreviation, and the unsupported/unknown classifier in ONE scan.
        // The classifier runs regardless of -f: GNU `rm -f` suppresses missing-file errors, NOT a
        // usage error for a bad option (a valid-but-unsupported -i/-I/-d is refused loudly, never
        // silently ignored). Nothing after `--` is ever classified: `rm -- -weird` deletes it.
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "rm", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "rm", parsed)) return;

        bool recursive = parsed.Has(OptRecursive);
        bool force = parsed.Has(OptForce);
        bool verbose = parsed.Has(OptVerbose);
        var operands = parsed.Operands();

        if (operands.Count == 0)
        {
            if (!force)
            {
                FileSystemHelpers.WriteBashError(this, "rm: missing operand");
            }
            return;
        }

        var resolved = new List<FileSystemHelpers.OperandPath>();
        foreach (var op in operands)
        {
            foreach (var expanded in FileSystemHelpers.ResolveOperands(this, op))
            {
                resolved.Add(expanded);
            }
        }

        bool hadError = false;
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var operand in resolved)
        {
            var target = operand.Path;
            // Diagnostics quote the operand AS TYPED (GNU: `rm: cannot remove 'nosuch'`), never the
            // resolved full path.
            var display = operand.Display;
            // Windows reserved-device-name guard (psm1 oracle parity).
            if (isWindows)
            {
                var leaf = Path.GetFileName(target.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var baseName = leaf.Contains('.')
                    ? leaf.Substring(0, leaf.IndexOf('.'))
                    : leaf;
                if (WinReservedNames.Contains(baseName))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: cannot remove '{display}': Windows reserved device name");
                    hadError = true;
                    continue;
                }
            }

            // Protected-path guard: refuse to delete the drive root or the
            // current user's home directory. Mirrors the psm1 oracle's
            // "refusing to remove: protected path" branch.
            string? resolvedFull = null;
            try
            {
                if (File.Exists(target) || Directory.Exists(target))
                {
                    resolvedFull = Path.GetFullPath(target);
                }
            }
            catch { /* fall through */ }

            if (resolvedFull is not null)
            {
                var normalized = resolvedFull.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var pathRoot = Path.GetPathRoot(resolvedFull) ?? string.Empty;
                var normalizedRoot = pathRoot.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                bool isProtected = false;

                // Filesystem root: on POSIX `Path.GetPathRoot("/")` returns
                // "/" which trims to "" — both the path and root are empty
                // after trimming. Catch this case explicitly so the protected
                // guard fires for "/" (not just for a non-empty Windows
                // drive root like "C:").
                if (string.IsNullOrEmpty(normalized) && !string.IsNullOrEmpty(pathRoot))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: refusing to remove '{display}': protected path");
                    hadError = true;
                    isProtected = true;
                }
                else if (!string.IsNullOrEmpty(normalizedRoot) &&
                         string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: refusing to remove '{display}': protected path");
                    hadError = true;
                    isProtected = true;
                }
                else if (!string.IsNullOrEmpty(homeDir))
                {
                    var normalizedHome = homeDir.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!string.IsNullOrEmpty(normalizedHome) &&
                        string.Equals(normalized, normalizedHome, StringComparison.OrdinalIgnoreCase))
                    {
                        FileSystemHelpers.WriteBashError(this,
                            $"rm: refusing to remove '{display}': protected path");
                        hadError = true;
                        isProtected = true;
                    }
                }
                if (isProtected) continue;
            }

            bool isFile = File.Exists(target);
            bool isDir = !isFile && Directory.Exists(target);

            if (!isFile && !isDir)
            {
                if (!force)
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: cannot remove '{display}': No such file or directory");
                    hadError = true;
                }
                continue;
            }

            if (isDir && !recursive)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"rm: cannot remove '{display}': Is a directory");
                hadError = true;
                continue;
            }

            if (verbose && isDir && recursive)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(
                             target, "*", SearchOption.AllDirectories))
                {
                    WriteObject(BashRuntime.NewBashObject(
                        $"removed '{FileSystemHelpers.AppendDisplay(display, Path.GetRelativePath(target, child))}'\n"));
                }
            }

            if (verbose)
            {
                WriteObject(BashRuntime.NewBashObject(
                    $"removed '{FileSystemHelpers.ToBashPath(display)}'\n"));
            }

            try
            {
                // Shared OS-interface force-delete: native recursive delete with a
                // read-only-clearing fallback for Windows (.git packs, node_modules).
                FileSystemHelpers.DeleteEntryForce(target, isDir);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"rm: cannot remove '{display}': {ex.Message}");
                hadError = true;
                continue;
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
