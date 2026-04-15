# SQLite History Store Schema

This document specifies the SQLite database schema for the built-in command history store used by the ps-bash interactive shell.

**Status:** DESIGNED — Schema specified. Implementation pending.

---

## 1. Database Location

The SQLite database is stored at:

```
~/.psbash/history.db
```

On Windows, `~` expands to `%USERPROFILE%`. The `.psbash` directory is created automatically on first run.

---

## 2. Schema Definition

### 2.1 history Table

The main table storing command execution records.

```sql
CREATE TABLE history (
    id INTEGER PRIMARY KEY,
    command TEXT NOT NULL,
    cwd TEXT NOT NULL,
    exit_code INTEGER,
    timestamp TEXT NOT NULL,
    duration_ms INTEGER,
    session TEXT
);
```

#### Column Descriptions

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER | Primary key, auto-incrementing row identifier |
| `command` | TEXT | The command line as typed by the user (before transpilation) |
| `cwd` | TEXT | Working directory where the command was executed |
| `exit_code` | INTEGER | Exit code from command execution (0 = success, non-zero = failure). NULL if unknown. |
| `timestamp` | TEXT | ISO 8601 timestamp (UTC) when the command was executed |
| `duration_ms` | INTEGER | Command execution duration in milliseconds. NULL if unknown. |
| `session` | TEXT | Unique identifier for the shell session (UUID or similar) |

#### timestamp Format

The `timestamp` column stores ISO 8601 formatted strings in UTC:

```
2025-04-14T12:34:56.789Z
```

Using TEXT (instead of INTEGER Unix timestamps) enables:
- Direct human readability with `SELECT * FROM history`
- Native SQLite date/time functions
- Easy debugging with standard SQLite tools

### 2.2 Indexes

Three indexes optimize common query patterns:

```sql
CREATE INDEX idx_history_cwd ON history(cwd);
CREATE INDEX idx_history_ts ON history(timestamp);
CREATE INDEX idx_history_cmd ON history(command);
```

#### Index Usage

| Index | Purpose | Example Query |
|-------|---------|---------------|
| `idx_history_cwd` | CWD-filtered history (Up arrow in specific directories) | `SELECT * FROM history WHERE cwd = '/home/user/project' ORDER BY timestamp DESC LIMIT 10` |
| `idx_history_ts` | Recency-based queries (recent history, time-based filtering) | `SELECT * FROM history WHERE timestamp > datetime('now', '-7 days')` |
| `idx_history_cmd` | Prefix search for autosuggestions | `SELECT * FROM history WHERE command LIKE 'git %'` |

---

## 3. command_sequences View

A materialized view for efficient command pair frequency analysis, used for sequence-aware suggestions (P6 feature).

```sql
CREATE VIEW command_sequences AS
SELECT
    h1.command AS prev,
    h2.command AS next,
    h2.cwd,
    COUNT(*) AS freq
FROM history h1
JOIN history h2 ON h1.id + 1 = h2.id
WHERE h2.timestamp > datetime('now', '-30 days')
GROUP BY prev, next, h2.cwd
ORDER BY freq DESC;
```

### View Semantics

- **prev**: The command that was executed previously
- **next**: The command that followed (current row)
- **cwd**: Working directory where `next` was executed
- **freq**: Number of times this sequence occurred

### Time Window

The view filters to the last 30 days:

```sql
WHERE h2.timestamp > datetime('now', '-30 days')
```

This keeps suggestions relevant to recent workflows. Old patterns naturally fade out.

### Adjacent ID Join

```sql
JOIN history h2 ON h1.id + 1 = h2.id
```

Pairs are identified by consecutive row IDs. This assumes sequential IDs within a session. For cross-session sequences, session filtering would need to be added.

---

## 4. Migration Strategy

### 4.1 Auto-Creation on First Run

The database and schema are created automatically when `SqliteHistoryStore` is instantiated:

