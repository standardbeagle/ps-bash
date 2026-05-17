using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashYes</c> function
/// (REFACTOR-2). Emits its argument string (or the literal <c>"y"</c> when no
/// argument is given) repeatedly until the pipeline is shut down, matching the
/// GNU coreutils <c>yes</c> builtin.
///
/// Behavioral parity oracle: the original psm1 function. The cmdlet reproduces
/// its exact behavior — operands are joined with a single space, an empty
/// operand list defaults to <c>"y"</c>, and the emit loop continues forever.
/// Termination matches bash <c>yes</c>: SIGPIPE / broken pipe / consumer
/// shutdown. In PowerShell this surfaces as <see cref="PSCmdlet.Stopping"/>
/// flipping <c>true</c> when the upstream pipeline is shut down (e.g. when
/// <c>yes | head -n 5</c> closes the pipe after head completes); the loop
/// checks <see cref="PSCmdlet.Stopping"/> between writes and bails cleanly.
///
/// Output goes through <see cref="BashRuntime.NewBashObject"/> with the default
/// <c>PsBash.TextOutput</c> type, which short-circuits to a bare
/// <see cref="string"/> — exactly the shape downstream pipeline consumers
/// (head, grep, etc.) already handle. The <c>--help</c> path delegates to
/// psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="System.Management.Automation.PSCmdlet.InvokeCommand"/> with no
/// <see cref="ScriptBlock"/> construction (AOT-safe).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashYes")]
[OutputType(typeof(string))]
public sealed class InvokeBashYesCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "yes"))
            {
                WriteObject(line);
            }
            return;
        }

        var text = args.Length > 0 ? string.Join(' ', args) : "y";

        while (!Stopping)
        {
            WriteObject(BashRuntime.NewBashObject(text));
        }
    }
}
