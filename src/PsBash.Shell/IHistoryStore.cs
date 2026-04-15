namespace PsBash.Shell;

/// <summary>
/// A pluggable command history store with async I/O support.
/// Implementations should handle their own connection management and error recovery.
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Records a command execution in the history store.
    /// </summary>
    /// <param name="entry">The history entry to record.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Implementations should:
    /// - Deduplicate consecutive identical commands
    /// - Trim history to avoid unbounded growth (respect MaxEntries config)
    /// - Handle write errors gracefully (log, don't throw)
    /// </remarks>
    Task RecordAsync(HistoryEntry entry);

    /// <summary>
    /// Searches history entries matching the specified criteria.
    /// </summary>
    /// <param name="query">The search query with filters and limits.</param>
    /// <returns>
    /// History entries matching the query, ordered by relevance (newest first for timestamp queries).
    /// </returns>
    /// <remarks>
    /// Search behavior:
    /// - Prefix match on Command when Filter is set
    /// - Filter by CWD when Cwd is set
    /// - Filter by SessionId when SessionId is set
    /// - Filter by exit code when ExitCode is set (0 = success, non-zero = failure)
    /// - Limit results to avoid large result sets (default: 100)
    /// - Reverse ordering when Reverse is true (oldest first)
    /// </remarks>
    Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query);

    /// <summary>
    /// Gets sequence-based suggestions for the next command based on recent history.
    /// </summary>
    /// <param name="lastCommand">The most recently executed command (full text).</param>
    /// <param name="cwd">The current working directory for context filtering.</param>
    /// <returns>
    /// Suggested commands ordered by score (highest first). Empty list if no data.
    /// </returns>
    /// <remarks>
    /// Sequence suggestions are based on command pair frequency:
    /// - "After running X, users often run Y"
    /// - Higher score = stronger sequence correlation
    /// - CWD filtering boosts local relevance
    /// - Used for post-command autosuggestions (P6 feature)
    /// </remarks>
    Task<IReadOnlyList<SequenceSuggestion>> GetSequenceSuggestionsAsync(
        string? lastCommand,
        string cwd);
}
