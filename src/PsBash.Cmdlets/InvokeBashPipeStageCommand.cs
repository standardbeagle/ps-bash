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

    protected override void BeginProcessing()
    {
        // A stage that sets no status of its own (a pure filter) reports success, never a predecessor's leftovers.
        FileSystemHelpers.SetLastExitCode(this, 0);
        _stage = Script.GetSteppablePipeline(CommandOrigin.Internal);
        _stage.Begin(this);
    }

    protected override void ProcessRecord() => _stage!.Process(InputObject);

    protected override void EndProcessing()
    {
        try { _stage!.End(); }
        finally { _stage!.Dispose(); }
        int code = 0;
        try
        {
            if (SessionState.PSVariable.GetValue("global:LASTEXITCODE") is { } v)
                code = LanguagePrimitives.ConvertTo<int>(v);
        }
        catch { /* a non-numeric leftover is no status */ }
        if (Index >= 0 && Index < Slot.Length) Slot[Index] = code;
    }

    protected override void StopProcessing() => _stage?.Dispose();
}
