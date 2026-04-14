# IHistoryStore Interface Specification

This document specifies the `IHistoryStore` interface design for the ps-bash interactive shell, including all record types and async design rationale.

**Status:** DESIGNED — Interface specification complete. Implementation pending.

---

## 1. Overview

The `IHistoryStore` interface is the abstraction layer for command history storage in ps-bash. It enables:

- **Pluggable history backends** — SQLite, file-based, or remote stores like Atuin
- **Rich metadata** — Commands are stored with context (CWD, exit code, duration, session)
- **Async I/O** — Non-blocking database queries for responsive shell interaction
- **Sequence awareness** — Command pair tracking for intelligent suggestions

---

## 2. Interface Definition

```csharp
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
```

---

## 3. Record Types

### 3.1 HistoryEntry

Represents a single command execution with full context metadata.

```csharp
namespace PsBash.Shell;

/// <summary>
/// A single command execution record with full metadata.
/// </summary>
public sealed record HistoryEntry
{
    /// <summary>
    /// The command line as typed by the user (before transpilation).
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// The working directory where the command was executed.
    /// Used for CWD-aware history filtering and ranking.
    /// </summary>
    public required string Cwd { get; init; }

    /// <summary>
    /// The exit code from command execution.
    /// Null if the command is still running or exit code is unknown.
    /// 0 = success, non-zero = failure.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// UTC timestamp when the command was executed.
    /// Used for recency ranking and history navigation.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Command execution duration in milliseconds.
    /// Null if duration is unknown or command is still running.
    /// Used for performance analysis and long-command filtering.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Unique identifier for the shell session.
    /// Used to group commands by session and track session-level patterns.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional database row ID (for stores that use auto-incrementing IDs).
    /// Null for in-memory or remote stores without integer IDs.
    /// </summary>
    public long? Id { get; init; }
}
```

### 3.2 HistoryQuery

Encapsulates search criteria for history queries.

```csharp
namespace PsBash.Shell;

/// <summary>
/// Query parameters for searching history entries.
/// All properties are optional; unspecified filters are not applied.
/// </summary>
public sealed record HistoryQuery
{
    /// <summary>
    /// Prefix text filter for commands.
    /// When set, only commands starting with this text are returned (case-sensitive).
    /// Null or empty = no prefix filtering.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Working directory filter.
    /// When set, only commands executed in this directory are returned.
    /// Null = search all directories.
    /// </summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// Session ID filter.
    /// When set, only commands from this session are returned.
    /// Null = search all sessions.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Maximum number of entries to return.
    /// Prevents unbounded queries. Default: 100.
    /// </summary>
    public int Limit { get; init; } = 100;

    /// <summary>
    /// When true, returns results in ascending order (oldest first).
    /// When false (default), returns results in descending order (newest first).
    /// </summary>
    public bool Reverse { get; init; }

    /// <summary>
    /// Exit code filter.
    /// When set, only commands with this exit code are returned.
    /// Null = no exit code filtering.
    /// Common usage: 0 for successful commands, non-zero for failures.
    /// </summary>
    public int? ExitCode { get; init; }
}
```

### 3.3 SequenceSuggestion

Represents a suggested command based on sequence patterns.

```csharp
namespace PsBash.Shell;

/// <summary>
/// A command suggestion based on sequence pattern analysis.
/// Produced by analyzing command pair frequencies in history.
/// </summary>
public sealed record SequenceSuggestion
{
    /// <summary>
    /// The suggested command text.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Relevance score (0.0 to 1.0).
    /// Higher scores indicate stronger sequence correlation.
    /// Scoring factors:
    /// - Frequency: How often this command follows the previous command
    /// - Recency: How recently this sequence was used
    /// - CWD match: Whether the sequence occurred in the current directory
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Human-readable explanation for why this was suggested.
    /// Examples:
    /// - "Followed 'git commit' 12 times in this directory"
    /// - "Common sequence after 'docker build'"
    /// </summary>
    public required string Reason { get; init; }
}
```

---

## 4. Async Design Rationale

### 4.1 Why Async I/O?

The `IHistoryStore` interface uses async methods (`Task`-returning) for several reasons:

#### SQLite Disk I/O

- **Database operations are blocking** — SQLite reads/writes can stall on disk I/O
- **History queries on every keystroke** — Autosuggestions query history on each keypress
- **Prevent UI lag** — Async calls keep the shell responsive during database operations

Without async, a slow disk (network mount, spinning HDD) would cause noticeable lag when:
- Pressing Up/Down arrows (history navigation)
- Typing with autosuggestions enabled
- Recording commands after execution

#### Future Network Stores

