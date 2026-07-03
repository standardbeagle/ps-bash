using System.Management.Automation;
using System.Numerics;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashExpr</c> function
/// (REFACTOR-2 follow-on). Coreutils <c>expr</c> — an arithmetic / string
/// evaluator whose tokens are passed as separate args (e.g. <c>expr 2 + 3</c>).
///
/// Behavioral parity oracle: the original psm1 function. The cmdlet
/// reimplements the oracle's exact dispatch order byte-for-byte:
/// <list type="number">
/// <item><c>length STR</c> — string length</item>
/// <item><c>substr STR POS LEN</c> — 1-based substring</item>
/// <item><c>index STR CHARS</c> — first occurrence (1-based) of any char in CHARS</item>
/// <item><c>match STR REGEX</c> — POSIX BRE anchored at start; emits group 1 or match length</item>
/// <item>Infix <c>OP1 OP OP2</c> — when both sides are numeric (regex <c>^-?\d+$</c>),
/// evaluate <c>+ - * / % &lt; &lt;= = != &gt;= &gt;</c> as 64-bit integer math; otherwise
/// string compare (case-sensitive for <c>=</c> / <c>!=</c>, case-insensitive
/// for the inequalities, matching the oracle's <c>-lt</c> / <c>-ceq</c> mix)</item>
/// <item>Single operand — echo it back</item>
/// </list>
///
/// Output is a typed <c>PsBash.ExprOutput</c> PSObject with <c>Value</c>
/// (long when numeric, else string) and <c>BashText</c> properties — exact
/// oracle shape. Error paths route through psm1 <c>Write-BashError</c> with
/// <c>-ExitCode 2</c> (the GNU <c>expr</c> "error in expression" code).
///
/// No PowerShell common-parameter prefix collisions: expr operands are
/// digits, operators, and arbitrary user strings — none of the short flags
/// the oracle parses start with letters that collide with <c>-Verbose</c>
/// / <c>-Debug</c> / <c>-ErrorAction</c> / etc. The cmdlet declares only
/// the catch-all <c>Arguments</c> parameter.
///
/// Directive 12: user-controlled tokens never concatenate into a script
/// body. The <c>--help</c> path uses parameter-bound
/// <see cref="System.Management.Automation.PSCmdlet.InvokeCommand"/> with a
/// fixed <c>param($n) Show-BashHelp $n</c> body; error-path delegation
/// likewise binds the message text through <c>$args</c>. No
/// <see cref="ScriptBlock"/> construction — AOT-safe.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashExpr")]
[OutputType("PsBash.ExprOutput")]
public sealed class InvokeBashExprCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static readonly Regex NumericPattern = new(@"^-?\d+$", RegexOptions.Compiled);

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "expr", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "expr"))
            {
                WriteObject(line);
            }
            return;
        }

        if (args.Length == 0)
        {
            WriteBashErrorWithExitCode("expr: missing operand", 2);
            return;
        }

        string? result = null;
        var keyword = args[0];

        if (string.Equals(keyword, "length", StringComparison.Ordinal) && args.Length >= 2)
        {
            result = args[1].Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (string.Equals(keyword, "substr", StringComparison.Ordinal) && args.Length >= 4)
        {
            var str = args[1];
            if (!int.TryParse(args[2], System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out var pos)
                || !int.TryParse(args[3], System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out var len))
            {
                WriteBashErrorWithExitCode("expr: non-integer argument", 2);
                return;
            }
            // GNU `expr substr STRING POS LENGTH` (POS is 1-based) yields "" when POS
            // is out of range or LENGTH < 1 — it does NOT crash. The old code mirrored
            // an unclamped PowerShell Substring, so `expr substr hello 10 3` computed
            // Substring(9, -2) and threw. Clamp to bash semantics.
            int start = pos - 1;
            if (start < 0 || start >= str.Length || len <= 0)
                result = "";
            else
                result = str.Substring(start, Math.Min(len, str.Length - start));
        }
        else if (string.Equals(keyword, "index", StringComparison.Ordinal) && args.Length >= 3)
        {
            var str = args[1];
            var chars = args[2];
            int minPos = -1;
            foreach (var ch in chars)
            {
                int p = str.IndexOf(ch);
                if (p >= 0 && (minPos < 0 || p < minPos))
                {
                    minPos = p;
                }
            }
            int val = minPos >= 0 ? minPos + 1 : 0;
            result = val.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (string.Equals(keyword, "match", StringComparison.Ordinal) && args.Length >= 3)
        {
            var str = args[1];
            var pattern = args[2];
            // POSIX BRE \(...\) -> .NET (...) per the oracle's two -replace passes.
            var netPattern = pattern.Replace("\\(", "(").Replace("\\)", ")");
            if (!netPattern.StartsWith('^')) netPattern = "^" + netPattern;
            Match m;
            try
            {
                m = Regex.Match(str, netPattern);
            }
            catch (ArgumentException)
            {
                WriteBashErrorWithExitCode("expr: invalid regular expression", 2);
                return;
            }
            if (m.Success)
            {
                // Oracle uses PowerShell $Matches.Count > 1 (i.e. at least one capture group).
                if (m.Groups.Count > 1)
                {
                    result = m.Groups[1].Value;
                }
                else
                {
                    result = m.Value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            else
            {
                result = "0";
            }
        }
        else if (args.Length >= 3)
        {
            var left = args[0];
            var op = args[1];
            var right = args[2];

            bool numericLeft = NumericPattern.IsMatch(left);
            bool numericRight = NumericPattern.IsMatch(right);

            if (numericLeft && numericRight)
            {
                // GNU expr uses arbitrary-precision integers. Parse as BigInteger so a
                // 20-digit operand no longer overflows a bare long.Parse and crashes.
                BigInteger l = BigInteger.Parse(left, System.Globalization.CultureInfo.InvariantCulture);
                BigInteger r = BigInteger.Parse(right, System.Globalization.CultureInfo.InvariantCulture);
                switch (op)
                {
                    case "+": result = (l + r).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case "-": result = (l - r).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case "*": result = (l * r).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case "/":
                        if (r == 0)
                        {
                            WriteBashErrorWithExitCode("expr: division by zero", 2);
                            return;
                        }
                        // BigInteger division already truncates toward zero (like the
                        // oracle's float-divide-then-truncate), and keeps full precision.
                        result = (l / r).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "%":
                        if (r == 0)
                        {
                            WriteBashErrorWithExitCode("expr: division by zero", 2);
                            return;
                        }
                        result = (l % r).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "<": result = l < r ? "1" : "0"; break;
                    case "<=": result = l <= r ? "1" : "0"; break;
                    case "=": result = l == r ? "1" : "0"; break;
                    case "!=": result = l != r ? "1" : "0"; break;
                    case ">=": result = l >= r ? "1" : "0"; break;
                    case ">": result = l > r ? "1" : "0"; break;
                    default:
                        WriteBashErrorWithExitCode($"expr: unknown operator '{op}'", 2);
                        return;
                }
            }
            else
            {
                // Oracle string compare: -lt / -le / -ceq / -cne / -ge / -gt.
                // PowerShell -lt/-le/-ge/-gt on strings are case-insensitive ordinal.
                // -ceq / -cne are case-sensitive ordinal. Match exactly.
                var ciCmp = StringComparer.OrdinalIgnoreCase;
                switch (op)
                {
                    case "<": result = ciCmp.Compare(left, right) < 0 ? "1" : "0"; break;
                    case "<=": result = ciCmp.Compare(left, right) <= 0 ? "1" : "0"; break;
                    case "=": result = string.Equals(left, right, StringComparison.Ordinal) ? "1" : "0"; break;
                    case "!=": result = string.Equals(left, right, StringComparison.Ordinal) ? "0" : "1"; break;
                    case ">=": result = ciCmp.Compare(left, right) >= 0 ? "1" : "0"; break;
                    case ">": result = ciCmp.Compare(left, right) > 0 ? "1" : "0"; break;
                    default:
                        WriteBashErrorWithExitCode("expr: non-integer argument", 2);
                        return;
                }
            }
        }
        else
        {
            // Single operand: echo it.
            result = args[0];
        }

        // Build the typed PsBash.ExprOutput PSObject (Value + BashText shape). A
        // numeric result that exceeds long (a big-integer computation or literal) must
        // not crash the typed-value parse — fall back to BigInteger, then string.
        bool numericResult = NumericPattern.IsMatch(result);
        object value = result;
        if (numericResult)
        {
            if (long.TryParse(result, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var lv))
                value = lv;
            else if (BigInteger.TryParse(result, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var bv))
                value = bv;
        }

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.ExprOutput");
        obj.Properties.Add(new PSNoteProperty("Value", value));
        obj.Properties.Add(new PSNoteProperty("BashText", result));
        WriteObject(obj);
    }

    /// <summary>
    /// Emit a bash-style error with the oracle's <c>-ExitCode 2</c>. The psm1
    /// <c>Write-BashError</c> sets <c>$global:LASTEXITCODE</c>; we delegate
    /// via a parameter-bound script body so the error-mode switch
    /// (<c>$script:BashErrorMode</c>) — psm1-scoped — applies. No
    /// <see cref="ScriptBlock"/> construction; user text binds through
    /// <c>$args</c> (Directive 12).
    /// </summary>
    private void WriteBashErrorWithExitCode(string message, int exitCode)
    {
        FileSystemHelpers.WriteBashError(this, message);
        FileSystemHelpers.SetLastExitCode(this, exitCode);
    }
}
