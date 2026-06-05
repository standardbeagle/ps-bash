using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPwd</c> function
/// (REFACTOR-2 Phase 1b). Prints the shell's current working directory,
/// matching the bash <c>pwd</c> builtin.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet
/// reproduces its exact location-resolution ladder:
/// <list type="bullet">
/// <item>Default (logical): honor an explicit <c>$global:__PsBashCwd</c>
/// override if set, otherwise fall through.</item>
/// <item><c>-P</c> (physical): resolve the provider path of the current
/// location; on failure, emit a bash-style error and return.</item>
/// <item>Final fallback: PowerShell's current filesystem location
/// (<see cref="PathIntrinsics.CurrentLocation"/>), which tracks
/// <c>Set-Location</c> / <c>Push-Location</c> across platforms — unlike
/// <c>$env:PWD</c>, which is unreliable on macOS Pester runners.</item>
/// </list>
/// Backslashes are normalized to forward slashes, and the result is emitted
/// as a typed <c>PsBash.PwdLine</c> BashObject via
/// <see cref="BashRuntime.NewBashObject"/> (bypassing the bare-string fast
/// path so consumers and tests get a PSCustomObject with <c>.BashText</c>).
///
/// psm1-only dependencies, and why a clean migration is still possible:
/// a <see cref="PSCmdlet"/> has <see cref="PSCmdlet.SessionState"/> access, so
/// it can read <c>$global:__PsBashCwd</c> and the current location directly —
/// no script callback on the hot path. The <c>-P</c> failure path delegates
/// to the psm1 <c>Write-BashError</c> function (which owns the script-scoped
/// error-sink switch) via a string-bodied <c>InvokeCommand.InvokeScript</c>,
/// so the cmdlet stays AOT-safe. The <c>--help</c> path likewise delegates to
/// <c>Show-BashHelp</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPwd")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashPwdCommand : PSCmdlet
{
    /// <summary>
    /// The bash <c>-P</c> (physical) switch. Declared as an explicit
    /// parameter — not scanned out of <see cref="Arguments"/> — because the
    /// bare token <c>-P</c> is a prefix of the PowerShell common parameter
    /// <c>-ProgressAction</c>, so an unbound <c>-P</c> would be consumed by
    /// common-parameter prefix matching before reaching
    /// <see cref="Arguments"/>. An exact parameter-name match takes
    /// precedence over a common-parameter prefix match, so declaring <c>P</c>
    /// here makes <c>Invoke-BashPwd -P</c> bind the way the psm1 oracle's
    /// <c>$args</c> scan did.
    /// </summary>
    [Parameter]
    public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (FileSystemHelpers.TryHandleVersion(this, "pwd", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "pwd"))
            {
                WriteObject(line);
            }
            return;
        }

        bool physical = P.IsPresent;

        string? location = null;

        if (!physical)
        {
            // StrictMode-safe override lookup: the psm1 oracle uses
            // Get-Variable -ErrorAction SilentlyContinue; PSVariable.GetValue
            // returns null when the variable is undefined.
            var cwdOverride = SessionState.PSVariable.GetValue("global:__PsBashCwd", null);
            if (cwdOverride != null)
            {
                var asString = cwdOverride.ToString();
                if (!string.IsNullOrEmpty(asString))
                {
                    location = asString;
                }
            }
        }
        else
        {
            try
            {
                // psm1 oracle: (Resolve-Path (Get-Location).Path).ProviderPath.
                // PathInfo.ProviderPath is the resolved physical path of the
                // current location — the same value, without a Resolve-Path
                // round trip.
                location = SessionState.Path.CurrentLocation.ProviderPath;
            }
            catch (Exception ex)
            {
                FileSystemHelpers.WriteBashError(this, $"pwd: error resolving path: {ex.Message}");
                return;
            }
        }

        // Final fallback: PowerShell's current location, which respects
        // Set-Location / Push-Location in Pester runners.
        if (string.IsNullOrEmpty(location))
        {
            location = SessionState.Path.CurrentLocation.Path;
        }

        location = location.Replace('\\', '/');

        WriteObject(BashRuntime.NewBashObject(
            location, "PsBash.PwdLine", noTrailingNewline: false, command: "pwd"));
    }
}
