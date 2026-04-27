using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Registers a scriptblock to run when the current directory changes.
/// Re-registering an existing name replaces the hook atomically.
/// </summary>
[Cmdlet(VerbsLifecycle.Register, "BashChpwdHook")]
public sealed class RegisterBashChpwdHookCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    public string Name { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public ScriptBlock ScriptBlock { get; set; } = ScriptBlock.Create("");

    protected override void ProcessRecord()
    {
        HookRegistry.Instance.Register(HookKind.ChpwdHook, Name, ScriptBlock);
        // Auto-enable the prompt wrapper on first hook registration so chpwd hooks fire.
        SessionState.InvokeCommand.InvokeScript("Enable-BashHookPrompt");
    }
}
