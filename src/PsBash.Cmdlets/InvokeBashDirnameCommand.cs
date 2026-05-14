using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashDirname</c> function
/// (REFACTOR-2 Phase 1). Strips the last path component from each operand,
/// matching GNU <c>dirname</c>.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact path normalization (backslash -> slash, trailing slash trim) and
/// the three-way directory decision: empty -> "/", no slash -> ".", slash at
/// index 0 -> "/", otherwise the substring up to the last slash. The psm1
/// function takes <em>all</em> arguments as path operands (it has no flags), so
/// this cmdlet does the same.
///
/// Output model: emits a bare <see cref="string"/> per operand, identical to
/// the psm1 <c>New-BashObject -TypeName 'PsBash.TextOutput'</c> fast path. The
/// <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c> function via
/// InvokeCommand. No ScriptBlock construction on the hot path -> AOT-safe.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDirname")]
[OutputType(typeof(string))]
public sealed class InvokeBashDirnameCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "dirname"))
            {
                WriteObject(line);
            }
            return;
        }

        foreach (var path in args)
        {
            var normalized = path.Replace('\\', '/').TrimEnd('/');

            string dir;
            if (normalized.Length == 0)
            {
                dir = "/";
            }
            else
            {
                int slashIdx = normalized.LastIndexOf('/');
                if (slashIdx < 0)
                    dir = ".";
                else if (slashIdx == 0)
                    dir = "/";
                else
                    dir = normalized.Substring(0, slashIdx);
            }

            WriteObject(dir);
        }
    }
}
