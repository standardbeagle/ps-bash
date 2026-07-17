using System.Globalization;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashSeq</c> function
/// (REFACTOR-2 follow-on). Generates an integer or decimal sequence,
/// matching GNU coreutils <c>seq</c>.
///
/// <para>
/// Behavioral parity oracle: the original psm1 function. The cmdlet
/// reproduces its exact branches byte-for-byte:
/// </para>
/// <list type="bullet">
/// <item>Forms: <c>seq LAST</c> / <c>seq FIRST LAST</c> / <c>seq FIRST INCR LAST</c>.</item>
/// <item><c>-s SEP</c> / <c>--separator=SEP</c> / <c>--separator SEP</c> —
/// joined output (single emitted string, no per-value object).</item>
/// <item><c>-w</c> / <c>--equal-width</c> — zero-pad to the width of the
/// larger of <c>|first|</c> / <c>|last|</c> (integer mode only, matching the
/// oracle).</item>
/// <item>Decimal-place handling: when any operand contains a <c>.</c>, the
/// max-decimal-places across operands determines the <c>FN</c> format
/// (invariant culture). Otherwise output is a long integer.</item>
/// <item>Loop termination uses a <c>±1e-9</c> epsilon to match the oracle's
/// floating-point comparison.</item>
/// </list>
///
/// <para>
/// <b>One colliding flag</b> declared as an explicit <see cref="SwitchParameter"/>:
/// <c>-w</c> prefix-collides with <c>-WarningAction</c> / <c>-WarningVariable</c>
/// (same hazard <c>wc -w</c> handled in Phase 1c). <c>-s</c> has no
/// PowerShell common-parameter prefix collision (no <c>-S*</c> common params)
/// so it stays in <see cref="Arguments"/> and is parsed by the manual
/// value-flag scan.
/// </para>
///
/// <para>
/// Output: per-value <c>PsBash.SeqOutput</c> typed PSObjects (with
/// <c>Value</c> / <c>Index</c> / <c>BashText</c>) when no separator is set;
/// a single bare string (<c>PsBash.TextOutput</c> fast path via
/// <see cref="BashRuntime.NewBashObject"/>) when <c>-s</c> joins the values.
/// </para>
///
/// <para>
/// <c>--help</c> delegates to the psm1 <c>Show-BashHelp</c> via
/// parameter-bound <c>InvokeCommand.InvokeScript</c> (no
/// <see cref="ScriptBlock"/> construction, AOT-safe).
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSeq")]
[OutputType("PsBash.SeqOutput")]
[OutputType(typeof(string))]
public sealed class InvokeBashSeqCommand : PSCmdlet
{
    [Parameter] public SwitchParameter w { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "seq", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "seq"))
            {
                WriteObject(line);
            }
            return;
        }

        var status = SeqCore.Generate(args, w.IsPresent,
            out var values, out string? separator, out bool isInteger, out string? badIncrement);

        if (status == SeqCore.Status.ZeroIncrement)
        {
            FileSystemHelpers.WriteBashError(this, $"seq: invalid Zero increment value: '{badIncrement}'");
            return;
        }

        if (separator != null)
        {
            WriteObject(BashRuntime.NewBashObject(string.Join(separator, values)));
            return;
        }

        for (int j = 0; j < values.Count; j++)
        {
            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.SeqOutput");
            object value = isInteger
                ? (object)(long)Math.Round(
                    double.Parse(values[j], CultureInfo.InvariantCulture))
                : double.Parse(values[j], CultureInfo.InvariantCulture);
            obj.Properties.Add(new PSNoteProperty("Value", value));
            obj.Properties.Add(new PSNoteProperty("Index", j));
            obj.Properties.Add(new PSNoteProperty("BashText", values[j]));
            WriteObject(obj);
        }
    }
}

