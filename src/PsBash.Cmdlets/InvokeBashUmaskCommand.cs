using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>umask</c> builtin. The shell's mask is remembered per session and reported back (<c>umask</c>,
/// <c>umask -S</c>); it starts as the mask the process inherited (<see cref="PsBash.Core.Platform.ProcessInfo.ProcessUmask"/>,
/// 022 on Windows) and is what ps-bash's own mode arithmetic reads (<see cref="FileModeSpec.CurrentUmask"/>: chmod/mkdir
/// symbolic modes, cp, tar). It never calls <c>umask(2)</c>: one host process serves many sessions, so files the kernel
/// creates keep the inherited process mask.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUmask")]
public sealed class InvokeBashUmaskCommand : PSCmdlet
{
    // The mask belongs to the shell session (the runspace), like the shopt table: a pooled host serves many
    // commands, and `umask 077` in one must not change what the next one reports.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        System.Management.Automation.Runspaces.Runspace, System.Runtime.CompilerServices.StrongBox<int>> PerRunspace = new();
    private static readonly System.Runtime.CompilerServices.StrongBox<int> Fallback = new(PsBash.Core.Platform.ProcessInfo.ProcessUmask());

    private static System.Runtime.CompilerServices.StrongBox<int> Box =>
        System.Management.Automation.Runspaces.Runspace.DefaultRunspace is { } rs
            ? PerRunspace.GetValue(rs, _ => new System.Runtime.CompilerServices.StrongBox<int>(PsBash.Core.Platform.ProcessInfo.ProcessUmask()))
            : Fallback;

    private static int Mask
    {
        get => Box.Value;
        set => Box.Value = value;
    }

    /// <summary>The current session's mask (as <c>umask</c> would print it).</summary>
    internal static int ShellMask => Mask;

    // Decoy for a direct `Invoke-BashUmask -p` (an ambiguous common-parameter prefix otherwise). The emitter
    // single-quotes umask's dash words, so transpiled calls always reach Arguments.
    [Parameter] public SwitchParameter p { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        if (p.IsPresent) args = [.. new[] { "-p" }, .. args];
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "umask", args)) return;
        // Options as bash's getopt reads them: -p and -S (bundled or not), `--` ends them, any other letter is
        // "invalid option" (status 2, mask unchanged: `umask -rwx` is not a symbolic mode). A lone `-` is a no-op.
        bool symbolic = false, reusable = false;
        int at = 0;
        for (; at < args.Length; at++)
        {
            var a = args[at];
            if (a == "--") { at++; break; }
            if (a == "-") return;
            if (a.Length < 2 || a[0] != '-') break;
            foreach (var c in a.AsSpan(1))
            {
                if (c == 'p') reusable = true;
                else if (c == 'S') symbolic = true;
                else
                {
                    FileSystemHelpers.WriteStderr(this, $"umask: -{c}: invalid option");
                    FileSystemHelpers.WriteStderr(this, "umask: usage: umask [-p] [-S] [mode]");
                    FileSystemHelpers.SetLastExitCode(this, 2);
                    return;
                }
            }
        }
        var operand = at < args.Length ? args[at] : null;
        if (operand is null)
        {
            // -p: a form that can be read back in (`umask 0022`, `umask -S u=rwx,g=rx,o=rx`).
            var shown = symbolic ? Symbolic(Mask) : Convert.ToString(Mask, 8).PadLeft(4, '0');
            WriteObject(reusable ? "umask " + (symbolic ? "-S " : "") + shown : shown);
            return;
        }
        if (operand.Length > 0 && !char.IsDigit(operand[0]))
        {
            if (UmaskSymbolic.TryApply(Mask, operand, out var applied))
            {
                Mask = applied;
                if (symbolic) WriteObject(Symbolic(Mask));
            }
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
            Mask = v;
            if (symbolic) WriteObject(Symbolic(Mask));
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
