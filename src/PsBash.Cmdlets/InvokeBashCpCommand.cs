using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet <c>Invoke-BashCp</c>: GNU coreutils 9.4 <c>cp</c>. Copies each source operand to the
/// destination; the per-entry behaviour (identity, overwrite policy, backups, links, reflink, symlink
/// handling, <c>-v</c>/<c>--debug</c> output) is <see cref="CpEngine"/>, the option resolution is
/// <see cref="CpPlan"/>; this class owns the OPERAND SHAPES: <c>-t DIR</c>, <c>-T</c>, <c>--parents</c>,
/// <c>--strip-trailing-slashes</c>, glob expansion, "several sources need a directory".
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec <see cref="CpSpec"/>).
/// The transpiler single-quotes every dash-leading word for cp (<c>PsEmitter.OrderedArgCommands</c>), so
/// <c>-r</c>/<c>-R</c> (indistinguishable to the case-insensitive binder) and every colliding letter arrive in
/// <c>Arguments</c> intact. The <c>v</c>/<c>p</c>/<c>I</c>/<c>D</c>/<c>A</c> decoy switches exist ONLY for
/// direct calls and are re-injected before parsing. (<c>-P</c> cannot be decoyed: parameter names are
/// case-insensitive and <c>p</c> is taken — quote it in a direct call.)
/// </para>
/// <para>
/// <b>Prompts</b> (<c>-i</c>) read their answers from the command's stdin exactly like <c>rm -i</c>
/// (<see cref="StdinLineSource"/>); no answer is "no", and — unlike rm — a declined prompt makes the exit
/// status 1.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCp")]
[OutputType(typeof(string))]
public sealed class InvokeBashCpCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    /// <summary>
    /// Bash <c>-p</c> (preserve) decoy. The bare token <c>-p</c> prefix-collides with the value-bearing
    /// common parameter <c>-PipelineVariable</c>, which would otherwise consume the next token (the source)
    /// as its value. An exact single-letter parameter name beats the common-parameter prefix, so a bare
    /// <c>-p</c> binds here; bundled forms (<c>-rp</c>) still flow through <see cref="Arguments"/>.
    /// </summary>
    [Parameter] public SwitchParameter p { get; set; }

    /// <summary>Decoy for <c>-i</c> (interactive): bare <c>-i</c> prefix-collides with
    /// <c>-InformationAction</c>/<c>-InformationVariable</c> and the binder would crash ("ambiguous").</summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>Decoy for <c>-d</c> (no-dereference, preserve links): bare <c>-d</c> silently bound <c>-Debug</c>.</summary>
    [Parameter] public SwitchParameter D { get; set; }

    /// <summary>Decoy for <c>-a</c> (archive): the bare token prefix-matches this cmdlet's own
    /// <c>-Arguments</c> parameter, which swallowed the flag AND the operands silently.</summary>
    [Parameter] public SwitchParameter A { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>The command's stdin, read only for an <c>-i</c> answer; the command runs once, in <see cref="EndProcessing"/>.</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _stdin = new();

    /// <summary>Valid GNU <c>cp</c> options ps-bash refuses. <c>-Z</c>/<c>--context</c> need SELinux, which no
    /// supported platform has; <c>--copy-contents</c> only matters for special files (FIFOs), which cp here never copies.
    /// (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static string sets.)</summary>
    private static readonly string[] CpValidButUnsupported =
    {
        "-Z", "--context", "--copy-contents",
    };

    /// <summary>GNU cp long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] CpLongOptionOrder =
    {
        "archive", "attributes-only", "copy-contents", "context", "debug", "dereference", "no-clobber",
        "no-dereference", "no-preserve", "no-target-directory", "parents", "preserve", "recursive",
        "remove-destination", "reflink", "sparse", "strip-trailing-slashes", "suffix", "symbolic-link",
        "verbose", "version",
    };

    /// <summary>cp's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet CpSpec = new(
        new[]
        {
            new OptSpec(CpPlan.OptRecursive, 'r', "recursive"),
            new OptSpec(CpPlan.OptRecursive, 'R', null),
            new OptSpec(CpPlan.OptNoClobber, 'n', "no-clobber"),
            new OptSpec(CpPlan.OptForce, 'f', "force"),
            new OptSpec(CpPlan.OptVerbose, 'v', "verbose"),
            new OptSpec(CpPlan.OptPreserve, 'p', null),
            // --preserve[=ATTR_LIST] (argument optional, attached only) and --no-preserve=ATTR_LIST
            // (argument required): resolved in command-line order by CpPreserve.
            new OptSpec(CpPlan.OptPreserveList, '\0', "preserve", OptKind.OptionalValue),
            new OptSpec(CpPlan.OptNoPreserve, '\0', "no-preserve", OptKind.Value),
            new OptSpec(CpPlan.OptUpdate, 'u', null),
            // GNU >= 9.3: --update[=all|none|older]; bare / =older is -u.
            new OptSpec(CpPlan.OptUpdate, '\0', "update", OptKind.OptionalValue),
            new OptSpec(CpPlan.OptArchive, 'a', "archive"),
            new OptSpec(CpPlan.OptInteractive, 'i', "interactive"),
            new OptSpec(CpPlan.OptLink, 'l', "link"),
            new OptSpec(CpPlan.OptSymbolic, 's', "symbolic-link"),
            new OptSpec(CpPlan.OptBackup, 'b', null),
            new OptSpec(CpPlan.OptBackupControl, '\0', "backup", OptKind.OptionalValue),
            new OptSpec(CpPlan.OptSuffix, 'S', "suffix", OptKind.Value),
            new OptSpec(CpPlan.OptTargetDirectory, 't', "target-directory", OptKind.Value),
            new OptSpec(CpPlan.OptNoTargetDirectory, 'T', "no-target-directory"),
            new OptSpec(CpPlan.OptReflink, '\0', "reflink", OptKind.OptionalValue),
            new OptSpec(CpPlan.OptDereference, 'L', "dereference"),
            new OptSpec(CpPlan.OptNoDereference, 'P', "no-dereference"),
            new OptSpec(CpPlan.OptDerefCommandLine, 'H', null),
            new OptSpec(CpPlan.OptNoDerefPreserveLinks, 'd', null),
            new OptSpec(CpPlan.OptOneFileSystem, 'x', "one-file-system"),
            new OptSpec(CpPlan.OptSparse, '\0', "sparse", OptKind.Value),
            new OptSpec(CpPlan.OptStripSlashes, '\0', "strip-trailing-slashes"),
            new OptSpec(CpPlan.OptAttributesOnly, '\0', "attributes-only"),
            new OptSpec(CpPlan.OptRemoveDestination, '\0', "remove-destination"),
            new OptSpec(CpPlan.OptParents, '\0', "parents"),
            new OptSpec(CpPlan.OptDebug, '\0', "debug"),
        },
        validButUnsupported: CpValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: CpLongOptionOrder);

    private const string TryHelp = "\nTry 'cp --help' for more information.";

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, CpSpec);

    protected override void ProcessRecord()
    {
        if (InputObject != null) _stdin.Add(InputObject);
    }

    protected override void EndProcessing() => Execute();

    private void Execute()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word for cp
        // (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT call
        // (`Invoke-BashCp -v a b`, Pester) binds the decoys instead. Prepending is safe: a decoy can only
        // have been bound before any `--`.
        var args = BashRuntime.PrependDecoys(Arguments,
            (v.IsPresent, "-v"), (p.IsPresent, "-p"), (I.IsPresent, "-i"), (D.IsPresent, "-d"), (A.IsPresent, "-a"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "cp", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "cp"))
                WriteObject(line);
            return;
        }

        // Classification happens DURING the scan, so nothing after `--` is ever an option — `cp -- -a b`
        // copies a file named "-a".
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "cp", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "cp", parsed)) return;

        if (!CpPlan.TryBuild(parsed, BashVariableStore.Get, out var plan, out var planError))
        {
            FileSystemHelpers.WriteBashError(this, planError!);
            return;
        }
        // coreutils 9.4 warns once per invocation, before any other diagnostic, and leaves the exit status alone.
        if (plan.NoClobberWarning)
            FileSystemHelpers.WriteStderr(this,
                "cp: warning: behavior of -n is non-portable and may change in future; use --update=none instead");

        // -p / -a / --preserve[=LIST] / --no-preserve=LIST, resolved in command-line order. A bad attribute
        // word is a usage error; naming `context` needs SELinux, which is never present here.
        if (!CpPreserve.TryFrom(parsed, CpPlan.OptPreserve, CpPlan.OptArchive, CpPlan.OptPreserveList, CpPlan.OptNoPreserve,
                out var preserve, out var preserveError))
        {
            FileSystemHelpers.WriteBashError(this, preserveError!);
            return;
        }
        if (preserve.ContextRequested)
        {
            FileSystemHelpers.WriteBashError(this, "cp: cannot preserve security context without an SELinux-enabled kernel");
            return;
        }

        var operands = parsed.Operands();
        var shape = ResolveShape(plan, operands);
        if (shape is null) return;
        var (sourceOperands, destRaw, destIsDirectoryMode) = shape.Value;

        // Expand globs on the source list, preserving order.
        var sources = new List<FileSystemHelpers.OperandPath>();
        foreach (var s in sourceOperands)
        {
            // --strip-trailing-slashes applies when the destination is a directory (GNU do_copy's directory
            // branch); a single `cp -r --strip a/ b` leaves the slash, as GNU does.
            var typed = plan.StripSlashes && destIsDirectoryMode ? StripSlashes(s) : s;
            foreach (var expanded in FileSystemHelpers.ResolveOperands(this, typed)) sources.Add(expanded);
        }

        var destAbs = FileSystemHelpers.ProviderPath(this, destRaw);

        // Validate the operand shape BEFORE any write: several sources need an existing directory
        // (`cp a b result` used to leave only b's bytes in a file named result).
        if (!destIsDirectoryMode || plan.TargetDirectory is null)
        {
            var shapeError = TransferValidation.CheckOperandShape("cp", sources.Count, destRaw, destAbs);
            if (shapeError != null)
            {
                FileSystemHelpers.WriteBashError(this, shapeError);
                return;
            }
        }

        // Answers to -i prompts come from this command's stdin (the pipeline); none at all is EOF = "no".
        var stdin = new StdinLineSource(this, _stdin);
        bool Confirm(string prompt)
        {
            FileSystemHelpers.WriteStderr(this, prompt);
            return StdinLineSource.IsYes(stdin.ReadLine());
        }

        var cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        var engine = new CpEngine(plan, preserve, Confirm,
            say: text => WriteObject(BashRuntime.NewBashObject(text)),
            error: message => FileSystemHelpers.WriteBashError(this, message),
            cwd: cwd);

        bool hadError = false;
        foreach (var operand in sources)
        {
            var src = operand.Path;
            var srcDisplay = operand.Display;

            // A dangling symbolic link is still an entry (copied as a link under -P); only a name that
            // does not exist at all is "cannot stat".
            if (!File.Exists(src) && !Directory.Exists(src) && !FileSystemHelpers.IsReparsePoint(src))
            {
                FileSystemHelpers.WriteBashError(this, $"cp: cannot stat '{srcDisplay}': No such file or directory");
                hadError = true;
                continue;
            }

            string target, targetDisplay;
            if (plan.Parents)
            {
                if (!TryParentsTarget(plan, engine, srcDisplay, destAbs, destRaw, out target, out targetDisplay)) { hadError = true; continue; }
            }
            else if (destIsDirectoryMode)
            {
                target = TransferValidation.ResolveTarget(src, destAbs, destIsDir: true);
                // Diagnostics name the destination as typed (GNU): `cp f d/` reports 'd/f', not the full path.
                targetDisplay = FileSystemHelpers.JoinDisplay(destRaw, srcDisplay);
            }
            else
            {
                target = destAbs;
                targetDisplay = destRaw;
            }

            if (!engine.CopyEntry(src, srcDisplay, target, targetDisplay, commandLine: true)) hadError = true;
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    /// <summary>
    /// Splits the operands into sources and destination per GNU's four forms: <c>-t DIR SOURCE...</c>,
    /// <c>-T SOURCE DEST</c>, <c>SOURCE DEST</c> and <c>SOURCE... DIRECTORY</c> (the destination is
    /// "directory mode" when it is an existing directory and <c>-T</c> is not given). Reports the GNU
    /// diagnostic and returns null when the shape is wrong.
    /// </summary>
    private (List<string> Sources, string Dest, bool DirectoryMode)? ResolveShape(CpPlan plan, List<string> operands)
    {
        if (plan.TargetDirectory is { } tdir)
        {
            if (operands.Count == 0)
            {
                FileSystemHelpers.WriteBashError(this, "cp: missing file operand" + TryHelp);
                return null;
            }
            var abs = FileSystemHelpers.ProviderPath(this, tdir);
            if (!Directory.Exists(abs))
            {
                FileSystemHelpers.WriteBashError(this, File.Exists(abs)
                    ? $"cp: target directory '{tdir}': Not a directory"
                    : $"cp: target directory '{tdir}': No such file or directory");
                return null;
            }
            return (operands, tdir, true);
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "cp: missing file operand" + TryHelp);
            return null;
        }
        if (operands.Count == 1)
        {
            FileSystemHelpers.WriteBashError(this, $"cp: missing destination file operand after '{operands[0]}'" + TryHelp);
            return null;
        }

        var destRaw = operands[^1];
        var destAbs = FileSystemHelpers.ProviderPath(this, destRaw);

        if (plan.NoTargetDirectory)
        {
            if (operands.Count > 2)
            {
                FileSystemHelpers.WriteBashError(this, $"cp: extra operand '{operands[2]}'" + TryHelp);
                return null;
            }
            return (new List<string> { operands[0] }, destRaw, false);
        }

        bool dirMode = Directory.Exists(destAbs);
        if (plan.Parents && !dirMode)
        {
            FileSystemHelpers.WriteBashError(this, "cp: with --parents, the destination must be a directory" + TryHelp);
            return null;
        }
        return (operands.GetRange(0, operands.Count - 1), destRaw, dirMode);
    }

    private static string StripSlashes(string operand)
    {
        var trimmed = operand.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? operand : trimmed;
    }

    /// <summary>
    /// <c>--parents</c>: the destination of <paramref name="srcDisplay"/> is DIR/&lt;its path as typed&gt;; the
    /// leading directories are created, each announced as <c>SRCPREFIX -&gt; DESTPREFIX</c> (no quotes) under
    /// <c>-v</c>/<c>--debug</c> (oracle: <c>p -&gt; out/p</c>, <c>/etc -&gt; out/etc</c>).
    /// </summary>
    private bool TryParentsTarget(CpPlan plan, CpEngine engine, string srcDisplay, string destAbs, string destRaw,
        out string target, out string targetDisplay)
    {
        _ = engine;
        var rooted = srcDisplay.StartsWith('/');
        var relative = srcDisplay.TrimStart('/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        target = destAbs;
        targetDisplay = destRaw;
        var srcPrefix = rooted ? "/" : "";
        for (int i = 0; i < parts.Length; i++)
        {
            target = Path.Combine(target, parts[i]);
            targetDisplay = FileSystemHelpers.AppendDisplay(targetDisplay, parts[i]);
            srcPrefix = srcPrefix.Length == 0 ? parts[i] : srcPrefix.EndsWith('/') ? srcPrefix + parts[i] : srcPrefix + "/" + parts[i];
            if (i == parts.Length - 1) break;
            if (Directory.Exists(target)) continue;
            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
            {
                FileSystemHelpers.WriteBashError(this, $"cp: cannot make directory '{targetDisplay}': {ex.Message}");
                return false;
            }
            if (plan.Verbose || plan.Debug)
                WriteObject(BashRuntime.NewBashObject($"{srcPrefix} -> {FileSystemHelpers.ToBashPath(targetDisplay)}\n"));
        }
        return parts.Length > 0;
    }
}
