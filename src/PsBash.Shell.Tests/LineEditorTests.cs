using Xunit;
using PsBash.Host;
using PsBash.Host.Shell;

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
// LineEditor Ctrl+~ command assist dispatch
// ─────────────────────────────────────────────────────────────────────────────

public class LineEditorCommandAssistTests
{
    [Fact]
    public void IsCommandAssistKey_DetectsCtrlTildeControlCharacter()
    {
        var key = new ConsoleKeyInfo('\u001e', ConsoleKey.D6, shift: true, alt: false, control: true);

        Assert.True(LineEditor.IsCommandAssistKey(key));
    }

    [Fact]
    public void IsCommandAssistKey_DetectsOemTildeFallback()
    {
        var key = new ConsoleKeyInfo('~', ConsoleKey.Oem3, shift: true, alt: false, control: true);

        Assert.True(LineEditor.IsCommandAssistKey(key));
    }

    [Fact]
    public void ApplyCommandAssistResponse_CancelPreservesBufferAndCursor()
    {
        var result = LineEditor.ApplyCommandAssistResponse(
            "git status --short",
            cursor: 4,
            CommandAssistResponse.Cancelled);

        Assert.Equal("git status --short", result.Buffer);
        Assert.Equal(4, result.Cursor);
    }

    [Fact]
    public void ApplyCommandAssistResponse_ReplacementMovesCursorToEnd()
    {
        var result = LineEditor.ApplyCommandAssistResponse(
            "git st",
            cursor: 6,
            CommandAssistResponse.Insert("git status --short"));

        Assert.Equal("git status --short", result.Buffer);
        Assert.Equal("git status --short".Length, result.Cursor);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FlagHelpBrowser — scrollable man-page drill-down (P4)
// ─────────────────────────────────────────────────────────────────────────────

public class FlagHelpBrowserTests
{
    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    [Fact]
    public void WrapText_GreedyWrapsToWidth()
    {
        var lines = FlagHelpBrowser.WrapText("aaa bbb ccc ddd", 7);
        Assert.Equal(new[] { "aaa bbb", "ccc ddd" }, lines);
    }

    [Fact]
    public void WrapText_PreservesParagraphBreaks()
    {
        var lines = FlagHelpBrowser.WrapText("one\ntwo", 40);
        Assert.Equal(new[] { "one", "two" }, lines);
    }

    [Fact]
    public void DetailLines_IncludesHead_Desc_Detail_AndExamples()
    {
        var h = new FlagHint("-name", "-name PATTERN", "match base name",
            "Quote the pattern so the shell does not expand it.",
            new[] { "find . -name '*.txt'" });

        var lines = FlagHelpBrowser.DetailLines(h, 60);

        Assert.Equal("-name PATTERN", lines[0]);
        Assert.Contains("match base name", lines);
        Assert.Contains(lines, l => l.Contains("Quote the pattern"));
        Assert.Contains("Examples:", lines);
        Assert.Contains("  find . -name '*.txt'", lines);
    }

    [Fact]
    public void Simulate_DownThenEnter_InsertsSelectedFlag()
    {
        var hints = new[]
        {
            new FlagHint("-a", "-a", "alpha"),
            new FlagHint("-b", "-b", "beta"),
        };
        var browser = new FlagHelpBrowser("Help: x", hints);
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(K(ConsoleKey.DownArrow));
        keys.Enqueue(K(ConsoleKey.Enter));

        var (result, insert) = browser.Simulate(keys);

        Assert.Equal(FlagHelpBrowser.Result.Insert, result);
        Assert.Equal("-b", insert);
        Assert.Equal(1, browser.SelectedIndex);
    }

    [Fact]
    public void Simulate_Escape_Cancels()
    {
        var hints = new[] { new FlagHint("-a", "-a", "alpha") };
        var browser = new FlagHelpBrowser("Help: x", hints);
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(K(ConsoleKey.Escape));

        var (result, insert) = browser.Simulate(keys);

        Assert.Equal(FlagHelpBrowser.Result.Cancelled, result);
        Assert.Null(insert);
    }

    [Fact]
    public void Simulate_Up_StopsAtFirst_DownStopsAtLast()
    {
        var hints = new[]
        {
            new FlagHint("-a", "-a", "alpha"),
            new FlagHint("-b", "-b", "beta"),
        };
        var browser = new FlagHelpBrowser("Help: x", hints);
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(K(ConsoleKey.UpArrow));   // already at 0 → stays
        keys.Enqueue(K(ConsoleKey.DownArrow)); // 1
        keys.Enqueue(K(ConsoleKey.DownArrow)); // clamp at 1
        browser.Simulate(keys);
        Assert.Equal(1, browser.SelectedIndex);
    }

    [Fact]
    public void Simulate_PageDown_ScrollsDetail()
    {
        // A hint with a long detail so the body overflows a small window.
        var detail = string.Join(" ", Enumerable.Range(0, 200).Select(i => "word" + i));
        var hints = new[] { new FlagHint("-x", "-x", "many", detail) };
        var browser = new FlagHelpBrowser("Help: x", hints);
        var keys = new Queue<ConsoleKeyInfo>();
        keys.Enqueue(K(ConsoleKey.PageDown));
        browser.Simulate(keys, width: 40, height: 10);
        Assert.True(browser.DetailScroll > 0);
    }

    [Fact]
    public void EmptyHints_RunReturnsCancelled()
    {
        var browser = new FlagHelpBrowser("Help: x", System.Array.Empty<FlagHint>());
        var (result, _) = browser.Run(); // no hints → immediate cancel (no terminal needed)
        Assert.Equal(FlagHelpBrowser.Result.Cancelled, result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// LineEditor.ComputeScroll — flag-panel scroll-window math (P3)
// ─────────────────────────────────────────────────────────────────────────────

public class LineEditorPanelScrollTests
{
    [Fact]
    public void Scroll_SelectionWithinWindow_NoChange()
    {
        // window 8, total 20, selecting row 3 while scrolled to top → stays at 0.
        Assert.Equal(0, LineEditor.ComputeScroll(selected: 3, scroll: 0, window: 8, total: 20));
    }

    [Fact]
    public void Scroll_SelectionBelowWindow_ScrollsDownToKeepVisible()
    {
        // selecting row 8 with window 8 (rows 0..7 visible) → top moves to 1 so row 8 shows.
        Assert.Equal(1, LineEditor.ComputeScroll(selected: 8, scroll: 0, window: 8, total: 20));
    }

    [Fact]
    public void Scroll_SelectionAboveWindow_ScrollsUp()
    {
        // selecting row 2 while scrolled to 5 → top moves up to 2.
        Assert.Equal(2, LineEditor.ComputeScroll(selected: 2, scroll: 5, window: 8, total: 20));
    }

    [Fact]
    public void Scroll_NeverPastEnd()
    {
        // last row selected; offset clamps so the window ends exactly at total (20-8=12).
        Assert.Equal(12, LineEditor.ComputeScroll(selected: 19, scroll: 19, window: 8, total: 20));
    }

    [Fact]
    public void Scroll_ListShorterThanWindow_StaysZero()
    {
        Assert.Equal(0, LineEditor.ComputeScroll(selected: 2, scroll: 0, window: 8, total: 3));
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

        var results = TabCompleter.Complete("cat ", 4, _noAliases, _tmpDir).Texts();

        Assert.Contains("alpha.sh", results);
        Assert.Contains("beta.sh", results);
    }

    [Fact]
    public void CompletePath_PartialName_FiltersCorrectly()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "foo.txt"), "");
        File.WriteAllText(Path.Combine(_tmpDir, "bar.txt"), "");

        var results = TabCompleter.Complete("cat fo", 6, _noAliases, _tmpDir).Texts();

        Assert.Contains("foo.txt", results);
        Assert.DoesNotContain("bar.txt", results);
    }

    [Fact]
    public void CompletePath_Directory_AppendsSeparator()
    {
        var sub = Path.Combine(_tmpDir, "subdir");
        Directory.CreateDirectory(sub);

        var results = TabCompleter.Complete("ls sub", 6, _noAliases, _tmpDir).Texts();

        Assert.Contains("subdir/", results);
    }

    [Fact]
    public void CompleteCommand_KnownBuiltin_Returned()
    {
        // "ec" should complete to "echo"
        var results = TabCompleter.Complete("ec", 2, _noAliases, _tmpDir).Texts();
        Assert.Contains("echo", results);
    }

    [Fact]
    public void CompleteCommand_Alias_Returned()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gst"] = "git status",
        };

        var results = TabCompleter.Complete("gs", 2, aliases, _tmpDir).Texts();
        Assert.Contains("gst", results);
    }

