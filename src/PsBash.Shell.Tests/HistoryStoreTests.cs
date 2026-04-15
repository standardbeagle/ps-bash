using Xunit;
using PsBash.Shell;

namespace PsBash.Shell.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteHistoryStore _store;

    public HistoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "psbash-test-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new SqliteHistoryStore(_dbPath);
    }

    public void Dispose()
    {
        try
        {
            _store.Dispose();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);

            var walPath = _dbPath + "-wal";
            var shmPath = _dbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch { }
    }

    [Fact]
    public async Task RecordAsync_EntryAdded_CanRetrieve()
    {
        var entry = new HistoryEntry
        {
            Command = "ls -la",
            Cwd = "/home/user",
            Timestamp = DateTime.UtcNow,
            SessionId = "test-session",
        };

        await _store.RecordAsync(entry);

        var results = await _store.SearchAsync(new HistoryQuery { Limit = 10 });

        Assert.Single(results);
        Assert.Equal("ls -la", results[0].Command);
        Assert.Equal("/home/user", results[0].Cwd);
    }

    [Fact]
    public async Task RecordAsync_WithNullOptionalFields_Works()
    {
        var entry = new HistoryEntry
        {
            Command = "echo test",
            Cwd = "/tmp",
            Timestamp = DateTime.UtcNow,
            SessionId = "session-1",
            ExitCode = null,
            DurationMs = null,
        };

        await _store.RecordAsync(entry);

        var results = await _store.SearchAsync(new HistoryQuery { Limit = 10 });

        Assert.Single(results);
        Assert.Equal("echo test", results[0].Command);
        Assert.Null(results[0].ExitCode);
        Assert.Null(results[0].DurationMs);
    }

    [Fact]
    public async Task RecordAsync_WithExitCodeAndDuration_StoresCorrectly()
    {
        var entry = new HistoryEntry
        {
            Command = "sleep 1",
            Cwd = "/home/user",
            Timestamp = DateTime.UtcNow,
            SessionId = "session-2",
            ExitCode = 0,
            DurationMs = 1234,
        };

        await _store.RecordAsync(entry);

        var results = await _store.SearchAsync(new HistoryQuery { Limit = 10 });

        Assert.Single(results);
        Assert.Equal(0, results[0].ExitCode);
        Assert.Equal(1234, results[0].DurationMs);
    }

    [Fact]
    public async Task SearchAsync_WithFilter_ReturnsMatchingPrefixes()
    {
        await _store.RecordAsync(new HistoryEntry { Command = "git status", Cwd = "/home/user", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "git commit", Cwd = "/home/user", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "ls -la", Cwd = "/home/user", Timestamp = DateTime.UtcNow, SessionId = "s1" });

        var results = await _store.SearchAsync(new HistoryQuery { Filter = "git", Limit = 10 });

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.StartsWith("git", r.Command));
    }

    [Fact]
    public async Task SearchAsync_WithCwdFilter_ReturnsOnlyFromThatDirectory()
    {
        await _store.RecordAsync(new HistoryEntry { Command = "cmd1", Cwd = "/home/user", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "cmd2", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "cmd3", Cwd = "/home/user", Timestamp = DateTime.UtcNow, SessionId = "s1" });

        var results = await _store.SearchAsync(new HistoryQuery { Cwd = "/home/user", Limit = 10 });

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("/home/user", r.Cwd));
    }

    [Fact]
    public async Task SearchAsync_WithExitCodeFilter_ReturnsMatchingExitCode()
    {
        await _store.RecordAsync(new HistoryEntry { Command = "success", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1", ExitCode = 0 });
        await _store.RecordAsync(new HistoryEntry { Command = "failure", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1", ExitCode = 1 });

        var successResults = await _store.SearchAsync(new HistoryQuery { ExitCode = 0, Limit = 10 });
        var failureResults = await _store.SearchAsync(new HistoryQuery { ExitCode = 1, Limit = 10 });

        Assert.Single(successResults);
        Assert.Equal("success", successResults[0].Command);
        Assert.Single(failureResults);
        Assert.Equal("failure", failureResults[0].Command);
    }

    [Fact]
    public async Task SearchAsync_WithSessionIdFilter_ReturnsOnlyFromSession()
    {
        await _store.RecordAsync(new HistoryEntry { Command = "cmd1", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "session-a" });
        await _store.RecordAsync(new HistoryEntry { Command = "cmd2", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "session-b" });
        await _store.RecordAsync(new HistoryEntry { Command = "cmd3", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "session-a" });

        var results = await _store.SearchAsync(new HistoryQuery { SessionId = "session-a", Limit = 10 });

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("session-a", r.SessionId));
    }

    [Fact]
    public async Task SearchAsync_DefaultOrdering_NewestFirst()
    {
        var baseTime = DateTime.UtcNow;

        await _store.RecordAsync(new HistoryEntry { Command = "first", Cwd = "/tmp", Timestamp = baseTime.AddSeconds(-2), SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "second", Cwd = "/tmp", Timestamp = baseTime.AddSeconds(-1), SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "third", Cwd = "/tmp", Timestamp = baseTime, SessionId = "s1" });

        var results = await _store.SearchAsync(new HistoryQuery { Limit = 10 });

        Assert.Equal("third", results[0].Command);
        Assert.Equal("second", results[1].Command);
        Assert.Equal("first", results[2].Command);
    }

    [Fact]
    public async Task SearchAsync_ReverseOrdering_OldestFirst()
    {
        var baseTime = DateTime.UtcNow;

        await _store.RecordAsync(new HistoryEntry { Command = "first", Cwd = "/tmp", Timestamp = baseTime.AddSeconds(-2), SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "second", Cwd = "/tmp", Timestamp = baseTime.AddSeconds(-1), SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "third", Cwd = "/tmp", Timestamp = baseTime, SessionId = "s1" });

        var results = await _store.SearchAsync(new HistoryQuery { Reverse = true, Limit = 10 });

        Assert.Equal("first", results[0].Command);
        Assert.Equal("second", results[1].Command);
        Assert.Equal("third", results[2].Command);
    }

    [Fact]
    public async Task SearchAsync_Limit_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            await _store.RecordAsync(new HistoryEntry { Command = $"cmd{i}", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        }

        var results = await _store.SearchAsync(new HistoryQuery { Limit = 5 });

        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task SearchAsync_MultipleFilters_AppliesAllFilters()
    {
        var baseTime = DateTime.UtcNow;

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git status",
            Cwd = "/home/user/project",
            Timestamp = baseTime.AddSeconds(-2),
            SessionId = "s1",
            ExitCode = 0
        });
        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git commit",
            Cwd = "/home/user/project",
            Timestamp = baseTime.AddSeconds(-1),
            SessionId = "s1",
            ExitCode = 0
        });
        await _store.RecordAsync(new HistoryEntry
        {
            Command = "ls",
            Cwd = "/tmp",
            Timestamp = baseTime,
            SessionId = "s2",
            ExitCode = 0
        });

        var results = await _store.SearchAsync(new HistoryQuery
        {
            Filter = "git",
            Cwd = "/home/user/project",
            SessionId = "s1",
            ExitCode = 0,
            Limit = 10
        });

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.StartsWith("git", r.Command);
            Assert.Equal("/home/user/project", r.Cwd);
            Assert.Equal("s1", r.SessionId);
            Assert.Equal(0, r.ExitCode);
        });
    }

    [Fact]
    public async Task GetSequenceSuggestionsAsync_NoLastCommand_ReturnsEmpty()
    {
        var results = await _store.GetSequenceSuggestionsAsync(null, "/tmp");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSequenceSuggestionsAsync_EmptyLastCommand_ReturnsEmpty()
    {
        var results = await _store.GetSequenceSuggestionsAsync("", "/tmp");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSequenceSuggestionsAsync_WithSequence_ReturnsSuggestions()
    {
        // Create a sequence: git push followed by git status (3 times)
        var cwd = "/home/user/project";
        for (int i = 0; i < 3; i++)
        {
            await _store.RecordAsync(new HistoryEntry
            {
                Command = "git push",
                Cwd = cwd,
                Timestamp = DateTime.UtcNow.AddSeconds(i * 2),
                SessionId = "s1"
            });
            await _store.RecordAsync(new HistoryEntry
            {
                Command = "git status",
                Cwd = cwd,
                Timestamp = DateTime.UtcNow.AddSeconds(i * 2 + 1),
                SessionId = "s1"
            });
        }

        var results = await _store.GetSequenceSuggestionsAsync("git push", cwd);

        // Should get "git status" as a suggestion
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Command == "git status");
    }

    [Fact]
    public async Task RecordAsync_MultipleEntries_AutoIncrementsId()
    {
        await _store.RecordAsync(new HistoryEntry { Command = "cmd1", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "cmd2", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await _store.RecordAsync(new HistoryEntry { Command = "cmd3", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });

        var results = await _store.SearchAsync(new HistoryQuery { Limit = 10 });

        Assert.Equal(3, results.Count);
        Assert.NotNull(results[0].Id);
        Assert.NotNull(results[1].Id);
        Assert.NotNull(results[2].Id);
        Assert.True(results[0].Id > results[1].Id);
        Assert.True(results[1].Id > results[2].Id);
    }

    [Fact]
    public async Task SearchAsync_EmptyDatabase_ReturnsEmpty()
    {
        var results = await _store.SearchAsync(new HistoryQuery { Limit = 10 });

        Assert.Empty(results);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryHistoryStore Tests
// ─────────────────────────────────────────────────────────────────────────────

public class InMemoryHistoryStoreTests
{
    [Fact]
    public async Task RecordAsync_AndSearch_RoundTrip()
    {
        var store = new InMemoryHistoryStore();

        await store.RecordAsync(new HistoryEntry
        {
            Command = "ls -la",
            Cwd = "/home/user",
            Timestamp = DateTime.UtcNow,
            SessionId = "test-session",
        });

        var results = await store.SearchAsync(new HistoryQuery { Limit = 10 });

        Assert.Single(results);
        Assert.Equal("ls -la", results[0].Command);
    }

    [Fact]
    public async Task SearchAsync_WithFilter_PrefixMatch()
    {
        var store = new InMemoryHistoryStore();

        await store.RecordAsync(new HistoryEntry { Command = "git status", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "git commit", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "ls -la", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });

        var results = await store.SearchAsync(new HistoryQuery { Filter = "git", Limit = 10 });

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.StartsWith("git", r.Command));
    }

    [Fact]
    public async Task SearchAsync_WithCwdFilter_OnlyMatchesDirectory()
    {
        var store = new InMemoryHistoryStore();

        await store.RecordAsync(new HistoryEntry { Command = "cmd1", Cwd = "/home/user", Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "cmd2", Cwd = "/tmp", Timestamp = DateTime.UtcNow, SessionId = "s1" });

        var results = await store.SearchAsync(new HistoryQuery { Cwd = "/home/user", Limit = 10 });

        Assert.Single(results);
        Assert.Equal("/home/user", results[0].Cwd);
    }

    [Fact]
    public async Task GetSequenceSuggestionsAsync_ReturnsSuggestions()
    {
        var store = new InMemoryHistoryStore();
        var cwd = "/home/user/project";

        // Create sequence
        await store.RecordAsync(new HistoryEntry { Command = "docker build", Cwd = cwd, Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "docker test", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(1), SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "docker build", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(2), SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "docker test", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(3), SessionId = "s1" });

        var results = await store.GetSequenceSuggestionsAsync("docker build", cwd);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Command == "docker test");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HistoryEntry Tests
// ─────────────────────────────────────────────────────────────────────────────

public class HistoryEntryTests
{
    [Fact]
    public void HistoryEntry_WithRequiredFields_CreatesSuccessfully()
    {
        var entry = new HistoryEntry
        {
            Command = "test",
            Cwd = "/tmp",
            Timestamp = DateTime.UtcNow,
            SessionId = "session-1",
        };

        Assert.Equal("test", entry.Command);
        Assert.Equal("/tmp", entry.Cwd);
        Assert.Equal("session-1", entry.SessionId);
        Assert.Null(entry.ExitCode);
        Assert.Null(entry.DurationMs);
        Assert.Null(entry.Id);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HistoryQuery Tests
// ─────────────────────────────────────────────────────────────────────────────

public class HistoryQueryTests
{
    [Fact]
    public void HistoryQuery_DefaultValues_AreCorrect()
    {
        var query = new HistoryQuery();

        Assert.Null(query.Filter);
        Assert.Null(query.Cwd);
        Assert.Null(query.SessionId);
        Assert.Equal(100, query.Limit);
        Assert.False(query.Reverse);
        Assert.Null(query.ExitCode);
    }

    [Fact]
    public void HistoryQuery_WithCustomLimit_UsesCustomLimit()
    {
        var query = new HistoryQuery { Limit = 50 };

        Assert.Equal(50, query.Limit);
    }

    [Fact]
    public void HistoryQuery_WithReverseTrue_IsTrue()
    {
        var query = new HistoryQuery { Reverse = true };

        Assert.True(query.Reverse);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SequenceSuggestion Tests
// ─────────────────────────────────────────────────────────────────────────────

public class SequenceSuggestionTests
{
    [Fact]
    public void SequenceSuggestion_WithRequiredFields_CreatesSuccessfully()
    {
        var suggestion = new SequenceSuggestion
        {
            Command = "git status",
            Score = 0.8,
            Reason = "Followed 'git commit' 12 times",
        };

        Assert.Equal("git status", suggestion.Command);
        Assert.Equal(0.8, suggestion.Score);
        Assert.Equal("Followed 'git commit' 12 times", suggestion.Reason);
    }
}
