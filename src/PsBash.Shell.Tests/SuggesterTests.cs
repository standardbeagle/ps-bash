using Xunit;
using PsBash.Shell;

namespace PsBash.Shell.Tests;

public class SuggesterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteHistoryStore _store;
    private readonly Suggester _suggester;

    public SuggesterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "psbash-suggester-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new SqliteHistoryStore(_dbPath);
        _suggester = new Suggester(_store);
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
    public async Task SuggestAsync_EmptyPrefix_ReturnsNull()
    {
        var cwd = "/home/user";

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git status",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow,
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("", cwd);

        Assert.Null(result);
    }

    [Fact]
    public async Task SuggestAsync_NoHistory_ReturnsNull()
    {
        var result = await _suggester.SuggestAsync("git", "/home/user");

        Assert.Null(result);
    }

    [Fact]
    public async Task SuggestAsync_NoMatch_ReturnsNull()
    {
        var cwd = "/home/user";

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "ls -la",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow,
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("git", cwd);

        Assert.Null(result);
    }

    [Fact]
    public async Task SuggestAsync_PrefixMatch_ReturnsCompletion()
    {
        var cwd = "/home/user";

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git commit -m \"fix\"",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow,
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("git comm", cwd);

        Assert.Equal("it -m \"fix\"", result); // Returns only the suffix
    }

    [Fact]
    public async Task SuggestAsync_CwdMatch_PreferredOverNonCwd()
    {
        var cwd = "/home/user/project";

        // First add a global match (older)
        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git checkout main",
            Cwd = "/other/dir",
            Timestamp = DateTime.UtcNow.AddSeconds(-10),
            SessionId = "s1"
        });

        // Then add a CWD match (more recent)
        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git commit -m \"fix\"",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow,
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("git c", cwd);

        // Should prefer CWD match even though checkout was added first
        Assert.Equal("ommit -m \"fix\"", result);
    }

    [Fact]
    public async Task SuggestAsync_Recency_BreaksTies()
    {
        var cwd = "/home/user";

        // Add two matching commands at different times
        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git checkout main",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow.AddSeconds(-2),
            SessionId = "s1"
        });

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git commit -m \"fix\"",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow,
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("git c", cwd);

        // More recent (commit) should win
        Assert.Equal("ommit -m \"fix\"", result);
    }

    [Fact]
    public async Task SuggestAsync_ExactMatch_ReturnsEmptyString()
    {
        var cwd = "/home/user";

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git status",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow,
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("git status", cwd);

        Assert.Equal("", result); // No completion needed
    }

    [Fact]
    public async Task SuggestAsync_NoCwdMatch_FallsBackToGlobal()
    {
        var cwd = "/home/user/project";

        // Add commands in different directories
        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git commit -m \"fix\"",
            Cwd = "/other/dir",
            Timestamp = DateTime.UtcNow,
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("git comm", cwd);

        // Should fall back to global match
        Assert.Equal("it -m \"fix\"", result);
    }

    [Fact]
    public async Task SuggestAsync_MultipleMatches_ReturnsMostRecent()
    {
        var cwd = "/home/user";

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git add .",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow.AddSeconds(-3),
            SessionId = "s1"
        });

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git commit -m \"fix\"",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow.AddSeconds(-2),
            SessionId = "s1"
        });

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git status",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow.AddSeconds(-1),
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("git ", cwd);

        // Most recent should win
        Assert.Equal("status", result);
    }

    [Fact]
    public async Task SuggestAsync_DeduplicatesConsecutiveIdentical()
    {
        var cwd = "/home/user";

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git status",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow.AddSeconds(-2),
            SessionId = "s1"
        });

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "git status",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow.AddSeconds(-1),
            SessionId = "s1"
        });

        var result = await _suggester.SuggestAsync("git s", cwd);

        // Should suggest "tatus" (from git status)
        Assert.Equal("tatus", result);
    }

    [Fact]
    public async Task SuggestAsync_CaseSensitive_OnlyMatchesExactCase()
    {
        var cwd = "/home/user";

        await _store.RecordAsync(new HistoryEntry
        {
            Command = "Git status",
            Cwd = cwd,
            Timestamp = DateTime.UtcNow,
            SessionId = "s1"
        });

        var resultLower = await _suggester.SuggestAsync("git", cwd);
        var resultUpper = await _suggester.SuggestAsync("Git", cwd);

        Assert.Null(resultLower); // "git" != "Git"
        Assert.Equal(" status", resultUpper); // "Git" matches "Git status"
    }
}