```csharp
public SqliteHistoryStore(string dbPath)
{
    _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    InitializeSchema();
}
```

`InitializeSchema()` uses `CREATE TABLE IF NOT EXISTS` and `CREATE INDEX IF NOT EXISTS` to ensure idempotency:

```sql
CREATE TABLE IF NOT EXISTS history (...);
CREATE INDEX IF NOT EXISTS idx_history_cwd ON history(cwd);
CREATE INDEX IF NOT EXISTS idx_history_ts ON history(timestamp);
CREATE INDEX IF NOT EXISTS idx_history_cmd ON history(command);
CREATE VIEW IF NOT EXISTS command_sequences AS ...;
```

### 4.2 Legacy Text File Migration

For users upgrading from the plain text history file (`~/.psbash/history`):

1. Check if SQLite DB exists
2. If not, check for legacy text file
3. If found, import each line as a history entry
4. Preserve order (lines = sequential commands)
5. Set timestamp to file modification time (or approximate)

```csharp
private void MigrateFromLegacyFile(string dbPath, string legacyPath)
{
    if (File.Exists(dbPath)) return;
    if (!File.Exists(legacyPath)) return;

    var lines = File.ReadAllLines(legacyPath);
    var fileTime = File.GetLastWriteTimeUtc(legacyPath);

    using var connection = new SqliteConnection(_connectionString);
    connection.Open();

    var insert = connection.CreateCommand();
    insert.CommandText = @"
        INSERT INTO history (command, cwd, timestamp, session)
        VALUES (@command, @cwd, @timestamp, @session);
    ";

    // Approximate timestamps stepping back from file time
    var timestamp = fileTime;
    var session = Guid.NewGuid().ToString();

    foreach (var line in lines)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;

        insert.Parameters.Clear();
        insert.Parameters.AddWithValue("@command", line);
        insert.Parameters.AddWithValue("@cwd", Environment.CurrentDirectory);
        insert.Parameters.AddWithValue("@timestamp", timestamp.ToString("o"));
        insert.Parameters.AddWithValue("@session", session);
        insert.ExecuteNonQuery();

        // Step back 1 second per command to maintain order
        timestamp = timestamp.AddSeconds(-1);
    }
}
```

---

## 5. Retention Policy

### 5.1 Default Limit

The default retention policy keeps 100,000 history entries:

```csharp
private const int DefaultMaxEntries = 100_000;
```

This provides:
- ~1-2 years of history for typical developers (100-200 commands/day)
- Fast queries even with large databases (indexes scale well)
- Manageable disk usage (~10-20 MB for 100k entries)

### 5.2 Configurable Limit

The max entries limit is configurable via constructor parameter:

```csharp
public SqliteHistoryStore(string dbPath, int maxEntries = 100000)
{
    _maxEntries = maxEntries;
    // ...
}
```

A future config file (`~/.psbash/config.json`) could expose this setting:

```json
{
    "history": {
        "maxEntries": 50000
    }
}
```

### 5.3 Trimming Strategy

After each insert, entries beyond the limit are trimmed:

```sql
DELETE FROM history
WHERE id IN (
    SELECT id FROM history
    ORDER BY timestamp DESC
    LIMIT -1 OFFSET @max
);
```

This query:
1. Orders history by timestamp (newest first)
2. Skips the first `@max` entries (keeping them)
3. Deletes everything beyond that (oldest entries)

The `LIMIT -1 OFFSET @max` pattern is SQLite-specific for "skip first N, return rest."

---

## 6. WAL Journaling

### 6.1 Why WAL Mode?

WAL (Write-Ahead Logging) is enabled for concurrent reads during writes:

```sql
PRAGMA journal_mode = WAL;
```

Benefits:
- **Readers don't block writers**: Shell can query history while a command is being recorded
- **Writers don't block readers**: No UI lag when saving history
- **Better concurrency**: Multiple shell sessions can read/write simultaneously
- **Crash recovery**: Automatic recovery after power loss

