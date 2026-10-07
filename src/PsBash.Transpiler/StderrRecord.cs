namespace PsBash.Core;

/// <summary>
/// The bytes of one bash stderr record — the single rule shared by every place a PowerShell error record
/// becomes stderr output: the host's STDERR frame (SdkWorker), a <c>2&gt; file</c> target and a
/// <c>2&gt;&amp;1</c> merge (Invoke-BashRedirect). SMA-free on purpose: it lives in the assembly both the
/// host (via Core) and the cmdlets reference.
/// <para>
/// A record is a LINE (LF-terminated) unless its writer marked it as not ending in a newline —
/// <c>printf x &gt;&amp;2</c> / <c>echo -n x &gt;&amp;2</c> write exactly <c>x</c>. <c>Write-BashHostStderr</c>
/// marks such a record by setting its <c>TargetObject</c> to <see cref="NoTrailingNewlineMarker"/>; a cmdlet
/// diagnostic is never marked, so it stays a line.
/// </para>
/// </summary>
public static class StderrRecord
{
    /// <summary>The <c>ErrorRecord.TargetObject</c> of a stderr record that does not end in a newline.</summary>
    public const string NoTrailingNewlineMarker = "PsBash.Stderr.NoTrailingNewline";

    /// <summary>The record's bytes as bash writes them: <paramref name="text"/> plus the LF record
    /// boundary, unless marked (<paramref name="targetObject"/>) or already LF-terminated.</summary>
    public static string Payload(string text, object? targetObject) =>
        targetObject is NoTrailingNewlineMarker || text.EndsWith('\n') ? text : text + "\n";
}
