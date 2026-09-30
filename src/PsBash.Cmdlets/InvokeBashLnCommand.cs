using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashLn</c> (REFACTOR-2).
/// Creates a hard link or symbolic link from the second operand to the
/// first, matching the GNU coreutils <c>ln</c> argument order
/// (<c>ln TARGET LINK_NAME</c>). Supports <c>-s</c> (symbolic), <c>-f</c>
/// (force — remove existing link target first), <c>-v</c> (verbose).
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved:
/// <list type="bullet">
/// <item>Fewer than 2 operands → "missing file operand" error.</item>
/// <item>With <c>-f</c>, an existing link name is removed before creation.</item>
/// <item>Without <c>-f</c>, an existing link name → "File exists" error
/// and return.</item>
/// <item><c>-s</c> → <see cref="File.CreateSymbolicLink"/> /
/// <see cref="Directory.CreateSymbolicLink"/> depending on target type.</item>
/// <item>No <c>-s</c> → <see cref="File.CreateHardLink"/> (.NET 11+, fall
/// back to a P/Invoke on older runtimes — but the project's target framework
/// pins us above the bar).</item>
/// <item>Verbose mode emits <c>'link' -&gt; 'target'\n</c> for symlinks,
/// <c>'link' =&gt; 'target'\n</c> for hard links.</item>
/// </list>
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
    /// (InformationAction) and <c>-P</c> (ProgressAction / PipelineVariable) so a DIRECT call
    /// (`Invoke-BashLn -i a b`) reaches the classifier instead of crashing or being swallowed by
    /// the binder. The transpiler never binds them (it single-quotes every dash word).</summary>
    [Parameter] public SwitchParameter D { get; set; }
    [Parameter] public SwitchParameter I { get; set; }
    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Valid GNU <c>ln</c> options ps-bash does not implement; refused loudly (exit 2). NOT
    /// listed: <c>-n/--no-dereference</c>, which is accepted (see <see cref="LnSpec"/>). GNU 9.4
    /// ln has no <c>-Z</c>/<c>--context</c>. (A string[] on purpose:
    /// CommonParameterCollisionGuardTests enumerates each cmdlet's static string sets.)
    /// </summary>
    private static readonly string[] LnValidButUnsupported =
    {
        "-b", "--backup", "-d", "-F", "--directory", "-i", "--interactive",
        "-L", "--logical", "-P", "--physical", "-r", "--relative",
        "-S", "--suffix", "-t", "--target-directory", "-T", "--no-target-directory",
    };

    private const string OptSymbolic = "symbolic", OptForce = "force", OptVerbose = "verbose",
        OptNoDereference = "no-dereference";

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
        var operands = parsed.Operands();

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "ln: missing file operand");
            return;
        }

        var target = operands[0];
        var linkName = operands[1];
        var linkAbsolute = SessionState.Path.GetUnresolvedProviderPathFromPSPath(linkName);

        // GNU ln: when LINK_NAME is an existing REAL directory, the link is
        // created INSIDE it as basename(TARGET) — the directory is never
        // removed. (The old code ran Directory.Delete(recursive:true) here,
        // silently destroying a populated tree on `ln -sf x existing_dir`.)
        if (Directory.Exists(linkAbsolute) && !IsReparsePoint(linkAbsolute))
        {
            var leaf = Path.GetFileName(target.TrimEnd('/', '\\'));
            if (!string.IsNullOrEmpty(leaf))
                linkAbsolute = Path.Combine(linkAbsolute, leaf);
        }

        // -f force-removes an existing link name, but ONLY a file or a symlink.
        // A real directory at the (resolved) link path is left intact and falls
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
                return;
            }
        }

        if (File.Exists(linkAbsolute) || Directory.Exists(linkAbsolute))
        {
            FileSystemHelpers.WriteBashError(this,
                $"ln: failed to create {(symbolic ? "symbolic " : "")}link '{linkName}': File exists");
            return;
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
                // Hard link via P/Invoke-free path: File.CreateHardLink is
                // not in .NET stdlib, so we go through the Win32 helper.
                // POSIX uses link(2) via System.IO.File.CreateSymbolicLink
                // ... actually we shell out to ln via psm1 fallback on POSIX
                // since System.IO lacks a hard-link API. On Windows we use
                // the Win32 CreateHardLink. The psm1 oracle used
                // New-Item -ItemType HardLink which delegates to the same
                // OS calls — replicate via InvokeCommand.InvokeScript with
                // a parameter-bound body.
                var rc = InvokeCommand.InvokeScript(
                    "param($lk, $tg) New-Item -ItemType HardLink -Path $lk -Target $tg -Force | Out-Null; $?",
                    linkAbsolute, target);
                if (rc.Count == 0 || !(rc[0] is PSObject po && po.BaseObject is bool ok && ok))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"ln: failed to create hard link '{linkName}': New-Item returned failure");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this,
                $"ln: failed to create {(symbolic ? "symbolic " : "")}link '{linkName}': {ex.Message}");
            return;
        }

        if (verbose)
        {
            var bashLink = FileSystemHelpers.ToBashPath(linkName);
            var bashTarget = FileSystemHelpers.ToBashPath(target);
            WriteObject(BashRuntime.NewBashObject(
                symbolic ? $"'{bashLink}' -> '{bashTarget}'\n"
                         : $"'{bashLink}' => '{bashTarget}'\n"));
        }
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
