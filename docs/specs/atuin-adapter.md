# Atuin Plugin Adapter Design

This document specifies the design of the Atuin history store adapter for ps-bash, including schema mapping, read-only semantics, merge strategy, and configuration.

**Status:** DESIGNED — Specification complete. Implementation pending.

---

## 1. Overview

Atuin is a modern shell history manager that stores commands in a SQLite database at `~/.local/share/atuin/history.db`. The ps-bash Atuin adapter:

1. **Reads from Atuin's SQLite database** via `IHistoryStore` interface
2. **Maps Atuin columns → HistoryEntry records**
3. **Provides sequence suggestions** via adjacent-row pair queries
4. **Coexists with built-in history** — both stores active, results merged
5. **No write support** — Atuin manages its own writes via shell hooks

---

## 2. Atuin SQLite Schema

### 2.1 Table Structure

Atuin's `history` table (as of Atuin v18+):

```sql
CREATE TABLE history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    command TEXT NOT NULL,
    cwd TEXT NOT NULL,
    exit_code INTEGER NOT NULL,
    duration INTEGER NOT NULL,
    timestamp TEXT NOT NULL,
    session TEXT NOT NULL,
    hostname TEXT,
    deleted_at TEXT,
    unique_uuid TEXT
);
```

### 2.2 Column Descriptions

| Atuin Column | Type | Description |
|--------------|------|-------------|
| `id` | INTEGER | Primary key, auto-incrementing row identifier |
| `command` | TEXT | The command line as typed by the user |
| `cwd` | TEXT | Working directory where the command was executed |
| `exit_code` | INTEGER | Exit code from command execution |
| `duration` | INTEGER | Command execution duration in **nanoseconds** |
| `timestamp` | TEXT | ISO 8601 timestamp (UTC) or Unix epoch integer |
| `session` | TEXT | Unique identifier for the shell session (UUID) |
| `hostname` | TEXT | Hostname where the command was executed |
| `deleted_at` | TEXT | Timestamp if soft-deleted, NULL otherwise |
| `unique_uuid` | TEXT | Globally unique identifier for sync |

### 2.3 Indexes

Atuin creates these indexes:

```sql
CREATE INDEX idx_history_timestamp ON history(timestamp DESC);
CREATE INDEX idx_history_command ON history(command);
CREATE INDEX idx_history_cwd ON history(cwd);
CREATE INDEX idx_history_session ON history(session);
```

---

## 3. Column Mapping: Atuin → HistoryEntry

### 3.1 Direct Mapping

| HistoryEntry Property | Atuin Column | Transformation |
|-----------------------|--------------|----------------|
| `Id` | `id` | Direct copy (INTEGER) |
| `Command` | `command` | Direct copy (TEXT) |
| `Cwd` | `cwd` | Direct copy (TEXT) |
| `ExitCode` | `exit_code` | Direct copy (INTEGER) |
| `Timestamp` | `timestamp` | Parse TEXT/INTEGER to DateTime |
| `DurationMs` | `duration` | Convert nanoseconds → milliseconds (÷ 1,000,000) |
| `SessionId` | `session` | Direct copy (TEXT) |

### 3.2 Timestamp Parsing

Atuin stores timestamps in two formats (depending on version):

**Format 1: ISO 8601 string** (newer versions)
```
2025-04-14T12:34:56.789Z
```

**Format 2: Unix epoch integer** (older versions)
```
1744625296789
```

The adapter must detect and handle both:

```csharp
private static DateTime ParseAtuinTimestamp(object value)
{
    if (value is long epoch)
        return DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime;

    if (value is string iso && DateTime.TryParse(iso, out var dt))
        return dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;

    return DateTime.UtcNow; // Fallback
}
```

### 3.3 Duration Conversion

Atuin stores duration in **nanoseconds**. Convert to milliseconds:

```csharp
DurationMs = reader.GetInt64(reader.GetOrdinal("duration")) / 1_000_000
```

