namespace PsBash.Shell;

/// <summary>
/// In-memory history store for testing. Not thread-safe for concurrent writes.
/// </summary>
internal sealed class InMemoryHistoryStore : IHistoryStore
{
    private readonly List<HistoryEntry> _entries = new();

    public Task RecordAsync(HistoryEntry entry)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
    {
        var queryable = _entries.AsEnumerable();

        if (!string.IsNullOrEmpty(query.Filter))
            queryable = queryable.Where(e => e.Command.StartsWith(query.Filter, StringComparison.Ordinal));

        if (!string.IsNullOrEmpty(query.Cwd))
            queryable = queryable.Where(e => e.Cwd == query.Cwd);

        if (!string.IsNullOrEmpty(query.SessionId))
            queryable = queryable.Where(e => e.SessionId == query.SessionId);

        if (query.ExitCode.HasValue)
            queryable = queryable.Where(e => e.ExitCode == query.ExitCode.Value);

        queryable = query.Reverse ? queryable.OrderBy(e => e.Timestamp) : queryable.OrderByDescending(e => e.Timestamp);

        return Task.FromResult<IReadOnlyList<HistoryEntry>>(
            queryable.Take(query.Limit).ToList());
    }

    public Task<IReadOnlyList<SequenceSuggestion>> GetSequenceSuggestionsAsync(
        string? lastCommand,
        string cwd)
    {
        if (string.IsNullOrEmpty(lastCommand))
            return Task.FromResult<IReadOnlyList<SequenceSuggestion>>(Array.Empty<SequenceSuggestion>());

        // Build sequences on-the-fly from history, grouped by command and CWD
        var groupedSequences = new Dictionary<string, (long totalFreq, long cwdFreq)>(StringComparer.Ordinal);

        for (int i = 0; i < _entries.Count - 1; i++)
        {
            if (_entries[i].Command == lastCommand)
            {
                var nextCmd = _entries[i + 1].Command;
                var nextCwd = _entries[i + 1].Cwd;

                if (!groupedSequences.TryGetValue(nextCmd, out var scores))
                {
                    var isCwdMatch = nextCwd == cwd;
                    groupedSequences[nextCmd] = (1, isCwdMatch ? 1L : 0L);
                }
                else
                {
                    var newTotal = scores.totalFreq + 1;
                    var newCwdFreq = scores.cwdFreq + (nextCwd == cwd ? 1L : 0L);
                    groupedSequences[nextCmd] = (newTotal, newCwdFreq);
                }
            }
        }

        var results = new List<SequenceSuggestion>();
        foreach (var (command, (totalFreq, cwdFreq)) in groupedSequences)
        {
            double score;
            string reason;

            if (cwdFreq > 0)
            {
                // CWD-boosted: give extra weight to sequences that occurred in current directory
                var boostedFreq = cwdFreq * 2 + (totalFreq - cwdFreq);
                score = Math.Min(1.0, boostedFreq / 20.0);
                reason = cwdFreq == totalFreq
                    ? $"Followed '{lastCommand}' {cwdFreq} times in this directory"
                    : $"Followed '{lastCommand}' {cwdFreq} times here, {totalFreq} total";
            }
            else
            {
                // Global-only: score based on overall frequency
                score = Math.Min(1.0, totalFreq / 20.0);
                reason = $"Followed '{lastCommand}' {totalFreq} times";
            }

            results.Add(new SequenceSuggestion
            {
                Command = command,
                Score = score,
                Reason = reason,
            });
        }

        // Sort by score (descending), then by total frequency (descending)
        results = results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Reason.Contains("total") ? int.Parse(r.Reason.Split(" ").Last()) : 0)
            .Take(10)
            .ToList();

        return Task.FromResult<IReadOnlyList<SequenceSuggestion>>(results);
    }

    // Helper for test access
    internal void Clear()
    {
        _entries.Clear();
    }

    internal int Count => _entries.Count;
}
