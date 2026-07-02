using Microsoft.Data.Sqlite;

namespace PsBash.Host.Shell;

/// <summary>
/// SQLite-backed <see cref="IFrecencyStore"/> implementing zoxide's frecency model:
/// each directory carries a <c>rank</c> (bumped +1 per visit) and a <c>last_access</c>
/// epoch. The score blends rank with recency (within the hour ×4, the day ×2, the
/// week ×0.5, else ×0.25). When the summed rank exceeds <see cref="AgingThreshold"/>
/// every rank is decayed ×0.9 and sub-1 rows are dropped, so the DB self-bounds.
/// Mirrors <see cref="SqliteHistoryStore"/>'s connection/locking/async conventions.
/// </summary>
public sealed class SqliteFrecencyStore : IFrecencyStore, IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private readonly object _lock = new();

    // zoxide's aging threshold: once total rank crosses this, decay everything.
    private const double AgingThreshold = 9000.0;

    public SqliteFrecencyStore(string dbPath)
    {
        _dbPath = dbPath;
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
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS dirs (
                        path TEXT PRIMARY KEY,
                        rank REAL NOT NULL,
                        last_access INTEGER NOT NULL
                    );
                    PRAGMA journal_mode = WAL;
                    """;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Console.Error.WriteLine($"ps-bash: frecency database init failed: {ex.Message}");
            }
        }
    }

    private void EnsureConnectionOpen()
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
            ApplyBusyTimeout(_connection);
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
            ApplyBusyTimeout(_connection);
        }
    }

    // Concurrent ps-bash processes record cd visits into the same frecency DB; a
    // busy timeout lets a contending writer wait out a brief write lock rather than
    // hitting SQLITE_BUSY and silently dropping the visit.
    private static void ApplyBusyTimeout(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 3000;";
        cmd.ExecuteNonQuery();
    }

    private static long NowEpoch() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public Task AddAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                lock (_lock)
                {
                    EnsureConnectionOpen();
                    var now = NowEpoch();

                    // Upsert: +1 rank on revisit, else insert at rank 1.
                    using (var cmd = _connection!.CreateCommand())
                    {
                        cmd.CommandText = """
                            INSERT INTO dirs (path, rank, last_access)
                            VALUES (@path, 1.0, @now)
                            ON CONFLICT(path) DO UPDATE SET
                                rank = rank + 1.0,
                                last_access = @now;
                            """;
                        cmd.Parameters.AddWithValue("@path", path);
                        cmd.Parameters.AddWithValue("@now", now);
                        cmd.ExecuteNonQuery();
                    }

                    MaybeAge();
                }
            }
            catch (Exception ex)
            {
                // Best-effort + fire-and-forget from the shell: swallow everything
                // (not just SqliteException) so a faulted task can't go unobserved.
                Console.Error.WriteLine($"ps-bash: frecency write failed: {ex.Message}");
            }
        });
    }

    // Decay all ranks ×0.9 and drop sub-1 rows once the total rank grows large,
    // matching zoxide so a long-lived DB stays bounded and recency stays relevant.
    private void MaybeAge()
    {
        using var sumCmd = _connection!.CreateCommand();
        sumCmd.CommandText = "SELECT COALESCE(SUM(rank), 0) FROM dirs;";
        var total = Convert.ToDouble(sumCmd.ExecuteScalar());
        if (total <= AgingThreshold) return;

        using var ageCmd = _connection.CreateCommand();
        ageCmd.CommandText = """
            UPDATE dirs SET rank = rank * 0.9;
            DELETE FROM dirs WHERE rank < 1.0;
            """;
        ageCmd.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<FrecencyMatch>> QueryAsync(IReadOnlyList<string> keywords, int limit = 50)
    {
        return Task.Run(() =>
        {
            try
            {
                lock (_lock)
                {
                    EnsureConnectionOpen();

                    // Cheap SQL pre-filter on the last keyword (LIKE is ASCII
                    // case-insensitive by default); C# refines order + basename
                    // + existence. Empty keywords → score everything.
                    using var cmd = _connection!.CreateCommand();
                    string? lastKw = keywords.Count > 0 ? keywords[^1] : null;
                    if (lastKw is { Length: > 0 })
                    {
                        cmd.CommandText = "SELECT path, rank, last_access FROM dirs WHERE path LIKE '%' || @kw || '%';";
                        cmd.Parameters.AddWithValue("@kw", lastKw);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT path, rank, last_access FROM dirs;";
                    }

                    var now = NowEpoch();
                    var matches = new List<FrecencyMatch>();
                    var stale = new List<string>();

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var path = reader.GetString(0);
                            var rank = reader.GetDouble(1);
                            var lastAccess = reader.GetInt64(2);

                            if (!KeywordsMatch(path, keywords)) continue;
                            if (!DirectoryExistsBounded(path)) { stale.Add(path); continue; }

                            matches.Add(new FrecencyMatch { Path = path, Score = Frecency(rank, now - lastAccess) });
                        }
                    }

                    PruneStale(stale);

                    matches.Sort((a, b) => b.Score.CompareTo(a.Score));
                    if (matches.Count > limit) matches = matches.GetRange(0, limit);
                    return (IReadOnlyList<FrecencyMatch>)matches;
                }
            }
            catch (Exception)
            {
                // Advisory query — empty result is acceptable.
                return Array.Empty<FrecencyMatch>();
            }
        });
    }

    /// <summary>
    /// Existence check that cannot freeze the completion query. <see cref="Directory.Exists"/>
    /// on a dead network path blocks for the full SMB timeout (seconds) — with the query on the
    /// keystroke path that froze typing. UNC paths get a bounded probe; on timeout we assume the
    /// dir exists (keep it, do NOT prune) so a transient network stall neither hangs the prompt
    /// nor evicts a real directory. Local paths take the direct, fast check.
    /// </summary>
    private static bool DirectoryExistsBounded(string path)
    {
        bool isUnc = path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal);
        if (!isUnc) return Directory.Exists(path);
        try
        {
            var probe = Task.Run(() => Directory.Exists(path));
            return probe.Wait(TimeSpan.FromMilliseconds(200)) ? probe.Result : true;
        }
        catch
        {
            return true;
        }
    }

    private void PruneStale(List<string> stale)
    {
        if (stale.Count == 0) return;
        try
        {
            using var del = _connection!.CreateCommand();
            del.CommandText = "DELETE FROM dirs WHERE path = @p;";
            var p = del.Parameters.Add("@p", SqliteType.Text);
            foreach (var s in stale) { p.Value = s; del.ExecuteNonQuery(); }
        }
        catch (SqliteException) { /* best-effort prune */ }
    }

    // zoxide recency multiplier on the base rank.
    private static double Frecency(double rank, long ageSeconds)
    {
        if (ageSeconds < 3600) return rank * 4.0;        // within the hour
        if (ageSeconds < 86_400) return rank * 2.0;      // within the day
        if (ageSeconds < 604_800) return rank * 0.5;     // within the week
        return rank * 0.25;                               // older
    }

    // Keywords must occur in order (case-insensitive) within the path, and the
    // LAST keyword must hit the final path component — zoxide's match rule. This
    // keeps `z proj` from matching a directory that only contains "proj" in a
    // parent segment.
    private static bool KeywordsMatch(string path, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0) return true;

        int idx = 0;
        for (int k = 0; k < keywords.Count; k++)
        {
            var kw = keywords[k];
            if (kw.Length == 0) continue;
            int pos = path.IndexOf(kw, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return false;
            idx = pos + kw.Length;
        }

        var lastKw = keywords[^1];
        if (lastKw.Length == 0) return true;
        var basename = LastSegment(path);
        return basename.Contains(lastKw, StringComparison.OrdinalIgnoreCase);
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        int slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
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
