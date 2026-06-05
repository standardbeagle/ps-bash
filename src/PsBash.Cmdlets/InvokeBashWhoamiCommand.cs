using System.Management.Automation;

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
public sealed class InvokeBashWhoamiCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

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

        var name = System.Environment.UserName;
        WriteObject(BashRuntime.NewBashObject(name));
    }
}
