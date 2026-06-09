using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashHostname</c> function
/// (REFACTOR-2). Prints the local host name, matching the bash <c>hostname</c>
/// builtin.
///
/// Behavioral parity oracle: the original psm1 function. The hostname surface is
/// trivial — no flags besides <c>--help</c>, no pipeline input. The cmdlet calls
/// <see cref="System.Net.Dns.GetHostName"/>, which is the same call the psm1
/// oracle dispatched to. On failure the cmdlet emits a bash-style error via the
/// psm1 <c>Write-BashError</c> function (it owns the script-scoped error-mode
/// switch) and returns, exactly as the oracle did.
///
/// Output goes through <see cref="BashRuntime.NewBashObject"/> with the default
/// <c>PsBash.TextOutput</c> type, which short-circuits to a bare
/// <see cref="string"/> for the fast path. Both <c>--help</c> and the error
/// surface delegate to psm1 via <c>InvokeCommand.InvokeScript</c> with a
/// parameter-bound script body, so no AOT-incompatible
/// <see cref="ScriptBlock"/> is constructed on the hot path.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashHostname")]
[OutputType(typeof(string))]
public sealed class InvokeBashHostnameCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "hostname", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "hostname"))
            {
                WriteObject(line);
            }
            return;
        }

        string name;
        try
        {
            name = System.Net.Dns.GetHostName();
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"hostname: {ex.Message}");
            return;
        }

        WriteObject(BashRuntime.NewBashObject(name));
    }
}
