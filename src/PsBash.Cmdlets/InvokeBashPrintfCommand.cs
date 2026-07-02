using System.Globalization;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPrintf</c> function
/// (REFACTOR-2 Phase 1b). Formats arguments against a printf format string,
/// matching the bash <c>printf</c> builtin.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet
/// reproduces its exact behavior:
/// <list type="bullet">
/// <item>Each argument is coerced to <see cref="int"/>, then
/// <see cref="double"/>, then left as a <see cref="string"/> — matching the
/// psm1 <c>TryParse</c> ladder.</item>
/// <item><c>%%</c> is protected by a sentinel before escape expansion, then
/// restored to a literal <c>%</c> at the end.</item>
/// <item>The format string runs through
/// <see cref="BashRuntime.ExpandEscapeSequences"/> (REFACTOR-2 Phase 2).</item>
/// <item>Conversions <c>%s %d %f %x %X %o %c %b</c> with <c>-+ 0#</c> flags,
/// width, and precision are handled exactly as the psm1 switch did.</item>
/// <item>Output is a single <c>NoTrailingNewline</c> BashObject via
/// <see cref="BashRuntime.NewBashObject"/>.</item>
/// </list>
///
/// The usage-error path (<c>printf</c> with no format) delegates to the psm1
/// <c>Write-BashError</c> function via <c>InvokeCommand.InvokeScript</c>. That
/// function reads the script-scoped <c>$script:BashErrorMode</c> switch (host
/// IPC stderr vs <c>Write-Error</c>) which lives in psm1 module scope, so
/// keeping it as the public error sink preserves exact parity. The invoke is
/// string-bodied — no ScriptBlock construction — so the cmdlet stays AOT-safe.
/// The <c>--help</c> path likewise delegates to <c>Show-BashHelp</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPrintf")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashPrintfCommand : PSCmdlet
{
    private const string EscapedPercentSentinel = "\0ESCAPED_PERCENT\0";

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "printf", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "printf"))
            {
                WriteObject(line);
            }
            return;
        }

        if (args.Length == 0)
        {
            FileSystemHelpers.WriteBashError(this, "printf: usage: printf format [arguments]");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        var format = args[0];
        var argList = args.Skip(1).ToArray();

        // Coerce each argument: int, then double, then string. Matches the
        // psm1 TryParse ladder.
        var converted = new List<object>(argList.Length);
        foreach (var a in argList)
        {
            if (int.TryParse(a, out int intVal))
            {
                converted.Add(intVal);
            }
            else if (double.TryParse(
                         a, NumberStyles.Any, CultureInfo.InvariantCulture,
                         out double doubleVal))
            {
                converted.Add(doubleVal);
            }
            else
            {
                converted.Add(a);
            }
        }

        format = format.Replace("%%", EscapedPercentSentinel);
        format = BashRuntime.ExpandEscapeSequences(format);

        var sb = new StringBuilder();
        int argIdx = 0;
        // bash reuses (recycles) the format string until the argument list is
        // exhausted: `printf '%s\n' a b c` prints three lines. We repeat the
        // whole format while a pass consumes at least one more argument; a format
        // with no conversions consumes none and the loop stops after one pass.
        do
        {
        int passStartArgIdx = argIdx;
        int i = 0;
        while (i < format.Length)
        {
            if (format[i] == '%' && i + 1 < format.Length)
            {
                int j = i + 1;
                var flags = new StringBuilder();
                while (j < format.Length && "-+ 0#".IndexOf(format[j]) >= 0)
                {
                    flags.Append(format[j]);
                    j++;
                }
                var width = new StringBuilder();
                while (j < format.Length && char.IsDigit(format[j]))
                {
                    width.Append(format[j]);
                    j++;
                }
                var precision = new StringBuilder();
                bool hasPrecision = false;
                if (j < format.Length && format[j] == '.')
                {
                    hasPrecision = true;
                    j++;
                    while (j < format.Length && char.IsDigit(format[j]))
                    {
                        precision.Append(format[j]);
                        j++;
                    }
                }
                char spec = j < format.Length ? format[j] : '\0';
                string flagStr = flags.ToString();
                string widthStr = width.ToString();
                string precStr = precision.ToString();

                switch (spec)
                {
                    case 's':
                        if (argIdx < converted.Count)
                        {
                            string val = converted[argIdx]?.ToString() ?? string.Empty;
                            if (widthStr.Length > 0 && hasPrecision && precStr.Length > 0)
                            {
                                val = val.PadLeft(ParseFieldWidth(widthStr));
                                int take = Math.Min(ParseFieldWidth(precStr), val.Length);
                                val = val.Substring(0, take);
                            }
                            else if (widthStr.Length > 0)
                            {
                                val = flagStr.Contains('-')
                                    ? val.PadRight(ParseFieldWidth(widthStr))
                                    : val.PadLeft(ParseFieldWidth(widthStr));
                            }
                            sb.Append(val);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'd':
                    case 'i': // %i is an alias of %d
                        if (argIdx < converted.Count)
                        {
                            int val = ToInt(converted[argIdx]);
                            if (widthStr.Length > 0)
                            {
                                bool zeroPad = flagStr.Contains('0');
                                bool leftAlign = flagStr.Contains('-');
                                bool showPlus = flagStr.Contains('+');
                                string prefix = val >= 0 && showPlus ? "+" : string.Empty;
                                string str = prefix + val.ToString(CultureInfo.InvariantCulture);
                                int w = ParseFieldWidth(widthStr);
                                if (zeroPad && !leftAlign)
                                    str = str.PadLeft(w, '0');
                                else if (leftAlign)
                                    str = str.PadRight(w);
                                else
                                    str = str.PadLeft(w);
                                sb.Append(str);
                            }
                            else
                            {
                                sb.Append(val);
                            }
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'u': // unsigned decimal
                        if (argIdx < converted.Count)
                        {
                            uint uval = unchecked((uint)ToInt(converted[argIdx]));
                            string str = uval.ToString(CultureInfo.InvariantCulture);
                            if (widthStr.Length > 0)
                            {
                                str = flagStr.Contains('-')
                                    ? str.PadRight(ParseFieldWidth(widthStr))
                                    : str.PadLeft(ParseFieldWidth(widthStr), flagStr.Contains('0') ? '0' : ' ');
                            }
                            sb.Append(str);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'e':
                    case 'E':
                    case 'g':
                    case 'G':
                        if (argIdx < converted.Count)
                        {
                            int prec = precStr.Length > 0 ? ParseFieldWidth(precStr) : 6;
                            double d = ToDouble(converted[argIdx]);
                            string formatted = (spec == 'e' || spec == 'E')
                                // 2-digit exponent like C printf (not .NET's 3-digit "E+00n").
                                ? d.ToString("0." + new string('0', prec) + (spec == 'e' ? "e+00" : "E+00"), CultureInfo.InvariantCulture)
                                : d.ToString((spec == 'g' ? "G" : "G") + prec, CultureInfo.InvariantCulture);
                            if (spec == 'G') formatted = formatted.ToUpperInvariant();
                            if (widthStr.Length > 0) formatted = formatted.PadLeft(ParseFieldWidth(widthStr));
                            sb.Append(formatted);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'f':
                        if (argIdx < converted.Count)
                        {
                            int prec = precStr.Length > 0 ? ParseFieldWidth(precStr) : 6;
                            double d = ToDouble(converted[argIdx]);
                            string formatted = d.ToString(
                                "F" + prec, CultureInfo.InvariantCulture);
                            if (widthStr.Length > 0)
                            {
                                // Honor the field width with the right fill: `0` flag
                                // zero-pads (sign kept ahead of the zeros), `-` left-
                                // justifies, otherwise space-pad on the left. The old
                                // code space-padded then Trim()'d it straight back off,
                                // so `%05.2f` produced `3.14` instead of `03.14`.
                                formatted = PadNumeric(formatted, ParseFieldWidth(widthStr),
                                    zeroPad: flagStr.Contains('0'), leftAlign: flagStr.Contains('-'));
                            }
                            sb.Append(formatted);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'x':
                        if (argIdx < converted.Count)
                        {
                            int val = ToInt(converted[argIdx]);
                            string str = val.ToString("x", CultureInfo.InvariantCulture);
                            if (flagStr.Contains('#')) str = "0x" + str;
                            if (widthStr.Length > 0)
                                str = str.PadLeft(ParseFieldWidth(widthStr), '0');
                            sb.Append(str);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'X':
                        if (argIdx < converted.Count)
                        {
                            int val = ToInt(converted[argIdx]);
                            string str = val.ToString("X", CultureInfo.InvariantCulture);
                            if (flagStr.Contains('#')) str = "0X" + str;
                            if (widthStr.Length > 0)
                                str = str.PadLeft(ParseFieldWidth(widthStr), '0');
                            sb.Append(str);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'o':
                        if (argIdx < converted.Count)
                        {
                            int val = ToInt(converted[argIdx]);
                            string str = Convert.ToString(val, 8);
                            if (flagStr.Contains('#') && !str.StartsWith("0", StringComparison.Ordinal))
                                str = "0" + str;
                            if (widthStr.Length > 0)
                                str = str.PadLeft(ParseFieldWidth(widthStr), '0');
                            sb.Append(str);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'c':
                        if (argIdx < converted.Count)
                        {
                            // bash %c prints the FIRST CHARACTER of the argument's
                            // string form (`printf '%c' 65` -> '6'), NOT the ASCII
                            // code of a numeric value (which would give 'A').
                            string s = converted[argIdx]?.ToString() ?? string.Empty;
                            if (s.Length > 0) sb.Append(s[0]);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'b':
                        if (argIdx < converted.Count)
                        {
                            string expanded = BashRuntime.ExpandEscapeSequences(
                                converted[argIdx]?.ToString() ?? string.Empty);
                            sb.Append(expanded);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    default:
                        sb.Append(format[i]);
                        i++;
                        break;
                }
            }
            else
            {
                sb.Append(format[i]);
                i++;
            }
        }
        // Recycle only while we keep consuming arguments.
        if (argIdx <= passStartArgIdx) break;
        }
        while (argIdx < converted.Count);

        string result = sb.ToString().Replace(EscapedPercentSentinel, "%");

        WriteObject(BashRuntime.NewBashObject(
            result, "PsBash.TextOutput", noTrailingNewline: true, command: "printf"));
    }

    /// <summary>
    /// Pad a formatted numeric string to <paramref name="width"/>. With
    /// <paramref name="leftAlign"/> (the <c>-</c> flag) pads on the right with
    /// spaces; with <paramref name="zeroPad"/> (the <c>0</c> flag) pads on the
    /// left with zeros, keeping any leading sign ahead of the zeros
    /// (<c>-1.5</c> width 6 → <c>-001.5</c>); otherwise pads left with spaces.
    /// </summary>
    // printf field width / precision from the format spec (a \d+ run). A naive
    // int.Parse throws OverflowException on an absurd spec like %999999999999d;
    // clamping to int.MaxValue only trades that for an OutOfMemoryException in
    // PadLeft. Cap at a value past any meaningful terminal field so neither can
    // happen. The spec regex only yields digits, so a parse failure IS overflow.
    private const int MaxPrintfField = 1_000_000;
    private static int ParseFieldWidth(string digits)
        => int.TryParse(digits, out int v) ? Math.Min(v, MaxPrintfField) : MaxPrintfField;

    private static string PadNumeric(string s, int width, bool zeroPad, bool leftAlign)
    {
        if (s.Length >= width) return s;
        if (leftAlign) return s.PadRight(width);
        if (zeroPad)
        {
            if (s.Length > 0 && (s[0] == '-' || s[0] == '+'))
                return s[0] + s.Substring(1).PadLeft(width - 1, '0');
            return s.PadLeft(width, '0');
        }
        return s.PadLeft(width);
    }

    private static int ToInt(object o)
    {
        if (o is int i) return i;
        if (o is long l) return (int)l;
        if (o is double d) return (int)d;
        return int.TryParse(o?.ToString(), out int parsed) ? parsed : 0;
    }

    private static double ToDouble(object o)
    {
        if (o is double d) return d;
        if (o is int i) return i;
        if (o is long l) return l;
        return double.TryParse(
            o?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture,
            out double parsed)
            ? parsed
            : 0.0;
    }
}
