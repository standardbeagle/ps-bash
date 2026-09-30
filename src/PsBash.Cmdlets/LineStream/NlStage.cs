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
/// <para><b>Certified argv subset:</b> every argv the cmdlet accepts with no file operands. The
/// cmdlet's own <see cref="InvokeBashNlCommand.Plan"/> resolves and VALIDATES the argv for both
/// lanes — <c>-b a|t|n</c>, <c>-n ln|rn|rz</c>, <c>-s SEP</c>, <c>-w N</c>, <c>-v N</c>, <c>-i N</c>,
/// in any bundling / joined / long / abbreviated spelling — so a bad value or an unsupported
/// option (<c>-h -f -d -l -p</c>, <c>-bp&lt;RE&gt;</c>) declines here exactly when the cmdlet
/// refuses it. (Bare <c>-w</c>/<c>-v</c>/<c>-i</c> used to decline because the cmdlet only saw them
/// through binder-bound decoys; the transpiler now quotes every flag, so both lanes read the
/// same argv.) Also declined: file operands (file mode), <c>--help</c> / <c>--version</c>.</para>/// </summary>
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
        // The cmdlet's own resolver (shared ordered parser + value validation) decides, so this
        // core can NEVER accept an argv the cmdlet would reject or read differently. File
        // operands (file mode) decline.
        var plan = InvokeBashNlCommand.Plan(argv);
        if (plan.Declined || plan.Operands.Count > 0) return null;
        return new NlStage(plan.NumberAll, plan.NumberNone, plan.Width, plan.Start, plan.Incr, plan.Sep, plan.Style);
    }
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
