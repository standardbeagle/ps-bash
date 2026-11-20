using System.Management.Automation;
using PsBash.Cmdlets.Args;
using PsBash.Core;

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
public sealed class InvokeBashBasenameCommand : BashCmdlet
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


    private static readonly OptSpecSet BasenameSpec = new(
        new[]
        {
            new OptSpec("multiple", 'a', "multiple"),
            new OptSpec("suffix", 's', "suffix", OptKind.Value),
            new OptSpec("zero", 'z', "zero"),
        },
        allowAbbrev: true,
        gnuInfoOptions: true);

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

        // GNU getopt_long: options anywhere (permuted), `--` ends them, an unknown one is a usage error (status 1 +
        // Try line), never a NAME. REGRESSION: `basename --bogus` printed `--bogus`, status 0.
        var parsed = ArgParser.Parse(args, BasenameSpec);
        if (FileSystemHelpers.TryWriteParseError(this, "basename", parsed)) return;
        string? suffix = parsed.Last("suffix")?.Value;
        bool allNames = A.IsPresent || parsed.Has("multiple") || suffix is not null; // GNU: -s implies -a
        bool zero = parsed.Has("zero");
        var operands = parsed.Operands();
        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteMissingOperand(this, "basename");
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
                FileSystemHelpers.WriteExtraOperand(this, "basename", operands[2]);
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
            var normalized = BashPath.ToShell(path).TrimEnd('/');
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

            WriteObject(zero ? NulRecords.Record(name) : name);
        }
    }
}
