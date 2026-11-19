using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Coreutils <c>true</c> as a PROGRAM. In a script <c>true</c> is a builtin the transpiler handles itself, but a
/// command that runs another by name (<c>env true</c>, <c>xargs false</c>, <c>timeout 1 true</c>, <c>nice false</c>)
/// resolves it as a command, and there was none: <c>xargs false</c> was 127 "No such file or directory" instead of
/// 123. Aliases <c>true</c> / <c>false</c> (psm1) name these cmdlets. Operands are ignored; stdin is drained.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTrue")]
public sealed class InvokeBashTrueCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    protected override void EndProcessing() => FileSystemHelpers.SetLastExitCode(this, 0);
}

/// <summary>Coreutils <c>false</c> as a program: status 1. See <see cref="InvokeBashTrueCommand"/>.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFalse")]
public sealed class InvokeBashFalseCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    protected override void EndProcessing() => FileSystemHelpers.SetLastExitCode(this, 1);
}
