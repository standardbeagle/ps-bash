using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashDirs</c> function
/// (REFACTOR-2 dir-stack batch). Implements the bash <c>dirs</c> builtin:
/// list the current location stack. <c>-c</c> clears the stack, <c>-p</c>
/// emits one entry per line, <c>-v</c> emits numbered entries; default emits
/// a single space-joined line with the current location first then the
/// stack contents reversed (oracle parity).
///
/// Behavioral parity oracle: the original psm1 function, which uses
/// <c>Get-Location -Stack</c> and <c>Pop-Location -Stack</c>. The cmdlet
/// preserves that identity exactly — see <see cref="InvokeBashPushdCommand"/>
/// for the state-ownership rationale.
///
/// <para>Three colliding flags declared as explicit <see cref="SwitchParameter"/>s
/// with single-letter names so the binder routes bare tokens by exact-name
/// match (beats common-parameter prefix-matching, same playbook pattern as
/// <see cref="InvokeBashShoptCommand"/>): <c>-c</c> vs <c>-Confirm</c> → <c>C</c>;
/// <c>-p</c> vs <c>-PipelineVariable</c> / <c>-ProgressAction</c> → <c>P</c>;
/// <c>-v</c> vs <c>-Verbose</c> → <c>V</c>.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDirs")]
public sealed class InvokeBashDirsCommand : PSCmdlet
{
    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter]
    public SwitchParameter P { get; set; }

    [Parameter]
    public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (FileSystemHelpers.TryHandleVersion(this, "dirs", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "dirs"))
            {
                WriteObject(line);
            }
            return;
        }

        if (C.IsPresent)
        {
            // Oracle: drain the stack via Pop-Location -Stack until empty.
            InvokeCommand.InvokeScript(
                @"while (Get-Location -Stack -ErrorAction SilentlyContinue) {
                    Pop-Location -Stack -ErrorAction SilentlyContinue
                }");
            return;
        }

        // Collect stack entries: Get-Location -Stack returns a Stack<PathInfo>
        // (top-first). The oracle reverses to bottom-first, then either
        // numbers or one-per-lines or joins with the current location.
        var stackResult = InvokeCommand.InvokeScript(
            @"@(Get-Location -Stack | ForEach-Object { $_.Path })");
        var stack = new List<string>();
        foreach (var item in stackResult)
        {
            if (item?.BaseObject is string s)
            {
                stack.Add(s);
            }
        }
        // [array]::Reverse — oracle's reverse step.
        stack.Reverse();

        if (V.IsPresent)
        {
            for (int i = 0; i < stack.Count; i++)
            {
                WriteObject(BashRuntime.NewBashObject($"{i}  {stack[i]}"));
            }
        }
        else if (P.IsPresent)
        {
            foreach (var entry in stack)
            {
                WriteObject(BashRuntime.NewBashObject(entry));
            }
        }
        else
        {
            // Default: current location followed by reversed stack, space-joined.
            var currentResult = InvokeCommand.InvokeScript("(Get-Location).Path");
            string current = string.Empty;
            foreach (var item in currentResult)
            {
                if (item?.BaseObject is string s) { current = s; break; }
            }
            var parts = new List<string> { current };
            parts.AddRange(stack);
            WriteObject(BashRuntime.NewBashObject(string.Join(' ', parts)));
        }
    }
}