The async design enables remote history backends without interface changes:

```csharp
// Example: Atuin sync server backend
public sealed class AtuinRemoteHistoryStore : IHistoryStore
{
    private readonly HttpClient _http = new();

    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
    {
        // Network call to Atuin server
        var response = await _http.PostAsync("https://api.atuin.dev/search", ...);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<HistoryEntry>>();
    }

    public async Task RecordAsync(HistoryEntry entry)
    {
        // Upload to Atuin server
        await _http.PostAsJsonAsync("https://api.atuin.dev/record", entry);
    }
}
```

With a sync interface, network stores would require thread pool threads or blocking the UI thread.

#### Composability

Async methods compose naturally:
- Parallel queries to multiple history stores
- Timeout support with `CancellationToken`
- Cancellation when the user presses a key mid-query

### 4.2 Error Handling

Implementations should handle errors gracefully:

```csharp
public async Task RecordAsync(HistoryEntry entry)
{
    try
    {
        await _connection.ExecuteAsync(entry);
    }
    catch (SqliteException ex)
    {
        // Log to stderr, don't throw — shell should remain functional
        Console.Error.WriteLine($"ps-bash: history write failed: {ex.Message}");
    }
}

public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
{
    try
    {
        return await QueryDatabaseAsync(query);
    }
    catch
    {
        // Return empty list on failure — fail silently for queries
        return Array.Empty<HistoryEntry>();
    }
}
```

A broken history store should not crash the shell.

---

## 5. Implementation Examples

### 5.1 SQLite HistoryStore

```csharp
using Microsoft.Data.Sqlite;

namespace PsBash.Shell;

public sealed class SqliteHistoryStore : IHistoryStore
{
    private readonly string _connectionString;
    private readonly int _maxEntries;

    public SqliteHistoryStore(string dbPath, int maxEntries = 100000)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        _maxEntries = maxEntries;

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var createTable = connection.CreateCommand();
        createTable.CommandText = @"
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                command TEXT NOT NULL,
                cwd TEXT NOT NULL,
                exit_code INTEGER,
                timestamp INTEGER NOT NULL,
                duration_ms INTEGER,
                session_id TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_history_timestamp
                ON history(timestamp DESC);

            CREATE INDEX IF NOT EXISTS idx_history_cwd_timestamp
                ON history(cwd, timestamp DESC);

            CREATE INDEX IF NOT EXISTS idx_history_session
                ON history(session_id, timestamp DESC);

            CREATE TABLE IF NOT EXISTS sequences (
                prev_command TEXT NOT NULL,
                next_command TEXT NOT NULL,
                cwd TEXT NOT NULL,
                count INTEGER NOT NULL,
                PRIMARY KEY (prev_command, next_command, cwd)
            );

            CREATE INDEX IF NOT EXISTS idx_sequences_prev
                ON sequences(prev_command, count DESC);
        ";
        createTable.ExecuteNonQuery();
    }

    public async Task RecordAsync(HistoryEntry entry)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Insert history entry
        var insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO history (command, cwd, exit_code, timestamp, duration_ms, session_id)
            VALUES (@command, @cwd, @exit_code, @timestamp, @duration_ms, @session_id);
        ";
        insert.Parameters.AddWithValue("@command", entry.Command);
        insert.Parameters.AddWithValue("@cwd", entry.Cwd);
        insert.Parameters.AddWithValue("@exit_code", entry.ExitCode ?? DBNull.Value);
        insert.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("@duration_ms", entry.DurationMs ?? DBNull.Value);
        insert.Parameters.AddWithValue("@session_id", entry.SessionId);
        await insert.ExecuteNonQueryAsync();

        // Trim to max entries
        if (_maxEntries > 0)
        {
            var trim = connection.CreateCommand();
            trim.CommandText = @"
                DELETE FROM history
                WHERE id IN (
                    SELECT id FROM history
                    ORDER BY timestamp DESC
                    LIMIT -1 OFFSET @max
                );
            ";
            trim.Parameters.AddWithValue("@max", _maxEntries);
            await trim.ExecuteNonQueryAsync();
        }

        // Update sequence data (if we have a previous command in the session)
        // This would be tracked separately by the shell
    }

    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        var sql = new System.Text.StringBuilder();
        sql.Append("SELECT id, command, cwd, exit_code, timestamp, duration_ms, session_id ");
        sql.Append("FROM history ");
        sql.Append("WHERE 1=1 ");

        if (!string.IsNullOrEmpty(query.Filter))
        {
            sql.Append("AND command LIKE @filter || '%' ");
            cmd.Parameters.AddWithValue("@filter", query.Filter);
        }

        if (!string.IsNullOrEmpty(query.Cwd))
        {
            sql.Append("AND cwd = @cwd ");
            cmd.Parameters.AddWithValue("@cwd", query.Cwd);
        }

        if (!string.IsNullOrEmpty(query.SessionId))
        {
            sql.Append("AND session_id = @session_id ");
            cmd.Parameters.AddWithValue("@session_id", query.SessionId);
        }

        if (query.ExitCode.HasValue)
        {
            sql.Append("AND exit_code = @exit_code ");
            cmd.Parameters.AddWithValue("@exit_code", query.ExitCode.Value);
        }

        sql.Append(query.Reverse ? "ORDER BY timestamp ASC " : "ORDER BY timestamp DESC ");
        sql.Append("LIMIT @limit");
        cmd.Parameters.AddWithValue("@limit", query.Limit);

        cmd.CommandText = sql.ToString();

        var results = new List<HistoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new HistoryEntry
            {
                Id = reader.GetInt64(0),
                Command = reader.GetString(1),
                Cwd = reader.GetString(2),
                ExitCode = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).DateTime,
                DurationMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                SessionId = reader.GetString(6),
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<SequenceSuggestion>> GetSequenceSuggestionsAsync(
        string? lastCommand,
        string cwd)
    {
        if (string.IsNullOrEmpty(lastCommand))
            return Array.Empty<SequenceSuggestion>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT next_command, sum(count) as total_count
            FROM sequences
            WHERE prev_command = @prev
            GROUP BY next_command
            ORDER BY total_count DESC
            LIMIT 10;
        ";
        cmd.Parameters.AddWithValue("@prev", lastCommand);

        var results = new List<SequenceSuggestion>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var nextCommand = reader.GetString(0);
            var count = reader.GetInt64(1);

            results.Add(new SequenceSuggestion
            {
                Command = nextCommand,
                Score = Math.Min(1.0, count / 10.0), // Simple scoring: max 1.0 at 10 occurrences
                Reason = $"Followed '{lastCommand}' {count} times",
            });
        }

        return results;
    }
}
```

