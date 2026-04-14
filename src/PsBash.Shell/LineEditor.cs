using System.Text;

namespace PsBash.Shell;

/// <summary>
/// VT100 line editor: emacs keybindings, persistent history, tab completion.
/// Replaces Console.ReadLine() in the interactive shell.
/// </summary>
internal sealed class LineEditor
{
    // ── history ──────────────────────────────────────────────────────────────
    private readonly List<string> _history;
    private readonly string _historyPath;
    private int _historyIndex;         // points into _history; _history.Count = current input
    private string _savedInput = "";   // stashed current input while navigating history

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

    public LineEditor(
        string historyPath,
        Func<string, int, IReadOnlyList<string>>? completer = null)
    {
        _historyPath = historyPath;
        _history = LoadHistory(historyPath);
        _historyIndex = _history.Count;
        _completer = completer;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read a line interactively. Returns null on EOF (Ctrl-D on empty input).
    /// Falls back to Console.ReadLine() when stdin is not a TTY (piped/redirected).
    /// </summary>
    public string? ReadLine(string prompt)
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

        Console.Write(prompt);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // Tab completion
            if (key.Key == ConsoleKey.Tab && key.Modifiers == 0)
            {
                HandleTab(prompt);
                continue;
            }

            // Any non-Tab key clears the completion cycle
            ClearCompletion();

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
                    break;
                case ConsoleKey.RightArrow when key.Modifiers == 0:
                    MoveCursor(1);
                    break;
                case ConsoleKey.Home:
                case ConsoleKey.A when key.Modifiers == ConsoleModifiers.Control:
                    MoveCursorTo(0);
                    break;
                case ConsoleKey.End:
                case ConsoleKey.E when key.Modifiers == ConsoleModifiers.Control:
                    MoveCursorTo(_buf.Length);
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
                    break;
                case ConsoleKey.Delete:
                    DeleteCharForward();
                    Redraw(prompt);
                    break;
                case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                    KillToEnd();
                    Redraw(prompt);
                    break;
                case ConsoleKey.U when key.Modifiers == ConsoleModifiers.Control:
                    KillToStart();
                    Redraw(prompt);
                    break;
                case ConsoleKey.W when key.Modifiers == ConsoleModifiers.Control:
                    KillWordBack();
                    Redraw(prompt);
                    break;
                case ConsoleKey.Y when key.Modifiers == ConsoleModifiers.Control:
                    Yank();
                    Redraw(prompt);
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
                    }
                    // Ignore other control sequences (F-keys, etc.)
                    break;
            }
        }
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
        Redraw(prompt);
    }

    private void HistoryNext(string prompt)
    {
        if (_historyIndex >= _history.Count) return;
        _historyIndex++;
        var text = _historyIndex == _history.Count ? _savedInput : _history[_historyIndex];
        SetBuffer(text);
        Redraw(prompt);
    }

    private void AddToHistory(string line)
    {
        // Deduplicate: remove previous identical entry
        var last = _history.Count > 0 ? _history[^1] : null;
        if (last == line) { SaveHistory(); return; }

        _history.Add(line);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
        SaveHistory();
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

    private void SaveHistory()
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
        // Strip ANSI from prompt to measure visual length
        var promptVisible = StripAnsi(prompt);
        var text = _buf.ToString();

        // Erase current line, reprint prompt + buffer, position cursor
        Console.Write(ClearLine);
        Console.Write(prompt);
        Console.Write(text);

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
}
