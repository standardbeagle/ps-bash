using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashLn</c> (REFACTOR-2).
/// Creates hard links or symbolic links with GNU coreutils <c>ln</c> semantics. Supports
/// <c>-s</c> (symbolic), <c>-f</c> (force — remove an existing link name first), <c>-v</c>
/// (verbose), <c>-n</c>, <c>-t DIR</c> and <c>-T</c>.
///
/// <para>
/// <b>Operand forms</b> (oracle: coreutils 9.4, checked against <c>wsl bash</c>):
/// <list type="number">
/// <item><c>ln TARGET LINK_NAME</c> — when LINK_NAME is an existing real directory the link is
/// created inside it as basename(TARGET) (unless <c>-T</c>).</item>
/// <item><c>ln TARGET</c> — a link named basename(TARGET) in the current directory.</item>
/// <item><c>ln TARGET... DIRECTORY</c> — one link per target inside DIRECTORY; 3+ operands need a
/// real last-operand directory ("target 'x': Not a directory" / "No such file or directory").
/// A failed link does not stop the rest; the exit status is 1 if any failed.</item>
/// <item><c>ln -t DIR TARGET...</c>; <c>-T</c> takes exactly two operands and never descends into
/// LINK_NAME; <c>-t</c> with <c>-T</c> is an error.</item>
/// </list>
/// A hard link needs an existing non-directory TARGET (GNU: "failed to access" / "hard link not
/// allowed for directory"). Verbose output is <c>'link' =&gt; 'target'</c> (hard) or
/// <c>'link' -&gt; 'target'</c> (symbolic), where a link made by concatenation shows
/// <c>DIR/leaf</c> (form 2 shows <c>./leaf</c>).
/// </para>
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec
/// <see cref="LnSpec"/>). The transpiler single-quotes every dash-leading word for ln
/// (<c>PsEmitter.OrderedArgCommands</c>) so flags arrive in <c>Arguments</c> in order; the
/// <c>v</c>/<c>D</c>/<c>I</c>/<c>P</c> decoy switches exist ONLY for direct calls and are
/// re-injected first.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashLn")]
[OutputType(typeof(string))]
public sealed class InvokeBashLnCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    /// <summary>Decoys for the valid-but-unsupported <c>-d</c> (Debug), <c>-i</c>
    /// (InformationAction) and <c>-P</c> (ProgressAction / PipelineVariable) so a DIRECT
    /// call (`Invoke-BashLn -i a b`) reaches the classifier instead of crashing or being swallowed by
    /// the binder. The transpiler never binds them (it single-quotes every dash word).</summary>
    [Parameter] public SwitchParameter D { get; set; }
    [Parameter] public SwitchParameter I { get; set; }
    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Valid GNU <c>ln</c> options ps-bash does not implement; refused loudly (exit 2). NOT
    /// listed: <c>-n/--no-dereference</c>, <c>-t</c> and <c>-T</c>, which are accepted (see
    /// <see cref="LnSpec"/>). GNU 9.4 ln has no <c>-Z</c>/<c>--context</c>. (A string[] on purpose:
    /// CommonParameterCollisionGuardTests enumerates each cmdlet's static string sets.)
    /// </summary>
    private static readonly string[] LnValidButUnsupported =
    {
        "-b", "--backup", "-d", "-F", "--directory", "-i", "--interactive",
        "-L", "--logical", "-P", "--physical", "-r", "--relative",
        "-S", "--suffix",
    };

    private const string OptSymbolic = "symbolic", OptForce = "force", OptVerbose = "verbose",
        OptNoDereference = "no-dereference", OptTargetDirectory = "target-directory",
        OptNoTargetDirectory = "no-target-directory";

    private const string TryHelp = "\nTry 'ln --help' for more information.";

    /// <summary>GNU ln long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] LnLongOptionOrder = { "no-dereference", "no-target-directory", "suffix", "symbolic", "verbose", "version" };

    /// <summary>
    /// ln's whole option surface, built once for the shared ordered parser.
    /// <c>-n/--no-dereference</c> is accepted because this cmdlet ALREADY treats an existing
    /// symlink-to-directory LINK_NAME as a plain name (it never descends into it), which is
    /// exactly what -n asks for; `ln -sfn TARGET LINK` is the ubiquitous re-point idiom. (GNU's
    /// default without -n dereferences such a LINK_NAME — a pre-existing divergence, not new.)
    /// </summary>
    private static readonly OptSpecSet LnSpec = new(
        new[]
        {
            new OptSpec(OptSymbolic, 's', "symbolic"),
            new OptSpec(OptForce, 'f', "force"),
            new OptSpec(OptVerbose, 'v', "verbose"),
            new OptSpec(OptNoDereference, 'n', "no-dereference"),
            new OptSpec(OptTargetDirectory, 't', "target-directory", OptKind.Value),
            new OptSpec(OptNoTargetDirectory, 'T', "no-target-directory"),
        },
        validButUnsupported: LnValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: LnLongOptionOrder);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, LnSpec);

    protected override void ProcessRecord()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word
        // for ln (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT
        // call (`Invoke-BashLn -v a b`, Pester) binds the decoys instead. Prepending is safe: a
        // decoy can only have been bound before any `--`.
        var args = BashRuntime.PrependDecoys(Arguments,
            (v.IsPresent, "-v"), (D.IsPresent, "-d"), (I.IsPresent, "-i"), (P.IsPresent, "-P"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "ln", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "ln"))
            {
                WriteObject(line);
            }
            return;
        }

        // Shared ordered parser: bundles in any order (-sf, -sfn, -ss), `--`, unique-prefix long
        // options, and the unsupported/unknown classifier in ONE scan. Before it, ln had NO
        // classifier: `ln -T a b` / `ln -sfn a b` fell through as OPERANDS, so the flag became
        // the link target and a wrongly-named link was created at exit 0.
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "ln", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "ln", parsed)) return;

        bool symbolic = parsed.Has(OptSymbolic);
        bool force = parsed.Has(OptForce);
        bool verbose = parsed.Has(OptVerbose);
        bool noTargetDir = parsed.Has(OptNoTargetDirectory);
        string? targetDir = parsed.Last(OptTargetDirectory)?.Value;
        var operands = parsed.Operands();

        if (targetDir != null && noTargetDir)
        {
            FileSystemHelpers.WriteBashError(this, "ln: cannot combine --target-directory and --no-target-directory");
            return;
        }
        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "ln: missing file operand" + TryHelp);
            return;
        }

        // Each form reduces to a list of (target, link-name) pairs.
        var pairs = new List<(string Target, string Link)>();
        if (targetDir != null)
        {
            // ln -t DIR TARGET...
            if (!RequireDirectory(targetDir, viaTargetOperand: false)) return;
            foreach (var t in operands) pairs.Add((t, ConcatName(targetDir, LeafOf(t))));
        }
        else if (noTargetDir)
        {
            // ln -T TARGET LINK_NAME — LINK_NAME is never a directory to descend into.
            if (operands.Count == 1)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"ln: missing destination file operand after '{operands[0]}'" + TryHelp);
                return;
            }
            if (operands.Count > 2)
            {
                FileSystemHelpers.WriteBashError(this, $"ln: extra operand '{operands[2]}'" + TryHelp);
                return;
            }
            pairs.Add((operands[0], operands[1]));
        }
        else if (operands.Count == 1)
        {
            // ln TARGET — a link named basename(TARGET) in the current directory.
            pairs.Add((operands[0], ConcatName(".", LeafOf(operands[0]))));
        }
        else if (operands.Count == 2)
        {
            // ln TARGET LINK_NAME; when LINK_NAME is an existing REAL directory the link is
            // created INSIDE it as basename(TARGET) — the directory is never removed. (The old
            // code ran Directory.Delete(recursive:true) here, silently destroying a populated
            // tree on `ln -sf x existing_dir`.) A symlink-to-directory LINK_NAME stays a plain name.
            var abs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[1]);
            var leaf = LeafOf(operands[0]);
            pairs.Add(Directory.Exists(abs) && !IsReparsePoint(abs) && leaf.Length > 0
                ? (operands[0], ConcatName(operands[1], leaf))
                : (operands[0], operands[1]));
        }
        else
        {
            // ln TARGET... DIRECTORY
            var dir = operands[^1];
            if (!RequireDirectory(dir, viaTargetOperand: true)) return;
            for (int k = 0; k < operands.Count - 1; k++)
                pairs.Add((operands[k], ConcatName(dir, LeafOf(operands[k]))));
        }

        // GNU keeps going after a failed link and reports failure in the exit status.
        bool failed = false;
        foreach (var (target, linkName) in pairs)
        {
            if (!CreateOne(target, linkName, symbolic, force, verbose)) failed = true;
        }
        if (failed) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    /// <summary>Last path component, trailing slashes ignored (GNU <c>last_component</c>).</summary>
    private static string LeafOf(string path) => Path.GetFileName(path.TrimEnd('/', '\\'));

    /// <summary>GNU <c>file_name_concat</c>: dir + one slash + leaf, trailing slashes of dir collapsed.</summary>
    private static string ConcatName(string dir, string leaf)
    {
        var trimmed = dir.TrimEnd('/', '\\');
        return trimmed.Length == 0 && dir.Length > 0 ? dir + leaf : trimmed + "/" + leaf;
    }

    /// <summary>
    /// The DIRECTORY of forms 3/4 must exist and be a directory. GNU words the diagnostics per
    /// form: <c>target 'x': ...</c> when it came from the trailing operand, <c>failed to access
    /// 'x'</c> when it came from <c>-t</c> (an existing non-directory is "Not a directory" either way).
    /// </summary>
    private bool RequireDirectory(string dir, bool viaTargetOperand)
    {
        var abs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(dir);
        if (Directory.Exists(abs)) return true;
        FileSystemHelpers.WriteBashError(this, File.Exists(abs)
            ? $"ln: target '{dir}': Not a directory"
            : viaTargetOperand ? $"ln: target '{dir}': No such file or directory"
                               : $"ln: failed to access '{dir}': No such file or directory");
        return false;
    }

    /// <summary>Create one link; reports its own error and returns false on failure.</summary>
    private bool CreateOne(string target, string linkName, bool symbolic, bool force, bool verbose)
    {
        var linkAbsolute = SessionState.Path.GetUnresolvedProviderPathFromPSPath(linkName);
        var targetPath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(target);

        if (!symbolic)
        {
            // link(2) needs an existing non-directory source.
            if (Directory.Exists(targetPath))
            {
                FileSystemHelpers.WriteBashError(this, $"ln: {target}: hard link not allowed for directory");
                return false;
            }
            if (!File.Exists(targetPath))
            {
                FileSystemHelpers.WriteBashError(this,
                    $"ln: failed to access '{target}': No such file or directory");
                return false;
            }
        }

        // -f force-removes an existing link name, but ONLY a file or a symlink.
        // A real directory at the link path is left intact and falls
        // through to the "File exists" guard below — matching GNU's refusal to
        // overwrite a directory.
        if (force && (File.Exists(linkAbsolute) || IsReparsePoint(linkAbsolute)))
        {
            try
            {
                RemoveLinkOrFile(linkAbsolute);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"ln: cannot remove existing '{linkName}': {ex.Message}");
                return false;
            }
        }

        if (File.Exists(linkAbsolute) || Directory.Exists(linkAbsolute))
        {
            FileSystemHelpers.WriteBashError(this,
                $"ln: failed to create {(symbolic ? "symbolic" : "hard")} link '{linkName}': File exists");
            return false;
        }

        try
        {
            if (symbolic)
            {
                // Target may not exist for a symlink, so we have to guess
                // file vs directory link type from whatever does exist at
                // the target path. If the target doesn't exist, default to
                // a file symlink (bash ln does the same).
                var targetAbsolute = Path.IsPathRooted(target)
                    ? target
                    : Path.Combine(Path.GetDirectoryName(linkAbsolute) ?? "", target);

                if (Directory.Exists(targetAbsolute))
                {
                    Directory.CreateSymbolicLink(linkAbsolute, target);
                }
                else
                {
                    File.CreateSymbolicLink(linkAbsolute, target);
                }
            }
            else
            {
                // System.IO has no hard-link API; New-Item -ItemType HardLink delegates to the OS
                // call (CreateHardLink / link(2)). The source is the PowerShell-location-resolved
                // path checked above, so the two cannot name different files.
                var rc = InvokeCommand.InvokeScript(
                    "param($lk, $tg) New-Item -ItemType HardLink -Path $lk -Target $tg -Force | Out-Null; $?",
                    linkAbsolute, targetPath);
                if (rc.Count == 0 || !(rc[0] is PSObject po && po.BaseObject is bool ok && ok))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"ln: failed to create hard link '{linkName}': New-Item returned failure");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this,
                $"ln: failed to create {(symbolic ? "symbolic" : "hard")} link '{linkName}': {ex.Message}");
            return false;
        }

        if (verbose)
        {
            var bashLink = FileSystemHelpers.ToBashPath(linkName);
            var bashTarget = FileSystemHelpers.ToBashPath(target);
            WriteObject(BashRuntime.NewBashObject(
                symbolic ? $"'{bashLink}' -> '{bashTarget}'\n"
                         : $"'{bashLink}' => '{bashTarget}'\n"));
        }
        return true;
    }

    /// <summary>
    /// Remove an existing link name that is a file or a symlink — never a real
    /// directory. A directory reparse point (symlink/junction) is removed with a
    /// non-recursive <see cref="Directory.Delete(string,bool)"/>, which unlinks
    /// the reparse point only and leaves its target untouched; everything else
    /// goes through the read-only-aware <see cref="FileSystemHelpers.DeleteFileForce"/>.
    /// </summary>
    private static void RemoveLinkOrFile(string path)
    {
        if (Directory.Exists(path) && IsReparsePoint(path))
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        FileSystemHelpers.DeleteFileForce(path);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }
}