### 5.2 In-Memory HistoryStore (for testing)

```csharp
namespace PsBash.Shell;

public sealed class InMemoryHistoryStore : IHistoryStore
{
    private readonly List<HistoryEntry> _entries = new();
    private readonly Dictionary<(string Prev, string Cwd), Dictionary<string, int>> _sequences = new();

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

        // Simple in-memory sequence lookup would require tracking pairs separately
        return Task.FromResult<IReadOnlyList<SequenceSuggestion>>(Array.Empty<SequenceSuggestion>());
    }
}
```

---

## 6. Usage Example

```csharp
// In InteractiveShell or LineEditor
var historyStore = new SqliteHistoryStore(
    dbPath: Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".psbash", "history.db"),
    maxEntries: 100000);

// Record a command after execution
await historyStore.RecordAsync(new HistoryEntry
{
    Command = userInput,
    Cwd = Directory.GetCurrentDirectory(),
    ExitCode = exitCode,
    Timestamp = DateTime.UtcNow,
    DurationMs = stopwatch.ElapsedMilliseconds,
    SessionId = _sessionId,
});

// Search for prefix matches (autosuggestions)
var suggestions = await historyStore.SearchAsync(new HistoryQuery
{
    Filter = currentInput,
    Cwd = Directory.GetCurrentDirectory(),  // Prefer CWD matches
    Limit = 1,
});

// Get sequence-based suggestions (post-command)
var nextSuggestions = await historyStore.GetSequenceSuggestionsAsync(
    lastCommand: lastExecutedCommand,
    cwd: Directory.GetCurrentDirectory());
```

---

## 7. Acceptance Criteria

Implementation is complete when:

- [ ] `IHistoryStore` interface exists with all three methods
- [ ] `HistoryEntry` record type is defined with all properties
- [ ] `HistoryQuery` record type is defined with all properties and defaults
- [ ] `SequenceSuggestion` record type is defined with all properties
- [ ] `SqliteHistoryStore` implementation exists
- [ ] Async methods do not block the UI thread
- [ ] Database schema includes all required columns and indexes
- [ ] Error handling is robust (no crashes on DB failures)
- [ ] Unit tests cover search, record, and sequence operations
- [ ] Performance is acceptable (< 10ms per query over 100k entries)

---

## 8. References

- Related specs:
  - `autosuggestions.md` — Fish-style suggestions using `IHistoryStore`
  - `shell-implementation-phases.md` — P2 (Built-in History) implementation plan
  - `plugin-architecture.md` — Plugin system for custom history stores
- Existing interface sketch in `plugin-architecture.md` (Section 3.1)
