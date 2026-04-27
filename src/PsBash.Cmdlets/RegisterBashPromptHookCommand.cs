using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Registers a scriptblock to run on every prompt tick.
/// Re-registering an existing name replaces the hook atomically.
/// </summary>
[Cmdlet(VerbsLifecycle.Register, "BashPromptHook")]
public sealed class RegisterBashPromptHookCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    public string Name { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public ScriptBlock ScriptBlock { get; set; } = ScriptBlock.Create("");

    protected override void ProcessRecord()
    {
        HookRegistry.Instance.Register(HookKind.PromptHook, Name, ScriptBlock);
    }
}
