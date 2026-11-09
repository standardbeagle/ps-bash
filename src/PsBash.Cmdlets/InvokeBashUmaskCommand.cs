using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>umask</c> builtin. Windows has no file-creation mask, so the value is only remembered for the
/// shell's lifetime and reported back (<c>umask</c>, <c>umask -S</c>); it does not change file modes.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUmask")]
public sealed class InvokeBashUmaskCommand : PSCmdlet
{
    private static int _mask = 0x12; // 0022

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "umask", args)) return;
        bool symbolic = args.Contains("-S");
        var operand = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (operand is null)
        {
            WriteObject(symbolic ? Symbolic(_mask) : Convert.ToString(_mask, 8).PadLeft(4, '0'));
            return;
        }
        if (operand.Length > 0 && !char.IsDigit(operand[0]))
        {
            if (UmaskSymbolic.TryApply(_mask, operand, out var applied)) _mask = applied;
            else
            {
                FileSystemHelpers.WriteStderr(this, $"umask: {operand}: invalid symbolic mode operator");
                FileSystemHelpers.SetLastExitCode(this, 1);
            }
            return;
        }
        try
        {
            int v = Convert.ToInt32(operand, 8);
            if (v is < 0 or > 0x1FF) throw new FormatException();
            _mask = v;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            FileSystemHelpers.WriteStderr(this, $"umask: {operand}: invalid octal number");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private static string Symbolic(int mask)
    {
        static string Triad(int m) => ((m & 4) == 0 ? "r" : "") + ((m & 2) == 0 ? "w" : "") + ((m & 1) == 0 ? "x" : "");
        return $"u={Triad(mask >> 6 & 7)},g={Triad(mask >> 3 & 7)},o={Triad(mask & 7)}";
    }
}
