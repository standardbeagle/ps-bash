using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// One stage of a bash pipeline whose individual exit status must be known (<c>set -o pipefail</c>).
/// A PowerShell pipeline keeps a single <c>$LASTEXITCODE</c>, so the emitter wraps each stage of a
/// pipefail script in this cmdlet. The stage runs as a steppable pipeline — records stream through it
/// exactly as through the bare stage — and its status is read the moment the stage finishes, into
/// <see cref="Slot"/>[<see cref="Index"/>]. The emitter folds the slots after the pipeline.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPipeStage")]
public sealed class InvokeBashPipeStageCommand : PSCmdlet
{
    /// <summary>The stage, as a script block holding one pipeline.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    public ScriptBlock Script { get; set; } = null!;

    /// <summary>One status cell per stage of the pipeline.</summary>
    [Parameter(Mandatory = true)]
    public int[] Slot { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public int Index { get; set; }

    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    private SteppablePipeline? _stage;

    /// <summary>The status once the stage had begun (a command that is not found has failed by then).</summary>
    private int _beginStatus;

    /// <summary>The last status a command of THIS stage set (<see cref="NoteStatus"/>); null when none did.</summary>
    private int? _ownStatus;

    /// <summary>The stage whose code is running on this thread: a later stage runs inside an earlier one's write.</summary>
    [ThreadStatic]
    private static InvokeBashPipeStageCommand? t_running;

    /// <summary>
    /// A command set <paramref name="code"/> as the status (<see cref="FileSystemHelpers.SetLastExitCode"/>): kept for the
    /// stage it runs in. <c>$LASTEXITCODE</c> is one variable for every stage, and a stage downstream overwrites it
    /// before it stops this one (head sets 0, then stops <c>nosuchcmd</c>, whose 127 was lost).
    /// </summary>
    internal static void NoteStatus(int code)
    {
        if (t_running is { } stage) stage._ownStatus = code;
    }

    private void Run(Action step)
    {
        var prior = t_running;
        t_running = this;
        try { step(); }
        finally { t_running = prior; }
    }

    protected override void BeginProcessing()
    {
        // A stage that sets no status of its own (a pure filter) reports success, never a predecessor's leftovers.
        FileSystemHelpers.SetLastExitCode(this, 0);
        _stage = Script.GetSteppablePipeline(CommandOrigin.Internal);
        Run(() => _stage!.Begin(this));
        // Read now: the later stages' own starts reset the status before this stage writes anything.
        _beginStatus = CurrentStatus();
    }

    protected override void ProcessRecord()
    {
        try { Run(() => _stage!.Process(InputObject)); }
        catch (Exception ex) when (FileSystemHelpers.IsPipelineStop(ex)) { RecordStopped(); throw; }
    }

    protected override void EndProcessing()
    {
        try { Run(() => _stage!.End()); }
        catch (Exception ex) when (FileSystemHelpers.IsPipelineStop(ex)) { RecordStopped(); throw; }
        finally { _stage!.Dispose(); }
        Record(CurrentStatus());
    }

    /// <summary>
    /// A later stage stopped this one (<c>nosuchcmd 2&gt;&amp;1 | head -1</c>: head has its line). The stage's status is
    /// what its own commands had set by then (127 for a command that is not found, whose message head took), else the
    /// status now when it is a failure, else the one it began with. A producer that had not failed stays 0 (bash: 141,
    /// SIGPIPE).
    /// </summary>
    private void RecordStopped()
    {
        int now = CurrentStatus();
        Record(_ownStatus is int own && own != 0 ? own : now != 0 ? now : _beginStatus);
    }

    private void Record(int code)
    {
        if (Index >= 0 && Index < Slot.Length) Slot[Index] = code;
    }

    private int CurrentStatus()
    {
        try
        {
            if (SessionState.PSVariable.GetValue("global:LASTEXITCODE") is { } v)
                return LanguagePrimitives.ConvertTo<int>(v);
        }
        catch { /* a non-numeric leftover is no status */ }
        return 0;
    }

    protected override void StopProcessing() => _stage?.Dispose();
}
