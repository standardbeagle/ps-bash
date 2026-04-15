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
}
