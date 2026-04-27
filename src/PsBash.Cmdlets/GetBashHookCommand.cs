using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Lists registered ps-bash hooks. Use <c>-Kind</c> to filter by hook kind.
/// </summary>
[Cmdlet(VerbsCommon.Get, "BashHook")]
[OutputType(typeof(BashHookInfo))]
public sealed class GetBashHookCommand : PSCmdlet
{
    /// <summary>Optional filter. Omit to return all hooks.</summary>
    [Parameter]
    public HookKind? Kind { get; set; }

    protected override void ProcessRecord()
    {
        var all = HookRegistry.Instance.GetAll();
        foreach (var info in all)
        {
            if (Kind == null || info.Kind == Kind.Value)
                WriteObject(info);
        }
    }
}
