namespace PsBash.Shell;

/// <summary>
/// Fish-style autosuggestion provider using history prefix search.
/// Returns the best completion suffix for a given input prefix and CWD.
/// </summary>
public sealed class Suggester
{
    private readonly IHistoryStore _store;

    public Suggester(IHistoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Gets the best suggestion suffix for the given prefix and CWD.
    /// </summary>
    /// <param name="prefix">The text to complete (must be a prefix of a history entry).</param>
    /// <param name="cwd">The current working directory for CWD-aware ranking.</param>
    /// <returns>
    /// The suffix to append to <paramref name="prefix"/> to complete the suggestion,
    /// or null if no suggestion is available.
    /// Returns empty string if the prefix is already a complete match.
    /// </returns>
    /// <remarks>
    /// Ranking algorithm:
    /// 1. Try CWD-scoped search first (prefer commands from current directory)
    /// 2. Fall back to global search if no CWD match
    /// 3. Within results, prefer most recent (newest timestamp first)
    /// 4. Case-sensitive prefix matching
    /// </returns>
    public async Task<string?> SuggestAsync(string prefix, string cwd)
    {
        // No suggestion for empty input
        if (string.IsNullOrEmpty(prefix))
            return null;

        // Try CWD-scoped search first
        var cwdResults = await _store.SearchAsync(new HistoryQuery
        {
            Filter = prefix,
            Cwd = cwd,
            Limit = 1
        });

        HistoryEntry? bestMatch = null;

        if (cwdResults.Count > 0)
        {
            bestMatch = cwdResults[0];
        }
        else
        {
            // Fall back to global search
            var globalResults = await _store.SearchAsync(new HistoryQuery
            {
                Filter = prefix,
                Limit = 1
            });

            if (globalResults.Count > 0)
            {
                bestMatch = globalResults[0];
            }
        }

        if (bestMatch is null)
            return null;

        // Check for exact match - no completion needed
        if (bestMatch.Command == prefix)
            return string.Empty;

        // Return the suffix (the part after the prefix)
        if (bestMatch.Command.StartsWith(prefix, StringComparison.Ordinal))
            return bestMatch.Command.Substring(prefix.Length);

        return null;
    }
}
