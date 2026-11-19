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
public sealed class InvokeBashPipeStageCommand : BashCmdlet
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
    /// before it stops this one (head sets 0, then stops <c>nosuchcmd</c>, whose 127 was lost). Public for the host's
    /// command-not-found handler (SdkRunspaceSetup.ps1), script code that sets its status without a cmdlet.
    /// </summary>
    public static void NoteStatus(int code)
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

    /// <summary>The stage has ended: its status is recorded and its steppable pipeline disposed.</summary>
    private bool _finished;

    protected override void ProcessRecord()
    {
        if (_finished) return;
        try { Run(() => _stage!.Process(InputObject)); }
        catch (Exception ex) when (StoppedByOwnCommand(ex))
        {
            // A command of THIS stage has what it needs (`head -c 3`) and asked to stop the commands before it. A
            // steppable pipeline hands that request out raw, and those commands are the stages before this one in the
            // OUTER pipeline, where the engine looks the asker up to stop what precedes it and complete it and what
            // follows. The inner command is not in that pipeline, so the engine completed NOTHING: the stages after this
            // one lost their end (`printenv -0 | head -c 3 | od -c` printed nothing) and every status stayed 0
            // (`nosuchcmd 2>&1 | head -1` gave PIPESTATUS `0 0`). Record this stage as done and ask again as itself, a
            // command the outer pipeline has; the engine then ends the stages after it and not this one.
            FinishStopped();
            UpstreamStop.Throw(this);
            throw;
        }
        catch (Exception ex) when (FileSystemHelpers.IsPipelineStop(ex)) { RecordStopped(); throw; }
    }

    protected override void EndProcessing()
    {
        if (_finished) return;
        _finished = true;
        try { Run(() => _stage!.End()); }
        catch (Exception ex) when (StoppedByOwnCommand(ex)) { Record(_ownStatus ?? CurrentStatus()); return; }
        catch (Exception ex) when (FileSystemHelpers.IsPipelineStop(ex)) { RecordStopped(); throw; }
        finally { _stage!.Dispose(); }
        Record(CurrentStatus());
    }

    /// <summary>This stage's own command stopped it, done (the steppable pipeline has already disposed its commands).</summary>
    private void FinishStopped()
    {
        _finished = true;
        _stage!.Dispose();
        Record(_ownStatus ?? CurrentStatus());
    }

    /// <summary>
    /// <paramref name="ex"/> is an upstream stop a command inside this stage asked for (head), not a later stage's
    /// (every stage is an <see cref="InvokeBashPipeStageCommand"/>, which turns its own command's stop into its own).
    /// </summary>
    private static bool StoppedByOwnCommand(Exception ex)
        => UpstreamStop.RequestedBy(ex) is { } asker && asker is not InvokeBashPipeStageCommand;

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