### 3.4 Filtering Deleted Entries

Atuin supports soft-deletion via `deleted_at`. Exclude these:

```sql
WHERE deleted_at IS NULL
```

---

## 4. Read-Only Adapter Design

### 4.1 No Write Support

The Atuin adapter is **read-only**. `RecordAsync` throws `NotImplementedException`:

```csharp
public Task RecordAsync(HistoryEntry entry)
{
    // Atuin manages its own database via the atuin binary and shell hooks.
    // ps-bash must NOT write to the database directly to avoid corruption.
    throw new NotImplementedException(
        "Atuin adapter is read-only. Atuin manages its own history via shell hooks.");
}
```

### 4.2 Read-Only Connection

All queries use `SqliteOpenMode.ReadOnly`:

```csharp
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = _dbPath,
    Mode = SqliteOpenMode.ReadOnly,
}.ToString();
```

This prevents:
- Accidental writes
- Database lock conflicts (multiple readers can share the file)
- WAL file creation

### 4.3 Connection Pooling

Create a new connection per query (no persistent connection):

```csharp
public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync();
    // ... query
}
```

This avoids holding locks and handles Atuin's shell hook writes gracefully.

---

## 5. SearchAsync Implementation

### 5.1 Query Generation

Build SQL based on `HistoryQuery` filters:

```csharp
public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync();

    var cmd = connection.CreateCommand();
    var sql = new StringBuilder();

    sql.Append("SELECT id, command, cwd, exit_code, timestamp, duration, session ");
    sql.Append("FROM history ");
    sql.Append("WHERE deleted_at IS NULL "); // Exclude soft-deleted

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
        sql.Append("AND session = @session ");
        cmd.Parameters.AddWithValue("@session", query.SessionId);
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
            ExitCode = reader.GetInt32(3),
            Timestamp = ParseAtuinTimestamp(reader.GetValue(4)),
            DurationMs = reader.GetInt64(5) / 1_000_000, // ns → ms
            SessionId = reader.GetString(6),
        });
    }

    return results;
}
```

---

## 6. GetSequenceSuggestionsAsync Implementation

### 6.1 Adjacent-Row Query

Sequence suggestions are based on **command pairs** (what command typically follows another). Query adjacent rows in Atuin's history:

```sql
SELECT
    h2.command AS next_command,
    COUNT(*) AS frequency
FROM history h1
JOIN history h2 ON h1.id + 1 = h2.id
WHERE
    h1.command = @prev_command
    AND h1.deleted_at IS NULL
    AND h2.deleted_at IS NULL
    AND h2.timestamp > datetime('now', '-30 days')
GROUP BY h2.command
ORDER BY frequency DESC
LIMIT 10;
```

### 6.2 CWD-Boosted Scoring

Boost scores for sequences that occurred in the current directory:

```csharp
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
        SELECT
            h2.command AS next_command,
            COUNT(*) AS frequency,
            SUM(CASE WHEN h2.cwd = @cwd THEN 1 ELSE 0 END) AS cwd_count
        FROM history h1
        JOIN history h2 ON h1.id + 1 = h2.id
        WHERE
            h1.command = @prev_command
            AND h1.deleted_at IS NULL
            AND h2.deleted_at IS NULL
            AND h2.timestamp > datetime('now', '-30 days')
        GROUP BY h2.command
        ORDER BY frequency DESC
        LIMIT 10;
    ";
    cmd.Parameters.AddWithValue("@prev_command", lastCommand);
    cmd.Parameters.AddWithValue("@cwd", cwd);

    var results = new List<SequenceSuggestion>();
    await using var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        var nextCommand = reader.GetString(0);
        var frequency = reader.GetInt64(1);
        var cwdCount = reader.GetInt64(2);

        // Score: 0.0 to 1.0, boosted by CWD matches
        var baseScore = Math.Min(1.0, frequency / 10.0);
        var cwdBoost = cwdCount > 0 ? 0.2 : 0.0;
        var score = Math.Min(1.0, baseScore + cwdBoost);

        results.Add(new SequenceSuggestion
        {
            Command = nextCommand,
            Score = score,
            Reason = cwdCount > 0
                ? $"Followed '{lastCommand}' {frequency} times ({cwdCount} in this directory)"
                : $"Followed '{lastCommand}' {frequency} times",
        });
    }

    return results;
}
```

