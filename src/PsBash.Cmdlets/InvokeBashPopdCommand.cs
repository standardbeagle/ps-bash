using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPopd</c> function
/// (REFACTOR-2 dir-stack batch). Implements the bash <c>popd</c> builtin:
/// pop the top of the location stack and chdir to it. With <c>+N</c>, pop
/// the Nth entry from the stack.
///
/// Behavioral parity oracle: the original psm1 function, which delegates
/// to PowerShell's built-in location stack via <c>Pop-Location</c> /
/// <c>Pop-Location -Stack</c>. The cmdlet preserves that identity exactly —
/// see <see cref="InvokeBashPushdCommand"/> for the state-ownership
/// rationale. No PowerShell common-parameter prefix collision.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPopd")]
public sealed class InvokeBashPopdCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "popd", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "popd"))
            {
                WriteObject(line);
            }
            return;
        }

        // +N: oracle pops (N+1) entries from the stack via Pop-Location -Stack.
        if (args.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(args[0], @"^\+(\d+)$"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(args[0], @"^\+(\d+)$");
            var n = BashRuntime.ParseCountClamped(m.Groups[1].Value);

            InvokeCommand.InvokeScript(
                @"param($n)
                for ($i = 0; $i -le $n; $i++) { Pop-Location -Stack -ErrorAction SilentlyContinue }",
                n);
            return;
        }

        // Default: pop the top of the stack and chdir there.
        try
        {
            InvokeCommand.InvokeScript("Pop-Location");
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            FileSystemHelpers.WriteBashError(this, $"popd: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }
}