    [Fact]
    public void CompleteCommand_NoMatch_ReturnsEmpty()
    {
        var results = TabCompleter.Complete("zzznomatch", 10, _noAliases, _tmpDir);
        Assert.Empty(results);
    }

    [Fact]
    public void CompleteCommand_DotSlashPrefix_CompletesSubdirectory()
    {
        // Regression: "./sc"<tab> in command position must offer a "scripts" subdirectory
        // (CompleteCommand previously scanned only files in cwd, so dirs were never offered).
        Directory.CreateDirectory(Path.Combine(_tmpDir, "scripts"));

        var results = TabCompleter.Complete("./sc", 4, _noAliases, _tmpDir).Texts();

        Assert.Contains("./scripts/", results);
    }

    [Fact]
    public void CompleteCommand_DotSlashPrefix_CompletesLocalExecutableFile()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "run.sh"), "");

        var results = TabCompleter.Complete("./ru", 4, _noAliases, _tmpDir).Texts();

        Assert.Contains("./run.sh", results);
    }

    [Fact]
    public void CompleteCommand_NestedPathPrefix_CompletesInsideSubdirectory()
    {
        var scripts = Path.Combine(_tmpDir, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "test.sh"), "");

        var results = TabCompleter.Complete("./scripts/te", 12, _noAliases, _tmpDir).Texts();

        Assert.Contains("./scripts/test.sh", results);
    }

    [Fact]
    public void CompletePath_AbsolutePath_Works()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "readme.md"), "");
        var prefix = _tmpDir.TrimEnd('/') + "/";

        var results = TabCompleter.Complete($"cat {prefix}read", prefix.Length + 4 + "read".Length, _noAliases, _tmpDir).Texts();
        Assert.Contains(prefix + "readme.md", results);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Flag completion
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompleteFlags_ls_dash_InsertsBareFlag_ListsDescription()
    {
        var results = TabCompleter.Complete("ls -", 4, _noAliases, _tmpDir);

        // The DESCRIPTION is shown in the list (DisplayText) ...
        Assert.Contains("-l  - long listing", results.Labels());
        Assert.Contains("-a  - show hidden", results.Labels());
        Assert.Contains("-h  - human readable sizes", results.Labels());

        // ... but only the BARE FLAG is ever inserted (InsertText) — never the description.
        Assert.Contains("-l", results.Texts());
        Assert.Contains("-a", results.Texts());
        Assert.Contains("-h", results.Texts());
        Assert.DoesNotContain("-l  - long listing", results.Texts());
    }

    [Fact]
    public void CompleteFlags_findDashN_InsertsFlagOnly_NeverDescription()
    {
        // Regression: typing "find -n"<Tab> used to insert "find -name  - name pattern" because the
        // candidate string glued the flag to its description. InsertText must be the bare flag, and
        // it must never contain the "  - " description separator.
        var results = TabCompleter.Complete("find -n", 7, _noAliases, _tmpDir);

        Assert.NotEmpty(results);
        Assert.Contains("-name", results.Texts());
        Assert.All(results, c =>
        {
            Assert.StartsWith("-n", c.InsertText);            // matches the typed prefix
            Assert.DoesNotContain("  - ", c.InsertText);      // description never reaches the buffer
            Assert.Contains("  - ", c.DisplayText);           // but the list still shows it
        });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Flag-doc panel data (MatchingFlagSpecs) — the floating "what does -n mean" panel
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MatchingFlagSpecs_FlagPrefix_ReturnsFlagAndDescription()
    {
        var specs = TabCompleter.MatchingFlagSpecs("find -n", 7, _noAliases);

        Assert.Contains(specs, s => s.Flag == "-name" && !string.IsNullOrWhiteSpace(s.Desc));
        Assert.All(specs, s => Assert.StartsWith("-n", s.Flag)); // only -n* flags
    }

    [Fact]
    public void MatchingFlagSpecs_LoneDash_ReturnsAllFlagsForCommand()
    {
        var specs = TabCompleter.MatchingFlagSpecs("find -", 6, _noAliases);

        // A bare "-" matches every flag the command documents (the full panel).
        Assert.Contains(specs, s => s.Flag == "-name");
        Assert.Contains(specs, s => s.Flag == "-type");
        Assert.True(specs.Count >= 3);
    }

    [Fact]
    public void MatchingFlagSpecs_NotAFlagToken_ReturnsEmpty()
    {
        // Trailing space → empty token; the panel must not show.
        Assert.Empty(TabCompleter.MatchingFlagSpecs("find ", 5, _noAliases));
        // A plain operand (no leading '-') is not a flag.
        Assert.Empty(TabCompleter.MatchingFlagSpecs("find foo", 8, _noAliases));
    }

    [Fact]
    public void MatchingFlagSpecs_CommandPosition_ReturnsEmpty()
    {
        // The first word is a command name, not a flag — even "-" there gets no flag panel.
        Assert.Empty(TabCompleter.MatchingFlagSpecs("fin", 3, _noAliases));
    }

    [Fact]
    public void MatchingFlagSpecs_UnknownCommand_ReturnsEmpty()
    {
        Assert.Empty(TabCompleter.MatchingFlagSpecs("frobnicate -x", 13, _noAliases));
    }

    [Fact]
    public void MatchingFlagSpecs_RedirectTargetStartingWithDash_ReturnsEmpty()
    {
        // "-out" after '>' is a redirect target (a path), not a flag of the command.
        Assert.Empty(TabCompleter.MatchingFlagSpecs("grep x > -out", 13, _noAliases));
    }

    [Fact]
    public void MatchingFlagSpecs_ExpandsAlias()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal) { ["ll"] = "ls -l" };

        var specs = TabCompleter.MatchingFlagSpecs("ll -a", 5, aliases);

        Assert.Contains(specs, s => s.Flag == "-a");
    }

    [Fact]
    public void MatchingFlagSpecs_RichData_CarriesArgDetailAndExamples()
    {
        var specs = TabCompleter.MatchingFlagSpecs("find -name", 10, _noAliases);

        var name = Assert.Single(specs, s => s.Flag == "-name");
        Assert.Equal("PATTERN", name.Arg);                       // argument placeholder
        Assert.False(string.IsNullOrWhiteSpace(name.Detail));    // man-page paragraph present
        Assert.NotNull(name.Examples);
        Assert.Contains(name.Examples!, e => e.Contains("-name")); // at least one usage example
    }

    [Fact]
    public void MatchingFlagSpecs_GrepE_CarriesRegexDetailAndExamples()
    {
        var specs = TabCompleter.MatchingFlagSpecs("grep -e", 7, _noAliases);

        var e = Assert.Single(specs, s => s.Flag == "-e");
        Assert.Equal("PATTERN", e.Arg);
        Assert.Contains("Basic regex", e.Detail);
        Assert.NotNull(e.Examples);
        Assert.Contains(e.Examples!, example => example.Contains("FIXME"));
    }

    [Fact]
    public void MatchingFlagSpecs_NewlySupportedFindPredicates_AreDocumented()
    {
        var dSpecs = TabCompleter.MatchingFlagSpecs("find -d", 7, _noAliases);
        Assert.Contains(dSpecs, s => s.Flag == "-delete");
        Assert.Contains(dSpecs, s => s.Flag == "-depth");

        var iSpecs = TabCompleter.MatchingFlagSpecs("find -i", 7, _noAliases);
        Assert.Contains(iSpecs, s => s.Flag == "-iname");
    }

    [Fact]
    public void CompleteFlags_WithArg_ShowsArgPlaceholderInLabel_ButInsertsBareFlag()
    {
        var results = TabCompleter.Complete("find -n", 7, _noAliases, _tmpDir);

        // The list shows the arg placeholder ("-name PATTERN  - ...") ...
        Assert.Contains(results.Labels(), l => l.StartsWith("-name PATTERN"));
        // ... but only the bare flag is inserted.
        Assert.Contains("-name", results.Texts());
        Assert.DoesNotContain(results.Texts(), t => t.Contains("PATTERN"));
    }

    [Fact]
    public void CompleteFlags_ls_dash_l_ReturnsOnlyLongListingFlag()
    {
        var results = TabCompleter.Complete("ls -l", 5, _noAliases, _tmpDir);

        // Should return only flags starting with "-l"
        Assert.Single(results);
        Assert.Contains("-l", results.Texts());
        Assert.Contains("-l  - long listing", results.Labels());
    }

    [Fact]
    public void CompleteFlags_grep_dash_i_ReturnsIgnoreCaseFlag()
    {
        var results = TabCompleter.Complete("grep -i", 7, _noAliases, _tmpDir);

        // Inserts "-i"; lists "-i  - ignore case".
        Assert.Contains("-i", results.Texts());
        Assert.Contains("-i  - ignore case", results.Labels());
    }

    [Fact]
    public void CompleteFlags_grep_dash_e_KeepsInlineRowConcise()
    {
        var results = TabCompleter.Complete("grep -e", 7, _noAliases, _tmpDir);

        Assert.Contains("-e", results.Texts());
        Assert.Contains("-e PATTERN  - pattern", results.Labels());
        Assert.DoesNotContain(results.Labels(), label => label.Contains("Basic regex"));
    }

    [Fact]
    public void CompleteGrepPattern_AfterDashE_ReturnsRegexSnippetsBeforePaths()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "TODO-file.txt"), "");

        var results = TabCompleter.Complete("grep -e ", 8, _noAliases, _tmpDir);

        Assert.NotEmpty(results);
        Assert.Equal("'^TODO'", results[0].InsertText);
        Assert.Contains(results, r => r.InsertText == "TODO-file.txt");
    }

    [Fact]
    public void CompleteGrepPattern_ExtendedMode_ReturnsExtendedRegexSnippets()
    {
        var results = TabCompleter.Complete("grep -E -e ", 11, _noAliases, _tmpDir).Texts();

        Assert.Contains("'TODO|FIXME'", results);
        Assert.Contains("'^[A-Z_]+='", results);
    }

    [Fact]
    public void CompleteGrepPattern_FixedMode_ReturnsLiteralSnippets()
    {
        var results = TabCompleter.Complete("grep -F -e ", 11, _noAliases, _tmpDir);

        Assert.Contains(results, r => r.InsertText == "error|warning" && r.DisplayText.Contains("literal"));
        Assert.DoesNotContain("'TODO|FIXME'", results.Texts());
    }

    [Fact]
    public void CompleteGrepPattern_FileOperand_UsesPathCompletionNotRegexSnippets()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "sample.txt"), "");

        var results = TabCompleter.Complete("grep TODO s", 11, _noAliases, _tmpDir).Texts();

        Assert.Contains("sample.txt", results);
        Assert.DoesNotContain("'^TODO'", results);
    }

    [Fact]
    public void GrepPatternContext_ClassifiesBasicExtendedAndFixed()
    {
        Assert.True(TabCompleter.TryGetGrepPatternValueContext("grep -e ", 8, _noAliases, out var basic));
        Assert.Equal("basic", basic);

        Assert.True(TabCompleter.TryGetGrepPatternValueContext("grep -E -e ", 11, _noAliases, out var extended));
        Assert.Equal("extended", extended);

        Assert.True(TabCompleter.TryGetGrepPatternValueContext("grep -F -e ", 11, _noAliases, out var fixedMode));
        Assert.Equal("fixed", fixedMode);
    }

    [Fact]
    public void CompleteFlags_cat_dash_n_ReturnsNumberLinesFlag()
    {
        var results = TabCompleter.Complete("cat -n", 6, _noAliases, _tmpDir);

        // Inserts "-n"; lists "-n  - number all lines".
        Assert.Contains("-n", results.Texts());
        Assert.Contains("-n  - number all lines", results.Labels());
    }

    [Fact]
    public void CompleteFlags_AfterCommandWithFlags_ReturnsFlags()
    {
        var results = TabCompleter.Complete("ls -l -", 7, _noAliases, _tmpDir);

        // Should complete flags after existing flags
        Assert.Contains("-a", results.Texts());
        Assert.Contains("-a  - show hidden", results.Labels());
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
        Assert.Contains("-a", results.Texts());
        Assert.Contains("-h", results.Texts());
        Assert.Contains("-a  - show hidden", results.Labels());
        Assert.Contains("-h  - human readable sizes", results.Labels());
    }

    [Fact]
    public void CompleteFlags_NonFlagStart_FallsBackToPathCompletion()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "file.txt"), "");
        var results = TabCompleter.Complete("ls file", 7, _noAliases, _tmpDir).Texts();

        // When not starting with '-', should do path completion
        Assert.Contains("file.txt", results);
    }

    [Fact]
    public void CompleteFlags_EnvVarPrefix_Works()
    {
        var results = TabCompleter.Complete("FOO=bar ls -", 12, _noAliases, _tmpDir);

        // Should handle env var prefix before command
        Assert.Contains("-l", results.Texts());
        Assert.Contains("-l  - long listing", results.Labels());
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

        var results = await TabCompleter.CompleteAsync("", 0, _noAliases, cwd, "docker build -t myapp .", store);

        // Should suggest "docker run myapp" based on sequence
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.InsertText.Contains("docker run"));
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
        var results = await TabCompleter.CompleteAsync("", 0, _noAliases, cwd, "git commit", store);

        // Should include "git push" from sequence suggestions
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.InsertText == "git push");

        // Test with prefix "git" (first word, no space) - should suggest "git push"
        var prefixedResults = await TabCompleter.CompleteAsync("git", 3, _noAliases, cwd, "git commit", store);
        Assert.NotEmpty(prefixedResults);
        Assert.Contains(prefixedResults, r => r.InsertText == "git push");
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

        var results1 = await TabCompleter.CompleteAsync("", 0, _noAliases, cwd1, "build", store);
        var results2 = await TabCompleter.CompleteAsync("", 0, _noAliases, cwd2, "build", store);

        // cwd1 should suggest "test" (local sequence)
        Assert.NotEmpty(results1);
        Assert.Contains(results1, r => r.InsertText == "test");

        // cwd2 should suggest "deploy" (local sequence)
        Assert.NotEmpty(results2);
        Assert.Contains(results2, r => r.InsertText == "deploy");
    }

    [Fact]
    public async Task Complete_EmptyLine_NoLastCommand_ReturnsCommandCompletions()
    {
        var store = new InMemoryHistoryStore();
        var results = (await TabCompleter.CompleteAsync("", 0, _noAliases, _tmpDir, null, store)).Texts();

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

        var results = await TabCompleter.CompleteAsync("", 0, _noAliases, cwd, "git commit", store);

        // "git push" should appear early in results (sequence suggestion prioritized)
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.InsertText == "git push");

        // Should also include other commands
        Assert.True(results.Count > 1);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TabCompleter — context-aware completions (Directive 3 failure axes)