---

## 7. Merge Strategy: Built-in + Atuin

### 7.1 Coexistence Model

Both history stores can be active simultaneously. The shell queries both and merges results.

### 7.2 Query Flow

```
User input: "git"
    ↓
Shell queries both stores in parallel:
    ├── Built-in SqliteHistoryStore.SearchAsync("git")
    └── AtuinHistoryStore.SearchAsync("git")
    ↓
Results merged (union, deduplicated by command text)
    ↓
Sorted by timestamp (newest first)
    ↓
Returned to user
```

### 7.3 Merge Algorithm

```csharp
public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
{
    // Query both stores in parallel
    var tasks = _historyStores.Select(store => store.SearchAsync(query)).ToList();
    var allResults = await Task.WhenAll(tasks);

    // Flatten and merge
    var merged = allResults
        .SelectMany(entries => entries)
        .GroupBy(e => e.Command) // Deduplicate by command text
        .Select(g => g.First()) // Take first occurrence
        .OrderByDescending(e => e.Timestamp) // Sort by timestamp
        .Take(query.Limit)
        .ToList();

    return merged;
}
```

### 7.4 Precedence

When the same command exists in both stores:
- **Deduplicate by command text** — keep the entry with the newest timestamp
- Built-in history is **not** given priority — timestamps decide

This ensures users get the most recent version of each command, regardless of source.

---

## 8. Auto-Detection of Atuin DB Path

### 8.1 Default Locations

Atuin stores its database at platform-specific locations:

| Platform | Path |
|----------|------|
| Linux | `~/.local/share/atuin/history.db` |
| macOS | `~/.local/share/atuin/history.db` |
| Windows | `%APPDATA%\atuin\history.db` |

### 8.2 Detection Logic

```csharp
private static string? DetectAtuinDbPath()
{
    var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    if (!string.IsNullOrEmpty(dataHome))
    {
        var path = Path.Combine(dataHome, "atuin", "history.db");
        if (File.Exists(path)) return path;
    }

    // Fallback to default locations
    var basePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var defaultPath = Path.Combine(basePath, ".local", "share", "atuin", "history.db");

    if (File.Exists(defaultPath)) return defaultPath;

    // Windows fallback
    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var windowsPath = Path.Combine(appData, "atuin", "history.db");
        if (File.Exists(windowsPath)) return windowsPath;
    }

    return null; // Not found
}
```

### 8.3 Fallback Behavior

If Atuin DB is not detected:
- Constructor logs a warning to stderr
- Adapter remains functional but returns empty results
- Shell continues to work with built-in history only

```csharp
public AtuinHistoryStore()
{
    _dbPath = DetectAtuinDbPath();

    if (_dbPath is null || !File.Exists(_dbPath))
    {
        Console.Error.WriteLine("ps-bash: Atuin database not found, Atuin adapter inactive");
        _available = false;
        return;
    }

    _available = true;
}
```

---

## 9. Configuration in config.toml

### 9.1 Config File Location

`~/.psbash/config.toml`

### 9.2 Atuin Configuration

```toml
[history.atuin]
# Path to Atuin's SQLite database (auto-detected if not set)
db_path = "/home/user/.local/share/atuin/history.db"

# Enable Atuin adapter (default: true if db detected)
enabled = true

# Priority for merge (lower = queried first)
# Built-in history has priority 0 by default
priority = 10
```

### 9.3 Configuration Loading

