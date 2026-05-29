using Xunit;
using PsBash.Host.Shell;

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

// ─────────────────────────────────────────────────────────────────────────────
// Behavioral Tests (via SimulateAsync / InMemoryHistoryStore)
// Oracle note (Directive 1): these are ps-bash-specific interactive-shell
// behaviors (no bash equivalent), so hand-written asserts are correct.
// ─────────────────────────────────────────────────────────────────────────────

public class CtrlRSearchBehaviorTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    // ConsoleKeyInfo(char keyChar, ConsoleKey key, bool shift, bool alt, bool control)
    // SimulateAsync dispatches on key.Key for control keys and key.KeyChar for printable chars,
    // so ConsoleKey.A is a safe dummy for all printable characters.
    private static ConsoleKeyInfo Key(char ch, ConsoleKey key = ConsoleKey.A)
        => new ConsoleKeyInfo(ch, key, false, false, false);

    private static ConsoleKeyInfo CtrlKey(char ch, ConsoleKey key)
        => new ConsoleKeyInfo(ch, key, false, false, true);

    private static ConsoleKeyInfo Enter()
        => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);

    private static ConsoleKeyInfo Esc()
        => new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false);

    private static ConsoleKeyInfo Tab()
        => new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);

    private static ConsoleKeyInfo CtrlR()
        => CtrlKey('\x12', ConsoleKey.R);

    private static ConsoleKeyInfo CtrlC()
        => CtrlKey('\x03', ConsoleKey.C);

    private static ConsoleKeyInfo CtrlG()
        => CtrlKey('\x07', ConsoleKey.G);

    private static ConsoleKeyInfo Backspace()
        => new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false);

    private static HistoryEntry MakeEntry(string cmd, string cwd = "/proj",
        string session = "s1", int minutesAgo = 1, int? exitCode = 0)
        => new HistoryEntry
        {
            Command = cmd,
            Cwd = cwd,
            SessionId = session,
            Timestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
            ExitCode = exitCode
        };

    private static CtrlRSearch MakeSearch(IHistoryStore store, string cwd = "/proj")
        => new CtrlRSearch(store, cwd, "$ ");

    private static Queue<ConsoleKeyInfo> TypeString(string text,
        IEnumerable<ConsoleKeyInfo>? after = null)
    {
        var q = new Queue<ConsoleKeyInfo>();
        foreach (var ch in text)
            q.Enqueue(Key(ch));
        if (after != null)
            foreach (var k in after)
                q.Enqueue(k);
        return q;
    }

    // ── search results ───────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyQuery_ShowsAllHistoryEntries()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git commit -m 'init'", minutesAgo: 3));
        await store.RecordAsync(MakeEntry("docker build .", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("npm test", minutesAgo: 1));

        using var search = MakeSearch(store);
        // Press Esc immediately — no typing, just check initial result count
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        // After seeding results, all 3 entries should have been loaded
        Assert.Equal(3, search.ResultCount);
    }

    [Fact]
    public async Task SubstringQuery_FiltersToMatchingCommands()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git commit -m 'init'", minutesAgo: 3));
        await store.RecordAsync(MakeEntry("docker build .", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("git push origin main", minutesAgo: 1));

        using var search = MakeSearch(store);
        // Type "git" then Esc to inspect state
        var keys = TypeString("git", new[] { Esc() });
        await search.SimulateAsync(keys);

        // InMemoryHistoryStore does prefix filtering; "git" matches "git commit" and "git push"
        Assert.Equal(2, search.ResultCount);
    }

    [Fact]
    public async Task SubstringQuery_MostRecentMatchIsSelected()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git commit -m 'init'", minutesAgo: 5));
        await store.RecordAsync(MakeEntry("docker build .", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("git push origin main", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = TypeString("git", new[] { Esc() });
        await search.SimulateAsync(keys);

        // Newest match ("git push") should be selected first (highest recency score)
        Assert.Equal("git push origin main", search.SelectedCommand);
    }

    [Fact]
    public async Task NoMatchingQuery_ResultCountIsZero()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("docker build .", minutesAgo: 1));
        await store.RecordAsync(MakeEntry("npm test", minutesAgo: 2));

        using var search = MakeSearch(store);
        var keys = TypeString("xyzzy", new[] { Esc() });
        await search.SimulateAsync(keys);

        Assert.Equal(0, search.ResultCount);
        Assert.Equal(-1, search.SelectedIndex);
        Assert.Null(search.SelectedCommand);
    }

    [Fact]
    public async Task EmptyHistory_ResultCountIsZero()
    {
        var store = new InMemoryHistoryStore();

        using var search = MakeSearch(store);
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        Assert.Equal(0, search.ResultCount);
        Assert.Equal(-1, search.SelectedIndex);
    }

    // ── Enter: execute ───────────────────────────────────────────────────────

    [Fact]
    public async Task Enter_WithMatch_ExecutesSelectedCommand()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("docker build .", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("npm test", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = TypeString("npm", new[] { Enter() });
        var (result, cmd) = await search.SimulateAsync(keys);

        Assert.Equal(CtrlRSearch.Result.Execute, result);
        Assert.Equal("npm test", cmd);
    }

    [Fact]
    public async Task Enter_WithNoMatches_DoesNotExecute()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("npm test", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = TypeString("xyzzy", new[] { Enter() });
        var (result, cmd) = await search.SimulateAsync(keys);

        // No match → Enter is a no-op → keys exhausted → Cancelled
        Assert.Equal(CtrlRSearch.Result.Cancelled, result);
        Assert.Null(cmd);
    }

    // ── Ctrl-R: cycle to next match ──────────────────────────────────────────

    [Fact]
    public async Task CtrlR_CyclesToNextMatch()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git commit -m 'v1'", minutesAgo: 10));
        await store.RecordAsync(MakeEntry("git push origin main", minutesAgo: 5));
        await store.RecordAsync(MakeEntry("git status", minutesAgo: 1));

        using var search = MakeSearch(store);
        // Type "git" → 3 matches, selected=0 (most recent: "git status")
        // Press Ctrl-R → selected=1 (next: "git push origin main")
        // Press Enter → execute that command
        var keys = TypeString("git");
        keys.Enqueue(CtrlR());
        keys.Enqueue(Enter());

        var (result, cmd) = await search.SimulateAsync(keys);

        Assert.Equal(CtrlRSearch.Result.Execute, result);
        // Second-newest "git" command (index 1 after cycling)
        Assert.NotEqual("git status", cmd); // first would be "git status"
        Assert.StartsWith("git", cmd);
    }

    [Fact]
    public async Task CtrlR_WrapsAroundToFirstMatch()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git commit", minutesAgo: 3));
        await store.RecordAsync(MakeEntry("git push", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("git status", minutesAgo: 1));

        using var search = MakeSearch(store);
        // 3 matches: indices 0,1,2. Pressing Ctrl-R 3 times wraps to 0.
        var keys = TypeString("git");
        keys.Enqueue(CtrlR()); // → 1
        keys.Enqueue(CtrlR()); // → 2
        keys.Enqueue(CtrlR()); // → 0 (wrap)
        keys.Enqueue(Esc());

        await search.SimulateAsync(keys);

        Assert.Equal(0, search.SelectedIndex);
    }

    [Fact]
    public async Task CtrlR_WithNoMatches_DoesNotCrash()
    {
        var store = new InMemoryHistoryStore();

        using var search = MakeSearch(store);
        var keys = TypeString("xyzzy");
        keys.Enqueue(CtrlR()); // No-op — no results
        keys.Enqueue(Esc());

        var (result, _) = await search.SimulateAsync(keys);
        Assert.Equal(CtrlRSearch.Result.Cancelled, result);
    }

    // ── Esc: cancel ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Esc_CancelsSearch_ReturnsNullCommand()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("docker build .", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = TypeString("docker", new[] { Esc() });
        var (result, cmd) = await search.SimulateAsync(keys);

        Assert.Equal(CtrlRSearch.Result.Cancelled, result);
        Assert.Null(cmd);
    }

    [Fact]
    public async Task CtrlC_CancelsSearch_ReturnsNullCommand()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("docker build .", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = TypeString("docker", new[] { CtrlC() });
        var (result, cmd) = await search.SimulateAsync(keys);

        Assert.Equal(CtrlRSearch.Result.Cancelled, result);
        Assert.Null(cmd);
    }

    // ── Tab: edit mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task Tab_WithMatch_EntersEditMode_ThenEnterExecutesEdited()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("docker build .", minutesAgo: 1));

        using var search = MakeSearch(store);
        // Tab → edit mode. Then type " --no-cache" and Enter.
        var keys = TypeString("docker");
        keys.Enqueue(Tab()); // Enter edit mode
        // Type suffix to append
        foreach (var ch in " --no-cache")
            keys.Enqueue(Key(ch));
        keys.Enqueue(Enter());

        var (result, cmd) = await search.SimulateAsync(keys);

        Assert.Equal(CtrlRSearch.Result.Execute, result);
        Assert.Equal("docker build . --no-cache", cmd);
    }

    [Fact]
    public async Task Tab_EditMode_EscCancelsEdit_NotSearch()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("npm test", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = TypeString("npm");
        keys.Enqueue(Tab());  // Enter edit mode
        keys.Enqueue(Esc());  // Cancel edit (returns to search mode)
        keys.Enqueue(Esc());  // Cancel search
        var (result, cmd) = await search.SimulateAsync(keys);

        Assert.Equal(CtrlRSearch.Result.Cancelled, result);
        Assert.Null(cmd);
    }

    // ── CWD filter ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CwdFilter_Enabled_OnlyShowsLocalCommands()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git push", cwd: "/proj", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("git push", cwd: "/other", minutesAgo: 1));

        // CWD = /proj: "git push" from /proj is shown, from /other is not
        using var search = MakeSearch(store, cwd: "/proj");
        var keys = TypeString("git", new[] { Esc() });
        await search.SimulateAsync(keys);

        // InMemoryHistoryStore filters by exact cwd, so only 1 result (the /proj entry)
        Assert.Equal(1, search.ResultCount);
    }

    [Fact]
    public async Task CwdFilter_ToggleOff_ShowsAllCommands()
    {
        var store = new InMemoryHistoryStore();
        // Distinct command strings so the toggle's effect is visible without
        // being masked by duplicate-command collapsing.
        await store.RecordAsync(MakeEntry("git push", cwd: "/proj", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("git pull", cwd: "/other", minutesAgo: 1));

        using var search = MakeSearch(store, cwd: "/proj");
        // Ctrl-G toggles CWD filter off
        var keys = TypeString("git");
        keys.Enqueue(CtrlG()); // toggle → all
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        // Both entries (from /proj and /other) should now be visible
        Assert.Equal(2, search.ResultCount);
        Assert.False(search.CwdFilterEnabled);
    }

    [Fact]
    public async Task CwdFilter_ToggleTwice_RestoresState()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("npm test", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(CtrlG()); // off
        keys.Enqueue(CtrlG()); // on
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        Assert.True(search.CwdFilterEnabled);
    }

    // ── Backspace ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backspace_ShortensQuery_BroadensResults()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git commit", minutesAgo: 3));
        await store.RecordAsync(MakeEntry("git push", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("docker build", minutesAgo: 1));

        using var search = MakeSearch(store);
        // Type "git push" → 1 result (prefix match on "git push")
        // Backspace×4 → "git" → 2 results
        var keys = TypeString("git push");
        keys.Enqueue(Backspace()); // "git pus"
        keys.Enqueue(Backspace()); // "git pu"
        keys.Enqueue(Backspace()); // "git p"
        keys.Enqueue(Backspace()); // "git "
        keys.Enqueue(Backspace()); // "git"
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        Assert.Equal("git", search.CurrentQuery);
        Assert.Equal(2, search.ResultCount);
    }

    // ── Initial query (seeded from the current line) ───────────────────────────

    [Fact]
    public async Task InitialQuery_SeedsSearch_AndFiltersImmediately()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git commit", minutesAgo: 3));
        await store.RecordAsync(MakeEntry("git push", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("docker build", minutesAgo: 1));

        // Ctrl-R opened while "git" was on the line → search starts already filtered by "git".
        using var search = new CtrlRSearch(store, "/proj", "$ ", initialQuery: "git");
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        Assert.Equal("git", search.CurrentQuery);
        Assert.Equal(2, search.ResultCount); // "git commit", "git push" — not "docker build"
    }

    [Fact]
    public async Task InitialQuery_BackspaceNarrowsBackFromSeed()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git commit", minutesAgo: 2));
        await store.RecordAsync(MakeEntry("docker build", minutesAgo: 1));

        using var search = new CtrlRSearch(store, "/proj", "$ ", initialQuery: "git c");
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(Backspace()); // "git "
        keys.Enqueue(Backspace()); // "git"
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        Assert.Equal("git", search.CurrentQuery); // edits start from the seed, not blank
    }

    [Fact]
    public async Task InitialQuery_NullOrEmpty_StartsBlank()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("npm test", minutesAgo: 1));

        using var search = MakeSearch(store); // no initialQuery
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        Assert.Equal("", search.CurrentQuery);
    }

    [Fact]
    public async Task Backspace_EmptyQuery_DoesNotCrash()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("npm test", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(Backspace()); // No-op on empty query
        keys.Enqueue(Backspace()); // Also no-op
        keys.Enqueue(Esc());
        var (result, _) = await search.SimulateAsync(keys);

        Assert.Equal(CtrlRSearch.Result.Cancelled, result);
    }

    // ── History persistence ──────────────────────────────────────────────────

    [Fact]
    public async Task Persistence_SecondSearchSeesPriorSessionCommands()
    {
        // Oracle note: SQLite-specific behavior; hand-written assert is correct.
        var dbPath = Path.Combine(Path.GetTempPath(),
            "ctrlr-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Session 1: record commands
            using (var store1 = new SqliteHistoryStore(dbPath))
            {
                await store1.RecordAsync(MakeEntry("kubectl get pods", session: "sess1"));
                await store1.RecordAsync(MakeEntry("kubectl describe pod/web", session: "sess1"));
            }

            // Session 2: new store instance, should still see prior commands
            using var store2 = new SqliteHistoryStore(dbPath);
            using var search = MakeSearch(store2);
            var keys = TypeString("kubectl", new[] { Esc() });
            await search.SimulateAsync(keys);

            Assert.Equal(2, search.ResultCount);
        }
        finally
        {
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(dbPath + ext); } catch { }
            }
        }
    }

    [Fact]
    public async Task Persistence_EmptySecondSession_SeesZeroWhenNoHistory()
    {
        // Sanity: fresh DB with no records returns nothing
        var dbPath = Path.Combine(Path.GetTempPath(),
            "ctrlr-test-empty-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteHistoryStore(dbPath);
            using var search = MakeSearch(store);
            var keys = new Queue<ConsoleKeyInfo>();
            keys.Enqueue(Esc());
            await search.SimulateAsync(keys);

            Assert.Equal(0, search.ResultCount);
        }
        finally
        {
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(dbPath + ext); } catch { }
            }
        }
    }

    // ── Duplicate collapsing ───────────────────────────────────────────────────

    [Fact]
    public async Task DuplicateCommands_CollapseToSingleRow()
    {
        // History holds one row per invocation, so re-running the same command
        // produces many rows. The list must show each command string only once.
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git status", minutesAgo: 5));
        await store.RecordAsync(MakeEntry("git status", minutesAgo: 3));
        await store.RecordAsync(MakeEntry("git status", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = TypeString("git", new[] { Esc() });
        await search.SimulateAsync(keys);

        Assert.Equal(1, search.ResultCount);
        Assert.Equal("git status", search.SelectedCommand);
    }

    [Fact]
    public async Task DuplicateCommands_DistinctCommandsAllSurvive()
    {
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("git status", minutesAgo: 6));
        await store.RecordAsync(MakeEntry("git push", minutesAgo: 5));
        await store.RecordAsync(MakeEntry("git status", minutesAgo: 4));
        await store.RecordAsync(MakeEntry("git push", minutesAgo: 3));
        await store.RecordAsync(MakeEntry("git status", minutesAgo: 1));

        using var search = MakeSearch(store);
        var keys = TypeString("git", new[] { Esc() });
        await search.SimulateAsync(keys);

        // 5 history rows, 2 unique command strings.
        Assert.Equal(2, search.ResultCount);
    }

    [Fact]
    public async Task DuplicateCommands_KeepsBestRankedOccurrence()
    {
        // The surviving row for a collapsed command must be the highest-scoring
        // occurrence (here: the most recent), not an arbitrary one.
        var store = new InMemoryHistoryStore();
        await store.RecordAsync(MakeEntry("npm test", cwd: "/proj", minutesAgo: 120, exitCode: 1));
        await store.RecordAsync(MakeEntry("npm test", cwd: "/proj", minutesAgo: 2, exitCode: 0));

        using var search = MakeSearch(store, cwd: "/proj");
        var keys = TypeString("npm", new[] { Esc() });
        await search.SimulateAsync(keys);

        Assert.Equal(1, search.ResultCount);
        // The kept occurrence is the recent successful one (recency boost wins).
        Assert.Equal(0, search.SelectedEntryExitCode);
    }

    // ── Scoring / ordering ───────────────────────────────────────────────────

    [Fact]
    public async Task Scoring_CwdMatchRanksHigherThanOtherCwd()
    {
        var store = new InMemoryHistoryStore();
        // Two "git status" entries: one from /proj (current), one from /other
        // /other entry is MORE RECENT (minutesAgo=1) so without CWD boost it would rank first
        await store.RecordAsync(MakeEntry("git status", cwd: "/other", minutesAgo: 1));
        await store.RecordAsync(MakeEntry("git status", cwd: "/proj", minutesAgo: 60));

        using var search = MakeSearch(store, cwd: "/proj");
        // Ctrl-G disables CWD filter so both entries are candidates; scoring gives /proj a 50pt boost
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(CtrlG()); // toggle CWD filter OFF so both entries are candidates
        keys.Enqueue(Esc());
        await search.SimulateAsync(keys);

        // Both "git status" rows share the same command string, so they collapse to
        // one row — the best-ranked occurrence survives. The /proj entry wins on the
        // CWD boost (50pt > the ~29pt recency advantage the /other entry has), so the
        // surviving row is the /proj one.
        Assert.Equal(1, search.ResultCount);
        Assert.Equal("git status", search.SelectedCommand);
        Assert.Equal("/proj", search.SelectedEntryCwd);
    }
}
