using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashMkdir</c>
/// (REFACTOR-2). Creates each operand directory, matching GNU coreutils
/// <c>mkdir</c> semantics. Supports <c>-p</c> (parents — create intermediate
/// dirs, no error on existing) and <c>-v</c> (verbose — emit a line per
/// created directory).
///
/// Behavioral parity oracle: the original psm1 function. The cmdlet
/// reproduces its exact branches:
/// <list type="bullet">
/// <item>No operands → bash-style "missing operand" error, no exit code
/// change (matches psm1 oracle).</item>
/// <item>Operand exists and <c>-p</c> not set → "File exists" error and
/// $LASTEXITCODE=1. With <c>-p</c>, silently continue.</item>
/// <item>Parent dir missing and <c>-p</c> not set → "No such file or
/// directory" error and $LASTEXITCODE=1. With <c>-p</c>, create the whole
/// chain via <c>System.IO.Directory.CreateDirectory</c>.</item>
/// <item>Verbose output goes through <see cref="BashRuntime.NewBashObject"/>
/// with a trailing newline so it streams as a separate line in pipeline
/// output.</item>
/// </list>
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec
/// <c>MkdirSpec</c>). The transpiler single-quotes every dash-leading word for mkdir
/// (<c>PsEmitter.OrderedArgCommands</c>) so flags arrive in <c>Arguments</c> in order — which is
/// also what makes <c>-pv</c> work (bare, the PowerShell binder eats it as
/// <c>-PipelineVariable</c>). The <c>p</c>/<c>v</c> decoy switches exist ONLY for direct calls
/// and are re-injected first.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMkdir")]
[OutputType(typeof(string))]
public sealed class InvokeBashMkdirCommand : PSCmdlet
{
    [Parameter] public SwitchParameter p { get; set; }
    [Parameter] public SwitchParameter v { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Valid GNU <c>mkdir</c> flags ps-bash does not implement
    /// (<c>-Z</c>/SELinux is Linux-only; <c>-m MODE</c> IS implemented — see
    /// <see cref="FileModeSpec"/> and <see cref="FileSystemHelpers.ApplyDirectoryMode"/>). Classified via
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>.</summary>
    // (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static
    // string sets to find short flags the binder could eat.)
    private static readonly string[] MkdirValidButUnsupported =
    {
        "-Z", "--context",
    };

    private const string OptParents = "parents", OptVerbose = "verbose", OptMode = "mode";

    /// <summary>mkdir's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet MkdirSpec = new(
        new[]
        {
            new OptSpec(OptParents, 'p', "parents"),
            new OptSpec(OptVerbose, 'v', "verbose"),
            new OptSpec(OptMode, 'm', "mode", OptKind.Value),
        },
        validButUnsupported: MkdirValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, MkdirSpec);

    protected override void ProcessRecord()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word
        // for mkdir (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT
        // call (`Invoke-BashMkdir -p d`, Pester) binds the decoys instead. Prepending is safe: a
        // decoy can only have been bound before any `--`.
        var args = BashRuntime.PrependDecoys(Arguments, (p.IsPresent, "-p"), (v.IsPresent, "-v"));
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "mkdir", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "mkdir"))
            {
                WriteObject(line);
            }
            return;
        }

        // Shared ordered parser: bundles in any order (-pv, -vp, -pp), `--` (a dir literally named
        // "-foo" can be created after it), unique-prefix long options, and the unsupported/unknown
        // classifier in ONE scan. `-pv` used to be eaten by the binder as -PipelineVariable when
        // typed at PowerShell; the transpiler now single-quotes it so it reaches Arguments intact.
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "mkdir", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "mkdir", parsed)) return;

        bool parents = parsed.Has(OptParents);
        bool verbose = parsed.Has(OptVerbose);
        var operands = parsed.Operands();

        // -m MODE: compiled ONCE, before any operand is touched, against GNU's base mode 0777 and the
        // process umask. A bad mode is a usage error and creates nothing. Last -m wins (getopt).
        int? requestedMode = null;
        if (parsed.Last(OptMode) is { } modeToken)
        {
            var spec = modeToken.Value ?? "";
            if (!FileModeSpec.TryParse(spec, isDirectory: true, FileModeSpec.CurrentUmask(), baseMode: 0x1FF, out var compiled, out var mentioned))
            {
                FileSystemHelpers.WriteBashError(this, $"mkdir: invalid mode '{spec}'");
                return;
            }
            requestedMode = FileModeSpec.ResultingDirMode(compiled, mentioned, FileModeSpec.CurrentUmask());
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "mkdir: missing operand");
            return;
        }

        bool hadError = false;

        foreach (var dir in operands)
        {
            // The psm1 oracle used Test-Path -LiteralPath, which on Windows
            // matches both files and directories. System.IO.File.Exists OR
            // Directory.Exists gives the same answer.
            var absolute = FileSystemHelpers.ProviderPath(this, dir);
            bool exists = File.Exists(absolute) || Directory.Exists(absolute);

            if (exists)
            {
                if (!parents)
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"mkdir: cannot create directory '{dir}': File exists");
                    hadError = true;
                }
                continue;
            }

            var parent = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent) && !parents)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"mkdir: cannot create directory '{dir}': No such file or directory");
                hadError = true;
                continue;
            }

            try
            {
                // Directory.CreateDirectory handles both the -p (create chain)
                // and the no-flag (parent exists) cases — it's a no-op on
                // existing dirs which we already filtered above.
                Directory.CreateDirectory(absolute);
                // Only the FINAL directory gets the mode, even with -p (intermediates keep the umask
                // default, as in GNU).
                if (requestedMode is { } m) FileSystemHelpers.ApplyDirectoryMode(absolute, m);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"mkdir: cannot create directory '{dir}': {ex.Message}");
                hadError = true;
                continue;
            }

            if (verbose)
            {
                WriteObject(BashRuntime.NewBashObject(
                    $"mkdir: created directory '{FileSystemHelpers.ToBashPath(dir)}'\n"));
            }
        }

        if (hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }
}
