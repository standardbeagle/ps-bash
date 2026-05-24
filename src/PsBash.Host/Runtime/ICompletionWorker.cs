namespace PsBash.Host.Runtime;

/// <summary>
/// Optional capability a worker may expose: run PowerShell's own completion engine
/// (<c>CommandCompletion.CompleteInput</c> / <c>TabExpansion2</c>) against its live runspace.
/// The completion engine probes for this via <c>as ICompletionWorker</c>, so workers without a
/// hostable runspace simply don't implement it and the engine falls back to introspection.
/// </summary>
internal interface ICompletionWorker
{
    /// <summary>
    /// Return the completion texts PowerShell produces for <paramref name="input"/> with the
    /// caret at <paramref name="cursorIndex"/>. This is the full PS engine: parameter values,
    /// <c>[ValidateSet]</c>, enums, provider paths, and anything registered with
    /// <c>Register-ArgumentCompleter</c>. Never throws — returns empty on failure/cancellation.
    /// </summary>
    Task<IReadOnlyList<string>> CompleteInputAsync(string input, int cursorIndex, CancellationToken ct = default);
}
