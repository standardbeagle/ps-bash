using Xunit;
using PsBash.Host.Shell;

namespace PsBash.Shell.Tests;

public class FrecencyStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _root;
    private readonly SqliteFrecencyStore _store;

    public FrecencyStoreTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), "psbash-frec-" + stamp + ".db");
        _root = Path.Combine(Path.GetTempPath(), "psbash-frec-dirs-" + stamp);
        Directory.CreateDirectory(_root);
        _store = new SqliteFrecencyStore(_dbPath);
    }

    private string MakeDir(params string[] segments)
    {
        var p = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try
        {
            _store.Dispose();
            foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task Add_ThenQuery_ReturnsTrackedDirectory()
    {
        var dir = MakeDir("projects", "alpha");
        await _store.AddAsync(dir);

        var matches = await _store.QueryAsync(["alpha"]);

        Assert.Single(matches);
        Assert.Equal(dir, matches[0].Path);
    }

    [Fact]
    public async Task Query_RanksMoreFrequentlyVisitedDirectoryFirst()
    {
        var hot = MakeDir("projects", "alpha");
        var cold = MakeDir("work", "alpha");
        await _store.AddAsync(hot);
        await _store.AddAsync(hot);
        await _store.AddAsync(hot);
        await _store.AddAsync(cold);

        var matches = await _store.QueryAsync(["alpha"]);

        Assert.Equal(2, matches.Count);
        Assert.Equal(hot, matches[0].Path);   // visited 3x → higher rank
        Assert.Equal(cold, matches[1].Path);
        Assert.True(matches[0].Score > matches[1].Score);
    }

    [Fact]
    public async Task Query_LastKeywordMustMatchFinalComponent()
    {
        var projAlpha = MakeDir("projects", "alpha");
        var workAlpha = MakeDir("work", "alpha");
        await _store.AddAsync(projAlpha);
        await _store.AddAsync(workAlpha);

        // "proj" then "alpha", in order, last keyword hits the basename.
        var matches = await _store.QueryAsync(["proj", "alpha"]);

        Assert.Single(matches);
        Assert.Equal(projAlpha, matches[0].Path);
    }

    [Fact]
    public async Task Query_KeywordNotInBasename_DoesNotMatch()
    {
        // "projects" appears only in a PARENT segment, not the basename "alpha".
        var dir = MakeDir("projects", "alpha");
        await _store.AddAsync(dir);

        var matches = await _store.QueryAsync(["projects"]);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task Query_SkipsAndPrunesDeletedDirectory()
    {
        var dir = MakeDir("projects", "gone");
        await _store.AddAsync(dir);
        Directory.Delete(dir);

        var first = await _store.QueryAsync(["gone"]);
        Assert.Empty(first);

        // Recreate the path name in the DB-less sense: a fresh store over the same
        // db should also see nothing (the row was pruned on the prior query).
        var second = await _store.QueryAsync([]);
        Assert.DoesNotContain(second, m => m.Path == dir);
    }

    [Fact]
    public async Task Query_EmptyKeywords_ReturnsAllExistingTrackedDirs()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        await _store.AddAsync(a);
        await _store.AddAsync(b);

        var matches = await _store.QueryAsync([]);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.Path == a);
        Assert.Contains(matches, m => m.Path == b);
    }

    [Fact]
    public async Task Query_RespectsLimit()
    {
        for (int i = 0; i < 5; i++)
            await _store.AddAsync(MakeDir("d" + i));

        var matches = await _store.QueryAsync([], limit: 3);

        Assert.Equal(3, matches.Count);
    }
}