/// <summary>
/// The pure value-generation core of <c>seq</c>, shared by
/// <see cref="InvokeBashSeqCommand"/> and the fused-pipeline streaming stage
/// (PERF phase-2b). It reproduces the cmdlet's exact parse + epsilon loop, so
/// the fused lane's <c>BashText</c> sequence is identical to the unfused
/// per-object path by construction. Version / <c>--help</c> are handled by the
/// caller before this runs.
/// </summary>
internal static class SeqCore
{
    internal enum Status { Ok, ZeroIncrement }

    /// <summary>
    /// Produce the formatted value strings (the exact <c>BashText</c> the cmdlet
    /// emits per line). <paramref name="separator"/> is non-null when <c>-s</c>
    /// was given (the whole sequence is one joined string). Returns
    /// <see cref="Status.ZeroIncrement"/> without producing values when an
    /// explicit 3-operand step is zero (GNU error case).
    /// </summary>
    internal static Status Generate(
        string[] args, bool equalWidthFlag,
        out List<string> values, out string? separator, out bool isInteger,
        out string? badIncrement)
    {
        values = new List<string>();
        separator = null;
        isInteger = true;
        badIncrement = null;

        bool equalWidth = equalWidthFlag;
        var operands = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (arg == "-s" || arg == "--separator")
            {
                i++;
                if (i < args.Length) { separator = args[i]; }
                i++;
                continue;
            }

            if (arg.StartsWith("--separator=", StringComparison.Ordinal))
            {
                separator = arg.Substring("--separator=".Length);
                i++;
                continue;
            }

            if (arg == "-w" || arg == "--equal-width")
            {
                equalWidth = true;
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        // Determine first, increment, last (psm1 oracle semantics).
        double first = 1, increment = 1, last = 1;

        if (operands.Count == 1)
        {
            last = ParseDouble(operands[0]);
        }
        else if (operands.Count == 2)
        {
            first = ParseDouble(operands[0]);
            last = ParseDouble(operands[1]);
        }
        else if (operands.Count >= 3)
        {
            first = ParseDouble(operands[0]);
            increment = ParseDouble(operands[1]);
            last = ParseDouble(operands[2]);
        }

        // GNU seq rejects an explicit zero increment (exit 1) rather than looping
        // forever. Only an explicit step (3 operands) can be zero — the 1/2-operand
        // forms default the increment to 1.
        if (operands.Count >= 3 && increment == 0)
        {
            badIncrement = operands[1];
            return Status.ZeroIncrement;
        }

        isInteger = (first == Math.Floor(first))
            && (increment == Math.Floor(increment))
            && (last == Math.Floor(last));

        int decPlaces = 0;
        if (!isInteger)
        {
            foreach (var op in operands)
            {
                int dotPos = op.IndexOf('.');
                if (dotPos >= 0)
                {
                    int dp = op.Length - dotPos - 1;
                    if (dp > decPlaces) { decPlaces = dp; }
                }
            }
        }

        int padWidth = 0;
        if (equalWidth && isInteger)
        {
            double maxVal = Math.Max(Math.Abs(first), Math.Abs(last));
            padWidth = ((long)maxVal).ToString(CultureInfo.InvariantCulture).Length;
        }

        bool ascending = increment > 0;
        int index = 0;
        double current = first;

        while ((ascending && current <= (last + 1e-9))
               || (!ascending && current >= (last - 1e-9)))
        {
            string formatted;
            if (isInteger)
            {
                long intVal = (long)Math.Round(current);
                formatted = (equalWidth && padWidth > 0)
                    ? intVal.ToString(CultureInfo.InvariantCulture)
                        .PadLeft(padWidth, '0')
                    : intVal.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                formatted = current.ToString(
                    "F" + decPlaces.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture);
            }
            values.Add(formatted);
            index++;
            current = first + (increment * index);
            if (increment == 0) break;
        }

        return Status.Ok;
    }

    private static double ParseDouble(string s) =>
        double.Parse(s, CultureInfo.InvariantCulture);
}