```csharp
public sealed class AtuinHistoryStore : IHistoryStore
{
    public AtuinHistoryStore(string? dbPath = null)
    {
        _dbPath = dbPath ?? DetectAtuinDbPath();

        if (_dbPath is null || !File.Exists(_dbPath))
        {
            Console.Error.WriteLine("ps-bash: Atuin database not found");
            _available = false;
        }
    }
}
```

The `ShellConfig.HistoryStores` list order determines query precedence:

```csharp
// Built-in first, then Atuin (configurable)
config.HistoryStores.Add(new SqliteHistoryStore());
config.HistoryStores.Add(new AtuinHistoryStore());
```

---

## 10. Error Handling

### 10.1 Database Locked

Atuin's shell hooks may write to the database while ps-bash is reading. Handle lock errors gracefully:

```csharp
public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
{
    if (!_available) return Array.Empty<HistoryEntry>();

    try
    {
        return await QueryDatabaseAsync(query);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
    {
        // Retry once after a short delay
        await Task.Delay(50);
        return await QueryDatabaseAsync(query);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY again
    {
        // Give up, log warning, return empty
        Console.Error.WriteLine("ps-bash: Atuin database busy, skipping query");
        return Array.Empty<HistoryEntry>();
    }
}
```

### 10.2 Database Corrupted

```csharp
public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
{
    try
    {
        return await QueryDatabaseAsync(query);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQL_CORRUPT
    {
        Console.Error.WriteLine($"ps-bash: Atuin database corrupted, disabling adapter");
        _available = false;
        return Array.Empty<HistoryEntry>();
    }
}
```

### 10.3 Missing Columns

Older Atuin versions may have different schemas. Handle missing columns:

```csharp
private HistoryEntry MapFromReader(SqliteDataReader reader)
{
    return new HistoryEntry
    {
        Id = reader.GetInt64(0),
        Command = reader.GetString(1),
        Cwd = reader.SafeGetString(2) ?? "", // Handle missing cwd
        ExitCode = reader.SafeGetInt(3) ?? 0, // Handle missing exit_code
        Timestamp = ParseAtuinTimestamp(reader.GetValue(4)),
        DurationMs = reader.SafeGetInt64(5) is long ns ? ns / 1_000_000 : null,
        SessionId = reader.SafeGetString(6) ?? "",
    };
}
```

---

## 11. Implementation Example

### 11.1 Full Adapter Implementation

