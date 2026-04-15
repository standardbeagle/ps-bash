using System.Text;

namespace PsBash.Shell;

/// <summary>
/// VT100 line editor: emacs keybindings, persistent history, tab completion.
/// Replaces Console.ReadLine() in the interactive shell.
/// </summary>
internal sealed class LineEditor
{
    // ── history ──────────────────────────────────────────────────────────────
    private readonly IHistoryStore _historyStore;
    private readonly List<string> _history;  // In-memory cache for fast navigation
    private int _historyIndex;         // points into _history; _history.Count = current input
    private string _savedInput = "";   // stashed current input while navigating history
    private readonly SemaphoreSlim _historyLock = new(1, 1);

    // ── autosuggestion ────────────────────────────────────────────────────────
    private readonly Suggester _suggester;
    private string? _currentSuggestion;  // Suffix to append (null = no suggestion)
    private readonly string _cwd;  // Current working directory for suggestions

    // ── completion ───────────────────────────────────────────────────────────
    private readonly Func<string, int, IReadOnlyList<string>>? _completer;

    // Cycle state for successive Tab presses
    private IReadOnlyList<string>? _completions;
    private int _completionIndex;
    private string _completionBase = "";   // text before the token being completed
    private string _completionToken = "";  // partial token that triggered completion

    // ── buffer ───────────────────────────────────────────────────────────────
    private readonly StringBuilder _buf = new();
    private int _cursor;   // byte offset into _buf

    // ── kill ring ────────────────────────────────────────────────────────────
    private string _killRing = "";

    // ── ANSI sequences ───────────────────────────────────────────────────────
    private const string ClearLine = "\x1b[2K\r";

    // ── constants ────────────────────────────────────────────────────────────
    private const int MaxHistory = 5000;

    /// <summary>
    /// Creates a new LineEditor with a history store for persistent history.
    /// </summary>
    public LineEditor(
        IHistoryStore historyStore,
        Func<string, int, IReadOnlyList<string>>? completer = null,
        string? cwd = null)
    {
        _historyStore = historyStore;
        _completer = completer;
        _suggester = new Suggester(historyStore);
        _cwd = cwd ?? Environment.CurrentDirectory;
        _history = new List<string>();
        _historyIndex = 0;

        // Load history asynchronously in the background
        _ = LoadHistoryAsync();
    }

    /// <summary>
    /// Creates a new LineEditor with legacy file-based history (for backward compatibility).
    /// </summary>
    public LineEditor(
        string historyPath,
        Func<string, int, IReadOnlyList<string>>? completer = null,
        string? cwd = null)
    {
        _historyStore = new LegacyFileHistoryStore(historyPath);
        _completer = completer;
        _suggester = new Suggester(_historyStore);
        _cwd = cwd ?? Environment.CurrentDirectory;
        _history = LoadHistory(historyPath);
        _historyIndex = _history.Count;
    }

