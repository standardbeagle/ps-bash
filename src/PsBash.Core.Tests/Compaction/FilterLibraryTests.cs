using PsBash.Core.Runtime.Compaction;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Compaction;

public class FilterLibraryTests
{
    private static FilterSpec Spec(string name, string command, params string[] args) => new()
    {
        Name = name,
        Match = new FilterMatch { Command = command, Args = args },
    };

    // ---- Merge (pure precedence) ----

    [Fact]
    public void Merge_ProjectShadowsUserShadowsBuiltin_BySameName()
    {
        var builtin = new[] { Spec("git/status", "git", "status") with { OnSuccess = "builtin" } };
        var user = new[] { Spec("git/status", "git", "status") with { OnSuccess = "user" } };
        var project = new[] { Spec("git/status", "git", "status") with { OnSuccess = "project" } };

        var merged = FilterLibrary.Merge(builtin, user, project);

        var spec = Assert.Single(merged);
        Assert.Equal("project", spec.OnSuccess);
    }

    [Fact]
    public void Merge_UserShadowsBuiltin_WhenNoProjectOverride()
    {
        var builtin = new[] { Spec("git/status", "git", "status") with { OnSuccess = "builtin" } };
        var user = new[] { Spec("git/status", "git", "status") with { OnSuccess = "user" } };

        var merged = FilterLibrary.Merge(builtin, user, []);

        Assert.Equal("user", Assert.Single(merged).OnSuccess);
    }

    [Fact]
    public void Merge_DistinctNames_AllRetained()
    {
        var builtin = new[] { Spec("git/status", "git", "status"), Spec("git/log", "git", "log") };

        var merged = FilterLibrary.Merge(builtin, [], []);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_ExcludeCommands_DropsByCommand()
    {
        var builtin = new[] { Spec("git/status", "git", "status"), Spec("ls", "ls") };

        var merged = FilterLibrary.Merge(builtin, [], [], new HashSet<string> { "git" });

        Assert.Equal("ls", Assert.Single(merged).Name);
    }

    [Fact]
    public void Merge_OrdersMostSpecificFirst()
    {
        var catchAll = Spec("git", "git");                 // 0 args
        var status = Spec("git/status", "git", "status");  // 1 arg

        var merged = FilterLibrary.Merge(new[] { catchAll, status }, [], []);

        Assert.Equal("git/status", merged[0].Name); // more args wins the position
    }

    // ---- Load (disk + cache) ----

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash-tests", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Load_ReadsProjectAndUserDirs_ProjectWins()
    {
        var userDir = NewTempDir();
        var projectDir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(userDir, "g.json"),
                """{ "name": "git/status", "match": { "command": "git", "args": ["status"] }, "onSuccess": "user" }""");
            File.WriteAllText(Path.Combine(projectDir, "g.json"),
                """{ "name": "git/status", "match": { "command": "git", "args": ["status"] }, "onSuccess": "project" }""");

            var merged = FilterLibrary.Load(userDir, projectDir);

            Assert.Equal("project", Assert.Single(merged).OnSuccess);
        }
        finally { Directory.Delete(userDir, true); Directory.Delete(projectDir, true); }
    }

    [Fact]
    public void Load_MissingDirs_ReturnsEmptyOrEmbeddedOnly()
    {
        var merged = FilterLibrary.Load(
            Path.Combine(Path.GetTempPath(), "ps-bash-tests", "does-not-exist-" + System.Guid.NewGuid().ToString("N")),
            Path.Combine(Path.GetTempPath(), "ps-bash-tests", "also-missing-" + System.Guid.NewGuid().ToString("N")));

        // No user/project specs and (in P1) no embedded built-ins -> empty. Must not throw.
        Assert.Empty(merged);
    }

    [Fact]
    public void Load_MalformedFile_SkippedButOthersLoad()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "good.json"),
                """{ "name": "ls", "match": { "command": "ls" } }""");

            var merged = FilterLibrary.Load(null, dir);

            Assert.Equal("ls", Assert.Single(merged).Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_ExcludeCommand_DropsMatching()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "g.json"),
                """{ "name": "git/status", "match": { "command": "git", "args": ["status"] } }""");

            var merged = FilterLibrary.Load(null, dir, new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "git" });

            Assert.Empty(merged);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_CachesUntilFileMtimeChanges()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "g.json");
            File.WriteAllText(file, """{ "name": "ls", "match": { "command": "ls" }, "onSuccess": "v1" }""");

            var first = FilterLibrary.Load(null, dir);
            var second = FilterLibrary.Load(null, dir);
            Assert.Same(first, second); // identical signature -> cached instance

            // Bump mtime forward deterministically (avoid same-tick write).
            File.WriteAllText(file, """{ "name": "ls", "match": { "command": "ls" }, "onSuccess": "v2" }""");
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(5));

            var third = FilterLibrary.Load(null, dir);
            Assert.NotSame(first, third);
            Assert.Equal("v2", Assert.Single(third).OnSuccess);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- End-to-end through FilterEngine ----

    [Fact]
    public void LoadedFilter_AppliedByEngine()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "push.json"),
                """{ "name": "git/push", "match": { "command": "git", "args": ["push"] }, "onSuccess": "ok pushed" }""");

            var filters = FilterLibrary.Load(null, dir);
            var result = FilterEngine.Apply("git push", 0, false,
                [new OutputFrame(StreamTag.Stdout, "noise\n")], filters);

            Assert.Contains("ok pushed", result);
        }
        finally { Directory.Delete(dir, true); }
    }
}