```csharp
using Microsoft.Data.Sqlite;

namespace PsBash.Shell;

public sealed class AtuinHistoryStore : IHistoryStore
{
    private readonly string? _dbPath;
    private readonly string _connectionString;
    private bool _available = true;

    public AtuinHistoryStore(string? dbPath = null)
    {
        _dbPath = dbPath ?? DetectAtuinDbPath();

        if (_dbPath is null || !File.Exists(_dbPath))
        {
            Console.Error.WriteLine("ps-bash: Atuin database not found, adapter inactive");
            _available = false;
            _connectionString = string.Empty;
            return;
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    public Task RecordAsync(HistoryEntry entry)
    {
        throw new NotImplementedException(
            "Atuin adapter is read-only. Atuin manages its own history via shell hooks.");
    }

    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
    {
        if (!_available) return Array.Empty<HistoryEntry>();

        try
        {
            return await QueryDatabaseAsync(query);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
        {
            await Task.Delay(50);
            try { return await QueryDatabaseAsync(query); }
            catch { return Array.Empty<HistoryEntry>(); }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQL_CORRUPT
        {
            Console.Error.WriteLine("ps-bash: Atuin database corrupted, disabling adapter");
            _available = false;
            return Array.Empty<HistoryEntry>();
        }
        catch
        {
            return Array.Empty<HistoryEntry>();
        }
    }

    public async Task<IReadOnlyList<SequenceSuggestion>> GetSequenceSuggestionsAsync(
        string? lastCommand,
        string cwd)
    {
        if (!_available || string.IsNullOrEmpty(lastCommand))
            return Array.Empty<SequenceSuggestion>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                h2.command AS next_command,
                COUNT(*) AS frequency,
                SUM(CASE WHEN h2.cwd = @cwd THEN 1 ELSE 0 END) AS cwd_count
            FROM history h1
            JOIN history h2 ON h1.id + 1 = h2.id
            WHERE
                h1.command = @prev_command
                AND h1.deleted_at IS NULL
                AND h2.deleted_at IS NULL
                AND h2.timestamp > datetime('now', '-30 days')
            GROUP BY h2.command
            ORDER BY frequency DESC
            LIMIT 10;
        ";
        cmd.Parameters.AddWithValue("@prev_command", lastCommand);
        cmd.Parameters.AddWithValue("@cwd", cwd);

        var results = new List<SequenceSuggestion>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var nextCommand = reader.GetString(0);
            var frequency = reader.GetInt64(1);
            var cwdCount = reader.GetInt64(2);

            var baseScore = Math.Min(1.0, frequency / 10.0);
            var cwdBoost = cwdCount > 0 ? 0.2 : 0.0;
            var score = Math.Min(1.0, baseScore + cwdBoost);

            results.Add(new SequenceSuggestion
            {
                Command = nextCommand,
                Score = score,
                Reason = cwdCount > 0
                    ? $"Followed '{lastCommand}' {frequency} times ({cwdCount} in this directory)"
                    : $"Followed '{lastCommand}' {frequency} times",
            });
        }

        return results;
    }

    private static string? DetectAtuinDbPath()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(dataHome))
        {
            var path = Path.Combine(dataHome, "atuin", "history.db");
            if (File.Exists(path)) return path;
        }

        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(basePath, ".local", "share", "atuin", "history.db");
        if (File.Exists(defaultPath)) return defaultPath;

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var windowsPath = Path.Combine(appData, "atuin", "history.db");
            if (File.Exists(windowsPath)) return windowsPath;
        }

        return null;
    }

    private static DateTime ParseAtuinTimestamp(object value)
    {
        if (value is long epoch)
            return DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime;
        if (value is string iso && DateTime.TryParse(iso, out var dt))
            return dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
        return DateTime.UtcNow;
    }
}
```

---

## 12. Acceptance Criteria

Implementation is complete when:

- [ ] `AtuinHistoryStore` implements `IHistoryStore`
- [ ] `SearchAsync` queries Atuin's `history` table with all filters (prefix, CWD, session, exit code)
- [ ] `RecordAsync` throws `NotImplementedException` (read-only)
- [ ] Column mapping handles all `HistoryEntry` properties (duration converted ns→ms, timestamp parsed)
- [ ] Deleted entries (`deleted_at IS NOT NULL`) are excluded
- [ ] `GetSequenceSuggestionsAsync` queries adjacent-row pairs with CWD-boosted scoring
- [ ] Auto-detection finds Atuin DB at `~/.local/share/atuin/history.db` (and platform variants)
- [ ] Config file `atuin_db_path` overrides auto-detection
- [ ] Database locked errors are handled with retry + graceful fallback
- [ ] Database corruption disables adapter with log message
- [ ] Merge with built-in history deduplicates by command text, keeps newest timestamp
- [ ] Connection uses `SqliteOpenMode.ReadOnly`

---

## 13. References

- Related specs:
  - `history-store-interface.md` — `IHistoryStore` interface and `HistoryEntry` type
  - `plugin-architecture.md` — Plugin loading and `ShellConfig` (Section 6.1 has basic Atuin example)
  - `sqlite-history-schema.md` — Built-in SQLite schema (for comparison)
- Atuin repository: https://github.com/atuinsh/atuin
- Atuin schema reference: https://github.com/atuinsh/atuin/blob/main/history/schema.sql
