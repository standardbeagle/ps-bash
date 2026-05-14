using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashBasename</c> function
/// (REFACTOR-2 Phase 1). Strips the directory portion (and an optional suffix)
/// from each path operand, matching GNU <c>basename</c>.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact arg parsing (<c>-s SUFFIX</c> / <c>--suffix SUFFIX</c> /
/// <c>--suffix=SUFFIX</c>), path normalization (backslash -> slash, trailing
/// slash trim, empty -> "/"), and suffix stripping (only when the basename is
/// strictly longer than the suffix and ends with it).
///
/// Output model: emits a bare <see cref="string"/> per operand, identical to
/// the psm1 <c>New-BashObject -TypeName 'PsBash.TextOutput'</c> fast path which
/// returns a plain string for default TextOutput. The <c>--help</c> path
/// delegates to the psm1 <c>Show-BashHelp</c> function (it reads script-scoped
/// help-spec tables that live in the psm1 module scope) via InvokeCommand.
/// This cmdlet uses no ScriptBlock construction on its hot path, so it stays
/// AOT-safe.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashBasename")]
[OutputType(typeof(string))]
public sealed class InvokeBashBasenameCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "basename"))
            {
                WriteObject(line);
            }
            return;
        }

        string? suffix = null;
        var operands = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (string.Equals(arg, "-s", StringComparison.Ordinal) ||
                string.Equals(arg, "--suffix", StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length) suffix = args[i];
                i++;
                continue;
            }

            if (arg.StartsWith("--suffix=", StringComparison.Ordinal))
            {
                suffix = arg.Substring("--suffix=".Length);
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        foreach (var path in operands)
        {
            var normalized = path.Replace('\\', '/').TrimEnd('/');
            if (normalized.Length == 0) normalized = "/";

            int slashIdx = normalized.LastIndexOf('/');
            var name = slashIdx >= 0 ? normalized.Substring(slashIdx + 1) : normalized;
            if (name.Length == 0) name = "/";

            if (suffix != null &&
                name.Length > suffix.Length &&
                name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - suffix.Length);
            }

            WriteObject(name);
        }
    }
}
