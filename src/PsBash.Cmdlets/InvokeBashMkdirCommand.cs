using System.Linq;
using System.Management.Automation;

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
/// <b>Two colliding flags</b> declared as explicit
/// <see cref="SwitchParameter"/>s: <c>-p</c> prefix-collides with
/// <c>-ProgressAction</c> / <c>-PipelineVariable</c>, and <c>-v</c>
/// prefix-collides with <c>-Verbose</c>. An exact parameter-name match beats
/// a common-parameter prefix match, so declaring <c>p</c> and <c>v</c> here
/// makes the bash invocation bind correctly.
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
    /// (<c>-m MODE</c> has no faithful Windows ACL mapping; <c>-Z</c>/SELinux is
    /// Linux-only). Classified via
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>.</summary>
    private static readonly HashSet<string> MkdirValidButUnsupported = new(StringComparer.Ordinal)
    {
        "-m", "--mode", "-Z", "--context",
    };

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

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

        // Bare -p / -v arrive via the decoy SwitchParameters above. Bundled
        // (-pv) and long (--parents / --verbose) forms flow through Arguments and
        // are parsed here; `--` ends flag parsing so a dir literally named "-foo"
        // can be created after it.
        bool parents = p.IsPresent;
        bool verbose = v.IsPresent;

        var operands = new List<string>();
        bool pastDoubleDash = false;
        foreach (var a in args)
        {
            if (pastDoubleDash) { operands.Add(a); continue; }
            if (a == "--") { pastDoubleDash = true; continue; }
            if (a == "-p" || a == "--parents") { parents = true; continue; }
            if (a == "-v" || a == "--verbose") { verbose = true; continue; }
            // De-bundle a pure -p/-v short bundle (e.g. -pv, -vp).
            if (a.Length > 2 && a[0] == '-' && a[1] != '-'
                && a.Skip(1).All(ch => ch == 'p' || ch == 'v'))
            {
                foreach (var ch in a.Skip(1))
                {
                    if (ch == 'p') parents = true;
                    else if (ch == 'v') verbose = true;
                }
                continue;
            }
            operands.Add(a);
        }

        if (!pastDoubleDash &&
            FileSystemHelpers.TryWriteOperandOptionError(this, "mkdir", operands, MkdirValidButUnsupported))
            return;

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
            var absolute = SessionState.Path.GetUnresolvedProviderPathFromPSPath(dir);
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
