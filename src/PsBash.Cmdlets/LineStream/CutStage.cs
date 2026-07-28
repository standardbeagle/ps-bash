using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>cut</c> pipeline mode for the fused streaming lane (S3 of the fan-out epic).
///
/// <para><b>The <c>-d</c> binder collision is what bounds the certified subset</b>, and it is
/// worth stating precisely because it is the reason a very common spelling declines. On the
/// cmdlet, bare <c>-d</c> and bare <c>-c</c> are DECLARED value-bearing parameters (both
/// prefix-collide with a PowerShell common parameter — <c>-Debug</c>, <c>-Confirm</c>), so
/// their values never reach the cmdlet's own scan; and the joined <c>-d:</c> form is recovered
/// by re-scanning <c>MyInvocation.Line</c>. A streaming stage has neither surface: it sees a
/// plain <c>string[]</c> and no invocation line. Rather than guess how the binder would have
/// routed a token, this core certifies only spellings where the argv token and the cmdlet's
/// resolved value provably agree — the JOINED <c>-dX</c> / <c>-cLIST</c> forms and the
/// <c>=</c>-bearing long forms — and DECLINES bare <c>-d</c> / bare <c>-c</c>. A core that
/// guesses is a correctness bug; a core that declines is merely slower.</para>
///
/// <para><b>Certified argv subset:</b> <c>-f LIST</c> / <c>-fLIST</c>, <c>-cLIST</c> (joined),
/// <c>-dX</c> (joined, single-char delimiter), <c>-s</c> / <c>--only-delimited</c>,
/// <c>--output-delimiter=STR</c>, and the <c>=</c> long aliases <c>--fields=</c> /
/// <c>--characters=</c> / <c>--delimiter=</c>. DECLINED: bare <c>-d</c> / <c>-c</c> (above),
/// file operands (file mode), <c>--</c>, <c>--help</c> / <c>--version</c>, the separated long
/// forms, <c>-b</c>/<c>--bytes</c>/<c>--complement</c> and every other unimplemented flag (the
/// cmdlet owns "recognized but not supported" and its exit 2), and any list spec the cmdlet
/// rejects — a zero position, a decreasing range, or a non-numeric token — because each is an
/// error message plus an exit code the streaming lane cannot emit.</para>
///
/// <para>Lazy per-line transform: a downstream <c>head</c> still early-exits its producer.
/// The list spec is parsed ONCE at build time, as the cmdlet parses it once in
/// <c>ParseOnce</c>; open ranges (<c>-f2-</c>) stay open and are resolved per line against
/// that line's actual field/char count, which is why they cannot be flattened up front.</para>
///
/// <para>Parity oracle is <see cref="InvokeBashCutCommand"/>, not GNU: the "line with no
/// delimiter is one field" rule, the spec-order (not sorted) output, and the silent drop of
/// out-of-range positions are ports of the cmdlet's, divergences included. The logic is
/// duplicated rather than shared because this slice's file scope did not include the cmdlet
/// file; the byte-parity tests diff both paths over every certified argv, so drift fails the
/// build.</para>
/// </summary>
internal sealed class CutStage : ILineStreamStage
{
    private readonly string _delimiter;
    private readonly string _fieldSpec;
    private readonly string _charSpec;
    private readonly List<(int Lo, int Hi)>? _ranges;
    private readonly bool _suppressNonDelimited;
    private readonly string? _outputDelimiter;

    private CutStage(string delimiter, string fieldSpec, string charSpec,
                     List<(int Lo, int Hi)>? ranges, bool suppressNonDelimited, string? outputDelimiter)
    {
        _delimiter = delimiter; _fieldSpec = fieldSpec; _charSpec = charSpec;
        _ranges = ranges; _suppressNonDelimited = suppressNonDelimited; _outputDelimiter = outputDelimiter;
    }

    /// <summary>Every non-zero exit path (bad list, unsupported flag, unreadable file) is
    /// declined in <see cref="TryCreate"/> before a line is emitted.</summary>
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        string delimiter = "\t";           // the cmdlet's default
        string fieldSpec = string.Empty, charSpec = string.Empty;
        bool suppressNonDelimited = false;
        string? outputDelimiter = null;

        int i = 0;
        while (i < argv.Length)
        {
            string a = argv[i];

            if (a == "--" || a == "--help" || a == "--version") return null;

            // Joined -dX (the cmdlet's ^-d(.)$ branch). A longer -dABC is NOT that branch —
            // the cmdlet classifies it as an unknown option, so decline.
            if (a.Length > 2 && a[0] == '-' && a[1] == 'd')
            {
                if (a.Length != 3) return null;
                delimiter = a.Substring(2, 1);
                i++;
                continue;
            }

            if (a == "-f")
            {
                i++;
                if (i >= argv.Length) return null;   // dangling value flag → cmdlet
                fieldSpec = argv[i];
                i++;
                continue;
            }

            if (a.Length > 2 && a[0] == '-' && a[1] == 'f') { fieldSpec = a.Substring(2); i++; continue; }
            if (a.Length > 2 && a[0] == '-' && a[1] == 'c') { charSpec = a.Substring(2); i++; continue; }

            if (a == "-s" || a == "--only-delimited") { suppressNonDelimited = true; i++; continue; }

            if (a.StartsWith("--output-delimiter=", StringComparison.Ordinal))
            { outputDelimiter = a.Substring("--output-delimiter=".Length); i++; continue; }
            if (a.StartsWith("--fields=", StringComparison.Ordinal))
            { fieldSpec = a.Substring("--fields=".Length); i++; continue; }
            if (a.StartsWith("--characters=", StringComparison.Ordinal))
            { charSpec = a.Substring("--characters=".Length); i++; continue; }
            if (a.StartsWith("--delimiter=", StringComparison.Ordinal))
            { delimiter = a.Substring("--delimiter=".Length); i++; continue; }

            // Bare -d / -c (binder-bound on the cmdlet — see the class remarks), every
            // separated long form, every unimplemented flag, and any file operand.
            return null;
        }

