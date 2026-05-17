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
    /// Emit a bash-style error visible to the caller via <c>2&gt;&amp;1</c>
    /// pipeline merge — including under Pester where
    /// <c>$ErrorActionPreference = Stop</c> would otherwise convert a plain
    /// <see cref="PSCmdlet.WriteError"/> into a terminating error mid-test.
    /// <para>
    /// Strategy: invoke the psm1 <c>Write-BashError</c> through
    /// <see cref="PSCmdlet.InvokeCommand"/> with an inner <c>2&gt;&amp;1</c>
    /// redirect so the resulting <see cref="ErrorRecord"/> lands in the
    /// script's success stream as a captured object. We then re-emit it via
    /// <see cref="PSCmdlet.WriteObject(object)"/> into the outer cmdlet's
    /// success stream. Callers using <c>cmd 2&gt;&amp;1 | Where {$_ -is [ErrorRecord]}</c>
    /// find the record without triggering the caller's
    /// <c>$ErrorActionPreference</c> escalation. Bash-mode formatting
    /// (production host launcher) is still handled by the psm1 helper.
    /// </para>
    /// <para>Sets <c>$global:LASTEXITCODE = 1</c>.</para>
    /// </summary>
    public static void WriteBashError(PSCmdlet cmdlet, string message)
    {
        SetLastExitCode(cmdlet, 1);

        // Invoke the psm1 helper with an inner 2>&1 redirect so any
        // ErrorRecord it emits ends up in the script's success stream.
        var emitted = cmdlet.InvokeCommand.InvokeScript(
            "param($m) Write-BashError -Message $m 2>&1", message);

        foreach (var item in emitted)
        {
            if (item == null) continue;
            // Surface ErrorRecord-typed items to the outer cmdlet's pipeline
            // via WriteObject — benign w.r.t. $ErrorActionPreference.
            var baseObj = (item is PSObject po) ? po.BaseObject : item;
            if (baseObj is ErrorRecord er)
            {
                cmdlet.WriteObject(er);
            }
        }
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
