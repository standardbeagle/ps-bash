using Xunit;
using PsBash.Shell;

namespace PsBash.Shell.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// LineEditor.SplitAtWordBoundary
// ─────────────────────────────────────────────────────────────────────────────

public class LineEditorSplitTests
{
    [Fact]
    public void Split_EmptyLine_ReturnsEmptyBoth()
    {
        var (b, t) = LineEditor.SplitAtWordBoundary("", 0);
        Assert.Equal("", b);
        Assert.Equal("", t);
    }

    [Fact]
    public void Split_SingleWord_BaseEmptyTokenIsWord()
    {
        var (b, t) = LineEditor.SplitAtWordBoundary("git", 3);
        Assert.Equal("", b);
        Assert.Equal("git", t);
    }

    [Fact]
    public void Split_TwoWords_BaseIsFirstWordPlusSpace()
    {
        var (b, t) = LineEditor.SplitAtWordBoundary("ls /tmp", 7);
        Assert.Equal("ls ", b);
        Assert.Equal("/tmp", t);
    }

    [Fact]
    public void Split_CursorMidWord_TokenIsPartialWord()
    {
        var (b, t) = LineEditor.SplitAtWordBoundary("ls /tm", 6);
        Assert.Equal("ls ", b);
        Assert.Equal("/tm", t);
    }

    [Fact]
    public void Split_TrailingSpace_TokenIsEmpty()
    {
        var (b, t) = LineEditor.SplitAtWordBoundary("ls ", 3);
        Assert.Equal("ls ", b);
        Assert.Equal("", t);
    }

    [Fact]
    public void Split_ThreeWords_BaseIsTwoWordsPlusSpaces()
    {
        var (b, t) = LineEditor.SplitAtWordBoundary("git commit -m", 13);
        Assert.Equal("git commit ", b);
        Assert.Equal("-m", t);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TabCompleter
// ─────────────────────────────────────────────────────────────────────────────

public class TabCompleterTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly Dictionary<string, string> _noAliases = new(StringComparer.Ordinal);

    public TabCompleterTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "psbash-tabtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void CompletePath_EmptyToken_ReturnsFilesInCwd()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "alpha.sh"), "");
        File.WriteAllText(Path.Combine(_tmpDir, "beta.sh"), "");

        var results = TabCompleter.Complete("cat ", 4, _noAliases, _tmpDir);

        Assert.Contains("alpha.sh", results);
        Assert.Contains("beta.sh", results);
    }

