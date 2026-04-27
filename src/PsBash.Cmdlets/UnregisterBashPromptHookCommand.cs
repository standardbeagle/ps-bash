using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Removes a previously registered prompt hook. No-op if the name is not registered.
/// </summary>
[Cmdlet(VerbsLifecycle.Unregister, "BashPromptHook")]
public sealed class UnregisterBashPromptHookCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    public string Name { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        HookRegistry.Instance.Unregister(HookKind.PromptHook, Name);
    }
}
