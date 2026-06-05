using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPushd</c> function
/// (REFACTOR-2 dir-stack batch). Implements the bash <c>pushd</c> builtin:
/// push current directory onto the location stack and chdir to a new path,
/// or with <c>+N</c> rotate the Nth stack entry to the top.
///
/// Behavioral parity oracle: the original psm1 function. The oracle uses
/// PowerShell's built-in location stack (the same one <c>Push-Location</c> /
/// <c>Pop-Location -Stack</c> / <c>Get-Location -Stack</c> manage), NOT a
/// separate <c>$global:BashDirStack</c> array. The cmdlet preserves that
/// identity exactly by delegating to those same cmdlets via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// (AOT-safe; no <see cref="ScriptBlock"/> construction). Sharing the
/// runspace location stack means <c>cd</c>, <c>Set-Location</c>, and bash
/// <c>pushd</c>/<c>popd</c>/<c>dirs</c> all see the same stack — exactly
/// what the oracle did.
///
/// <para>Flag surface: a single <c>+N</c> positional, or a single directory
/// path operand, or no operands (defaults to "."). No PowerShell common
/// parameter prefix collision — all tokens flow through <c>Arguments</c>.</para>
///
/// <para><c>--help</c> delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPushd")]
public sealed class InvokeBashPushdCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (FileSystemHelpers.TryHandleVersion(this, "pushd", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "pushd"))
            {
                WriteObject(line);
            }
            return;
        }

        // +N rotation: oracle's `^\+(\d+)$` match.
        if (args.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(args[0], @"^\+(\d+)$"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(args[0], @"^\+(\d+)$");
            var n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            // Replicate the oracle slice byte-for-byte: get stack, pick Nth,
            // pop (N+1) entries, push the target. The delegation script reads
            // its arg via $args[0] so user input cannot reach the script body.
            InvokeCommand.InvokeScript(
                @"param($n)
                $stack = @(Get-Location -Stack)
                if ($n -ge 0 -and $n -lt $stack.Count) {
                    $target = $stack[$n]
                    for ($i = 0; $i -le $n; $i++) { Pop-Location -Stack -ErrorAction SilentlyContinue }
                    Push-Location -Path $target.Path
                }",
                n);
            return;
        }

        // Default: push current location and chdir to the given path (or '.').
        // The path token is bound as a positional $args[0] so a path containing
        // ; / $() / scriptblock chars stays a literal path (Directive 12).
        var path = args.Length > 0 ? args[0] : ".";
        try
        {
            InvokeCommand.InvokeScript(
                "param($p) Push-Location -Path $p",
                path);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            // Surface a bash-style error rather than a raw PS exception.
            FileSystemHelpers.WriteBashError(this, $"pushd: {path}: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }
}
