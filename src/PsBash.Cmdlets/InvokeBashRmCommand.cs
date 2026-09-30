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
    /// Decoy for <c>-i</c>/<c>-I</c> (interactive). Bare <c>-i</c> prefix-collides with
    /// <c>-InformationAction</c>/<c>-InformationVariable</c> and crashed the binder. The binder is
    /// case-insensitive, so this one switch receives both spellings; the typed case is recovered
    /// from the command's own pipeline segment and re-injected in <see cref="Execute"/>.
    /// </summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>
    /// Decoy for <c>-d</c> (remove empty dirs). Bare <c>-d</c> silently bound <c>-Debug</c>.
    /// Re-injected so a direct call behaves like the transpiled one.
    /// </summary>
    [Parameter] public SwitchParameter D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Valid GNU <c>rm</c> flags ps-bash does not implement (only the root-protection
    /// and mount-boundary options are left: <c>-i</c>/<c>-I</c>/<c>--interactive</c>/<c>-d</c> are
    /// implemented). See
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/> /
    /// <see cref="InvokeBashCpCommand"/> for the classification contract.</summary>
    // (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static
    // string sets to find short flags the binder could eat.)
    private static readonly string[] RmValidButUnsupported =
    {
        "--one-file-system", "--no-preserve-root", "--preserve-root",
    };

    private const string OptRecursive = "recursive", OptForce = "force", OptVerbose = "verbose",
        OptDir = "dir", OptPromptAlways = "prompt-always", OptPromptOnce = "prompt-once",
        OptInteractive = "interactive";

    /// <summary>rm's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet RmSpec = new(
        new[]
        {
            new OptSpec(OptRecursive, 'r', "recursive"),
            new OptSpec(OptRecursive, 'R', null),
            new OptSpec(OptForce, 'f', "force"),
            new OptSpec(OptVerbose, 'v', "verbose"),
            new OptSpec(OptDir, 'd', "dir"),
            new OptSpec(OptPromptAlways, 'i', null),
            new OptSpec(OptPromptOnce, 'I', null),
            // --interactive[=WHEN]: the argument is optional and only ever attached.
            new OptSpec(OptInteractive, '\0', "interactive", OptKind.OptionalValue),
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

    /// <summary>
    /// The command's stdin. Only read for an interactive answer (<c>rm -i</c>/<c>-I</c>); collected
    /// here and acted on in <see cref="EndProcessing"/> so the whole command runs ONCE, not per record.
    /// </summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _stdin = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _stdin.Add(InputObject);
    }

    protected override void EndProcessing() => Execute();

    /// <summary>
    /// GNU <c>--interactive[=WHEN]</c> argument (<c>XARGMATCH</c>): <c>never|no|none</c>,
    /// <c>once</c>, <c>always|yes</c>; a bare <c>--interactive</c> means <c>always</c>. An exact
    /// name wins; otherwise a unique prefix does (<c>n</c> is fine — all three spellings mean the same
    /// thing); a prefix shared by different meanings (<c>''</c>) is ambiguous. Pure: unit-tested.
    /// </summary>
    internal static bool TryParseInteractiveWhen(string? arg, out RmPrompt mode, out string? error)
    {
        error = null;
        if (arg is null) { mode = RmPrompt.Always; return true; }
        return GnuArgMatch.TryMatch("rm", "interactive", arg, InteractiveWords,
            "  - 'never', 'no', 'none'\n  - 'once'\n  - 'always', 'yes'", out mode, out error);
    }

    private static readonly (string Name, RmPrompt Value)[] InteractiveWords =
    {
        ("never", RmPrompt.Never), ("no", RmPrompt.Never), ("none", RmPrompt.Never),
        ("once", RmPrompt.Once),
        ("always", RmPrompt.Always), ("yes", RmPrompt.Always),
    };

    private void Execute()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word
        // for rm (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT
        // call (`Invoke-BashRm -v f`, Pester) binds the decoys instead (bare -v/-i/-d never reach
        // Arguments). Prepending is safe: a decoy can only have been bound before any `--`.
        // The case-insensitive binder cannot tell a bare `-i` (ask each) from `-I` (ask once): one
        // decoy binds both, so recover the typed case from THIS command's own pipeline segment.
        var interactiveFlag = I.IsPresent
            && System.Text.RegularExpressions.Regex.IsMatch(
                BashRuntime.CurrentPipelineSegment(MyInvocation), @"(?<![\w-])-I(?![\w])")
            ? "-I" : "-i";
        var args = BashRuntime.PrependDecoys(Arguments, (v.IsPresent, "-v"), (I.IsPresent, interactiveFlag), (D.IsPresent, "-d"));

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
        bool verbose = parsed.Has(OptVerbose);
        bool dirOnly = parsed.Has(OptDir);
        var operands = parsed.Operands();

        // -f / -i / -I / --interactive[=WHEN] share ONE setting and the LAST one wins (`-if` does not
        // ask, `-fi` does), exactly as in GNU rm.c. Only -f (and -i/-I/--interactive=once|always)
        // touch "ignore missing operands"; `--interactive=never` leaves it as it was.
        var mode = RmPrompt.Never;
        bool ignoreMissing = false;
        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            switch (t.OptId)
            {
                case OptForce:
                    mode = RmPrompt.Never; ignoreMissing = true; break;
                case OptPromptAlways:
                    mode = RmPrompt.Always; ignoreMissing = false; break;
                case OptPromptOnce:
                    mode = RmPrompt.Once; ignoreMissing = false; break;
                case OptInteractive:
                    if (!TryParseInteractiveWhen(t.Value, out var when, out var whenError))
                    {
                        FileSystemHelpers.WriteBashError(this, whenError!);
                        return;
                    }
                    mode = when;
                    if (when != RmPrompt.Never) ignoreMissing = false;
                    break;
            }
        }
        bool promptEach = mode == RmPrompt.Always;

        if (operands.Count == 0)
        {
            if (!ignoreMissing)
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

        // Answers to prompts come from this command's stdin (the pipeline); none at all is EOF = "no".
        var stdin = new StdinLineSource(this, _stdin);
        bool Confirm(string prompt)
        {
            FileSystemHelpers.WriteStderr(this, prompt);
            return StdinLineSource.IsYes(stdin.ReadLine());
        }

        // -I: ask ONCE, up front, when removing more than three operands or anything recursively.
        if (mode == RmPrompt.Once && resolved.Count > 0 && (recursive || resolved.Count > 3))
        {
            var n = resolved.Count;
            if (!Confirm($"rm: remove {n} argument{(n == 1 ? "" : "s")}{(recursive ? " recursively" : "")}? "))
                return;
        }

        var remover = new RmRemover(recursive, dirOnly, promptEach, verbose, Confirm,
            say: text => WriteObject(BashRuntime.NewBashObject(text)),
            error: message => FileSystemHelpers.WriteBashError(this, message));

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
                if (!ignoreMissing)
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: cannot remove '{display}': No such file or directory");
                    hadError = true;
                }
                continue;
            }

            // Anything rm has to SAY while it works (a prompt, -v lines, -d on a directory) goes
            // through the step-by-step walk; a quiet removal keeps the native fast path below.
            if (promptEach || verbose || (dirOnly && isDir && !recursive))
            {
                if (!remover.Remove(target, display)) hadError = true;
                continue;
            }

            if (isDir && !recursive)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"rm: cannot remove '{display}': Is a directory");
                hadError = true;
                continue;
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
