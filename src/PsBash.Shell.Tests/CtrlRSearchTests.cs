using Xunit;
using PsBash.Shell;

namespace PsBash.Shell.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Fuzzy Matching Tests
// ─────────────────────────────────────────────────────────────────────────────

public class CtrlRSearchFuzzyTests
{
    [Fact]
    public void QueryMatchScore_ExactMatch_Returns1()
    {
        var score = CtrlRSearch.QueryMatchScore("docker build", "docker build");
        Assert.Equal(1.0, score, 3);
    }

    [Fact]
    public void QueryMatchScore_PrefixMatch_Returns0_9()
    {
        var score = CtrlRSearch.QueryMatchScore("docker build -t app", "docker build");
        Assert.Equal(0.9, score, 3);
    }

    [Fact]
    public void QueryMatchScore_SubstringMatch_Returns0_7()
    {
        var score = CtrlRSearch.QueryMatchScore("docker build --no-cache", "build");
        Assert.Equal(0.7, score, 3);
    }

    [Fact]
    public void QueryMatchScore_NoMatch_Returns0()
    {
        var score = CtrlRSearch.QueryMatchScore("docker build", "xyz");
        Assert.Equal(0.0, score, 3);
    }

    [Fact]
    public void QueryMatchScore_CaseSensitive_ReturnsLowerScore()
    {
        var score = CtrlRSearch.QueryMatchScore("Docker Build", "docker");
        // Fuzzy subsequence match (case-sensitive, so not prefix)
        Assert.InRange(score, 0.0, 0.5);
    }

    [Fact]
    public void FuzzySubsequenceScore_TightCluster_ReturnsHighScore()
    {
        var score = CtrlRSearch.FuzzySubsequenceScore("docker build", "db");
        // d...b should match with decent density
        Assert.True(score > 0);
    }

    [Fact]
    public void FuzzySubsequenceScore_SpreadOut_ReturnsLowerScore()
    {
        var score1 = CtrlRSearch.FuzzySubsequenceScore("abc", "ab");
        var score2 = CtrlRSearch.FuzzySubsequenceScore("a___b", "ab");
        // Tighter cluster should score higher
        Assert.True(score1 >= score2);
    }

    [Fact]
    public void FuzzySubsequenceScore_PartialMatch_Returns0()
    {
        var score = CtrlRSearch.FuzzySubsequenceScore("docker", "dockerx");
        Assert.Equal(0.0, score, 3);
    }

    [Fact]
    public void FuzzySubsequenceScore_EmptyQuery_Returns0()
    {
        var score = CtrlRSearch.FuzzySubsequenceScore("docker", "");
        Assert.Equal(0.0, score, 3);
    }

    [Fact]
    public void ScoreFuzzyMatch_CwdMatch_GetsBoost()
    {
        var entry = new HistoryEntry
        {
            Command = "docker build",
            Cwd = "/current/dir",
            Timestamp = DateTime.UtcNow,
            SessionId = "test"
        };

        var cwdScore = CtrlRSearch.ScoreFuzzyMatch(entry, "docker", "/current/dir");
        var otherCwdScore = CtrlRSearch.ScoreFuzzyMatch(entry, "docker", "/other/dir");

        Assert.True(cwdScore > otherCwdScore);
    }

    [Fact]
    public void ScoreFuzzyMatch_RecentEntry_GetsBoost()
    {
        var recentEntry = new HistoryEntry
        {
            Command = "docker build",
            Cwd = "/dir",
            Timestamp = DateTime.UtcNow.AddMinutes(-5),
            SessionId = "test"
        };

        var oldEntry = new HistoryEntry
        {
            Command = "docker build",
            Cwd = "/dir",
            Timestamp = DateTime.UtcNow.AddDays(-10),
            SessionId = "test"
        };

        var recentScore = CtrlRSearch.ScoreFuzzyMatch(recentEntry, "docker", "/dir");
        var oldScore = CtrlRSearch.ScoreFuzzyMatch(oldEntry, "docker", "/dir");

        Assert.True(recentScore > oldScore);
    }