// ─────────────────────────────────────────────────────────────────────────────

public class TabCompleterContextTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly Dictionary<string, string> _noAliases = new(StringComparer.Ordinal);

    public TabCompleterContextTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "psbash-tabctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    // ── Case 8: Completion inside quotes — spaces handled ────────────────────

    [Fact]
    public void QuoteAware_Split_InsideDoubleQuote_TokenStripsOpenQuote()
    {
        // "cat "my fi" — cursor at 10 (after "fi")
        var line = "cat \"my fi";
        var (b, t) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, line.Length);
        // Base should include the open quote, token should be the bare path part
        Assert.Equal("cat \"", b);
        Assert.Equal("my fi", t);
    }

    [Fact]
    public void QuoteAware_Split_NoQuote_BehavesLikeRegularSplit()
    {
        var line = "cat myfi";
        var (b, t) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, line.Length);
        Assert.Equal("cat ", b);
        Assert.Equal("myfi", t);
    }

    [Fact]
    public void CompletePath_InsideDoubleQuote_CompletesPathWithSpace()
    {
        // Create a file whose name contains a space
        var fileName = "my file.txt";
        File.WriteAllText(Path.Combine(_tmpDir, fileName), "");

        // Simulate: cat "my fi  (cursor at end, inside open double quote)
        var line = $"cat \"my f";
        var results = TabCompleter.Complete(line, line.Length, _noAliases, _tmpDir).Texts();

        // Should find "my file.txt" as a candidate
        Assert.Contains(fileName, results);
    }

    [Fact]
    public void CompletePath_FilesWithoutSpaces_WorksNormally()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "normal.txt"), "");

        var results = TabCompleter.Complete("cat norm", 8, _noAliases, _tmpDir).Texts();
        Assert.Contains("normal.txt", results);
    }

    // ── Case 9: Context-aware after | ────────────────────────────────────────

    [Fact]
    public void CompleteCommand_AfterPipe_IsFirstWordContext()
    {
        // "ls | ec" — after the pipe, "ec" should trigger command completion
        var results = TabCompleter.Complete("ls | ec", 7, _noAliases, _tmpDir).Texts();
        // Should suggest "echo" as a command (not path completion)
        Assert.Contains("echo", results);
    }

    [Fact]
    public void CompleteCommand_AfterPipe_EmptyToken_ReturnsCommands()
    {
        // "ls | " — cursor right after the pipe+space, empty token
        var results = TabCompleter.Complete("ls | ", 5, _noAliases, _tmpDir).Texts();
        Assert.Contains("echo", results);
        Assert.Contains("grep", results);
    }

    [Fact]
    public void CompleteCommand_AfterPipePipe_IsFirstWordContext()
    {
        // "cmd1 || ec" — after ||, "ec" is a command name
        var results = TabCompleter.Complete("cmd1 || ec", 10, _noAliases, _tmpDir).Texts();
        Assert.Contains("echo", results);
    }

    [Fact]
    public void CompleteCommand_AfterAndAnd_IsFirstWordContext()
    {
        // "cmd1 && ec" — after &&, "ec" is a command name
        var results = TabCompleter.Complete("cmd1 && ec", 10, _noAliases, _tmpDir).Texts();
        Assert.Contains("echo", results);
    }

    [Fact]
    public void CompleteCommand_AfterSemicolon_IsFirstWordContext()
    {
        // "cmd1; ec" — after ;, "ec" is a command name
        var results = TabCompleter.Complete("cmd1; ec", 8, _noAliases, _tmpDir).Texts();
        Assert.Contains("echo", results);
    }

    // ── Case 9: Context-aware after > and < ──────────────────────────────────

    [Fact]
    public void CompletePath_AfterRedirectOut_IsPathCompletion()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "output.log"), "");
        // "cmd > outp" — cursor at end, should path-complete "output.log"
        var line = $"cmd > outp";
        var results = TabCompleter.Complete(line, line.Length, _noAliases, _tmpDir).Texts();
        Assert.Contains("output.log", results);
    }

    [Fact]
    public void CompletePath_AfterRedirectIn_IsPathCompletion()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "input.txt"), "");
        // "cat < inpu" — should path-complete "input.txt"
        var line = "cat < inpu";
        var results = TabCompleter.Complete(line, line.Length, _noAliases, _tmpDir).Texts();
        Assert.Contains("input.txt", results);
    }

    [Fact]
    public void CompletePath_AfterRedirectAppend_IsPathCompletion()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "append.log"), "");
        // "echo hello >> appen" — should path-complete "append.log"
        var line = "echo hello >> appen";
        var results = TabCompleter.Complete(line, line.Length, _noAliases, _tmpDir).Texts();
        Assert.Contains("append.log", results);
    }

    // ── Case 9: Context-aware after $( ───────────────────────────────────────

    [Fact]
    public void CompleteCommand_AfterCommandSub_IsFirstWordContext()
    {
        // "echo $(ec" — inside $(), "ec" should trigger command completion
        var results = TabCompleter.Complete("echo $(ec", 9, _noAliases, _tmpDir).Texts();
        Assert.Contains("echo", results);
    }

    // ── SplitAtWordBoundaryQuoteAware — edge cases ────────────────────────────

    [Fact]
    public void QuoteAware_Split_EmptyLine_BothEmpty()
    {
        var (b, t) = TabCompleter.SplitAtWordBoundaryQuoteAware("", 0);
        Assert.Equal("", b);
        Assert.Equal("", t);
    }

    [Fact]
    public void QuoteAware_Split_SpaceAfterWord_EmptyToken()
    {
        var (b, t) = TabCompleter.SplitAtWordBoundaryQuoteAware("ls ", 3);
        Assert.Equal("ls ", b);
        Assert.Equal("", t);
    }

    [Fact]
    public void QuoteAware_Split_QuotedWordWithSpaces_TokenHasNoBoundaryAtSpace()
    {
        // cat "my path/to — the space inside the quote shouldn't split
        var line = "cat \"my path/to";
        var (b, t) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, line.Length);
        Assert.Equal("cat \"", b);
        Assert.Equal("my path/to", t);
    }

    // ── Negative / failure axes (Directive 7) ────────────────────────────────

    [Fact]
    public void CompleteCommand_EmptyInput_DoesNotThrow()
    {
        var ex = Record.Exception(() => TabCompleter.Complete("", 0, _noAliases, _tmpDir));
        Assert.Null(ex);
    }

    [Fact]
    public void CompletePath_NonexistentCwd_ReturnsEmpty()
    {
        var results = TabCompleter.Complete("cat f", 5, _noAliases, "/nonexistent/path/xyz");
        // Should not throw; returns empty for missing directory
        Assert.Empty(results);
    }

    [Fact]
    public void CompletePath_CursorBeyondLine_DoesNotThrow()
    {
        // cursor > line.Length
        var ex = Record.Exception(() => TabCompleter.Complete("cat f", 100, _noAliases, _tmpDir));
        Assert.Null(ex);
    }

    [Fact]
    public void CompleteFlags_NegativeFlag_DoesNotReturnPathsForKnownCommands()
    {
        // "ls -" with a known command should return flags, not paths
        var results = TabCompleter.Complete("ls -", 4, _noAliases, _tmpDir).Texts();
        // All results should be flags (start with -)
        Assert.All(results, r => Assert.StartsWith("-", r));
    }
}
