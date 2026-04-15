using Microsoft.Data.Sqlite;

namespace PsBash.Shell;

/// <summary>
/// SQLite-backed history store with WAL journaling and automatic schema management.
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore, IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly int _maxEntries;
    private readonly string _legacyHistoryPath;
    private SqliteConnection? _connection;
    private readonly object _lock = new();

    private const int DefaultMaxEntries = 100_000;

    public SqliteHistoryStore(string dbPath, int maxEntries = DefaultMaxEntries)
    {
        _dbPath = dbPath;
        _maxEntries = maxEntries;
        _legacyHistoryPath = Path.Combine(
            Path.GetDirectoryName(dbPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".psbash_history");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        _connectionString = builder.ToString();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        lock (_lock)
        {
            try
            {
                EnsureConnectionOpen();

                // Create history table
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS history (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        command TEXT NOT NULL,
                        cwd TEXT NOT NULL,
                        exit_code INTEGER,
                        timestamp TEXT NOT NULL,
                        duration_ms INTEGER,
                        session TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_history_cwd ON history(cwd);
                    CREATE INDEX IF NOT EXISTS idx_history_ts ON history(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_history_cmd ON history(command);

                    CREATE VIEW IF NOT EXISTS command_sequences AS
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

                    PRAGMA journal_mode = WAL;
                    """;
                cmd.ExecuteNonQuery();

                // Migrate from legacy file if this is a new database
                MigrateFromLegacyFileIfNeeded();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQL_CORRUPT
            {
                Console.Error.WriteLine($"ps-bash: history database corrupted, recreating...");
                try
                {
                    _connection?.Close();
                    _connection?.Dispose();

                    var corruptPath = _dbPath + ".corrupt";
                    File.Move(_dbPath, corruptPath);

                    // WAL files
                    var walPath = _dbPath + "-wal";
                    var shmPath = _dbPath + "-shm";
                    if (File.Exists(walPath)) File.Move(walPath, walPath + ".corrupt");
                    if (File.Exists(shmPath)) File.Move(shmPath, shmPath + ".corrupt");

                    _connection = null;
                    EnsureConnectionOpen();

                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS history (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            command TEXT NOT NULL,
                            cwd TEXT NOT NULL,
                            exit_code INTEGER,
                            timestamp TEXT NOT NULL,
                            duration_ms INTEGER,
                            session TEXT NOT NULL
                        );

                        CREATE INDEX IF NOT EXISTS idx_history_cwd ON history(cwd);
                        CREATE INDEX IF NOT EXISTS idx_history_ts ON history(timestamp);
                        CREATE INDEX IF NOT EXISTS idx_history_cmd ON history(command);

                        PRAGMA journal_mode = WAL;
                        """;
                    cmd.ExecuteNonQuery();
                }
                catch (Exception innerEx)
                {
                    Console.Error.WriteLine($"ps-bash: failed to recreate history database: {innerEx.Message}");
                }
            }
        }
    }

    private void MigrateFromLegacyFileIfNeeded()
    {
        if (_connection is null) return;

        // Check if database is empty (new)
        using var checkCmd = _connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM history;";
        var count = Convert.ToInt32(checkCmd.ExecuteScalar());

        if (count > 0) return; // Already has data, skip migration
        if (!File.Exists(_legacyHistoryPath)) return; // No legacy file

        try
        {
            var lines = File.ReadAllLines(_legacyHistoryPath);
            if (lines.Length == 0) return;

            var fileTime = File.GetLastWriteTimeUtc(_legacyHistoryPath);
            var session = Guid.NewGuid().ToString();

            // Use a transaction for faster bulk insert
            using var transaction = _connection.BeginTransaction();

            using var insertCmd = _connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO history (command, cwd, timestamp, session)
                VALUES (@command, @cwd, @timestamp, @session);
                """;
            insertCmd.Parameters.AddWithValue("@command", (string?)null);
            insertCmd.Parameters.AddWithValue("@cwd", (string?)null);
            insertCmd.Parameters.AddWithValue("@timestamp", (string?)null);
            insertCmd.Parameters.AddWithValue("@session", session);

            // Step back from file time to maintain order
            var timestamp = fileTime;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                insertCmd.Parameters["@command"].Value = line;
                insertCmd.Parameters["@cwd"].Value = Environment.CurrentDirectory;
                insertCmd.Parameters["@timestamp"].Value = timestamp.ToString("o");

                insertCmd.ExecuteNonQuery();

                // Step back 1 second per command to maintain order
                timestamp = timestamp.AddSeconds(-1);
            }

            transaction.Commit();

            Console.Error.WriteLine($"ps-bash: migrated {lines.Count(l => !string.IsNullOrWhiteSpace(l))} entries from legacy history file");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ps-bash: failed to migrate legacy history: {ex.Message}");
        }
    }

    private void EnsureConnectionOpen()
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }
    }

    public async Task RecordAsync(HistoryEntry entry)
    {
        await Task.Run(() =>
        {
            try
            {
                lock (_lock)
                {
                    EnsureConnectionOpen();

                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO history (command, cwd, exit_code, timestamp, duration_ms, session)
                        VALUES (@command, @cwd, @exit_code, @timestamp, @duration_ms, @session);
                        """;
                    cmd.Parameters.AddWithValue("@command", entry.Command);
                    cmd.Parameters.AddWithValue("@cwd", entry.Cwd);
                    cmd.Parameters.AddWithValue("@exit_code", entry.ExitCode.HasValue ? (object)entry.ExitCode.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToString("o"));
                    cmd.Parameters.AddWithValue("@duration_ms", entry.DurationMs.HasValue ? (object)entry.DurationMs.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@session", entry.SessionId);

                    cmd.ExecuteNonQuery();

                    // Trim to max entries
                    if (_maxEntries > 0)
                    {
                        using var trimCmd = _connection.CreateCommand();
                        trimCmd.CommandText = """
                            DELETE FROM history
                            WHERE id IN (
                                SELECT id FROM history
                                ORDER BY timestamp DESC
                                LIMIT -1 OFFSET @max
                            );
                            """;
                        trimCmd.Parameters.AddWithValue("@max", _maxEntries);
                        trimCmd.ExecuteNonQuery();
                    }
                }
            }
            catch (SqliteException ex)
            {
                Console.Error.WriteLine($"ps-bash: history write failed: {ex.Message}");
            }
        });
    }

    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
    {
        return Task.Run(() =>
        {
            try
            {
                lock (_lock)
                {
                    EnsureConnectionOpen();

                    var cmd = _connection!.CreateCommand();
                    var sql = new System.Text.StringBuilder();
                    sql.Append("SELECT id, command, cwd, exit_code, timestamp, duration_ms, session ");
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
                        sql.Append("AND session = @session_id ");
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
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        results.Add(new HistoryEntry
                        {
                            Id = reader.GetInt64(0),
                            Command = reader.GetString(1),
                            Cwd = reader.GetString(2),
                            ExitCode = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                            Timestamp = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.AssumeUniversal),
                            DurationMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                            SessionId = reader.GetString(6),
                        });
                    }

                    return (IReadOnlyList<HistoryEntry>)results;
                }
            }
            catch
            {
                // Return empty list on failure — fail silently for queries
                return Array.Empty<HistoryEntry>();
            }
        });
    }

    public Task<IReadOnlyList<SequenceSuggestion>> GetSequenceSuggestionsAsync(
        string? lastCommand,
        string cwd)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(lastCommand))
                return Array.Empty<SequenceSuggestion>();

            try
            {
                lock (_lock)
                {
                    EnsureConnectionOpen();

                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = """
                        SELECT next_command, SUM(count) as total_count
                        FROM (
                            SELECT
                                h2.command as next_command,
                                1 as count
                            FROM history h1
                            JOIN history h2 ON h1.id + 1 = h2.id
                            WHERE h1.command = @prev
                                AND h2.timestamp > datetime('now', '-30 days')
                            UNION ALL
                            SELECT
                                h2.command as next_command,
                                COUNT(*) as count
                            FROM history h1
                            JOIN history h2 ON h1.id + 1 = h2.id
                            WHERE h1.command = @prev
                                AND h2.cwd = @cwd
                                AND h2.timestamp > datetime('now', '-30 days')
                            GROUP BY h2.command
                        )
                        GROUP BY next_command
                        ORDER BY total_count DESC
                        LIMIT 10;
                        """;
                    cmd.Parameters.AddWithValue("@prev", lastCommand);
                    cmd.Parameters.AddWithValue("@cwd", cwd);

                    var results = new List<SequenceSuggestion>();
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var nextCommand = reader.GetString(0);
                        var count = reader.GetInt64(1);

                        results.Add(new SequenceSuggestion
                        {
                            Command = nextCommand,
                            Score = Math.Min(1.0, count / 10.0),
                            Reason = $"Followed '{lastCommand}' {count} times",
                        });
                    }

                    return (IReadOnlyList<SequenceSuggestion>)results;
                }
            }
            catch
            {
                return Array.Empty<SequenceSuggestion>();
            }
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
