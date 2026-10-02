namespace PsBash.Cmdlets;

/// <summary>
/// <c>sort</c> pipeline mode for the fused streaming lane. <c>cat f | grep x | sort</c> is the dominant
/// real-world pipeline shape and it could not stream at all before this stage existed: the lane is
/// ALL-OR-NOTHING, so one missing core dropped the whole chain back to the per-line-PSObject scriptblock.
///
/// <para><b>THIS STAGE IS BLOCKING — it is NOT lazy.</b> A comparison sort cannot be. <see cref="Run"/>
/// drains the whole upstream enumerable into a list (the buffer) BEFORE it yields its first line.
/// A DOWNSTREAM stage therefore cannot early-exit past a sort (<c>… | sort | head -n 3</c> still reads
/// every upstream line — the unfused lane has the same property); stages UPSTREAM still stream into the
/// buffer one line at a time.</para>
///
/// <para>The argv is resolved by the cmdlet's own <see cref="InvokeBashSortCommand.Plan"/> and the work is
/// done by the cmdlet's <see cref="SortEngine"/> — one resolver and one engine for both lanes, so they
/// cannot disagree (the old hand-ported comparator is gone). DECLINED (the real cmdlet owns the message,
/// the output file and the exit status): any argv the plan rejects, <c>--help</c>/<c>--version</c>, file
/// operands (file mode), <c>--files0-from</c>, <c>-z</c> (NUL records), <c>-R</c> (random order), <c>-o</c>, <c>-c</c>/<c>-C</c> (exit 1 paths) and <c>-m</c> (a merge of a single
/// stream is a pass-through, not a sort).</para>
/// </summary>
internal sealed class SortStage : ILineStreamStage
{
    private readonly SortPlan _plan;

    private SortStage(SortPlan plan) => _plan = plan;

    /// <summary>sort never sets a non-zero code on a path this stage certifies (the -c check mode, the only
    /// exit-1 path, is declined). Valid after full enumeration, like every stage.</summary>
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        var a = InvokeBashSortCommand.Plan(argv);
        if (a.Declined || a.Operands.Count > 0 || a.Output is not null || a.CheckMode != 0 || a.Plan.Merge
            || a.Plan.Zero || a.Plan.UsesRandom || a.Plan.Files0From is not null)
            return null;
        return new SortStage(a.Plan);
    }

    /// <summary>Drain, sort, emit. <b>Blocking:</b> <c>emit</c>/<c>texts</c> ARE the buffer.</summary>
    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        // `emit` holds the text each item is written as; `texts` its sort text. The cmdlet's pipeline path
        // splits an item whose BashText contains an embedded newline into one item per sub-line and emits
        // the SUB-LINE, but emits the ORIGINAL object (trailing newline intact) otherwise. Fused stages
        // yield one bare line at a time so the split never fires in practice, but reproducing it keeps the
        // two paths identical for any producer that ever hands over a multi-line record.
        var emit = new List<string>();
        var texts = new List<string>();
        foreach (var item in input)
        {
            string trimmed = item.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var sub in trimmed.Split('\n')) { emit.Add(sub); texts.Add(sub); }
            }
            else
            {
                emit.Add(item);
                texts.Add(trimmed);
            }
        }

        var engine = new SortEngine(_plan);
        engine.Prepare(texts);
        foreach (int idx in engine.Order()) yield return emit[idx];
    }
}
