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
/// Guard: <c>BashCmdletBaseGuardTests</c> (every cmdlet derives from this; no cmdlet writes the status itself).
/// </summary>
public abstract class BashCmdlet : PSCmdlet, IDisposable
{
    /// <summary>The last status this instance set; null when it set none.</summary>
    internal int? OwnStatus { get; set; }

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
        if (OwnStatus is not int status || MyInvocation is not { PipelinePosition: > 1 }) return;
        try { SessionState.PSVariable.Set(new PSVariable("global:LASTEXITCODE", status)); }
        catch (Exception ex) when (ex is InvalidOperationException or PSInvalidOperationException) { /* torn-down runspace */ }
    }
}
