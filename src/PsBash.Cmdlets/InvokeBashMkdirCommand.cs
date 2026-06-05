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

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

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

        bool parents = p.IsPresent;
        bool verbose = v.IsPresent;

        var operands = new List<string>();
        foreach (var a in args)
        {
            // The psm1 oracle treated any remaining token (including unknown
            // -flags) as an operand. Match that — no flag re-parsing here.
            operands.Add(a);
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
