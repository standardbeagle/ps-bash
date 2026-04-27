using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Describes a single registered hook as returned by <c>Get-BashHook</c>.
/// </summary>
public sealed class BashHookInfo
{
    public HookKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public ScriptBlock ScriptBlock { get; init; } = ScriptBlock.Create("");

    public override string ToString() => $"{Kind}/{Name}";
}
