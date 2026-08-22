using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>cd</c> for <b>module mode</b> (<c>Import-Module PsBash</c> in a plain pwsh): delegates to
/// <c>Set-Location</c> and then writes <see cref="Environment.CurrentDirectory"/> too, so both
/// halves of the working directory move together.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> The psm1 used to alias <c>cd</c> straight to
/// <c>Set-Location</c>, which moves only the PowerShell location. A bash working directory is
/// BOTH that and the process working directory — the root every .NET path API resolves against.
/// Leaving the second behind is a silent-wrong-output bug, not an inconsistency: under
/// <c>Import-Module PsBash</c>, <c>cd sub; cat data.txt | grep x | sort</c> read the OUTER
/// <c>data.txt</c> at exit 0. That was the third instance of the same failure
/// (<c>pushd</c> and the emitter's subshell <c>Pop-Location</c> were the first two), and it is
/// why <c>InvokeBashPushdCommand.SyncProcessWorkingDirectory</c> is shared rather than re-derived
/// per caller.</para>
/// <para>The transpiled <c>ps-bash</c> lane does not come through here — the emitter writes both
/// halves itself (<c>docs/specs/emitter-strategy.md</c> §4 <c>EmitCd</c>). This is the module-mode
/// path only.</para>
/// <para><b>Semantics are Set-Location's, unchanged.</b> Arguments are splatted through verbatim,
/// so <c>cd</c> (home), <c>cd -</c> / <c>cd +</c>, <c>cd ~</c>, and named forms like
/// <c>cd -LiteralPath 'weird[1]'</c> all behave exactly as they did when <c>cd</c> was the bare
/// alias. A failed move (missing directory) propagates PowerShell's own error and, having not
/// moved, syncs nothing.</para>
/// </remarks>
[Cmdlet(VerbsLifecycle.Invoke, "BashCd")]
public sealed class InvokeBashCdCommand : PSCmdlet
{
    /// <summary>The operands, forwarded to <c>Set-Location</c> unchanged.</summary>
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "cd", args)) return;

        // Splatted as a BOUND parameter, never concatenated into the script text, so a path
        // containing ; / $() / scriptblock characters stays a literal path (Directive 12).
        InvokeCommand.InvokeScript("param($a) Set-Location @a", new object[] { args });

        // Only reached when the move succeeded — a throwing Set-Location propagates above.
        InvokeBashPushdCommand.SyncProcessWorkingDirectory(this);
    }
}
