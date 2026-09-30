using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashCp</c> (REFACTOR-2).
/// Copies each source operand to the destination, matching GNU coreutils
/// <c>cp</c> semantics. Supports <c>-r</c> / <c>-R</c> (recursive),
/// <c>-v</c> (verbose), <c>-n</c> (no-clobber), <c>-f</c> (force).
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved
/// byte for byte:
/// <list type="bullet">
/// <item>Fewer than 2 operands → "missing file operand" error and no copy.</item>
/// <item>Last operand is the destination; everything else is a source.
/// Sources are glob-expanded.</item>
/// <item>Source is a directory and <c>-r</c> not set → "omitting directory"
/// error and $LASTEXITCODE=1.</item>
/// <item>Destination is an existing directory → copy each source as a child
/// of the dest dir (preserving the source's basename).</item>
/// <item>With <c>-n</c>, skip a target FILE that already exists (a directory
/// source is still traversed, skipping only conflicting files). Recursive copy
/// MERGES into an existing target directory — nothing there is ever deleted;
/// <c>-f</c> only clears the read-only bit of a file it is about to replace.
/// Pre-mutation checks live in <see cref="TransferValidation"/>.</item>
/// <item>Verbose mode emits <c>'src' -> 'dest'\n</c> per copy.</item>
/// </list>
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec
/// <see cref="CpSpec"/>): bundles of any implemented flags (<c>-rfv</c>), <c>--</c>,
/// long options with unique-prefix abbreviation, and the unsupported/unknown classifier
/// are one left-to-right scan. The transpiler single-quotes every dash-leading word for
/// cp (<c>PsEmitter.OrderedArgCommands</c>), so <c>-r</c>/<c>-R</c> (indistinguishable to
/// the case-insensitive binder) and every colliding letter arrive in <c>Arguments</c>
/// intact. The <c>v</c>/<c>p</c>/<c>I</c>/<c>D</c> decoy switches exist ONLY for direct
/// calls and are re-injected before parsing.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCp")]
[OutputType(typeof(string))]
public sealed class InvokeBashCpCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    /// <summary>
    /// Bash <c>-p</c> (preserve) decoy. The bare token <c>-p</c> prefix-collides
    /// with the value-bearing common parameter <c>-PipelineVariable</c>, which
    /// would otherwise consume the next token (the source) as its value. An exact
    /// single-letter parameter name beats the common-parameter prefix, so a bare
    /// <c>-p</c> binds here; bundled forms (<c>-rp</c>) still flow through
    /// <see cref="Arguments"/>.
    /// </summary>
    [Parameter] public SwitchParameter p { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-i</c> (interactive). The bare <c>-i</c>
    /// prefix-collides with <c>-InformationAction</c>/<c>-InformationVariable</c> and
    /// the binder crashes ("ambiguous") before the classifier could emit its exit-2
    /// "recognized but not supported" message. Re-injected below so the classifier fires.
    /// </summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-d</c> (copy-as-is / no-dereference). Bare
    /// <c>-d</c> silently bound <c>-Debug</c> before this, so the classifier never fired.
    /// </summary>
    [Parameter] public SwitchParameter D { get; set; }

    /// <summary>
    /// Decoy for <c>-a</c> (archive). The bare token prefix-matches this cmdlet's own
    /// <c>-Arguments</c> parameter, which swallowed the flag AND the operands silently, so a direct
    /// <c>Invoke-BashCp -a src dst</c> degraded to a plain non-recursive copy.
    /// </summary>
    [Parameter] public SwitchParameter A { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Valid GNU <c>cp</c> flags ps-bash does not implement. An
    /// option-looking operand matching one of these gets the bash-parity
    /// "recognized but not supported" diagnostic; anything else option-looking
    /// gets "unrecognized/invalid option" (see
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>). Short forms
    /// that prefix-collide with a PowerShell common parameter never reach this
    /// list (the binder eats them first) so the long form is the catchable
    /// one.</summary>
    // (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static
    // string sets to find short flags the binder could eat.)
    private static readonly string[] CpValidButUnsupported =
    {
        "-i", "--interactive", "-l", "--link", "-s", "--symbolic-link",
        "-b", "--backup", "--reflink", "-P", "--no-dereference",
        "-L", "--dereference", "-H", "-t", "--target-directory",
        "-T", "--no-target-directory", "-x", "--one-file-system",
        "--sparse", "--strip-trailing-slashes", "-Z", "--context",
        "--attributes-only", "-d",
        // valid GNU long options
        "--parents", "--remove-destination", "--copy-contents", "--debug", "-S", "--suffix",
    };

    private const string OptRecursive = "recursive", OptNoClobber = "no-clobber", OptForce = "force",
        OptVerbose = "verbose", OptPreserve = "preserve", OptUpdate = "update", OptArchive = "archive",
        OptPreserveList = "preserve-list", OptNoPreserve = "no-preserve";

    /// <summary>GNU cp long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] CpLongOptionOrder = { "archive", "attributes-only", "copy-contents", "context", "debug", "dereference", "no-clobber", "no-dereference", "no-preserve", "no-target-directory", "parents", "preserve", "recursive", "remove-destination", "reflink", "sparse", "strip-trailing-slashes", "suffix", "symbolic-link", "verbose", "version" };

    /// <summary>cp's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet CpSpec = new(
        new[]
        {
            new OptSpec(OptRecursive, 'r', "recursive"),
            new OptSpec(OptRecursive, 'R', null),
            new OptSpec(OptNoClobber, 'n', "no-clobber"),
            new OptSpec(OptForce, 'f', "force"),
            new OptSpec(OptVerbose, 'v', "verbose"),
            new OptSpec(OptPreserve, 'p', null),
            // --preserve[=ATTR_LIST] (argument optional, attached only) and --no-preserve=ATTR_LIST
            // (argument required): resolved in command-line order by CpPreserve.
            new OptSpec(OptPreserveList, '\0', "preserve", OptKind.OptionalValue),
            new OptSpec(OptNoPreserve, '\0', "no-preserve", OptKind.Value),
            new OptSpec(OptUpdate, 'u', null),
            // GNU >= 9.3: --update[=older|all|none|none-fail]; bare / =older is -u.
            new OptSpec(OptUpdate, '\0', "update", OptKind.OptionalValue),
            new OptSpec(OptArchive, 'a', "archive"),
        },
        validButUnsupported: CpValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: CpLongOptionOrder);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, CpSpec);

    protected override void ProcessRecord()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word
        // for cp (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT
        // call (`Invoke-BashCp -v a b`, Pester) binds the decoys instead — bare -i/-d/-v/-p would
        // crash the binder or be silently swallowed as common parameters. Prepending is safe:
        // a decoy can only have been bound before any `--`.
        var args = BashRuntime.PrependDecoys(Arguments,
            (v.IsPresent, "-v"), (p.IsPresent, "-p"), (I.IsPresent, "-i"), (D.IsPresent, "-d"), (A.IsPresent, "-a"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "cp", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "cp"))
            {
                WriteObject(line);
            }
            return;
        }

        // Shared ordered parser: bundles, `--`, long options and abbreviations, and the
        // valid-but-unsupported / unknown classifier are all one scan (see CpSpec). Classification
        // happens DURING the scan, so nothing after `--` is ever an option — `cp -- -a b` copies
        // a file named "-a".
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "cp", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "cp", parsed)) return;

        // --update=WHEN: only the default/`older` policy (== -u) is implemented; refusing the
        // rest loudly beats silently copying with the wrong overwrite policy.
        foreach (var upd in parsed.All(OptUpdate))
        {
            if (upd.Value is not null && upd.Value != "older")
            {
                FileSystemHelpers.WriteBashError(this,
                    $"cp: option '--update={upd.Value}' is recognized but not supported by ps-bash");
                FileSystemHelpers.SetLastExitCode(this, ArgError.UnsupportedExitCode);
                return;
            }
        }

        // -a / --archive == -dR --preserve=all; on Windows we honor the recursive +
        // timestamp/attribute preservation that maps.
        bool archive = parsed.Has(OptArchive);
        bool recursive = archive || parsed.Has(OptRecursive);
        bool noClobber = parsed.Has(OptNoClobber);
        bool force = parsed.Has(OptForce);
        bool verbose = parsed.Has(OptVerbose);
        // -p / -a / --preserve[=LIST] / --no-preserve=LIST, resolved in command-line order. A bad
        // attribute word is a usage error; naming `context` needs SELinux, which is never present here.
        if (!CpPreserve.TryFrom(parsed, OptPreserve, OptArchive, OptPreserveList, OptNoPreserve, out var preserve, out var preserveError))
        {
            FileSystemHelpers.WriteBashError(this, preserveError!);
            return;
        }
        if (preserve.ContextRequested)
        {
            FileSystemHelpers.WriteBashError(this, "cp: cannot preserve security context without an SELinux-enabled kernel");
            return;
        }
        bool update = parsed.Has(OptUpdate);
        var operands = parsed.Operands();

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "cp: missing file operand");
            return;
        }

        var destRaw = operands[^1];
        var sourceOperands = operands.GetRange(0, operands.Count - 1);

        // Expand globs on the source list, preserving order.
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

        // Validate the operand shape BEFORE any write: several sources need an existing
        // directory (`cp a b result` used to leave only b's bytes in a file named result).
        var shapeError = TransferValidation.CheckOperandShape("cp", sources.Count, destRaw, destAbs);
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
                    $"cp: cannot stat '{srcDisplay}': No such file or directory");
                hadError = true;
                continue;
            }

            if (srcIsDir && !recursive)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"cp: -r not specified; omitting directory '{srcDisplay}'");
                hadError = true;
                continue;
            }

            var targetPath = TransferValidation.ResolveTarget(src, destAbs, destIsExistingDir);
            // Diagnostics name the destination as typed (GNU): `cp f d/` reports 'd/f', not the full path.
            var targetDisplay = destIsExistingDir ? FileSystemHelpers.JoinDisplay(destRaw, srcDisplay) : destRaw;

            // -n skips an existing destination FILE before anything else is asked — even when it is
            // the same file (GNU: `cp -n a a` and `cp -n a hardlink-of-a` are silent no-ops, exit 0).
            if (noClobber && !srcIsDir && File.Exists(targetPath))
            {
                continue;
            }

            var identityError = TransferValidation.CheckIdentity("cp", src, srcIsDir, targetPath, srcDisplay, targetDisplay);
            if (identityError != null)
            {
                FileSystemHelpers.WriteBashError(this, identityError);
                hadError = true;
                continue;
            }

            // Type conflicts (dir over file / file over dir) are errors; an existing target
            // DIRECTORY is not — cp merges into it (replaceEmptyDirOnly: false).
            var occupancyError = TransferValidation.CheckOccupancy("cp", src, srcIsDir, targetPath, replaceEmptyDirOnly: false,
                srcDisplay, targetDisplay);
            if (occupancyError != null)
            {
                FileSystemHelpers.WriteBashError(this, occupancyError);
                hadError = true;
                continue;
            }

            // GNU never creates missing parent directories for the destination
            // (`cp f nodir/x` -> "cannot create regular file 'nodir/x': No such file or directory");
            // only an explicit mkdir -p does. Refuse before any write.
            var targetParent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
            {
                FileSystemHelpers.WriteBashError(this,
                    $"cp: cannot create {(srcIsDir ? "directory" : "regular file")} '{targetDisplay}': No such file or directory");
                hadError = true;
                continue;
            }

            // -n skips a conflicting FILE. A directory source is traversed regardless: only the
            // individual files that already exist are skipped (handled per file below).
            if (noClobber && !srcIsDir && File.Exists(targetPath))
            {
                continue;
            }

            try
            {
                if (srcIsDir)
                {
                    // Merge: never delete the existing target tree. Same-named files are replaced
                    // (unless -n), destination-only files survive.
                    var errors = new List<string>();
                    CopyDirectoryRecursive(src, targetPath, preserve, update, noClobber, force, errors,
                        srcDisplay, targetDisplay);
                    if (errors.Count > 0)
                    {
                        foreach (var e in errors) FileSystemHelpers.WriteBashError(this, e);
                        hadError = true;
                    }
                }
                else
                {
                    // -u: skip when the destination exists and is not older than the source.
                    if (update && File.Exists(targetPath)
                        && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(targetPath))
                    {
                        continue;
                    }
                    if (force) FileSystemHelpers.ClearReadOnly(targetPath);
                    CopyFile(src, targetPath, preserve);
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"cp: cannot copy '{srcDisplay}' to '{targetDisplay}': {ex.Message}");
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

    /// <summary>
    /// Merge-copies <paramref name="src"/> into <paramref name="dest"/> (created if absent). Existing
    /// destination entries are never deleted: a same-named file is overwritten (skipped under
    /// <paramref name="noClobber"/> / <paramref name="update"/>), a same-named directory is descended
    /// into, and a file/dir type clash is appended to <paramref name="errors"/> and skipped.
    /// </summary>
    private static void CopyDirectoryRecursive(string src, string dest, CpPreserve preserve, bool update,
        bool noClobber, bool force, List<string> errors, string srcDisplay, string destDisplay)
    {
        bool destExisted = Directory.Exists(dest);
        int? previousMode = destExisted ? PlatformMode.TryGet(dest) : null;
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(file);
            var target = Path.Combine(dest, name);
            var clash = TransferValidation.CheckOccupancy("cp", file, srcIsDir: false, target, replaceEmptyDirOnly: false,
                FileSystemHelpers.JoinDisplay(srcDisplay, name), FileSystemHelpers.JoinDisplay(destDisplay, name));
            if (clash != null) { errors.Add(clash); continue; }
            if (File.Exists(target))
            {
                if (noClobber) continue;
                if (update && File.GetLastWriteTimeUtc(file) <= File.GetLastWriteTimeUtc(target)) continue;
                if (force) FileSystemHelpers.ClearReadOnly(target);
            }
            CopyFile(file, target, preserve);
        }
        foreach (var sub in Directory.EnumerateDirectories(src))
        {
            var subDest = Path.Combine(dest, Path.GetFileName(sub));
            // A directory junction / symlink must be copied as a LINK, never recursed into:
            // recursing would copy the link TARGET's contents (a destructive escape out of the
            // source tree), and a cycle (link -> ancestor) would recurse until the stack overflows.
            if (FileSystemHelpers.IsReparsePoint(sub))
            {
                FileSystemHelpers.TryCopyDirectoryLink(sub, subDest);
                continue;
            }
            var subName = Path.GetFileName(sub);
            var subSrcDisplay = FileSystemHelpers.JoinDisplay(srcDisplay, subName);
            var subDestDisplay = FileSystemHelpers.JoinDisplay(destDisplay, subName);
            var subClash = TransferValidation.CheckOccupancy("cp", sub, srcIsDir: true, subDest, replaceEmptyDirOnly: false,
                subSrcDisplay, subDestDisplay);
            if (subClash != null) { errors.Add(subClash); continue; }
            CopyDirectoryRecursive(sub, subDest, preserve, update, noClobber, force, errors, subSrcDisplay, subDestDisplay);
        }
        // Apply the directory's mode and timestamps LAST — writing children bumps the dir mtime
        // (GNU cp -p restores it after the contents are in place) and a read-only directory must
        // not block its own children.
        ApplyAttributes(src, dest, isDir: true, preserve, destExisted, previousMode);
    }

    /// <summary>
    /// Copies one file and then applies the requested attribute policy (see
    /// <see cref="CpPreserve"/>). The destination's prior Unix mode is captured BEFORE the copy
    /// because <see cref="File.Copy(string, string, bool)"/> overwrites it with the source's.
    /// </summary>
    private static void CopyFile(string src, string dest, CpPreserve preserve)
    {
        bool existed = File.Exists(dest);
        int? previousMode = existed ? PlatformMode.TryGet(dest) : null;
        File.Copy(src, dest, overwrite: true);
        ApplyAttributes(src, dest, isDir: false, preserve, existed, previousMode);
    }

    /// <summary>
    /// Applies the attribute policy to a finished copy, best-effort (a locked attribute or an
    /// unsupported timestamp must not fail the copy itself). <b>Mode</b> is the Unix permission bits
    /// on Linux/macOS — GNU semantics: preserved exactly, cleared to 0666/0777 masked by the umask, or
    /// by default the source's bits masked by the umask for a NEW file while an existing destination
    /// keeps its own; on Windows, where there are no mode bits, the read-only / hidden / archive
    /// attributes stand in (copied when preserving, read-only cleared by <c>--no-preserve=mode</c>).
    /// <b>Timestamps</b> (creation too on Windows) are real on every OS. Ownership, links and xattr
    /// have nothing to do.
    /// </summary>
    private static void ApplyAttributes(string src, string dest, bool isDir, CpPreserve preserve,
        bool destExisted, int? previousMode)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (preserve.Mode == CpModePolicy.Preserve)
                {
                    if (isDir) new DirectoryInfo(dest).Attributes = new DirectoryInfo(src).Attributes;
                    else new FileInfo(dest).Attributes = new FileInfo(src).Attributes;
                }
                else if (preserve.Mode == CpModePolicy.Clear)
                {
                    FileSystemHelpers.ClearReadOnly(dest);
                }
            }
            else
            {
                PlatformMode.Apply(src, dest, isDir, preserve.Mode, destExisted, previousMode);
            }

            if (!preserve.Timestamps)
            {
                // Windows CopyFile carries the source's modification time over; GNU stamps the
                // copy with "now" unless timestamps are preserved. (Linux/macOS already do.)
                if (OperatingSystem.IsWindows() && !isDir)
                {
                    var now = DateTime.UtcNow;
                    File.SetLastWriteTimeUtc(dest, now);
                    File.SetLastAccessTimeUtc(dest, now);
                }
            }
            else
            {
                if (isDir)
                {
                    var s = new DirectoryInfo(src);
                    var d = new DirectoryInfo(dest);
                    d.CreationTimeUtc = s.CreationTimeUtc;
                    d.LastWriteTimeUtc = s.LastWriteTimeUtc;
                    d.LastAccessTimeUtc = s.LastAccessTimeUtc;
                }
                else
                {
                    File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(src));
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                    File.SetLastAccessTimeUtc(dest, File.GetLastAccessTimeUtc(src));
                }
            }
        }
        catch
        {
            // Preservation is best-effort; see above.
        }
    }
}