    private async Task LoadHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            var entries = await _historyStore.SearchAsync(new HistoryQuery { Limit = MaxHistory });
            _history.Clear();
            _history.AddRange(entries.Select(e => e.Command).Reverse());
            _historyIndex = _history.Count;
        }
        finally
        {
            _historyLock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read a line interactively. Returns null on EOF (Ctrl-D on empty input).
    /// Falls back to Console.ReadLine() when stdin is not a TTY (piped/redirected).
    /// This is the synchronous version that does not support Ctrl-R (for backward compatibility).
    /// </summary>
    public string? ReadLine(string prompt) => ReadLineAsync(prompt).GetAwaiter().GetResult();

    /// <summary>
    /// Async version of ReadLine that supports Ctrl-R search.
    /// </summary>
    public async Task<string?> ReadLineAsync(string prompt)
    {
        // When stdin is redirected (not a real terminal), fall back to simple ReadLine.
        // This preserves compatibility with tests and piped usage.
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        _buf.Clear();
        _cursor = 0;
        _historyIndex = _history.Count;
        _savedInput = "";
        ClearCompletion();
        ClearSuggestion();

        Console.Write(prompt);

        // Initial suggestion for empty prompt (should be null)
        _ = UpdateSuggestionAsync();

        while (true)

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // Tab completion
            if (key.Key == ConsoleKey.Tab && key.Modifiers == 0)
            {
                ClearSuggestion();  // Clear suggestion when tab completes
                HandleTab(prompt);
                continue;
            }

            // Any non-Tab key clears the completion cycle
            ClearCompletion();
            ClearSuggestion();  // Clear suggestion on any other key

            // Ctrl-D: EOF on empty buffer, otherwise delete-char
            if (key.Key == ConsoleKey.D && key.Modifiers == ConsoleModifiers.Control)
            {
                if (_buf.Length == 0)
                {
                    Console.WriteLine();
                    return null;   // EOF
                }
                DeleteCharForward();
                Redraw(prompt);
                continue;
            }

            // Enter / newline
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                var result = _buf.ToString();
                if (result.Length > 0)
                    AddToHistory(result);
                return result;
            }

            // Ctrl-R — reverse-i-search
            if (key.Key == ConsoleKey.R && key.Modifiers == ConsoleModifiers.Control)
            {
                var cmd = await HandleCtrlRAsync(prompt);
                if (cmd is not null)
                {
                    SetBuffer(cmd);
                    Redraw(prompt);
                }
                // After Ctrl-R, redraw to restore normal mode
                continue;
            }

            // Ctrl-C — caller handles SIGINT via CancelKeyPress; we just clear the line
            if (key.Key == ConsoleKey.C && key.Modifiers == ConsoleModifiers.Control)
            {
                Console.WriteLine("^C");
                _buf.Clear();
                _cursor = 0;
                Console.Write(prompt);
                continue;
            }

            switch (key.Key)
            {
                // ── cursor movement ──────────────────────────────────────────
                case ConsoleKey.LeftArrow when key.Modifiers == 0:
                    MoveCursor(-1);
                    ClearSuggestion();
                    break;
                case ConsoleKey.RightArrow when key.Modifiers == 0:
                    // Accept suggestion if available and at end of buffer
                    if (_currentSuggestion is not null && _cursor == _buf.Length)
                    {
                        AcceptSuggestion();
                    }
                    else
                    {
                        MoveCursor(1);
                        ClearSuggestion();
                    }
                    break;
                case ConsoleKey.Home:
                case ConsoleKey.A when key.Modifiers == ConsoleModifiers.Control:
                    MoveCursorTo(0);
                    break;
                case ConsoleKey.End:
                case ConsoleKey.E when key.Modifiers == ConsoleModifiers.Control:
                    // Accept suggestion if available before moving to end
                    if (_currentSuggestion is not null)
                    {
                        AcceptSuggestion();
                    }
                    MoveCursorTo(_buf.Length);
                    ClearSuggestion();
                    break;

                // Word movement (Alt-B / Alt-F via escape sequences)
                case ConsoleKey.LeftArrow when key.Modifiers == ConsoleModifiers.Alt:
                    MoveCursorWordLeft();
                    break;
                case ConsoleKey.RightArrow when key.Modifiers == ConsoleModifiers.Alt:
                    MoveCursorWordRight();
                    break;

                // ── history navigation ───────────────────────────────────────
                case ConsoleKey.UpArrow:
                    HistoryPrev(prompt);
                    break;
                case ConsoleKey.DownArrow:
                    HistoryNext(prompt);
                    break;

                // ── deletion ─────────────────────────────────────────────────
                case ConsoleKey.Backspace:
                    DeleteCharBack();
                    Redraw(prompt);
                    _ = UpdateSuggestionAsync();
                    break;
                case ConsoleKey.Delete:
                    DeleteCharForward();
                    Redraw(prompt);
                    _ = UpdateSuggestionAsync();
                    break;
                case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                    KillToEnd();
                    Redraw(prompt);
                    _ = UpdateSuggestionAsync();
                    break;
                case ConsoleKey.U when key.Modifiers == ConsoleModifiers.Control:
                    KillToStart();
                    Redraw(prompt);
                    _ = UpdateSuggestionAsync();
                    break;
                case ConsoleKey.W when key.Modifiers == ConsoleModifiers.Control:
                    KillWordBack();
                    Redraw(prompt);
                    _ = UpdateSuggestionAsync();
                    break;
                case ConsoleKey.Y when key.Modifiers == ConsoleModifiers.Control:
                    Yank();
                    Redraw(prompt);
                    _ = UpdateSuggestionAsync();
                    break;

                // ── misc ─────────────────────────────────────────────────────
                case ConsoleKey.L when key.Modifiers == ConsoleModifiers.Control:
                    // Clear screen
                    Console.Write("\x1b[H\x1b[2J");
                    Redraw(prompt);
                    break;

                default:
                    // Printable character
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        InsertChar(key.KeyChar);
                        Redraw(prompt);
                        _ = UpdateSuggestionAsync();
                    }
                    // Ignore other control sequences (F-keys, etc.)
                    break;
            }
        }
    }

    /// <summary>
    /// Records a command execution in the history store with full metadata.
    /// </summary>
    public async Task RecordCommandAsync(string command, string cwd, int? exitCode, long? durationMs, string sessionId)
    {
        await _historyStore.RecordAsync(new HistoryEntry
        {
            Command = command,
            Cwd = cwd,
            ExitCode = exitCode,
            Timestamp = DateTime.UtcNow,
            DurationMs = durationMs,
            SessionId = sessionId,
        });

        // Update in-memory cache
        await _historyLock.WaitAsync();
        try
        {
            // Deduplicate
            if (_history.Count > 0 && _history[^1] == command)
                return;

            _history.Add(command);
            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }
        finally
        {
            _historyLock.Release();
        }
    }

    /// <summary>
    /// Gets the most recent history entries matching a prefix.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRecentAsync(string prefix, int limit = 10)
    {
        var results = await _historyStore.SearchAsync(new HistoryQuery
        {
            Filter = prefix,
            Limit = limit,
        });

        return results.Select(e => e.Command).ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // History
    // ─────────────────────────────────────────────────────────────────────────

    private void HistoryPrev(string prompt)
    {
        if (_history.Count == 0) return;
        if (_historyIndex == _history.Count)
            _savedInput = _buf.ToString();   // stash current edit
        if (_historyIndex <= 0) return;
        _historyIndex--;
        SetBuffer(_history[_historyIndex]);
        ClearSuggestion();  // No suggestions while navigating history
        Redraw(prompt, showSuggestion: false);
    }

    private void HistoryNext(string prompt)
    {
        if (_historyIndex >= _history.Count) return;
        _historyIndex++;
        var text = _historyIndex == _history.Count ? _savedInput : _history[_historyIndex];
        SetBuffer(text);
        ClearSuggestion();  // No suggestions while navigating history
        Redraw(prompt, showSuggestion: false);
    }

    private void AddToHistory(string line)
    {
        // Deduplicate: remove previous identical entry
        var last = _history.Count > 0 ? _history[^1] : null;
        if (last == line) return;

        _history.Add(line);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);

        // Async save to store
        _ = Task.Run(async () =>
        {
            try
            {
                await _historyStore.RecordAsync(new HistoryEntry
                {
                    Command = line,
                    Cwd = Environment.CurrentDirectory,
                    Timestamp = DateTime.UtcNow,
                    SessionId = Guid.NewGuid().ToString(),
                });
            }
            catch { }
        });
    }

    private static List<string> LoadHistory(string path)
    {
        try
        {
            if (File.Exists(path))
                return [.. File.ReadAllLines(path).Where(l => l.Length > 0)];
        }
        catch { }
        return [];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tab completion
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleTab(string prompt)
    {
        if (_completer is null) return;

        if (_completions is null)
        {
            // First Tab: compute completions
            _completions = _completer(_buf.ToString(), _cursor);
            _completionIndex = 0;

            if (_completions.Count == 0)
            {
                ClearCompletion();
                return;
            }

            // Determine base (text before the token) and token
            (_completionBase, _completionToken) = SplitAtWordBoundary(_buf.ToString(), _cursor);

            if (_completions.Count == 1)
            {
                // Unique match: complete immediately
                ApplyCompletion(_completions[0], prompt);
                ClearCompletion();
                return;
            }

            // Multiple matches: show list below, apply first
            ShowCompletionList(_completions);
            ApplyCompletion(_completions[0], prompt);
            _completionIndex = 1;
        }
        else
        {
            // Subsequent Tab: cycle through matches
            ApplyCompletion(_completions[_completionIndex], prompt);
            _completionIndex = (_completionIndex + 1) % _completions.Count;
        }
    }

    private void ApplyCompletion(string completion, string prompt)
    {
        // Replace buffer from base..cursor with base + completion
        var suffix = _buf.ToString(_cursor, _buf.Length - _cursor);
        var newBuf = _completionBase + completion;
        // Add trailing space only if there's nothing after cursor
        if (suffix.Length == 0)
            newBuf += ' ';
        SetBuffer(newBuf + suffix);
        // Place cursor at end of completion (before suffix)
        _cursor = (_completionBase + completion + (suffix.Length == 0 ? " " : "")).Length;
        Redraw(prompt);
    }

    private static void ShowCompletionList(IReadOnlyList<string> completions)
    {
        Console.WriteLine();
        var maxLen = completions.Max(c => c.Length) + 2;
        var cols = Math.Max(1, Console.WindowWidth / maxLen);
        int i = 0;
        foreach (var c in completions)
        {
            Console.Write(c.PadRight(maxLen));
            i++;
            if (i % cols == 0) Console.WriteLine();
        }
        if (i % cols != 0) Console.WriteLine();
    }

    private void ClearCompletion()
    {
        _completions = null;
        _completionIndex = 0;
        _completionBase = "";
        _completionToken = "";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Buffer manipulation
    // ─────────────────────────────────────────────────────────────────────────

    private void SetBuffer(string text)
    {
        _buf.Clear();
        _buf.Append(text);
        _cursor = text.Length;
    }

    private void InsertChar(char c)
    {
        _buf.Insert(_cursor, c);
        _cursor++;
    }

    private void DeleteCharBack()
    {
        if (_cursor <= 0) return;
        _cursor--;
        _buf.Remove(_cursor, 1);
    }

    private void DeleteCharForward()
    {
        if (_cursor >= _buf.Length) return;
        _buf.Remove(_cursor, 1);
    }

    private void KillToEnd()
    {
        _killRing = _buf.ToString(_cursor, _buf.Length - _cursor);
        _buf.Remove(_cursor, _buf.Length - _cursor);
    }

    private void KillToStart()
    {
        _killRing = _buf.ToString(0, _cursor);
        _buf.Remove(0, _cursor);
        _cursor = 0;
    }

    private void KillWordBack()
    {
        var end = _cursor;
        // Skip trailing spaces
        while (_cursor > 0 && _buf[_cursor - 1] == ' ') _cursor--;
        // Skip word chars
        while (_cursor > 0 && _buf[_cursor - 1] != ' ') _cursor--;
        _killRing = _buf.ToString(_cursor, end - _cursor);
        _buf.Remove(_cursor, end - _cursor);
    }

    private void Yank()
    {
        if (_killRing.Length == 0) return;
        _buf.Insert(_cursor, _killRing);
        _cursor += _killRing.Length;
    }

    private void MoveCursor(int delta)
    {
        _cursor = Math.Clamp(_cursor + delta, 0, _buf.Length);
    }

    private void MoveCursorTo(int pos)
    {
        _cursor = Math.Clamp(pos, 0, _buf.Length);
    }

    private void MoveCursorWordLeft()
    {
        while (_cursor > 0 && _buf[_cursor - 1] == ' ') _cursor--;
        while (_cursor > 0 && _buf[_cursor - 1] != ' ') _cursor--;
    }

    private void MoveCursorWordRight()
    {
        while (_cursor < _buf.Length && _buf[_cursor] == ' ') _cursor++;
        while (_cursor < _buf.Length && _buf[_cursor] != ' ') _cursor++;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Terminal rendering
    // ─────────────────────────────────────────────────────────────────────────

    private void Redraw(string prompt)
    {
        Redraw(prompt, showSuggestion: true);
    }

    private void Redraw(string prompt, bool showSuggestion)
    {
        // Strip ANSI from prompt to measure visual length
        var promptVisible = StripAnsi(prompt);
        var text = _buf.ToString();

        // Erase current line, reprint prompt + buffer, position cursor
        Console.Write(ClearLine);
        Console.Write(prompt);
        Console.Write(text);

        // Append suggestion in dim (gray) if present and requested
        if (showSuggestion && _currentSuggestion is not null && _currentSuggestion.Length > 0 && _cursor == _buf.Length)
        {
            Console.Write("\x1b[2m");      // Dim on
            Console.Write(_currentSuggestion);
            Console.Write("\x1b[0m");      // Reset
        }

        // Move cursor back from end to correct position
        var charsAfterCursor = _buf.Length - _cursor;
        if (charsAfterCursor > 0)
            Console.Write($"\x1b[{charsAfterCursor}D");
    }

    private static string StripAnsi(string s)
    {
        // Quick strip of ESC[...m sequences for length calculation
        var sb = new StringBuilder(s.Length);
        bool inEsc = false;
        foreach (var c in s)
        {
            if (inEsc)
            {
                if (char.IsLetter(c)) inEsc = false;
                continue;
            }
            if (c == '\x1b') { inEsc = true; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Split line at the last word boundary before <paramref name="cursor"/>,
    /// returning (textBeforeToken, partialToken).
    /// </summary>
    internal static (string Base, string Token) SplitAtWordBoundary(string line, int cursor)
    {
        var before = cursor <= line.Length ? line[..cursor] : line;
        // Find start of last token (not in quotes for simplicity)
        int i = before.Length - 1;
        while (i >= 0 && before[i] != ' ' && before[i] != '\t') i--;
        var tokenStart = i + 1;
        return (before[..tokenStart], before[tokenStart..]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Autosuggestion
    // ─────────────────────────────────────────────────────────────────────────

    private async Task UpdateSuggestionAsync()
    {
        var prefix = _buf.ToString();
        var suffix = await _suggester.SuggestAsync(prefix, _cwd);

        if (suffix is null)
        {
            _currentSuggestion = null;
        }
        else if (suffix.Length == 0)
        {
            // Exact match - no suggestion needed
            _currentSuggestion = null;
        }
        else
        {
            _currentSuggestion = suffix;
        }
    }

    private void AcceptSuggestion()
    {
        if (_currentSuggestion is null || _currentSuggestion.Length == 0)
            return;

        // Append suggestion to buffer
        foreach (var c in _currentSuggestion)
        {
            _buf.Append(c);
        }
        _cursor = _buf.Length;
        _currentSuggestion = null;
    }

    private void ClearSuggestion()
    {
        _currentSuggestion = null;
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Ctrl-R Search
    // ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles Ctrl-R reverse-i-search. Returns the command to insert, or null if cancelled.
    /// </summary>
    private async Task<string?> HandleCtrlRAsync(string prompt)
    {
        var search = new CtrlRSearch(_historyStore, _cwd, prompt);
        var (result, command) = await search.RunAsync();

        if (result == CtrlRSearch.Result.Execute && command is not null)
        {
            return command;
        }

        return null;
    }
}

/// <summary>
/// Legacy file-based history store for backward compatibility with the old LineEditor constructor.
/// </summary>
internal sealed class LegacyFileHistoryStore : IHistoryStore
{
    private readonly string _historyPath;
    private readonly List<string> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LegacyFileHistoryStore(string historyPath)
    {
        _historyPath = historyPath;
        Load();
    }

    private void Load()
    {
        _lock.Wait();
        try
        {
            if (File.Exists(_historyPath))
                _history.AddRange(File.ReadAllLines(_historyPath).Where(l => l.Length > 0));
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task RecordAsync(HistoryEntry entry)
    {
        return Task.Run(() =>
        {
            _lock.Wait();
            try
            {
                // Deduplicate
                if (_history.Count > 0 && _history[^1] == entry.Command)
                    return;

                _history.Add(entry.Command);
                if (_history.Count > 5000)
                    _history.RemoveAt(0);

                Save();
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query)
    {
        _lock.Wait();
        try
        {
            var queryable = _history.AsEnumerable();

            if (!string.IsNullOrEmpty(query.Filter))
                queryable = queryable.Where(cmd => cmd.StartsWith(query.Filter, StringComparison.Ordinal));

            var results = queryable
                .Take(query.Limit)
                .Select((cmd, idx) => new HistoryEntry
                {
                    Command = cmd,
                    Cwd = "",
                    Timestamp = DateTime.UtcNow,
                    SessionId = "",
                    Id = idx + 1,
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<HistoryEntry>>(results);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<IReadOnlyList<SequenceSuggestion>> GetSequenceSuggestionsAsync(string? lastCommand, string cwd)
    {
        return Task.FromResult<IReadOnlyList<SequenceSuggestion>>(Array.Empty<SequenceSuggestion>());
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_historyPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllLines(_historyPath, _history);
        }
        catch { }
    }
}
