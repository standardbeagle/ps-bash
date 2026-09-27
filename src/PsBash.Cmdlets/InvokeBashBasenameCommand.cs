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

    /// <summary>
    /// <c>-a</c> — decoy switch so the bare token reaches the cmdlet instead of
    /// prefix-binding to this cmdlet's own <c>-Arguments</c> (a
    /// ValueFromRemainingArguments parameter, which would otherwise swallow the
    /// following operand as its value). The manual scan in
    /// <see cref="ProcessRecord"/> still parses <c>-a</c> from <c>Arguments</c>
    /// when the emitter force-quotes it, so both paths set "all operands are
    /// NAMEs". Declared here for the direct-invocation (Pester) path.
    /// </summary>
    [Parameter]
    public SwitchParameter A { get; set; }


    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "basename", args)) return;
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
        bool allNames = A.IsPresent;
        bool pastDoubleDash = false;
        var operands = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                i++;
                continue;
            }

            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-a", StringComparison.Ordinal) ||
                string.Equals(arg, "--multiple", StringComparison.Ordinal))
            {
                allNames = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-s", StringComparison.Ordinal) ||
                string.Equals(arg, "--suffix", StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length) suffix = args[i];
                i++;
                // GNU: -s implies -a, so every operand gets the suffix stripped.
                allNames = true;
                continue;
            }

            if (arg.StartsWith("--suffix=", StringComparison.Ordinal))
            {
                suffix = arg.Substring("--suffix=".Length);
                allNames = true;
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        if (operands.Count == 0)
        {
            // No operand: emit nothing (psm1 oracle parity; existing tests pin
            // this). The two-operand SUFFIX form below is the GNU fix.
            return;
        }

        // GNU `basename NAME [SUFFIX]`: without -a/-s, exactly two operands
        // means the second is a SUFFIX. The old loop treated every operand as
        // a NAME, so `basename /a/b.txt .txt` printed `b.txt` then `.txt`.
        string? singleSuffix = suffix;
        if (!allNames)
        {
            if (operands.Count > 2)
            {
                FileSystemHelpers.WriteBashError(this, "basename: extra operand");
                return;
            }
            if (operands.Count == 2)
            {
                singleSuffix = operands[1];
            }
            operands.RemoveRange(1, operands.Count - 1);
        }

        foreach (var path in operands)
        {
            var normalized = path.Replace('\\', '/').TrimEnd('/');
            if (normalized.Length == 0) normalized = "/";

            int slashIdx = normalized.LastIndexOf('/');
            var name = slashIdx >= 0 ? normalized.Substring(slashIdx + 1) : normalized;
            if (name.Length == 0) name = "/";

            if (singleSuffix != null &&
                name.Length > singleSuffix.Length &&
                name.EndsWith(singleSuffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - singleSuffix.Length);
            }

            WriteObject(name);
        }
    }
}