    [Fact]
    public void ScoreFuzzyMatch_HigherScoreForExactMatch()
    {
        var entry = new HistoryEntry
        {
            Command = "docker build",
            Cwd = "/dir",
            Timestamp = DateTime.UtcNow,
            SessionId = "test"
        };

        var exactScore = CtrlRSearch.ScoreFuzzyMatch(entry, "docker build", "/dir");
        var partialScore = CtrlRSearch.ScoreFuzzyMatch(entry, "doc", "/dir");

        Assert.True(exactScore > partialScore);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Relative Time Format Tests
// ─────────────────────────────────────────────────────────────────────────────

public class CtrlRSearchTimeTests
{
    [Fact]
    public void FormatRelativeTime_JustNow_ReturnsCorrectFormat()
    {
        var result = CtrlRSearch.FormatRelativeTime(DateTime.UtcNow.AddSeconds(-30));
        Assert.Equal("0m ago", result);
    }

    [Fact]
    public void FormatRelativeTime_MinutesAgo_ReturnsCorrectFormat()
    {
        var result = CtrlRSearch.FormatRelativeTime(DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal("5m ago", result);
    }

    [Fact]
    public void FormatRelativeTime_HoursAgo_ReturnsCorrectFormat()
    {
        var result = CtrlRSearch.FormatRelativeTime(DateTime.UtcNow.AddHours(-3));
        Assert.Equal("3h ago", result);
    }

    [Fact]
    public void FormatRelativeTime_DaysAgo_ReturnsCorrectFormat()
    {
        var result = CtrlRSearch.FormatRelativeTime(DateTime.UtcNow.AddDays(-7));
        Assert.Equal("7d ago", result);
    }

    [Fact]
    public void FormatRelativeTime_WeeksAgo_ReturnsCorrectFormat()
    {
        var result = CtrlRSearch.FormatRelativeTime(DateTime.UtcNow.AddDays(-14));
        Assert.Equal("2w ago", result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Highlight Match Tests
// ─────────────────────────────────────────────────────────────────────────────

public class CtrlRSearchHighlightTests
{
    [Fact]
    public void HighlightMatch_Substring_WrapsInBoldUnderline()
    {
        // Match highlight uses bold+underline so it composes over the
        // selection-row reverse-video and any base/syntax style instead of
        // overriding fg/bg.
        var result = CtrlRSearch.HighlightMatch("docker build", "build");
        Assert.Contains("\x1b[1;4m", result); // bold + underline on
        Assert.Contains("build", result);
        Assert.Contains("\x1b[22;24m", result); // bold + underline off
        Assert.DoesNotContain("\x1b[7m", result); // must not use reverse video
    }

    [Fact]
    public void HighlightMatch_NoMatch_ReturnsOriginal()
    {
        var result = CtrlRSearch.HighlightMatch("docker build", "xyz");
        Assert.Equal("docker build", result);
    }

    [Fact]
    public void HighlightMatch_MultipleMatches_HighlightsAll()
    {
        var result = CtrlRSearch.HighlightMatch("docker build docker", "docker");
        // Count occurrences of reverse video on
        var onCount = CountOccurrences(result, "\x1b[1;4m");
        Assert.Equal(2, onCount);
    }

    [Fact]
    public void HighlightMatch_PreservesNonMatchedParts()
    {
        var result = CtrlRSearch.HighlightMatch("docker build", "build");
        // Should start with "docker " (the part before the match)
        Assert.StartsWith("docker ", result);
        // Should contain the build text somewhere
        Assert.Contains("build", result);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Terminal Size Handling Tests
// ─────────────────────────────────────────────────────────────────────────────

public class CtrlRSearchTerminalTests
{
    [Fact]
    public void TruncateCommand_FitsInWidth_ReturnsOriginal()
    {
        var result = CtrlRSearch.TruncateCommand("docker build", 50, "docker");
        Assert.Equal("docker build", result);
    }

    [Fact]
    public void TruncateCommand_TooLong_TruncatesWithEllipsis()
    {
        var cmd = new string('a', 100);
        var result = CtrlRSearch.TruncateCommand(cmd, 50, "a");
        Assert.True(result.Length <= 53); // 50 + "..."
        Assert.Contains("...", result);
    }

    [Fact]
    public void TruncateCommand_PreservesMatchVisibility()
    {
        var cmd = "prefix " + new string('x', 100) + " match suffix";
        var result = CtrlRSearch.TruncateCommand(cmd, 40, "match");
        // Match should still be visible
        Assert.Contains("match", result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CWD Path Shortening Tests
// ─────────────────────────────────────────────────────────────────────────────

public class CtrlRSearchPathTests
{
    [Fact]
    public void ShortenCwd_HomeDirectory_ReplacesWithTilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = CtrlRSearch.ShortenCwd(home + "/project", home);
        Assert.Equal("~/project", result);
    }

    [Fact]
    public void ShortenCwd_NotHome_ReturnsOriginal()
    {
        var result = CtrlRSearch.ShortenCwd("/tmp/project", "/home/user");
        Assert.Equal("/tmp/project", result);
    }

    [Fact]
    public void ShortenCwd_EmptyHome_ReturnsOriginal()
    {
        var result = CtrlRSearch.ShortenCwd("/tmp/project", "");
        Assert.Equal("/tmp/project", result);
    }
}
