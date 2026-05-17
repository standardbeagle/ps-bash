using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Shared utilities for the file-system mutator cmdlets — mkdir, rmdir, cp,
/// mv, rm — migrated from PsBash.psm1 in REFACTOR-2. Each method reproduces a
/// helper the psm1 oracle used (<c>Resolve-BashGlob</c>, <c>Get-BashItem</c>,
/// <c>Write-BashError</c>) so the cmdlets stay off the script-level helper
/// surface on their hot path.
/// </summary>
internal static class FileSystemHelpers
{
    /// <summary>
    /// Replicates the psm1 <c>Resolve-BashGlob</c> contract for a single
    /// operand: literal paths fall through unchanged so the caller can emit a
    /// bash-style "no such file" error on a missing target; wildcard paths
    /// (<c>*</c> / <c>?</c>) expand via
    /// <see cref="System.Management.Automation.PathIntrinsics.GetResolvedProviderPathFromPSPath(string, out ProviderInfo)"/>
    /// and fall through as literal if nothing matches. Same slice
    /// <see cref="InvokeBashCatCommand"/> and the ChecksumEngine use.
    /// </summary>
    public static IEnumerable<string> ResolveOperandPaths(PSCmdlet cmdlet, string raw)
    {
        if (raw.IndexOf('*') < 0 && raw.IndexOf('?') < 0)
        {
            yield return cmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath(raw);
            yield break;
        }

        var matched = new List<string>();
        try
        {
            foreach (var resolved in cmdlet.SessionState.Path
                         .GetResolvedProviderPathFromPSPath(raw, out _))
            {
                matched.Add(resolved);
            }
        }
        catch
        {
            // No matches — fall through to literal passthrough.
        }

        if (matched.Count == 0)
        {
            yield return raw;
        }
        else
        {
            foreach (var m in matched) yield return m;
        }
    }

    /// <summary>
    /// Emit a bash-style error via the psm1 <c>Write-BashError</c> function.
    /// The error-mode switch (<c>$script:BashErrorMode</c>) lives in psm1
    /// scope, so we go through <see cref="PSCmdlet.InvokeCommand"/>'s
    /// parameter-bound script body — no <see cref="ScriptBlock"/> construction,
    /// AOT-safe.
    /// </summary>
    public static void WriteBashError(PSCmdlet cmdlet, string message)
    {
        cmdlet.InvokeCommand.InvokeScript(
            "param($m) Write-BashError -Message $m", message);
    }

    /// <summary>
    /// Sets the bash-visible exit code via the global LASTEXITCODE. The psm1
    /// mutator oracles all do this on the last error in a multi-operand run;
    /// we mirror it exactly.
    /// </summary>
    public static void SetLastExitCode(PSCmdlet cmdlet, int code)
    {
        cmdlet.SessionState.PSVariable.Set(new PSVariable("global:LASTEXITCODE", code));
    }

    /// <summary>
    /// Bash-style path normalization for verbose output: backslash → slash.
    /// </summary>
    public static string ToBashPath(string winPath) => winPath.Replace('\\', '/');
}
