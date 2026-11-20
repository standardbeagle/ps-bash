using System.Globalization;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

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
public sealed class InvokeBashSeqCommand : BashCmdlet
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

        if (FileSystemHelpers.TryWriteParseError(this, "seq", SeqCore.ScanOptions(args).Parsed)) return;

        var status = SeqCore.Generate(args, w.IsPresent,
            out var values, out string? separator, out bool isInteger, out string? badIncrement);

        if (status == SeqCore.Status.MissingOperand)
        {
            FileSystemHelpers.WriteMissingOperand(this, "seq");
            return;
        }
        if (status == SeqCore.Status.ExtraOperand)
        {
            FileSystemHelpers.WriteExtraOperand(this, "seq", badIncrement!);
            return;
        }
        if (status == SeqCore.Status.ZeroIncrement)
        {
            FileSystemHelpers.WriteUsageError(this, "seq", $"seq: invalid Zero increment value: {GnuQuote.Locale(badIncrement!)}");
            return;
        }
        if (status == SeqCore.Status.UsageError) return;   // reported above

        if (status == SeqCore.Status.InvalidFloat)
        {
            FileSystemHelpers.WriteUsageError(this, "seq", $"seq: invalid floating point argument: {GnuQuote.Locale(badIncrement!)}");
            return;
        }

        if (separator != null)
        {
            WriteObject(BashRuntime.NewBashObject(string.Join(separator, values)));
            return;
        }

        int j = 0;
        foreach (var text in values)
        {
            if (Stopping) return;
            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.SeqOutput");
            // -f can make the text non-numeric ("n1"); the typed Value then falls back to the text.
            object value = !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? text
                : isInteger ? (long)Math.Round(parsed) : parsed;
            obj.Properties.Add(new PSNoteProperty("Value", value));
            obj.Properties.Add(new PSNoteProperty("Index", j));
            obj.Properties.Add(new PSNoteProperty("BashText", text));
            WriteObject(obj);
            j++;
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
    internal enum Status { Ok, ZeroIncrement, MissingOperand, ExtraOperand, UsageError, InvalidFloat }

    /// <summary>
    /// seq's option scan (pure): the parse of the leading option words and where they end (<c>cut</c>: the words from
    /// there on are operands). The cmdlet reports its error through <see cref="FileSystemHelpers.TryWriteParseError"/>.
    /// </summary>
    internal static (ParsedArgs Parsed, int Cut) ScanOptions(string[] args)
    {
        // GNU getopt_long with "+" (options end at the first operand: `seq 2 -w 4` has the operand `-w`), plus seq's
        // twist: they also end at a word that looks like a negative number (`-1`, `-.5`), so `seq -1 2 3` counts from
        // -1. An unknown option is a usage error, never an operand (REGRESSION: `seq --bogus` / `seq -Q` threw a .NET
        // FormatException that ended the script at status 0).
        int cut = args.Length;
        for (int k = 0; k < args.Length; k++)
        {
            var a = args[k];
            if (a == "--") break;
            if ((a.Length >= 2 && a[0] == '-' && a[1] != '-' && a[1..].TrimStart('w') is "s" or "f") || (a.Length > 2 && a.StartsWith("--", StringComparison.Ordinal) && a.IndexOf('=') < 0
                    && ("--separator".StartsWith(a, StringComparison.Ordinal) || "--format".StartsWith(a, StringComparison.Ordinal))))
            { k++; continue; } // the next word is that option's value, whatever it looks like
            if (a.Length < 2 || a[0] != '-' || a[1] == '.' || char.IsAsciiDigit(a[1])) { cut = k; break; }
        }
        return (ArgParser.Parse(cut == args.Length ? args : args[..cut], SeqSpec), cut);
    }

    /// <summary>
    /// Produce the formatted value strings (the exact <c>BashText</c> the cmdlet
    /// emits per line). <paramref name="separator"/> is non-null when <c>-s</c>
    /// was given (the whole sequence is one joined string). Returns
    /// <see cref="Status.ZeroIncrement"/> without producing values when an
    /// explicit 3-operand step is zero (GNU error case).
    /// </summary>
    internal static Status Generate(
        string[] args, bool equalWidthFlag,
        out IEnumerable<string> values, out string? separator, out bool isInteger,
        out string? badIncrement)
    {
        values = Array.Empty<string>();
        separator = null;
        isInteger = true;
        badIncrement = null;

        bool equalWidth = equalWidthFlag;
        string? format = null;

        var (parsed, cut) = ScanOptions(args);
        if (parsed.HasError) return Status.UsageError;   // the cmdlet reports it through TryWriteParseError
        foreach (var tok in parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case "separator": separator = tok.Value; break;
                case "format": format = tok.Value; break;
                case "equal-width": equalWidth = true; break;
            }
        }
        var operands = parsed.Operands();
        for (int k = cut; k < args.Length; k++) operands.Add(args[k]);
        // GNU: one to three operands. REGRESSION: a bare `seq` printed 1 with status 0.
        if (operands.Count == 0) return Status.MissingOperand;
        if (operands.Count > 3)
        {
            badIncrement = operands[3];
            return Status.ExtraOperand;
        }

        // Determine first, increment, last (psm1 oracle semantics).
        double first = 1, increment = 1, last = 1;

        // GNU: each operand must be a floating-point number (`seq abc`: invalid floating point argument, status 1).
        foreach (var op in operands)
            if (!TryParseDouble(op, out _)) { badIncrement = op; return Status.InvalidFloat; }
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
            // GNU: the widest of the end points AS PRINTED, a minus sign included (`seq -w -3 2`: -3 … 00 01 02).
            padWidth = Math.Max(((long)Math.Round(first)).ToString(CultureInfo.InvariantCulture).Length,
                ((long)Math.Round(last)).ToString(CultureInfo.InvariantCulture).Length);
        }

        // Everything that can fail (operand parse, zero increment) has been decided above, so
        // errors surface at the same moment as before; only the value loop is deferred.
        var generated = Enumerate(first, increment, last, isInteger, equalWidth, padWidth, decPlaces);
        values = format is null ? generated : generated.Select(v => FormatValue(format, double.Parse(v, CultureInfo.InvariantCulture)));
        return Status.Ok;
    }

    /// <summary>The value loop, lazily: <c>seq 1 100000000 | head -1</c> formats one value.</summary>
    private static IEnumerable<string> Enumerate(
        double first, double increment, double last,
        bool isInteger, bool equalWidth, int padWidth, int decPlaces)
    {
        bool ascending = increment > 0;
        int index = 0;
        double current = first;
        string fmt = "F" + decPlaces.ToString(CultureInfo.InvariantCulture);

        while ((ascending && current <= (last + 1e-9))
               || (!ascending && current >= (last - 1e-9)))
        {
            string formatted;
            if (isInteger)
            {
                long intVal = (long)Math.Round(current);
                formatted = (equalWidth && padWidth > 0)
                    ? (intVal < 0 ? "-" : "") + Math.Abs(intVal).ToString(CultureInfo.InvariantCulture)
                        .PadLeft(padWidth - (intVal < 0 ? 1 : 0), '0')
                    : intVal.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                formatted = current.ToString(fmt, CultureInfo.InvariantCulture);
            }
            yield return formatted;
            index++;
            current = first + (increment * index);
            if (increment == 0) break;
        }
    }

    /// <summary>
    /// <c>seq -f FORMAT</c>: one printf floating conversion (<c>%e %f %g</c>, flags <c>-+ 0#</c>, width, precision)
    /// amid literal text; <c>%%</c> is a percent sign.
    /// </summary>
    internal static string FormatValue(string format, double value)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c != '%') { sb.Append(c); continue; }
            if (i + 1 < format.Length && format[i + 1] == '%') { sb.Append('%'); i++; continue; }
            int j = i + 1;
            bool left = false, plus = false, space = false, zero = false;
            for (; j < format.Length && format[j] is '-' or '+' or ' ' or '0' or '#'; j++)
            {
                if (format[j] == '-') left = true;
                else if (format[j] == '+') plus = true;
                else if (format[j] == ' ') space = true;
                else if (format[j] == '0') zero = true;
            }
            int width = 0;
            while (j < format.Length && char.IsAsciiDigit(format[j])) width = width * 10 + (format[j++] - '0');
            int prec = -1;
            if (j < format.Length && format[j] == '.')
            {
                j++; prec = 0;
                while (j < format.Length && char.IsAsciiDigit(format[j])) prec = prec * 10 + (format[j++] - '0');
            }
            if (j >= format.Length || format[j] is not ('e' or 'E' or 'f' or 'F' or 'g' or 'G'))
            {
                sb.Append(format, i, Math.Min(j + 1, format.Length) - i);   // not a conversion we model: keep as text
                i = Math.Min(j, format.Length - 1);
                continue;
            }
            char conv = format[j];
            string body = FormatFloat(Math.Abs(value), conv, prec < 0 ? 6 : prec);
            string sign = value < 0 || (value == 0 && double.IsNegative(value)) ? "-" : plus ? "+" : space ? " " : "";
            string text = sign + body;
            if (text.Length < width)
            {
                if (left) text = text.PadRight(width);
                else if (zero) text = sign + body.PadLeft(width - sign.Length, '0');
                else text = text.PadLeft(width);
            }
            sb.Append(text);
            i = j;
        }
        return sb.ToString();
    }

    private static string FormatFloat(double v, char conv, int prec)
    {
        switch (conv)
        {
            case 'f': case 'F':
                return v.ToString("F" + prec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            case 'e': case 'E':
            {
                string s = v.ToString((conv == 'e' ? "0." + new string('0', prec) + "e+00" : "0." + new string('0', prec) + "E+00"), CultureInfo.InvariantCulture);
                return prec == 0 ? s.Replace(".", "") : s;
            }
            default:   // g / G: precision = significant digits (0 -> 1), trailing zeros dropped
            {
                int p = prec == 0 ? 1 : prec;
                if (v == 0) return "0";
                int exp = (int)Math.Floor(Math.Log10(v));
                string s;
                if (exp < -4 || exp >= p)
                {
                    s = v.ToString("0." + new string('#', Math.Max(p - 1, 0)) + (conv == 'g' ? "e+00" : "E+00"), CultureInfo.InvariantCulture);
                }
                else
                {
                    s = Math.Round(v, Math.Max(p - 1 - exp, 0), MidpointRounding.ToEven).ToString("0." + new string('#', Math.Max(p - 1 - exp, 0)), CultureInfo.InvariantCulture);
                }
                return s;
            }
        }
    }

    private static double ParseDouble(string s) =>
        double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>An operand strtold would take whole (leading blanks allowed, as in GNU's xstrtold).</summary>
    private static bool TryParseDouble(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>seq's options (GNU: <c>-f</c>/<c>--format</c>, <c>-s</c>/<c>--separator</c>, <c>-w</c>/<c>--equal-width</c>).</summary>
    private static readonly OptSpecSet SeqSpec = new(
        new[]
        {
            new OptSpec("format", 'f', "format", OptKind.Value),
            new OptSpec("separator", 's', "separator", OptKind.Value),
            new OptSpec("equal-width", 'w', "equal-width"),
        },
        allowAbbrev: true,
        gnuInfoOptions: true);
}