        // Same precedence as the cmdlet: -c wins over -f when both are present.
        List<(int Lo, int Hi)>? ranges = null;
        try
        {
            if (charSpec.Length > 0) ranges = ParseRanges(charSpec);
            else if (fieldSpec.Length > 0) ranges = ParseRanges(fieldSpec);
        }
        catch (CutSpecRejected)
        {
            return null; // zero position / decreasing range / invalid list → cmdlet's error + exit
        }

        return new CutStage(delimiter, fieldSpec, charSpec, ranges, suppressNonDelimited, outputDelimiter);
    }

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        foreach (var item in input)
        {
            foreach (var line in LineStreamRecord.SubLines(item))
            {
                if (_charSpec.Length > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var pos in ExpandRanges(_ranges!, line.Length))
                    {
                        int idx = pos - 1;
                        if (idx >= 0 && idx < line.Length) sb.Append(line[idx]);
                    }
                    yield return BashRuntime.NormalizeBashText(sb.ToString());
                    continue;
                }

                if (_fieldSpec.Length > 0)
                {
                    string[] fields = line.Split(_delimiter, StringSplitOptions.None);
                    // -s: a line with no delimiter is a single field; GNU --only-delimited
                    // suppresses it entirely (and emits NOTHING, not an empty line).
                    if (_suppressNonDelimited && fields.Length <= 1) continue;
                    var picks = new List<string>();
                    foreach (var pos in ExpandRanges(_ranges!, fields.Length))
                    {
                        int fi = pos - 1;
                        if (fi >= 0 && fi < fields.Length) picks.Add(fields[fi]);
                    }
                    yield return BashRuntime.NormalizeBashText(
                        string.Join(_outputDelimiter ?? _delimiter, picks));
                    continue;
                }

                yield return BashRuntime.NormalizeBashText(line);
            }
        }
    }

    /// <summary>Thrown for every list spec the cmdlet reports and exits non-zero on — the
    /// stage answers all of them the same way: decline.</summary>
    private sealed class CutSpecRejected : Exception { }

    /// <summary>Port of <c>InvokeBashCutCommand.ParseRanges</c>, collapsed: this stage does not
    /// need to distinguish the cmdlet's three error MESSAGES, only that a spec is rejected.</summary>
    private static List<(int Lo, int Hi)> ParseRanges(string spec)
    {
        var result = new List<(int, int)>();
        foreach (var part in spec.Split(','))
        {
            int dash = part.IndexOf('-');
            if (dash >= 0)
            {
                string lo = part.Substring(0, dash);
                string hi = part.Substring(dash + 1);
                if (lo.Length == 0 && hi.Length > 0 && AllDigits(hi))
                {
                    int m = BashRuntime.ParseCountClamped(hi);
                    if (m == 0) throw new CutSpecRejected();
                    result.Add((1, m));
                    continue;
                }
                if (hi.Length == 0 && lo.Length > 0 && AllDigits(lo))
                {
                    int nlo = BashRuntime.ParseCountClamped(lo);
                    if (nlo == 0) throw new CutSpecRejected();
                    result.Add((nlo, int.MaxValue));
                    continue;
                }
                if (lo.Length > 0 && hi.Length > 0 && AllDigits(lo) && AllDigits(hi))
                {
                    int a = BashRuntime.ParseCountClamped(lo), b = BashRuntime.ParseCountClamped(hi);
                    if (a == 0 || b == 0) throw new CutSpecRejected();
                    if (a > b) throw new CutSpecRejected();
                    result.Add((a, b));
                    continue;
                }
                throw new CutSpecRejected();
            }

            if (!int.TryParse(part, out int n))
            {
                if (!AllDigits(part)) throw new CutSpecRejected();
                n = int.MaxValue;                     // digit run too big for int → clamp
            }
            if (n == 0) throw new CutSpecRejected();
            result.Add((n, n));
        }
        return result;
    }

    /// <summary>Port of the cmdlet's expander: spec ORDER is preserved (not sorted), and an
    /// open range runs to this line's actual count.</summary>
    private static IEnumerable<int> ExpandRanges(List<(int Lo, int Hi)> ranges, int max)
    {
        foreach (var (lo, hi) in ranges)
        {
            int end = hi == int.MaxValue ? max : hi;
            for (int n = lo; n <= end && n <= max; n++)
                if (n >= 1) yield return n;
        }
    }

    private static bool AllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (c < '0' || c > '9') return false;
        return true;
    }
}
