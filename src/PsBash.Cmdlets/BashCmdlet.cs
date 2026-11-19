using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Base of every <c>Invoke-Bash*</c> cmdlet: a pipeline's status is its LAST stage's.
/// <para>
/// PowerShell runs a pipeline's stages interleaved, not in order: every BeginProcessing first, then records, then
/// EndProcessing stage by stage. A stage that sets its status before the stages ahead of it have finished
/// (<c>echo x | grep --bogus</c>: grep's usage error is found while parsing, in BeginProcessing) is then overwritten by
/// those stages (echo sets <c>$LASTEXITCODE = 0</c> as it runs), so the pipeline came out 0 (GNU 2). The engine
/// disposes a pipeline's cmdlets after every EndProcessing, in ascending pipeline position; so each stage after the
/// first re-asserts, in <see cref="Dispose"/>, the last status IT set (<see cref="FileSystemHelpers.SetLastExitCode"/>
/// records it), and the last stage that set one wins, as in bash. The first stage has no stage ahead of it.
/// </para>
/// <para>
/// Only an overwrite by a stage AHEAD is undone. The last stage always re-asserts: nothing runs after it. A stage
/// before the last re-asserts only when the last cmdlet that set a status is a stage ahead of it in the same pipeline
/// and the status is still the one it set; anything else ran later and stands: a stage after it, a cmdlet nested in a
/// later stage (a <c>#!</c> script's stand-in), something that is not a cmdlet (a program, the host's
/// command-not-found handler: <c>echo x | grep zzz | nosuchcmd</c> is 127, not grep's 1). So a cmdlet's own status
/// must be its final one: a cmdlet that runs another command takes that command's status as its own
/// (<see cref="FileSystemHelpers.AdoptLastExitCode"/>).
/// </para>
/// Guard: <c>BashCmdletBaseGuardTests</c> (every cmdlet derives from this; no cmdlet writes the status itself).
/// </summary>
public abstract class BashCmdlet : PSCmdlet, IDisposable
{
    private int? _ownStatus;

    /// <summary>
    /// The cmdlet that set the status last on this thread (a pipeline and the commands nested in it run on one thread;
    /// an in-process job has its own).
    /// </summary>
    [ThreadStatic]
    private static BashCmdlet? t_lastSetter;

    /// <summary>The last status this instance set; null when it set none.</summary>
    internal int? OwnStatus
    {
        get => _ownStatus;
        set { _ownStatus = value; if (value is not null) t_lastSetter = this; }
    }

    /// <summary>Releases what the cmdlet holds (a child process, an open file). Runs after the status re-assert.</summary>
    protected virtual void DisposeResources() { }

    public void Dispose()
    {
        ReassertOwnStatus();
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void ReassertOwnStatus()
    {
        if (OwnStatus is not int status || MyInvocation is not { PipelinePosition: > 1 } me) return;
        // The last stage: whatever set the status after it ran ahead of it (a program ahead sets it as itself:
        // `./five | grep --bogus` is 2, not 5). A stage before the last undoes only a cmdlet ahead of it, so a trailing
        // stage that sets no status (`| Invoke-BashRedirect`, a capture) still shows it.
        bool last = me.PipelinePosition == me.PipelineLength;
        int? overwritten = null; // the status the cmdlet ahead set, which this stage undoes
        if (!last)
        {
            if (t_lastSetter is not { OwnStatus: int theirs } setter || !IsAheadInMyPipeline(setter.MyInvocation, me)) return;
            overwritten = theirs;
        }
        try
        {
            // Set since by something that is not a cmdlet: it ran later, and its status stands.
            if (overwritten is int ahead && SessionState.PSVariable.GetValue("global:LASTEXITCODE") is { } now
                && LanguagePrimitives.TryConvertTo<int>(now, out int current) && current != ahead) return;
            SessionState.PSVariable.Set(new PSVariable("global:LASTEXITCODE", status));
            t_lastSetter = this;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PSInvalidOperationException) { /* torn-down runspace */ }
    }

    /// <summary><paramref name="them"/> is a stage before <paramref name="me"/> in the same pipeline statement.</summary>
    internal static bool IsAheadInMyPipeline(InvocationInfo? them, InvocationInfo me)
        => them is not null
           && them.PipelineLength == me.PipelineLength
           && them.PipelinePosition < me.PipelinePosition
           && string.Equals(them.ScriptName, me.ScriptName, StringComparison.Ordinal)
           && (them.ScriptLineNumber < me.ScriptLineNumber
               || (them.ScriptLineNumber == me.ScriptLineNumber && them.OffsetInLine < me.OffsetInLine));
}
