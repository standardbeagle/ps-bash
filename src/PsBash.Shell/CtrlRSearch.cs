using System.Text;
using System.Runtime.InteropServices;

namespace PsBash.Shell;

/// <summary>
/// Full-screen Ctrl-R reverse-i-search UI with fuzzy matching and metadata display.
/// Uses VT100 alternate screen buffer for overlay rendering.
/// </summary>
public sealed class CtrlRSearch : IDisposable
{
    // ── VT100 Escape Sequences ────────────────────────────────────────────────────
    private const string EnterAltScreen = "\x1b[?1049h";
    private const string ExitAltScreen = "\x1b[?1049l";
    private const string SaveCursor = "\x1b[s";
    private const string RestoreCursor = "\x1b[u";
    private const string HideCursor = "\x1b[?25l";
    private const string ShowCursor = "\x1b[?25h";
    private const string ClearScreen = "\x1b[2J";
    private const string MoveCursorHome = "\x1b[H";
    private const string ClearLine = "\x1b[2K";
    private const string ResetAttributes = "\x1b[0m";
    private const string ReverseVideoOn = "\x1b[7m";
    private const string ReverseVideoOff = "\x1b[27m";
    private const string Cyan = "\x1b[36m";
    private const string Gray = "\x1b[90m";
    private const string Green = "\x1b[32m";
    private const string Red = "\x1b[31m";
    private const string BoldWhite = "\x1b[1;37m";

    // ── Scoring Constants ───────────────────────────────────────────────────────────
    private const double CwdBoostPoints = 50.0;
    private const double MaxRecencyPoints = 30.0;
    private const double MaxFrequencyPoints = 20.0;
    private const double RecencyDecayHours = 24.0; // 30 hours = 0 boost
    private const int FrequencyCountForMax = 10; // 10 occurrences = max boost

    // ── State ───────────────────────────────────────────────────────────────────────
    private readonly IHistoryStore _historyStore;
    private readonly string _homeDir;
    private readonly string _currentCwd;
    private readonly string _originalPrompt;

    private StringBuilder _query = new();
    private List<ScoredEntry> _results = new();
    private int _selectedIndex = 0;
    private bool _cwdFilterEnabled = true;
    private bool _inEditMode = false;
    private int _editCursor = 0;
    private string? _editBuffer = null;
    private bool _disposed = false;
    private int _terminalWidth = 80;
    private int _terminalHeight = 24;

    // ── Result tracking for frequency boost ───────────────────────────────────────────
    private Dictionary<string, int> _commandFrequencies = new();

    /// <summary>
    /// Result of running Ctrl-R search.
    /// </summary>
    public enum Result
    {
        /// <summary>User cancelled with Esc or Ctrl-G</summary>
        Cancelled,
        /// <summary>User selected a command to execute</summary>
        Execute,
        /// <summary>User entered edit mode to modify the command</summary>
        Edit
    }

    private record ScoredEntry(HistoryEntry Entry, double Score);

