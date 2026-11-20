using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The stage after a PowerShell <c>2&gt;&amp;1</c> that only CAPTURES a nested command's error stream, so the caller can
/// re-write each record on its own (<see cref="InnerCommand"/>: <c>&amp; $c @rest 2&gt;&amp;1 | Invoke-BashKeepStderr</c>). It
/// passes every record on unchanged; what it changes is <see cref="StderrMerge.JoinsStdout"/> for the command ahead of
/// it: that <c>2&gt;&amp;1</c> is not a bash merge, so a spawning cmdlet keeps its child's stderr apart. Without it
/// <c>env sh -c 'echo e &gt;&amp;2' 2&gt;/dev/null</c> printed e: the child got one pipe for both streams and its stderr came
/// back as stdout, past the wrapper's redirect. Emitted by no translation of bash; runtime-internal.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashKeepStderr")]
public sealed class InvokeBashKeepStderrCommand : BashCmdlet
{
    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    protected override void ProcessRecord()
    {
        if (InputObject is not null) WriteObject(InputObject);
    }
}