### 6.2 WAL File Layout

With WAL mode enabled, three files exist:

```
~/.psbash/
├── history.db       (main database)
├── history.db-wal   (write-ahead log)
└── history.db-shm   (shared memory index)
```

The `-wal` and `-shm` files are managed automatically by SQLite. They can be safely deleted (SQLite will recreate them), but deleting them may lose recent uncommitted writes.

### 6.3 Checkpointing

SQLite automatically checkpoints the WAL file back into the main database. The default behavior is sufficient for ps-bash:

- When WAL reaches ~1MB, a checkpoint runs automatically
- Checkpoints are non-blocking for readers
- No manual checkpoint management needed

---

## 7. Error Handling

### 7.1 Database Corruption

If the database is corrupted, the shell should remain functional:

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
```

### 7.2 Query Failures

Search queries return empty lists on failure:

```csharp
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

A broken history store degrades gracefully: the shell works, history features are temporarily unavailable.

### 7.3 Recovery

For corrupted databases:

1. Detect corruption on connection open
2. Log error message to stderr
3. Delete or rename corrupted file
4. Create fresh database on next run

```csharp
private void InitializeSchema()
{
    try
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // Run schema creation
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQL_CORRUPT
    {
        Console.Error.WriteLine($"ps-bash: history database corrupted, recreating...");
        File.Move(_dbPath, _dbPath + ".corrupt");
        // Retry with fresh database
    }
}
```

---

## 8. Performance Considerations

### 8.1 Query Performance

With proper indexes, queries over 100k entries should complete in < 10ms:

| Query | Index Used | Expected Time |
|-------|------------|---------------|
| Prefix search (`command LIKE 'git %'`) | `idx_history_cmd` | ~5 ms |
| CWD filter (`cwd = '/path'`) | `idx_history_cwd` | ~2 ms |
| Recency filter (`timestamp > now-30d`) | `idx_history_ts` | ~3 ms |
| Complex query (CWD + prefix + recency) | Combined | ~10 ms |

### 8.2 Write Performance

Inserting a single history entry:

- Without WAL: ~1-2 ms (disk sync)
- With WAL: ~0.5 ms (async checkpoint)

For heavy command workflows (e.g., `for i in {1..1000}; do echo $i; done`), batch inserts could optimize:

```csharp
public async Task RecordBatchAsync(IEnumerable<HistoryEntry> entries)
{
    using var transaction = await _connection.BeginTransactionAsync();
    foreach (var entry in entries)
    {
        await InsertAsync(entry, transaction);
    }
    await transaction.CommitAsync();
}
```

### 8.3 Vacuum

Over time, deleted entries leave fragmented free space. Run `VACUUM` periodically:

```sql
VACUUM;
```

Trigger vacuum when:
- Database file size exceeds 2x the actual data size
- After trimming 50%+ of history entries

---

## 9. Acceptance Criteria

Implementation is complete when:

- [ ] SQLite database created at `~/.psbash/history.db` on first run
- [ ] All three indexes are created (`idx_history_cwd`, `idx_history_ts`, `idx_history_cmd`)
- [ ] `command_sequences` view is created
- [ ] WAL journaling is enabled (`PRAGMA journal_mode = WAL`)
- [ ] Retention policy trims history to 100k entries (configurable)
- [ ] Legacy text file history is imported on upgrade
- [ ] Queries complete in < 10ms over 100k entries
- [ ] Database corruption is handled gracefully (shell remains functional)
- [ ] Multiple shell sessions can read/write concurrently

---

## 10. References

- Related specs:
  - `history-store-interface.md` — `IHistoryStore` interface and record types
  - `shell-implementation-phases.md` — P2 (Built-in History) implementation plan
  - `autosuggestions.md` — Fish-style suggestions using history store
- SQLite documentation: https://www.sqlite.org/wal.html
