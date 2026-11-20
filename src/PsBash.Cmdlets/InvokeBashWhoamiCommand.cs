using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashWhoami</c> function
/// (REFACTOR-2). Prints the current user's login name, matching the bash
/// <c>whoami</c> builtin.
///
/// Behavioral parity oracle: the original psm1 function. The whoami surface is
/// trivial — no flags besides <c>--help</c>, no pipeline input. The cmdlet reads
/// <see cref="System.Environment.UserName"/>, which is the same value the psm1
/// oracle dispatched to. Output goes through <see cref="BashRuntime.NewBashObject"/>
/// with the default <c>PsBash.TextOutput</c> type, which short-circuits to a
/// bare <see cref="string"/> for the fast path.
///
/// The <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c> function
/// via <c>InvokeCommand.InvokeScript</c> with a parameter-bound script body so
/// no AOT-incompatible <see cref="ScriptBlock"/> is constructed.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashWhoami")]
[OutputType(typeof(string))]
public sealed class InvokeBashWhoamiCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static readonly OptSpecSet WhoamiSpec = new(Array.Empty<OptSpec>(), gnuInfoOptions: true);

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "whoami", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "whoami"))
            {
                WriteObject(line);
            }
            return;
        }

        // GNU whoami takes no options and no operand: either is a usage error (status 1 + Try line).
        // REGRESSION: `whoami --bogus` and `whoami extra` printed the user, status 0.
        var parsed = ArgParser.Parse(args, WhoamiSpec);
        if (FileSystemHelpers.TryWriteParseError(this, "whoami", parsed)) return;
        if (parsed.Operands() is [var extra, ..]) { FileSystemHelpers.WriteExtraOperand(this, "whoami", extra); return; }

        var name = System.Environment.UserName;
        WriteObject(BashRuntime.NewBashObject(name));
    }
}
