using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Removes a previously registered chpwd hook. No-op if the name is not registered.
/// </summary>
[Cmdlet(VerbsLifecycle.Unregister, "BashChpwdHook")]
public sealed class UnregisterBashChpwdHookCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    public string Name { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        HookRegistry.Instance.Unregister(HookKind.ChpwdHook, Name);
    }
}
