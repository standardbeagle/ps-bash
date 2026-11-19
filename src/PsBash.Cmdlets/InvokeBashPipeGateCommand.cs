using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The pipe in front of a <c>while read</c> loop that runs as a pipeline stage one record at a time
/// (<c>producer | Invoke-BashPipeGate | &amp; { begin { … } process { … } end { … } }</c>, PsBuild.StreamingReadLoopStage).
/// It passes the records on as whole lines (<see cref="CompleteLines"/>). When the loop has ended early (<c>break</c>, <c>exit</c>, a failed
/// <c>set -u</c> expansion), the loop marks its <see cref="BashPipeGate"/> done and the gate stops the upstream
/// commands (bash: the producer's next write gets SIGPIPE), so <c>yes | while read l; do break; done</c> ends.
/// A script block cannot stop its own upstream; a cmdlet in the outer pipeline can (<see cref="UpstreamStop"/>),
/// and the loop stage after it still completes.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPipeGate")]
public sealed class InvokeBashPipeGateCommand : BashCmdlet
{
    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    private BashPipeGate _gate = null!;

    /// <summary>The unterminated end of the records so far (printf / <c>echo -n</c> output), waiting for its newline.</summary>
    private string? _partial;

    protected override void BeginProcessing() => _gate = BashPipeGate.Open();

    protected override void ProcessRecord()
    {
        if (!_gate.Done && CompleteLines(InputObject) is { } lines) WriteObject(lines);
        if (_gate.Done) UpstreamStop.Throw(this);
    }

    /// <summary>
    /// What the loop may read of <paramref name="record"/>: whole lines only. A line is read once its newline has
    /// arrived, so an unterminated end (<c>printf 'a\nb'</c>, <c>echo -n</c>) waits for the next record to finish it
    /// (<c>{ printf a; echo b; }</c> reads <c>ab</c>). Null when no line is complete yet (<c>printf ''</c>). An end
    /// still unterminated when the input ends is never read: bash's <c>read</c> fails on it and the loop ends.
    /// A record with nothing pending and no unterminated end passes unchanged.
    /// </summary>
    private object? CompleteLines(PSObject? record)
    {
        bool unterminated = BashRuntime.IsUnterminated(record);
        if (_partial is null && !unterminated) return record;
        string text = (_partial ?? "") + BashRuntime.GetBashText(record);
        _partial = null;
        if (!unterminated) return text;
        int nl = text.LastIndexOf('\n');
        _partial = text[(nl + 1)..];
        // Through its newline: the loop drops one trailing newline per record, so an empty last line still counts.
        return nl < 0 ? null : text[..(nl + 1)];
    }
}

/// <summary>
/// The shared state between <see cref="InvokeBashPipeGateCommand"/> and the streamed read loop right after it.
/// The gate opens one in its BeginProcessing and the loop's <c>begin</c> block claims it: PowerShell runs the
/// begin blocks of a pipeline in order, one after the other, so nothing can open another gate in between.
/// </summary>
public sealed class BashPipeGate
{
    [ThreadStatic] private static BashPipeGate? s_pending;

    /// <summary>Set by the loop when it has ended: no more records are wanted.</summary>
    public bool Done { get; set; }

    internal static BashPipeGate Open() => s_pending = new BashPipeGate();

    /// <summary>The gate the stage in front of this loop opened (a detached one when there is none).</summary>
    public static BashPipeGate Claim()
    {
        var gate = s_pending ?? new BashPipeGate();
        s_pending = null;
        return gate;
    }
}
