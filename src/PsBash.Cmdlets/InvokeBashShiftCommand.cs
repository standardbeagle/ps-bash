using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashShift</c> function
/// (REFACTOR-2 follow-on, vars batch). Implements the bash <c>shift</c>
/// builtin: rotate positional parameters left by N (default 1).
///
/// Behavioral parity oracle: the original psm1 function. Reads / mutates the
/// runspace-global <c>$global:BashPositional</c> array — the same shared state
/// the transpiler emits for <c>set --</c> and <c>$1</c>..<c>$9</c>.
///
/// No flags besides <c>--help</c>. The first non-flag operand is the optional
/// integer count; non-numeric / negative counts emit a bash-style error and
/// the cmdlet returns with the array untouched. Shifting more than the
/// current count emits a bash-style "cannot shift past end" error and the
/// array is left untouched (matches the oracle byte-for-byte).
///
/// No stdout output (variable side-effect only). No exit-code mutation on
/// the happy path — the oracle did not set <c>$LASTEXITCODE</c>. The error
/// branches route through <see cref="FileSystemHelpers.WriteBashError"/>
/// which preserves the oracle's exit-code contract.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashShift")]
public sealed class InvokeBashShiftCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "shift", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "shift"))
            {
                WriteObject(line);
            }
            return;
        }

        int n = 1;
        if (args.Length > 0)
        {
            if (!int.TryParse(args[0], out n) || n < 0)
            {
                FileSystemHelpers.WriteBashError(
                    this, $"shift: {args[0]}: numeric argument required");
                return;
            }
        }

        // Read $global:BashPositional. The oracle treated an unset / null
        // value as an empty array.
        var pos = SessionState.PSVariable.GetValue("global:BashPositional") as object[];
        if (pos == null)
        {
            // Could also be string[]; coerce.
            var psVar = SessionState.PSVariable.GetValue("global:BashPositional");
            if (psVar is System.Collections.IEnumerable enumerable
                && !(psVar is string))
            {
                var list = new List<object>();
                foreach (var item in enumerable) list.Add(item ?? string.Empty);
                pos = list.ToArray();
            }
            else
            {
                pos = Array.Empty<object>();
            }
        }

        if (n > pos.Length)
        {
            FileSystemHelpers.WriteBashError(
                this, "shift: cannot shift past end of positional parameters");
            return;
        }

        // Rotate left by N: keep elements from index N onwards.
        var remaining = new object[pos.Length - n];
        Array.Copy(pos, n, remaining, 0, remaining.Length);
        SessionState.PSVariable.Set("global:BashPositional", remaining);
    }
}
