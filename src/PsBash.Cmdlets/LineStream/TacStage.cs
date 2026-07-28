namespace PsBash.Cmdlets;

/// <summary>
/// <c>tac</c> pipeline mode for the fused streaming lane (S3 of the fan-out epic).
///
/// <para><b>THIS STAGE IS BLOCKING — it is NOT lazy</b>, for the same structural reason
/// <see cref="SortStage"/> is: the FIRST line tac emits is the LAST line its producer
/// yields, so <see cref="Run"/> drains the whole upstream enumerable into a
/// <c>List&lt;string&gt;</c> (the buffer — one entry per input line, bounded by the input,
/// held for the duration of the stage) BEFORE it yields anything. Two consequences, both
/// deliberate and both identical to the unfused cmdlet, which buffers its pipeline input
/// too:</para>
/// <list type="bullet">
/// <item>A DOWNSTREAM stage cannot early-exit past a tac. <c>… | tac | head -n 3</c> still
/// reads every upstream line, because the third-from-last line is not known until the last
/// line has been read. That is what reversal means.</item>
/// <item>Stages UPSTREAM are unaffected — they still stream into the buffer one line at a
/// time, and a <c>head</c> BEFORE the tac still stops its own producer.</item>
/// </list>
///
/// <para><b>Certified argv subset:</b> nothing at all, <c>-s SEP</c>, or
/// <c>--separator=SEP</c>. DECLINED: file operands (file mode), <c>-r</c>/<c>--regex</c> and
/// <c>-b</c>/<c>--before</c> (valid-but-unsupported — the cmdlet owns the refusal text and its
/// exit code), <c>--help</c> / <c>--version</c>, a dangling <c>-s</c>, and every other flag.
/// The joined <c>-sSEP</c> spelling is NOT certified because the cmdlet does not parse it
/// either — it falls through to the operand list and becomes a file.</para>
///
/// <para>Parity oracle is <see cref="InvokeBashTacCommand"/>: an EMPTY <c>-s</c> value falls
/// through to the plain reverse branch (PowerShell's <c>if ($separator)</c> is false for the
/// empty string), and the separator path joins every line with <c>\n</c> first, splits the
/// whole text on SEP, and reverses the CHUNKS — not the lines. Both are ported as-is.</para>
/// </summary>
internal sealed class TacStage : ILineStreamStage
{
    private readonly string? _separator;

    private TacStage(string? separator) { _separator = separator; }

    /// <summary>The certified subset has no non-zero exit path (a file-read failure is file
    /// mode, which is declined).</summary>
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        string? separator = null;

        int i = 0;
        while (i < argv.Length)
        {
            var a = argv[i];

            if (a == "-s")
            {
                i++;
                if (i >= argv.Length) return null;   // the cmdlet treats a dangling -s as an operand
                separator = argv[i];
                i++;
                continue;
            }
            if (a.StartsWith("--separator=", StringComparison.Ordinal))
            {
                separator = a.Substring("--separator=".Length);
                i++;
                continue;
            }

            // Any flag (-r, -b, --help, --version, unknown) or file operand.
            return null;
        }

        return new TacStage(separator);
    }

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        // ---- the buffer: the WHOLE stream, materialized before the first yield ----
        var lines = new List<string>();
        foreach (var item in input)
        {
            foreach (var subLine in LineStreamRecord.SubLines(item)) lines.Add(subLine);
        }

        if (_separator is { Length: > 0 })
        {
            // Oracle path: join with \n, split on SEP, reverse the CHUNKS.
            var chunks = string.Join("\n", lines).Split(new[] { _separator }, StringSplitOptions.None);
            Array.Reverse(chunks);
            foreach (var chunk in chunks) yield return BashRuntime.NormalizeBashText(chunk);
            yield break;
        }

        lines.Reverse();
        foreach (var line in lines) yield return BashRuntime.NormalizeBashText(line);
    }
}
