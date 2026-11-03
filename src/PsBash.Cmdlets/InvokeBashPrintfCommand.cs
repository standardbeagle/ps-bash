using System.Globalization;
using System.Management.Automation;
using System.Text;
using PsBash.Cmdlets.Args;

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
/// Options follow the bash BUILTIN (<see cref="PrintfArgScan"/>): <c>-v VAR</c> assigns instead of
/// printing, <c>--</c> ends options, any other dash-led first word is
/// <c>printf: -x: invalid option</c> + usage, exit 2 (so <c>--help</c> / <c>--version</c> are errors
/// too, as in bash). printf is on <c>PsEmitter.OrderedArgCommands</c>: every dash word arrives
/// verbatim, so <c>printf '%s\n' -n</c> prints <c>-n</c>. Errors go through
/// <see cref="FileSystemHelpers.WriteBashError"/> (discardable with <c>2&gt;/dev/null</c>)./// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPrintf")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashPrintfCommand : PSCmdlet
{
    private const string EscapedPercentSentinel = "\0ESCAPED_PERCENT\0";

    /// <summary>
    /// An escape that DECODES to a percent sign (<c>\045</c>, <c>\x25</c>, <c>\u0025</c>) is a literal
    /// percent in bash, never the start of a conversion; swap it for the sentinel before escapes are expanded.
    /// </summary>
    private static string ProtectEscapedPercent(string format)
    {
        if (format.IndexOf('\\') < 0) return format;
        var sb = new StringBuilder(format.Length);
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c != '\\' || i + 1 >= format.Length) { sb.Append(c); continue; }
            string rest = format.Substring(i + 1);
            string? hit = null;
            foreach (var cand in new[] { "0045", "045", "45", "x25", "u0025", "U00000025" })
                if (rest.StartsWith(cand, StringComparison.Ordinal)) { hit = cand; break; }
            // `\45` is only an octal escape when the next char is not another octal digit (\450 is \45 then 0? bash takes up to 3 digits).
            if (hit is "45" or "045" or "0045")
            {
                int digits = 0;
                while (digits < rest.Length && digits < 3 && rest[digits] >= '0' && rest[digits] <= '7') digits++;
                if (!(digits == hit.Length || (hit == "0045" && digits == 3))) hit = null;
                else if (hit == "0045") hit = "045";
            }
            if (hit is null) { sb.Append(c).Append(format[i + 1]); i++; continue; }
            sb.Append(EscapedPercentSentinel);
            i += hit.Length;
        }
        return sb.ToString();
    }


    /// <summary>
    /// Decoy for a bare <c>-v</c> typed DIRECTLY at PowerShell (it prefix-matches
    /// <c>-Verbose</c>); the transpiler single-quotes every dash word, so it never binds there.
    /// <c>-v</c> must be printf's first argument, so it is re-injected at the head.
    /// </summary>
    [Parameter]
    public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private const string Usage = "printf: usage: printf [-v var] format [arguments]";

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();
        if (V.IsPresent)
        {
            var withV = new string[args.Length + 1];
            withV[0] = "-v";
            Array.Copy(args, 0, withV, 1, args.Length);
            args = withV;
        }

        FileSystemHelpers.SetLastExitCode(this, 0);

        var scan = PrintfArgScan.Scan(args);
        if (scan.IsError)
        {
            // bash builtins answer a leading `--help` with their help text on stdout, THEN the
            // invalid-option diagnostic (oracle: bash 5.2 prints both, exit 2).
            if (args.Length > 0 && args[0] == "--help")
            {
                foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "printf"))
                    WriteObject(line);
            }
            FileSystemHelpers.WriteBashError(this, scan.MissingValue
                ? "printf: -v: option requires an argument"
                : $"printf: -{scan.InvalidOption}: invalid option");
            FileSystemHelpers.WriteBashError(this, Usage);
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        if (scan.FirstOperand >= args.Length)
        {
            FileSystemHelpers.WriteBashError(this, Usage);
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        string? assignTo = scan.VarName;
        if (assignTo is not null && !IsAssignableName(assignTo))
        {
            FileSystemHelpers.WriteBashError(this, $"printf: `{assignTo}': not a valid identifier");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        var format = args[scan.FirstOperand];
        var argList = args.Skip(scan.FirstOperand + 1).ToArray();
        // Coerce each argument: int, then double, then string. Matches the
        // psm1 TryParse ladder.
        var converted = new List<object>(argList.Length);
        foreach (var a in argList)
        {
            if (a.Length >= 2 && (a[0] == '\'' || a[0] == '"'))
            {
                // POSIX: a leading quote makes the value the character code of the next character (`printf %d \'a` -> 97).
                converted.Add(char.ConvertToUtf32(a, 1));
            }
            else if (long.TryParse(a, out long intVal))
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
        format = ProtectEscapedPercent(format);
        format = BashEscapes.Expand(format, EscapeDialect.PrintfFormat);

        var sb = new StringBuilder();
        int argIdx = 0;
        bool stoppedByC = false; // %b hit \c: bash suppresses ALL further output
        string? badFormat = null, badSpec = "";
        bool numErr = false;

        // Integer conversions read the argument like strtoimax: 0x hex, 0 octal, a leading blank and a sign are fine,
        // trailing text is "invalid number" (the numeric prefix still prints and the status becomes 1).
        long IntArg(int idx)
        {
            string a = argList[idx];
            if (a.Length >= 2 && (a[0] == '\'' || a[0] == '"')) return ToLong(converted[idx]);
            if (a is "'" or "\"") return 0; // no character after the quote: code point 0, no complaint
            int p = 0;
            while (p < a.Length && char.IsWhiteSpace(a[p])) p++;
            bool neg = false;
            if (p < a.Length && (a[p] == '-' || a[p] == '+')) neg = a[p++] == '-';
            int radix = 10;
            if (p + 1 < a.Length && a[p] == '0' && (a[p + 1] is 'x' or 'X')) { radix = 16; p += 2; }
            else if (p < a.Length && a[p] == '0') radix = 8;
            int start = p;
            ulong acc = 0;
            bool over = false;
            while (p < a.Length)
            {
                int dv = a[p] >= '0' && a[p] <= '9' ? a[p] - '0' : a[p] >= 'a' && a[p] <= 'f' ? a[p] - 'a' + 10 : a[p] >= 'A' && a[p] <= 'F' ? a[p] - 'A' + 10 : 99;
                if (dv >= radix) break;
                if (acc > (ulong.MaxValue - (ulong)dv) / (ulong)radix) over = true; else acc = acc * (ulong)radix + (ulong)dv;
                p++;
            }
            if (a.Length > 0 && (p < a.Length || (p == start && radix != 8)))
            {
                FileSystemHelpers.WriteBashError(this, $"printf: {a}: invalid number");
                numErr = true;
            }
            if (over) { FileSystemHelpers.WriteBashError(this, $"printf: {a}: Numerical result out of range"); numErr = true; return neg ? long.MinValue : long.MaxValue; }
            if (neg) return acc > (ulong)long.MaxValue + 1 ? long.MinValue : unchecked(-(long)acc);
            return acc > long.MaxValue ? long.MaxValue : (long)acc;
        }
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
                if (j < format.Length && format[j] == '*')
                {
                    // `%*d`: the width is the next argument; a negative one means left-justify.
                    int starWidth = argIdx < converted.Count ? ToInt(converted[argIdx]) : 0;
                    argIdx++;
                    if (starWidth < 0) flags.Append('-');
                    width.Append(Math.Abs(starWidth));
                    j++;
                }
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
                    if (j < format.Length && format[j] == '*')
                    {
                        int starPrec = argIdx < converted.Count ? ToInt(converted[argIdx]) : 0;
                        argIdx++;
                        if (starPrec >= 0) precision.Append(starPrec); else hasPrecision = false; // negative: as if omitted
                        j++;
                    }
                    while (j < format.Length && char.IsDigit(format[j]))
                    {
                        precision.Append(format[j]);
                        j++;
                    }
                }
                while (j < format.Length && "hlLjzt".IndexOf(format[j]) >= 0 && j + 1 < format.Length && format[j + 1] != '%') j++; // length modifiers carry no meaning here
                char spec = j < format.Length ? format[j] : '\0';
                string flagStr = flags.ToString();
                string widthStr = width.ToString();
                string precStr = precision.ToString();

                if (spec == '(')
                {
                    // %(fmt)T: strftime of an epoch-seconds argument (-1 or none = now).
                    int close = format.IndexOf(")T", j, StringComparison.Ordinal);
                    if (close > 0)
                    {
                        string fmt = format.Substring(j + 1, close - j - 1);
                        string raw = argIdx < argList.Length ? argList[argIdx] : "-1";
                        long secs = long.TryParse(raw, out var parsed) ? parsed : -1;
                        var when = secs < 0 ? DateTimeOffset.Now : DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime();
                        sb.Append(InvokeBashDateCommand.ConvertDateFormat(when, fmt));
                        argIdx++;
                        i = close + 2;
                        continue;
                    }
                }

                switch (spec)
                {
                    case 'q':
                        if (argIdx < converted.Count)
                        {
                            string q = ShellQuote(argList[argIdx]);
                            if (widthStr.Length > 0) q = flagStr.Contains('-') ? q.PadRight(ParseFieldWidth(widthStr)) : q.PadLeft(ParseFieldWidth(widthStr));
                            sb.Append(q);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 's':
                        if (argIdx < converted.Count)
                        {
                            // Raw text: %s must not reformat numerics (`007`, `1.50`, `1e3` print as typed).
                            string val = argList[argIdx];
                            if (hasPrecision)
                                val = val.Substring(0, Math.Min(precStr.Length > 0 ? ParseFieldWidth(precStr) : 0, val.Length));
                            if (widthStr.Length > 0)
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
                            long val = IntArg(argIdx);
                            if (widthStr.Length > 0 || hasPrecision || flagStr.IndexOfAny(['+', ' ']) >= 0)
                            {
                                bool leftAlign = flagStr.Contains('-');
                                bool zeroPad = flagStr.Contains('0') && !hasPrecision; // C: an explicit precision cancels the 0 flag
                                bool showPlus = flagStr.Contains('+');
                                string digits = (val < 0 ? unchecked((ulong)(-(val + 1)) + 1UL) : (ulong)val).ToString(CultureInfo.InvariantCulture);
                                if (hasPrecision) digits = digits.PadLeft(precStr.Length > 0 ? ParseFieldWidth(precStr) : 0, '0');
                                string sign = val < 0 ? "-" : showPlus ? "+" : flagStr.Contains(' ') ? " " : string.Empty;
                                string str = sign + digits;
                                if (widthStr.Length == 0) { sb.Append(str); argIdx++; i = j + 1; break; }
                                int w = ParseFieldWidth(widthStr);
                                if (zeroPad && !leftAlign)
                                    str = sign + digits.PadLeft(w - sign.Length, '0');
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
                            ulong uval = unchecked((ulong)IntArg(argIdx));
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
                            if ((spec == 'g' || spec == 'G') && flagStr.Contains('#') && d != 0 && !double.IsNaN(d) && !double.IsInfinity(d))
                            {
                                // `#` keeps the trailing zeros: P significant digits, the point always shown.
                                int sig = prec == 0 ? 1 : prec;
                                int exp10 = (int)Math.Floor(Math.Log10(Math.Abs(d)));
                                if (exp10 >= -4 && exp10 < sig)
                                {
                                    formatted = d.ToString("F" + (sig - 1 - exp10), CultureInfo.InvariantCulture);
                                    if (!formatted.Contains('.')) formatted += ".";
                                }
                            }
                            else if ((spec == 'g' || spec == 'G') && flagStr.Contains('#') && d == 0)
                                formatted = "0." + new string('0', Math.Max((prec == 0 ? 1 : prec) - 1, 0));
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
                            if (prec == 0 && flagStr.Contains('#')) formatted += "."; // `#` always shows the point
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
                            long val = IntArg(argIdx);
                            string str = FormatRadix(val.ToString("x", CultureInfo.InvariantCulture), val != 0 && flagStr.Contains('#') ? "0x" : "", widthStr, precStr, hasPrecision, flagStr);
                            sb.Append(str);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'X':
                        if (argIdx < converted.Count)
                        {
                            long val = IntArg(argIdx);
                            string str = FormatRadix(val.ToString("X", CultureInfo.InvariantCulture), val != 0 && flagStr.Contains('#') ? "0X" : "", widthStr, precStr, hasPrecision, flagStr);
                            sb.Append(str);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'o':
                        if (argIdx < converted.Count)
                        {
                            long val = IntArg(argIdx);
                            string str = FormatRadix(Convert.ToString(val, 8), "", widthStr, precStr, hasPrecision, flagStr, octalAlt: flagStr.Contains('#'));
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
                            string s = argList[argIdx];
                            if (s.Length > 0) sb.Append(s[0]);
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    case 'b':
                        if (argIdx < converted.Count)
                        {
                            // Raw text, not the numeric coercion: `%b` of `0101` keeps its zero.
                            string expanded = BashEscapes.Expand(
                                argList[argIdx], EscapeDialect.PrintfB, out bool bStop);
                            sb.Append(expanded);
                            if (bStop) stoppedByC = true;
                        }
                        argIdx++;
                        i = j + 1;
                        break;
                    default:
                        // bash prints what came before, reports the bad conversion, and stops with status 1.
                        badFormat = j >= format.Length || (j + 1 >= format.Length && "hlLjzt".IndexOf(spec) >= 0)
                            ? "missing format character" : "invalid format character";
                        badSpec = badFormat[0] == 'm' ? format.Substring(i, Math.Min(j + 1, format.Length) - i) : spec.ToString();
                        stoppedByC = true;
                        break;
                }
                if (stoppedByC) break;
            }
            else
            {
                sb.Append(format[i]);
                i++;
            }
        }
        // Recycle only while we keep consuming arguments.
        if (stoppedByC || argIdx <= passStartArgIdx) break;
        }
        while (argIdx < converted.Count);

        string result = sb.ToString().Replace(EscapedPercentSentinel, "%");

        if (assignTo is not null)
        {
            AssignVariable(assignTo, result);
            if (badFormat is not null || numErr) FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        WriteObject(BashRuntime.NewBashObject(
            result, "PsBash.TextOutput", noTrailingNewline: true, command: "printf"));
        if (badFormat is not null)
            FileSystemHelpers.WriteBashError(this, $"printf: `{badSpec}': {badFormat}");
        if (badFormat is not null || numErr) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    // NAME or NAME[subscript] (bash accepts an array element as the -v target).
    private static readonly System.Text.RegularExpressions.Regex AssignableName =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\[[^\]]+\])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsAssignableName(string s) => AssignableName.IsMatch(s);

    /// <summary>
    /// <c>printf -v NAME</c>: set the shell variable in the caller scope AND the variable store
    /// (<c>$env:NAME</c> expansions in transpiled bash read it), exactly as <c>read</c> does.
    /// <c>NAME[i]</c> sets one element of the PowerShell array variable (created/extended as needed).
    /// </summary>
    private void AssignVariable(string target, string value)
    {
        int br = target.IndexOf('[');
        if (br < 0)
        {
            SessionState.PSVariable.Set(target, value);
            try { BashVariableStore.Set(target, value); } catch { }
            return;
        }

        string name = target.Substring(0, br);
        string sub = target.Substring(br + 1, target.Length - br - 2).Trim();
        if (!int.TryParse(sub, out int idx) || idx < 0)
        {
            // Associative / computed subscripts are not modelled; keep the value reachable.
            SessionState.PSVariable.Set(name, value);
            return;
        }
        var existing = SessionState.PSVariable.GetValue(name);
        var items = new List<object?>();
        if (existing is System.Collections.IEnumerable en && existing is not string)
            foreach (var o in en) items.Add(o);
        else if (existing is string one) items.Add(one);
        while (items.Count <= idx) items.Add("");
        items[idx] = value;
        FileSystemHelpers.SetShellVariable(SessionState, name, items.ToArray());
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
    /// <summary>
    /// bash <c>%q</c>: the argument quoted so the shell reads it back as the same word. Backslash-escapes
    /// metacharacters; a control character switches to the <c>$'...'</c> form; the empty string is <c>''</c>.
    /// </summary>
    internal static string ShellQuote(string s)
    {
        if (s.Length == 0) return "''";
        bool control = false;
        foreach (char c in s) if (c < ' ' || c == '\u007f') { control = true; break; }
        var sb = new StringBuilder();
        if (control)
        {
            sb.Append("$'");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\a': sb.Append("\\a"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\v': sb.Append("\\v"); break;
                    case '\u001b': sb.Append("\\E"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (c < ' ' || c == '\u007f') sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('\'').ToString();
        }
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            bool safe = char.IsLetterOrDigit(c) || c > '\u007f' || "_-./:+=@%".IndexOf(c) >= 0
                        || (k > 0 && (c == '#' || c == '~'));
            if (!safe) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
    /// <summary>C printf integer tail for %x %X %o: precision = minimum digits, then the <c>#</c> prefix, then the width
    /// (space-padded, left-justified with <c>-</c>, zero-padded with <c>0</c> unless a precision is given).</summary>
    private static string FormatRadix(string digits, string prefix, string widthStr, string precStr, bool hasPrecision, string flagStr, bool octalAlt = false)
    {
        if (hasPrecision) digits = digits.PadLeft(precStr.Length > 0 ? ParseFieldWidth(precStr) : 0, '0');
        if (octalAlt && !digits.StartsWith('0')) digits = "0" + digits;
        if (widthStr.Length == 0) return prefix + digits;
        int w = ParseFieldWidth(widthStr);
        if (flagStr.Contains('-')) return (prefix + digits).PadRight(w);
        if (flagStr.Contains('0') && !hasPrecision) return prefix + digits.PadLeft(Math.Max(digits.Length, w - prefix.Length), '0');
        return (prefix + digits).PadLeft(w);
    }

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

    // 64-bit like bash's intmax_t; a fraction truncates and a value past the range clamps (bash warns "out of range").
    private static long ToLong(object o)
    {
        if (o is long l) return l;
        if (o is int i) return i;
        if (o is double d) return double.IsNaN(d) ? 0 : d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;
        return long.TryParse(o?.ToString(), out long parsed) ? parsed : 0;
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