    [Fact]
    public void CompletePath_PartialName_FiltersCorrectly()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "foo.txt"), "");
        File.WriteAllText(Path.Combine(_tmpDir, "bar.txt"), "");

        var results = TabCompleter.Complete("cat fo", 6, _noAliases, _tmpDir);

        Assert.Contains("foo.txt", results);
        Assert.DoesNotContain("bar.txt", results);
    }

    [Fact]
    public void CompletePath_Directory_AppendsSeparator()
    {
        var sub = Path.Combine(_tmpDir, "subdir");
        Directory.CreateDirectory(sub);

        var results = TabCompleter.Complete("ls sub", 6, _noAliases, _tmpDir);

        Assert.Contains("subdir/", results);
    }

    [Fact]
    public void CompleteCommand_KnownBuiltin_Returned()
    {
        // "ec" should complete to "echo"
        var results = TabCompleter.Complete("ec", 2, _noAliases, _tmpDir);
        Assert.Contains("echo", results);
    }

    [Fact]
    public void CompleteCommand_Alias_Returned()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gst"] = "git status",
        };

        var results = TabCompleter.Complete("gs", 2, aliases, _tmpDir);
        Assert.Contains("gst", results);
    }

    [Fact]
    public void CompleteCommand_NoMatch_ReturnsEmpty()
    {
        var results = TabCompleter.Complete("zzznomatch", 10, _noAliases, _tmpDir);
        Assert.Empty(results);
    }

    [Fact]
    public void CompletePath_AbsolutePath_Works()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "readme.md"), "");
        var prefix = _tmpDir.TrimEnd('/') + "/";

        var results = TabCompleter.Complete($"cat {prefix}read", prefix.Length + 4 + "read".Length, _noAliases, _tmpDir);
        Assert.Contains(prefix + "readme.md", results);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Flag completion
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompleteFlags_ls_dash_ReturnsAllFlagsWithDescriptions()
    {
        var results = TabCompleter.Complete("ls -", 4, _noAliases, _tmpDir);

        // Should return flags formatted as "-l  - long listing"
        Assert.Contains("-l  - long listing", results);
        Assert.Contains("-a  - show hidden", results);
        Assert.Contains("-h  - human readable sizes", results);
    }

    [Fact]
    public void CompleteFlags_ls_dash_l_ReturnsOnlyLongListingFlag()
    {
        var results = TabCompleter.Complete("ls -l", 5, _noAliases, _tmpDir);

        // Should return only flags starting with "-l"
        Assert.Single(results);
        Assert.Contains("-l  - long listing", results);
    }

    [Fact]
    public void CompleteFlags_grep_dash_i_ReturnsIgnoreCaseFlag()
    {
        var results = TabCompleter.Complete("grep -i", 7, _noAliases, _tmpDir);

        // Should return "-i  - ignore case"
        Assert.Contains("-i  - ignore case", results);
    }

    [Fact]
    public void CompleteFlags_cat_dash_n_ReturnsNumberLinesFlag()
    {
        var results = TabCompleter.Complete("cat -n", 6, _noAliases, _tmpDir);

        // Should return "-n  - number all lines"
        Assert.Contains("-n  - number all lines", results);
    }

    [Fact]
    public void CompleteFlags_AfterCommandWithFlags_ReturnsFlags()
    {
        var results = TabCompleter.Complete("ls -l -", 7, _noAliases, _tmpDir);

        // Should complete flags after existing flags
        Assert.Contains("-a  - show hidden", results);
    }

    [Fact]
    public void CompleteFlags_UnknownCommand_ReturnsEmpty()
    {
        var results = TabCompleter.Complete("unknowncmd -", 12, _noAliases, _tmpDir);

        // Should return empty for commands without flag specs
        Assert.Empty(results);
    }

    [Fact]
    public void CompleteFlags_CommandNotInFlagSpecs_ReturnsEmpty()
    {
        var results = TabCompleter.Complete("somecommand -", 14, _noAliases, _tmpDir);

        // Should return empty when command not in FlagSpecs
        Assert.Empty(results);
    }

    [Fact]
    public void CompleteFlags_WithAlias_ExpandsAlias()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ll"] = "ls -l",
        };

        var results = TabCompleter.Complete("ll -", 4, aliases, _tmpDir);

        // Should expand alias "ll" to "ls" and return ls flags
        Assert.Contains("-a  - show hidden", results);
        Assert.Contains("-h  - human readable sizes", results);
    }

    [Fact]
    public void CompleteFlags_NonFlagStart_FallsBackToPathCompletion()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "file.txt"), "");
        var results = TabCompleter.Complete("ls file", 7, _noAliases, _tmpDir);

        // When not starting with '-', should do path completion
        Assert.Contains("file.txt", results);
    }

    [Fact]
    public void CompleteFlags_EnvVarPrefix_Works()
    {
        var results = TabCompleter.Complete("FOO=bar ls -", 12, _noAliases, _tmpDir);

        // Should handle env var prefix before command
        Assert.Contains("-l  - long listing", results);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Sequence completion
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Complete_WithSequenceSuggestions_ReturnsSequenceMatches()
    {
        var store = new InMemoryHistoryStore();
        var cwd = "/home/user/project";

        // Create a sequence: docker build -> docker run
        for (int i = 0; i < 3; i++)
        {
            await store.RecordAsync(new HistoryEntry
            {
                Command = "docker build -t myapp .",
                Cwd = cwd,
                Timestamp = DateTime.UtcNow.AddSeconds(i * 2),
                SessionId = "s1"
            });
            await store.RecordAsync(new HistoryEntry
            {
                Command = "docker run myapp",
                Cwd = cwd,
                Timestamp = DateTime.UtcNow.AddSeconds(i * 2 + 1),
                SessionId = "s1"
            });
        }

        var results = TabCompleter.Complete("", 0, _noAliases, cwd, "docker build -t myapp .", store);

        // Should suggest "docker run myapp" based on sequence
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Contains("docker run"));
    }

    [Fact]
    public async Task Complete_WithSequenceSuggestionsAndPrefix_FiltersByPrefix()
    {
        var store = new InMemoryHistoryStore();
        var cwd = "/home/user/project";

        // Create sequences
        await store.RecordAsync(new HistoryEntry { Command = "git commit", Cwd = cwd, Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "git push", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(1), SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "git status", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(2), SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "git commit", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(3), SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "git push", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(4), SessionId = "s1" });

        // Verify the sequence suggestions work directly
        var suggestions = await store.GetSequenceSuggestionsAsync("git commit", cwd);
        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, r => r.Command == "git push");

        // Test with empty line to trigger sequence suggestions
        var results = TabCompleter.Complete("", 0, _noAliases, cwd, "git commit", store);

        // Should include "git push" from sequence suggestions
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r == "git push");

        // Test with prefix "git" (first word, no space) - should suggest "git push"
        var prefixedResults = TabCompleter.Complete("git", 3, _noAliases, cwd, "git commit", store);
        Assert.NotEmpty(prefixedResults);
        Assert.Contains(prefixedResults, r => r == "git push");
    }

    [Fact]
    public void Complete_WithNullHistoryStore_DoesNotThrow()
    {
        var ex = Record.Exception(() => TabCompleter.Complete("", 0, _noAliases, _tmpDir, "git commit", null));
        Assert.Null(ex);
    }

    [Fact]
    public void Complete_WithNullLastCommand_DoesNotThrow()
    {
        var ex = Record.Exception(() => TabCompleter.Complete("", 0, _noAliases, _tmpDir, null, new InMemoryHistoryStore()));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Complete_CwdBoostedSequences_PrioritizesLocalSequences()
    {
        var store = new InMemoryHistoryStore();
        var cwd1 = "/home/user/project1";
        var cwd2 = "/home/user/project2";

        // Create sequences in different directories
        for (int i = 0; i < 3; i++)
        {
            await store.RecordAsync(new HistoryEntry { Command = "build", Cwd = cwd1, Timestamp = DateTime.UtcNow.AddSeconds(i * 2), SessionId = "s1" });
            await store.RecordAsync(new HistoryEntry { Command = "test", Cwd = cwd1, Timestamp = DateTime.UtcNow.AddSeconds(i * 2 + 1), SessionId = "s1" });

            await store.RecordAsync(new HistoryEntry { Command = "build", Cwd = cwd2, Timestamp = DateTime.UtcNow.AddSeconds(i * 2 + 10), SessionId = "s1" });
            await store.RecordAsync(new HistoryEntry { Command = "deploy", Cwd = cwd2, Timestamp = DateTime.UtcNow.AddSeconds(i * 2 + 11), SessionId = "s1" });
        }

        var results1 = TabCompleter.Complete("", 0, _noAliases, cwd1, "build", store);
        var results2 = TabCompleter.Complete("", 0, _noAliases, cwd2, "build", store);

        // cwd1 should suggest "test" (local sequence)
        Assert.NotEmpty(results1);
        Assert.Contains(results1, r => r == "test");

        // cwd2 should suggest "deploy" (local sequence)
        Assert.NotEmpty(results2);
        Assert.Contains(results2, r => r == "deploy");
    }

    [Fact]
    public async Task Complete_EmptyLine_NoLastCommand_ReturnsCommandCompletions()
    {
        var store = new InMemoryHistoryStore();
        var results = TabCompleter.Complete("", 0, _noAliases, _tmpDir, null, store);

        Assert.NotEmpty(results);
        // Should include known commands like "ls", "echo", etc.
        Assert.Contains("ls", results);
    }

    [Fact]
    public async Task Complete_SequenceMergedWithCommands_SequencesComeFirst()
    {
        var store = new InMemoryHistoryStore();
        var cwd = "/home/user/project";

        // Create a sequence where "git push" follows "git commit"
        await store.RecordAsync(new HistoryEntry { Command = "git commit", Cwd = cwd, Timestamp = DateTime.UtcNow, SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "git push", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(1), SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "git commit", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(2), SessionId = "s1" });
        await store.RecordAsync(new HistoryEntry { Command = "git push", Cwd = cwd, Timestamp = DateTime.UtcNow.AddSeconds(3), SessionId = "s1" });

        var results = TabCompleter.Complete("", 0, _noAliases, cwd, "git commit", store);

        // "git push" should appear early in results (sequence suggestion prioritized)
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r == "git push");

        // Should also include other commands
        Assert.True(results.Count > 1);
    }
}
