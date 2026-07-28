namespace PsBash.Cmdlets;

/// <summary>
/// <c>nl</c> pipeline mode for the fused streaming lane (S3 of the fan-out epic).
///
/// <para>Lazy per-line transform — the counter is the only state, so a downstream
/// <c>head</c> still early-exits its producer.</para>
///
/// <para><b>The numbering column must match the cmdlet byte-for-byte</b>, so width, padding
/// style, separator, start, and increment are ports of
/// <c>InvokeBashNlCommand.EmitNumbered</c>: default width 6 right-aligned, a tab separator,
/// and — the easy-to-miss one — under the default <c>-b t</c> an EMPTY line is emitted BARE
/// (no number, no separator) and does NOT consume a number, while under <c>-b n</c> the blank
/// number field plus the separator are still printed.</para>
///
/// <para><b>Certified argv subset:</b> nothing at all, <c>-b STYLE</c> / <c>-bSTYLE</c>
/// (<c>a</c>/<c>t</c>/<c>n</c>), <c>-n STYLE</c> / <c>-nSTYLE</c> (<c>ln</c>/<c>rn</c>/<c>rz</c>),
/// <c>-s SEP</c> / <c>-sSEP</c>, and the JOINED numeric forms <c>-wN</c> / <c>-vN</c> /
/// <c>-iN</c>. DECLINED: the BARE <c>-w</c> / <c>-v</c> / <c>-i</c> value flags — on the cmdlet
/// those are declared decoy parameters (each prefix-collides with a common parameter:
/// <c>-WarningAction</c>, <c>-Verbose</c>, <c>-Information*</c>), so their values are consumed
/// by the binder and never appear in the cmdlet's own scan. A stage has no binder to model, so
/// certifying them would mean guessing; the joined spellings, which both lanes resolve from the
/// same token, are certified instead. Also declined: file operands (file mode), <c>--</c>,
/// <c>--help</c> / <c>--version</c>, every long form, and any unknown flag.</para>
/// </summary>
internal sealed class NlStage : ILineStreamStage
{
    private readonly bool _numberAll, _numberNone;
    private readonly int _width, _start, _incr;
    private readonly string _sep, _style;

    private NlStage(bool numberAll, bool numberNone, int width, int start, int incr, string sep, string style)
    {
        _numberAll = numberAll; _numberNone = numberNone;
        _width = width; _start = start; _incr = incr; _sep = sep; _style = style;
    }

    /// <summary>nl never sets a non-zero code — even the cmdlet's file-read failure leaves it
    /// at 0 (psm1-oracle parity), and file mode is declined here anyway.</summary>
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        bool numberAll = false, numberNone = false;
        int width = 6, start = 1, incr = 1;
        string sep = "\t", style = "rn";

        int i = 0;
        while (i < argv.Length)
        {
            var a = argv[i];

            // -bSTYLE (joined, the cmdlet's length-3 branch) and -b STYLE (split).
            if (a.Length == 3 && a[0] == '-' && a[1] == 'b')
            {
                if (!ApplyBodyStyle(a[2], ref numberAll, ref numberNone)) return null;
                i++;
                continue;
            }
            if (a == "-b")
            {
                i++;
                if (i >= argv.Length || argv[i].Length != 1) return null;
                if (!ApplyBodyStyle(argv[i][0], ref numberAll, ref numberNone)) return null;
                i++;
                continue;
            }

            if (a == "-n")
            {
                i++;
                if (i >= argv.Length) return null;
                style = NormalizeStyle(argv[i]);
                i++;
                continue;
            }
            if (a.Length > 2 && a.StartsWith("-n", StringComparison.Ordinal))
            { style = NormalizeStyle(a.Substring(2)); i++; continue; }

            if (a == "-s")
            {
                i++;
                if (i >= argv.Length) return null;
                sep = argv[i];
                i++;
                continue;
            }
            if (a.Length > 2 && a.StartsWith("-s", StringComparison.Ordinal))
            { sep = a.Substring(2); i++; continue; }

            // Joined -wN / -vN / -iN only; the bare forms are binder-bound decoys on the
            // cmdlet (see the class remarks) and are NOT certified.
            if (a.Length > 2 && a.StartsWith("-w", StringComparison.Ordinal) && int.TryParse(a.Substring(2), out var w))
            { width = w; i++; continue; }
            if (a.Length > 2 && a.StartsWith("-v", StringComparison.Ordinal) && int.TryParse(a.Substring(2), out var v))
            { start = v; i++; continue; }
            if (a.Length > 2 && a.StartsWith("-i", StringComparison.Ordinal) && int.TryParse(a.Substring(2), out var inc))
            { incr = inc; i++; continue; }

            // Bare -w/-v/-i, --, --help/--version, long forms, unknown flags, file operands.
            return null;
        }

        return new NlStage(numberAll, numberNone, width, start, incr, sep, style);
    }

    /// <summary>The cmdlet SILENTLY IGNORES an unrecognized body style (its <c>switch</c> has
    /// no default). Rather than reproduce a silent ignore, decline it — the fallback cmdlet
    /// then produces exactly whatever it produces, and the two lanes cannot disagree.</summary>
    private static bool ApplyBodyStyle(char s, ref bool numberAll, ref bool numberNone)
    {
        switch (s)
        {
            case 'a': numberAll = true; numberNone = false; return true;
            case 'n': numberNone = true; numberAll = false; return true;
            case 't': numberAll = false; numberNone = false; return true;
            default: return false;      // 'p<BRE>' regex numbering and anything else
        }
    }

    private static string NormalizeStyle(string s) => s switch
    {
        "ln" or "rn" or "rz" => s,
        _ => "rn",
    };

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        // Seeded so the FIRST numbered line is exactly _start (the cmdlet's own seeding).
        int lineNum = _start - _incr;
        string? blankNum = null;

        foreach (var item in input)
        {
            foreach (var line in LineStreamRecord.SubLines(item))
            {
                if (_numberNone)
                {
                    blankNum ??= new string(' ', _width);
                    yield return BashRuntime.NormalizeBashText(blankNum + _sep + line);
                    continue;
                }
                // Default (-b t): an empty line is emitted BARE and does not take a number.
                if (!_numberAll && line.Length == 0)
                {
                    yield return string.Empty;
                    continue;
                }

                lineNum += _incr;
                string num = _style switch
                {
                    "ln" => lineNum.ToString().PadRight(_width),
                    "rz" => lineNum.ToString().PadLeft(_width, '0'),
                    _ => lineNum.ToString().PadLeft(_width),
                };
                yield return BashRuntime.NormalizeBashText(num + _sep + line);
            }
        }
    }
}
