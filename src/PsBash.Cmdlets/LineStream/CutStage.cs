namespace PsBash.Cmdlets;

/// <summary>
/// <c>cut</c> pipeline mode for the fused streaming lane.
///
/// <para>The argv is resolved by the cmdlet's own <see cref="InvokeBashCutCommand.Plan"/> and the
/// per-line work is the cmdlet's <see cref="CutPlan.Apply"/> — the two lanes share one resolver and
/// one engine, so they cannot disagree. The emitter builds the stage argv from static words, so the
/// declared-parameter binder collisions that once bounded this core (bare <c>-d</c>/<c>-c</c>) do not
/// exist here. DECLINED (the real cmdlet owns the message and the exit status): any argv the plan
/// rejects, <c>--help</c>/<c>--version</c>, file operands (file mode), and the unsupported <c>-z</c>.</para>
///
/// <para>Lazy per-line transform: a downstream <c>head</c> still early-exits its producer.</para>
/// </summary>
internal sealed class CutStage : ILineStreamStage
{
    private readonly CutPlan _plan;

    private CutStage(CutPlan plan) => _plan = plan;

    /// <summary>Every non-zero exit path (bad list, unsupported flag, unreadable file) is
    /// declined in <see cref="TryCreate"/> before a line is emitted.</summary>
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        var plan = InvokeBashCutCommand.Plan(argv);
        if (plan.Declined || plan.Operands.Count > 0 || plan.Selection is null) return null;
        return new CutStage(plan.Selection);
    }

    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        foreach (var item in input)
        {
            foreach (var line in LineStreamRecord.SubLines(item))
            {
                string? result = _plan.Apply(line);
                if (result is not null) yield return BashRuntime.NormalizeBashText(result);
            }
        }
    }
}
