using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRmdir</c>
/// (REFACTOR-2). Removes each operand directory, matching GNU coreutils
/// <c>rmdir</c> semantics — only empty directories are removed, with
/// <c>-p</c> chaining up through empty parent dirs and <c>-v</c> emitting
/// a verbose line per removal.
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved:
/// <list type="bullet">
/// <item>No operands → bash-style "missing operand" error.</item>
/// <item>Target missing → bash-style "No such file or directory".</item>
/// <item>Target is a file → "Not a directory" error and $LASTEXITCODE=1.</item>
/// <item>Target is a non-empty directory → "Directory not empty" error and
/// $LASTEXITCODE=1.</item>
/// <item>With <c>-p</c>, after the leaf is removed, remove the ancestors the operand's own text
/// names (<see cref="OperandChain.RemovalParents"/>); the first failure is an error and stops.</item>
/// </list>
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec
/// <c>RmdirSpec</c>); same delivery contract as <see cref="InvokeBashMkdirCommand"/>: the
/// transpiler single-quotes every dash word (<c>PsEmitter.OrderedArgCommands</c>) and the
/// <c>p</c>/<c>v</c> decoy switches exist ONLY for direct calls.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRmdir")]
[OutputType(typeof(string))]
public sealed class InvokeBashRmdirCommand : BashCmdlet
{
    [Parameter] public SwitchParameter p { get; set; }
    [Parameter] public SwitchParameter v { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Valid GNU <c>rmdir</c> flags ps-bash does not implement.
    /// Classified via <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>.</summary>
    // (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static
    // string sets to find short flags the binder could eat.) GNU coreutils 9.4 rmdir has NO
    // -Z/--context (`rmdir -Z` is "invalid option -- 'Z'"), so they are not listed here.
    private static readonly string[] RmdirValidButUnsupported =
    {
        "--ignore-fail-on-non-empty",
    };

    private const string OptParents = "parents", OptVerbose = "verbose";

    /// <summary>rmdir's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet RmdirSpec = new(
        new[]
        {
            new OptSpec(OptParents, 'p', "parents"),
            new OptSpec(OptVerbose, 'v', "verbose"),
        },
        validButUnsupported: RmdirValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, RmdirSpec);

    protected override void ProcessRecord()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word
        // for rmdir (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT
        // call (`Invoke-BashRmdir -p d`, Pester) binds the decoys instead. Prepending is safe: a
        // decoy can only have been bound before any `--`.
        var args = BashRuntime.PrependDecoys(Arguments, (p.IsPresent, "-p"), (v.IsPresent, "-v"));
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "rmdir", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "rmdir"))
            {
                WriteObject(line);
            }
            return;
        }

        // Shared ordered parser: bundles in any order (-pv, -vp, -pp), `--`, unique-prefix long
        // options, and the unsupported/unknown classifier in ONE scan.
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "rmdir", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "rmdir", parsed)) return;

        bool removeParents = parsed.Has(OptParents);
        bool verbose = parsed.Has(OptVerbose);
        var operands = parsed.Operands();

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteMissingOperand(this, "rmdir");
            return;
        }

        bool hadError = false;

        foreach (var dir in operands)
        {
            // GNU names each attempt BEFORE it (`rmdir: removing directory, 'X'`), the operand first.
            if (verbose) Removing(dir);
            if (TryRemove(dir) is { } why)
            {
                FileSystemHelpers.WriteBashError(this, $"rmdir: failed to remove '{dir}': {why}");
                hadError = true;
                continue;
            }

            if (!removeParents) continue;
            // -p: the ancestors the operand's OWN text names (`a/x//y/` → `a/x`, `a`), never the absolute
            // parents above it; the first that cannot go stops the walk with an error (GNU remove_parents).
            foreach (var parent in OperandChain.RemovalParents(dir))
            {
                if (verbose) Removing(parent);
                if (TryRemove(parent) is { } pwhy)
                {
                    FileSystemHelpers.WriteBashError(this, $"rmdir: failed to remove directory '{parent}': {pwhy}");
                    hadError = true;
                    break;
                }
            }
        }
        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    private void Removing(string typed)
        => WriteObject(BashRuntime.NewBashObject($"rmdir: removing directory, '{BashPath.ToShell(typed)}'\n"));

    /// <summary>Removes the empty directory <paramref name="typed"/>; null on success, else GNU's reason text.</summary>
    private string? TryRemove(string typed)
    {
        // rmdir(".") is EINVAL; never hand "." to Directory.Delete (it would remove the working directory).
        string trimmed = typed.TrimEnd('/');
        if (trimmed == "." || trimmed.EndsWith("/.", StringComparison.Ordinal)) return "Invalid argument";
        var absolute = FileSystemHelpers.ProviderPath(this, typed);
        if (!Directory.Exists(absolute))
            return File.Exists(absolute) ? "Not a directory" : "No such file or directory";
        try
        {
            if (HasAnyEntries(absolute)) return "Directory not empty";
            // Non-recursive (rmdir removes only empty dirs), but a read-only attribute still makes
            // Directory.Delete throw on Windows. Clear it first (os-interface rule).
            FileSystemHelpers.ClearReadOnly(absolute);
            Directory.Delete(absolute);
            return null;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            return FileSystemHelpers.ReadErrorMessage(ex, absolute);
        }
    }

    // EnumerateFileSystemEntries returns hidden + system entries by default    // on Windows, matching the psm1 oracle's Get-ChildItem -Force semantics.
    private static bool HasAnyEntries(string dir)
    {
        using var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
        return e.MoveNext();
    }
}