    /// <summary>
    /// Creates a new Ctrl-R search UI.
    /// </summary>
    public CtrlRSearch(
        IHistoryStore historyStore,
        string cwd,
        string prompt,
        string? homeDir = null)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _currentCwd = cwd;
        _originalPrompt = prompt;
        _homeDir = homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// Runs the Ctrl-R search UI interactively.
    /// </summary>
    /// <returns>
    /// Tuple of (Result, commandOrNull). Command is null if cancelled.
    /// </returns>
    public async Task<(Result Result, string? Command)> RunAsync()
    {
        if (Console.IsInputRedirected)
        {
            return (Result.Cancelled, null);
        }

        // Check terminal size
        UpdateTerminalSize();
        if (_terminalHeight < 10)
        {
            Console.Error.WriteLine("Terminal too small for Ctrl-R (min 10 rows required)");
            return (Result.Cancelled, null);
        }

        // Enter alternate screen
        EnterAlternateScreen();

        try
        {
            // Initial search (empty query shows recent commands)
            await UpdateResultsAsync();
            Render();

            // Main input loop
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (_inEditMode)
                {
                    var editResult = HandleEditModeKey(key);
                    if (editResult.HasValue)
                    {
                        ExitAlternateScreen();
                        return editResult.Value;
                    }
                    Render();
                    continue;
                }

                // Normal mode
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        // Esc or Ctrl-G (Ctrl-G sends Esc in some terminals)
                        ExitAlternateScreen();
                        return (Result.Cancelled, null);

                    case ConsoleKey.Enter:
                        // Execute selected command
                        if (_results.Count > 0 && _selectedIndex < _results.Count)
                        {
                            var cmd = _results[_selectedIndex].Entry.Command;
                            ExitAlternateScreen();
                            return (Result.Execute, cmd);
                        }
                        break;

                    case ConsoleKey.Tab:
                        // Enter edit mode
                        if (_results.Count > 0 && _selectedIndex < _results.Count)
                        {
                            EnterEditMode();
                            Render();
                        }
                        break;

                    case ConsoleKey.UpArrow:
                        if (key.Modifiers == 0)
                        {
                            MoveSelection(-1);
                            Render();
                        }
                        break;

                    case ConsoleKey.DownArrow:
                        if (key.Modifiers == 0)
                        {
                            MoveSelection(1);
                            Render();
                        }
                        break;

                    case ConsoleKey.PageUp:
                        MoveSelection(-(_terminalHeight - 4));
                        Render();
                        break;

                    case ConsoleKey.PageDown:
                        MoveSelection(_terminalHeight - 4);
                        Render();
                        break;

                    case ConsoleKey.Home:
                        _selectedIndex = 0;
                        Render();
                        break;

                    case ConsoleKey.End:
                        _selectedIndex = Math.Max(0, _results.Count - 1);
                        Render();
                        break;

                    case ConsoleKey.R when key.Modifiers == ConsoleModifiers.Control:
                        // Ctrl-R: cycle to next match (wrap around)
                        if (_results.Count > 0)
                        {
                            _selectedIndex = (_selectedIndex + 1) % _results.Count;
                            Render();
                        }
                        break;

                    case ConsoleKey.S when key.Modifiers == ConsoleModifiers.Control:
                        // Ctrl-S: cycle to previous match
                        if (_results.Count > 0)
                        {
                            _selectedIndex = (_selectedIndex - 1 + _results.Count) % _results.Count;
                            Render();
                        }
                        break;

                    case ConsoleKey.G when key.Modifiers == ConsoleModifiers.Control:
                        // Ctrl-G: toggle CWD filter
                        _cwdFilterEnabled = !_cwdFilterEnabled;
                        await UpdateResultsAsync();
                        _selectedIndex = 0;
                        Render();
                        break;

                    case ConsoleKey.Backspace:
                        if (_query.Length > 0)
                        {
                            _query.Remove(_query.Length - 1, 1);
                            await UpdateResultsAsync();
                            _selectedIndex = 0;
                            Render();
                        }
                        break;

                    case ConsoleKey.C when key.Modifiers == ConsoleModifiers.Control:
                        // Ctrl-C: cancel
                        ExitAlternateScreen();
                        Console.WriteLine("^C");
                        return (Result.Cancelled, null);

                    default:
                        // Printable character
                        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                        {
                            _query.Append(key.KeyChar);
                            await UpdateResultsAsync();
                            _selectedIndex = 0;
                            Render();
                        }
                        break;
                }
            }
        }
        finally
        {
            ExitAlternateScreen();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Edit Mode
    // ─────────────────────────────────────────────────────────────────────────────────

    private void EnterEditMode()
    {
        if (_results.Count == 0 || _selectedIndex >= _results.Count)
            return;

        _inEditMode = true;
        _editBuffer = _results[_selectedIndex].Entry.Command;
        _editCursor = _editBuffer.Length;
    }

    private (Result, string?)? HandleEditModeKey(ConsoleKeyInfo key)
    {
        if (_editBuffer == null)
            return null;

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                var cmd = _editBuffer;
                _editBuffer = null;
                _inEditMode = false;
                return (Result.Execute, cmd);

            case ConsoleKey.Escape:
                // Cancel edit, return to selection mode
                _editBuffer = null;
                _inEditMode = false;
                return null;

            case ConsoleKey.Backspace:
                if (_editCursor > 0)
                {
                    _editBuffer = _editBuffer.Remove(_editCursor - 1, 1);
                    _editCursor--;
                }
                return null;

            case ConsoleKey.Delete:
                if (_editCursor < _editBuffer.Length)
                {
                    _editBuffer = _editBuffer.Remove(_editCursor, 1);
                }
                return null;

            case ConsoleKey.LeftArrow:
                _editCursor = Math.Max(0, _editCursor - 1);
                return null;

            case ConsoleKey.RightArrow:
                _editCursor = Math.Min(_editBuffer.Length, _editCursor + 1);
                return null;

            case ConsoleKey.Home:
            case ConsoleKey.A when key.Modifiers == ConsoleModifiers.Control:
                _editCursor = 0;
                return null;

            case ConsoleKey.End:
            case ConsoleKey.E when key.Modifiers == ConsoleModifiers.Control:
                _editCursor = _editBuffer.Length;
                return null;

            case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                _editBuffer = _editBuffer.Substring(0, _editCursor);
                return null;

            case ConsoleKey.U when key.Modifiers == ConsoleModifiers.Control:
                _editBuffer = _editBuffer.Substring(_editCursor);
                _editCursor = 0;
                return null;

            default:
                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _editBuffer = _editBuffer.Insert(_editCursor, key.KeyChar.ToString());
                    _editCursor++;
                }
                return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Navigation
    // ─────────────────────────────────────────────────────────────────────────────────

    private void MoveSelection(int delta)
    {
        if (_results.Count == 0)
            return;

        _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _results.Count - 1);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Search and Scoring
    // ─────────────────────────────────────────────────────────────────────────────────

    private async Task UpdateResultsAsync()
    {
        var queryText = _query.ToString();
        var cwdFilter = _cwdFilterEnabled ? _currentCwd : null;

        // Fetch candidates from history store
        var candidates = await _historyStore.SearchAsync(new HistoryQuery
        {
            Filter = string.IsNullOrEmpty(queryText) ? null : queryText,
            Cwd = cwdFilter,
            Limit = 100,
            Reverse = false // Newest first
        });

        // If no results with CWD filter and query is non-empty, try global search
        if (candidates.Count == 0 && _cwdFilterEnabled && !string.IsNullOrEmpty(queryText))
        {
            candidates = await _historyStore.SearchAsync(new HistoryQuery
            {
                Filter = queryText,
                Cwd = null,
                Limit = 100,
                Reverse = false
            });
        }

        // Compute frequencies for this result set
        _commandFrequencies = ComputeFrequencies(candidates);

        // Score and sort
        _results = candidates
            .Select(e => new ScoredEntry(
                e,
                ScoreFuzzyMatch(e, queryText, _currentCwd)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.Timestamp)
            .ToList();

        _selectedIndex = _results.Count > 0 ? 0 : -1;
    }

    private Dictionary<string, int> ComputeFrequencies(IReadOnlyList<HistoryEntry> entries)
    {
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

        foreach (var entry in entries)
        {
            if (entry.Timestamp < thirtyDaysAgo)
                continue;

            var cmd = entry.Command;
            if (freq.ContainsKey(cmd))
                freq[cmd]++;
            else
                freq[cmd] = 1;
        }

        return freq;
    }

    /// <summary>
    /// Computes the fuzzy match score for a history entry.
    /// Score range: 0-200 points.
    /// </summary>
    public static double ScoreFuzzyMatch(HistoryEntry entry, string query, string currentCwd)
    {
        double score = 0;

        // Factor 1: Query match quality (0-100 points)
        score += QueryMatchScore(entry.Command, query) * 100.0;

        // Factor 2: CWD match boost (0-50 points)
        if (entry.Cwd == currentCwd)
            score += CwdBoostPoints;

        // Factor 3: Recency boost (0-30 points)
        var hoursSince = (DateTime.UtcNow - entry.Timestamp).TotalHours;
        var recencyBoost = Math.Max(0, MaxRecencyPoints * (1 - hoursSince / RecencyDecayHours));
        score += recencyBoost;

        // Factor 4: Frequency boost (0-20 points)
        // This would be passed in or computed externally; for now, use a simple heuristic
        // based on recency (more recent = likely more frequent)
        score += Math.Min(MaxFrequencyPoints, recencyBoost * (MaxFrequencyPoints / MaxRecencyPoints));

        return score;
    }

    /// <summary>
    /// Computes the query match score (0.0 to 1.0).
    /// Hierarchy: exact > prefix > substring > fuzzy subsequence.
    /// </summary>
    public static double QueryMatchScore(string command, string query)
    {
        if (string.IsNullOrEmpty(query))
            return 1.0;

        var cmd = command.AsSpan();
        var q = query.AsSpan();

        // Exact match (highest score)
        if (cmd.SequenceEqual(q))
            return 1.0;

        // Prefix match (second highest)
        if (cmd.StartsWith(q, StringComparison.Ordinal))
            return 0.9;

        // Substring match (third highest)
        if (command.Contains(query, StringComparison.Ordinal))
            return 0.7;

        // Fuzzy subsequence match (lowest non-zero score)
        var fuzzyScore = FuzzySubsequenceScore(command, query);
        return fuzzyScore > 0 ? fuzzyScore * 0.5 : 0;
    }

    /// <summary>
    /// Computes a fuzzy subsequence match score.
    /// Returns 0 if the query is not a subsequence of the text.
    /// </summary>
    public static double FuzzySubsequenceScore(string text, string query)
    {
        if (string.IsNullOrEmpty(query))
            return 0;

        int t = 0; // text index
        int q = 0; // query index
        int matches = 0;
        int totalGap = 0;

        while (t < text.Length && q < query.Length)
        {
            if (text[t] == query[q])
            {
                matches++;
                q++;
            }
            else
            {
                totalGap++;
            }
            t++;
        }

        // Full match of all query characters?
        if (q < query.Length)
            return 0; // Not a match

        // Score: favor tight clusters of matches
        double coverage = (double)matches / text.Length;
        double density = (double)matches / (matches + totalGap);

        return coverage * density;
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Rendering
    // ─────────────────────────────────────────────────────────────────────────────────

    private void Render()
    {
        // Clear screen and move cursor to home
        Console.Write(ClearScreen);
        Console.Write(MoveCursorHome);

        // Title bar
        RenderTitleBar();

        // Prompt line
        RenderPromptLine();

        // Search bar
        RenderSearchBar();

        // Results list
        RenderResults();

        // Status bar
        RenderStatusBar();
    }

    private void RenderTitleBar()
    {
        var width = _terminalWidth;
        var title = " History Search ";
        var padding = Math.Max(0, width - title.Length - 2);
        var leftPad = padding / 2;
        var rightPad = padding - leftPad;

        Console.Write(ResetAttributes);
        Console.Write(new string('─', leftPad));
        Console.Write(BoldWhite);
        Console.Write(title);
        Console.Write(ResetAttributes);
        Console.Write(new string('─', rightPad));
        Console.WriteLine();
    }

    private void RenderPromptLine()
    {
        Console.Write(ResetAttributes);
        Console.Write(ClearLine);
        Console.Write(_originalPrompt);
        Console.WriteLine();
    }

    private void RenderSearchBar()
    {
        Console.Write(ResetAttributes);
        Console.Write(ClearLine);

        var queryText = _query.ToString();

        if (_inEditMode)
        {
            Console.Write("(edit) ");
            Console.Write(BoldWhite);
            Console.Write($"\"{_editBuffer}\"");
            Console.Write(ResetAttributes);
        }
        else
        {
            Console.Write("(i-search) `");
            Console.Write(BoldWhite);
            Console.Write(queryText.Length > 0 ? queryText : "(empty)");
            Console.Write(ResetAttributes);
            Console.Write("' ");
            Console.Write(Cyan);
            Console.Write(_cwdFilterEnabled ? "[CWD]" : "[All]");
            Console.Write(ResetAttributes);
        }

        // Right-align match count
        var countText = _results.Count.ToString() + " matching";
        var countPos = _terminalWidth - countText.Length;
        if (countPos > queryText.Length + 30)
        {
            Console.SetCursorPosition(countPos, Console.CursorTop);
            Console.Write(Gray);
            Console.Write(countText);
            Console.Write(ResetAttributes);
        }

        Console.WriteLine();
    }

    private void RenderResults()
    {
        var resultRows = _terminalHeight - 4; // Reserve rows for title, prompt, search, status
        var startIdx = Math.Max(0, Math.Min(_selectedIndex - resultRows / 2, _results.Count - resultRows));
        var endIdx = Math.Min(_results.Count, startIdx + resultRows);

        for (int i = startIdx; i < endIdx; i++)
        {
            var scored = _results[i];
            var isSelected = i == _selectedIndex;

            Console.Write(ClearLine);

            if (isSelected && !_inEditMode)
            {
                Console.Write(ReverseVideoOn);
            }
            else if (_inEditMode && i == _selectedIndex)
            {
                Console.Write("> ");
            }

            RenderResultLine(scored.Entry, _query.ToString());

            if (isSelected && !_inEditMode)
            {
                Console.Write(ReverseVideoOff);
            }

            Console.WriteLine();
        }

        // Fill remaining rows with empty lines
        for (int i = endIdx; i < startIdx + resultRows; i++)
        {
            Console.Write(ClearLine);
            Console.WriteLine();
        }
    }

    private void RenderResultLine(HistoryEntry entry, string query)
    {
        var maxWidth = _terminalWidth - 50; // Reserve space for metadata
        if (maxWidth < 20) maxWidth = 20;

        var command = entry.Command;
        var displayedCmd = TruncateCommand(command, maxWidth, query);
        var highlightedCmd = HighlightMatch(displayedCmd, query);

        Console.Write(highlightedCmd);

        // Pad to align metadata
        var padLen = Math.Max(0, maxWidth + 2 - displayedCmd.Length);
        if (padLen > 0)
            Console.Write(new string(' ', padLen));

        // CWD (shortened)
        Console.Write(Cyan);
        var shortCwd = ShortenCwd(entry.Cwd, _homeDir);
        Console.Write(shortCwd.Length > 15 ? shortCwd.Substring(shortCwd.Length - 15) : shortCwd.PadLeft(15));
        Console.Write(ResetAttributes);
        Console.Write("  ");

        // Relative time
        Console.Write(Gray);
        var timeStr = FormatRelativeTime(entry.Timestamp);
        Console.Write(timeStr.PadLeft(8));
        Console.Write(ResetAttributes);
        Console.Write("  ");

        // Exit code
        if (entry.ExitCode.HasValue)
        {
            if (entry.ExitCode.Value == 0)
                Console.Write(Green);
            else
                Console.Write(Red);
            Console.Write($"Exit {entry.ExitCode.Value}");
        }
        else
        {
            Console.Write(Gray);
            Console.Write("Exit ?");
        }
        Console.Write(ResetAttributes);
        Console.Write("  ");

        // ID
        Console.Write(Gray);
        Console.Write($"#{entry.Id ?? 0}");
        Console.Write(ResetAttributes);
    }

    private void RenderStatusBar()
    {
        Console.Write(ResetAttributes);
        Console.Write(ClearLine);
        Console.Write(Gray);

        if (_inEditMode)
        {
            Console.Write("[Enter] Execute edited  [Esc] Cancel edit  [Arrows] Move cursor");
        }
        else
        {
            Console.Write("[Enter] Execute  [Tab] Edit  [Ctrl-G] Toggle CWD/All  [Esc] Cancel");
        }

        Console.Write(ResetAttributes);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Helper Methods (Public for Testing)
    // ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats a timestamp as relative time (e.g., "5m ago", "2h ago").
    /// </summary>
    public static string FormatRelativeTime(DateTime timestamp)
    {
        var delta = DateTime.UtcNow - timestamp;

        if (delta.TotalMinutes < 1)
            return "0m ago";
        if (delta.TotalMinutes < 60)
            return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24)
            return $"{(int)delta.TotalHours}h ago";
        // Days < 8 shows as days (so 7 days shows as "7d ago")
        if (delta.TotalDays < 8)
            return $"{(int)delta.TotalDays}d ago";

        // 8 or more days = weeks
        var weeks = (int)Math.Ceiling((delta.TotalDays - 1) / 7.0);
        return $"{weeks}w ago";
    }

    /// <summary>
    /// Highlights query matches in the command text using reverse video.
    /// </summary>
    public static string HighlightMatch(string command, string query)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(command))
            return command;

        var result = new StringBuilder();
        int cmdIdx = 0;

        while (cmdIdx < command.Length)
        {
            // Find next match
            var matchIdx = command.IndexOf(query, cmdIdx, StringComparison.Ordinal);
            if (matchIdx < 0)
            {
                // No more matches, append rest
                result.Append(command.AsSpan(cmdIdx));
                break;
            }

            // Append text before match
            if (matchIdx > cmdIdx)
            {
                result.Append(command.AsSpan(cmdIdx, matchIdx - cmdIdx));
            }

            // Append highlighted match
            result.Append(ReverseVideoOn);
            result.Append(command.AsSpan(matchIdx, query.Length));
            result.Append(ReverseVideoOff);

            cmdIdx = matchIdx + query.Length;
        }

        return result.ToString();
    }

    /// <summary>
    /// Truncates a command to fit within maxWidth, preserving match visibility.
    /// </summary>
    public static string TruncateCommand(string command, int maxWidth, string query)
    {
        if (command.Length <= maxWidth)
            return command;

        // If query is empty, truncate from the end
        if (string.IsNullOrEmpty(query))
            return command.Substring(0, maxWidth - 3) + "...";

        // Find the query match
        var matchIdx = command.IndexOf(query, StringComparison.Ordinal);
        if (matchIdx < 0)
        {
            // No match found, truncate from the end
            return command.Substring(0, maxWidth - 3) + "...";
        }

        // Try to show the match
        var matchEnd = matchIdx + query.Length;

        if (matchEnd <= maxWidth)
        {
            // Match fits at the start
            return command.Substring(0, maxWidth - 3) + "...";
        }

        // Match is too far right, truncate left side
        var startIdx = Math.Max(0, matchEnd - maxWidth + 3);
        return "..." + command.Substring(startIdx, Math.Min(maxWidth - 3, command.Length - startIdx));
    }

    /// <summary>
    /// Shortens a CWD path, replacing home directory with ~.
    /// </summary>
    public static string ShortenCwd(string cwd, string homeDir)
    {
        if (string.IsNullOrEmpty(homeDir))
            return cwd;

        if (cwd.StartsWith(homeDir, StringComparison.Ordinal))
        {
            if (cwd.Length == homeDir.Length)
                return "~";

            if (cwd[homeDir.Length] == '/' || cwd[homeDir.Length] == '\\')
                return "~" + cwd.Substring(homeDir.Length);
        }

        return cwd;
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Terminal Management
    // ─────────────────────────────────────────────────────────────────────────────────

    private void EnterAlternateScreen()
    {
        Console.Write(SaveCursor);
        Console.Write(EnterAltScreen);
        Console.Write(HideCursor);
        UpdateTerminalSize();
    }

    private void ExitAlternateScreen()
    {
        Console.Write(ShowCursor);
        Console.Write(ExitAltScreen);
        Console.Write(RestoreCursor);
        Console.Write(ClearLine); // Clear the line where we were
    }

    private void UpdateTerminalSize()
    {
        try
        {
            _terminalWidth = Console.WindowWidth;
            _terminalHeight = Console.WindowHeight;
        }
        catch
        {
            _terminalWidth = 80;
            _terminalHeight = 24;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        ExitAlternateScreen();
        _disposed = true;
    }
}
