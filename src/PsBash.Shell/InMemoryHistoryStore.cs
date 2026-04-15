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

        // Build sequences on-the-fly from history for this simple implementation
        var sequences = new Dictionary<string, (int Count, DateTime LastSeen)>();

        for (int i = 0; i < _entries.Count - 1; i++)
        {
            if (_entries[i].Command == lastCommand && _entries[i].Cwd == cwd)
            {
                var nextCmd = _entries[i + 1].Command;
                if (sequences.TryGetValue(nextCmd, out var existing))
                {
                    sequences[nextCmd] = (existing.Count + 1, _entries[i + 1].Timestamp);
                }
                else
                {
                    sequences[nextCmd] = (1, _entries[i + 1].Timestamp);
                }
            }
        }

        var results = sequences
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.LastSeen)
            .Take(10)
            .Select(kv =>
            {
                var score = Math.Min(1.0, kv.Value.Count / 10.0);
                return new SequenceSuggestion
                {
                    Command = kv.Key,
                    Score = score,
                    Reason = $"Followed '{lastCommand}' {kv.Value.Count} times in this directory",
                };
            })
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
