using System.Text;

namespace PsBash.Host.Shell;

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
    // Async so providers can round-trip to the runspace (live command/parameter
    // completion). A sync completer (tests, legacy) is adapted into this shape.
    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<CompletionItem>>>? _completer;

    // Upper bound on a single Tab's completion. A live runspace query that exceeds this is
    // cancelled and the static/local candidates computed so far are used — Tab never hangs.
    private const int CompletionTimeoutMs = 200;

    // Cycle state for successive Tab presses
    private IReadOnlyList<CompletionItem>? _completions;
    private int _completionIndex;
    private string _completionBase = "";   // text before the token being completed
    private string _completionToken = "";  // partial token that triggered completion

    // ── flag-doc panel ─────────────────────────────────────────────────────────
    // Aliases used to resolve the command under the cursor when building the floating
    // flag-doc panel (e.g. "ll" -> "ls"). Empty when none supplied.
    private readonly IReadOnlyDictionary<string, string> _aliases;
    // Max flag-doc rows shown at once (plus a "…" overflow row); keeps the panel from dominating.
    private const int MaxPanelRows = 8;

    // Async hint source for PowerShell-cmdlet parameters (the bash flag panel is computed
    // synchronously from the local flag specs; PS params need a bounded runspace round-trip).
    // Returns empty for bash commands / non-flag tokens. Null when no live worker is wired.
    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<FlagHint>>>? _flagHintProvider;
    // Cached PS-param hints + the "commandtoken" key they were fetched for. Computed on
    // keystroke (UpdatePsFlagHintsAsync) and rendered by ComputeFlagPanel only while the key still
    // matches the cursor — so a stale async result never paints under a changed token.
    private IReadOnlyList<FlagHint>? _psPanelHints;
    private string? _psPanelKey;

    // Ctrl+~ command assist. Most terminals encode Ctrl+~ as ASCII 0x1e, the
    // same control character as Ctrl+^; keep both as supported entry points.
    private readonly Func<CommandAssistRequest, CancellationToken, Task<CommandAssistResponse>>? _commandAssist;

    // Panel navigation: when the user presses ↓ with the panel visible, focus moves INTO the panel
    // (↑↓/PgUp/PgDn scroll a highlighted selection; Enter inserts the flag; Esc / ↑-past-top returns
    // to typing). _panelScroll is the index of the first visible row when the list overflows.
    private bool _panelFocused;
    private int _panelSelected;
    private int _panelScroll;

    // ── buffer ───────────────────────────────────────────────────────────────
    private readonly StringBuilder _buf = new();
    private int _cursor;   // byte offset into _buf

    // ── kill ring ────────────────────────────────────────────────────────────
    private string _killRing = "";

    // ── prompt ───────────────────────────────────────────────────────────────
    private string _prompt = "";

    // ── ANSI sequences ───────────────────────────────────────────────────────
    // Color for the fish-style inline autosuggestion ("ghost" text). Use an explicit gray
    // (bright-black, SGR 90) rather than faint (SGR 2): faint is widely unsupported and on many
    // terminals/color-schemes renders identically to the background, making the ghost vanish.
    // fish and zsh-autosuggestions both default to an explicit gray for exactly this reason.
    private const string SuggestionColor = "\x1b[90m";
    private const string SgrReset = "\x1b[0m";

    // ── constants ────────────────────────────────────────────────────────────
    private const int MaxHistory = 5000;

    // Adapt a synchronous completer into the async shape the editor now uses.
    private static Func<string, int, CancellationToken, Task<IReadOnlyList<CompletionItem>>>? Adapt(
        Func<string, int, IReadOnlyList<CompletionItem>>? sync)
        => sync is null ? null : (line, cursor, _) => Task.FromResult(sync(line, cursor));

    /// <summary>
    /// Creates a new LineEditor with a history store for persistent history.
    /// </summary>
    public LineEditor(
        IHistoryStore historyStore,
        Func<string, int, IReadOnlyList<CompletionItem>>? completer = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? aliases = null)
        : this(historyStore, Adapt(completer), cwd, aliases)
    {
    }

    /// <summary>
    /// Creates a new LineEditor with a history store and an async completer (runspace-backed
    /// completion). The completer receives a CancellationToken bounded by the Tab deadline.
    /// <paramref name="aliases"/> resolves the command under the cursor for the flag-doc panel.
    /// </summary>
    public LineEditor(
        IHistoryStore historyStore,
        Func<string, int, CancellationToken, Task<IReadOnlyList<CompletionItem>>>? completer,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? aliases = null,
        Func<string, int, CancellationToken, Task<IReadOnlyList<FlagHint>>>? flagHintProvider = null,
        Func<CommandAssistRequest, CancellationToken, Task<CommandAssistResponse>>? commandAssist = null)
    {
        _historyStore = historyStore;
        _completer = completer;
        _flagHintProvider = flagHintProvider;
        _commandAssist = commandAssist;
        _suggester = new Suggester(historyStore);
        _cwd = cwd ?? Environment.CurrentDirectory;
        _aliases = aliases ?? EmptyAliases;
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
        Func<string, int, IReadOnlyList<CompletionItem>>? completer = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        _historyStore = new LegacyFileHistoryStore(historyPath);
        _completer = Adapt(completer);
        _suggester = new Suggester(_historyStore);
        _cwd = cwd ?? Environment.CurrentDirectory;
        _aliases = aliases ?? EmptyAliases;
        _history = LoadHistory(historyPath);
        _historyIndex = _history.Count;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyAliases =
        new Dictionary<string, string>(StringComparer.Ordinal);

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

        _prompt = prompt;
        _buf.Clear();
        _cursor = 0;
        _historyIndex = _history.Count;
        _savedInput = "";
        ClearCompletion();
        ClearSuggestion();
        ExitPanelFocus();

        Console.Write(_prompt);

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                // Console handle closed mid-read (terminal disconnect, etc.).
                // Treat as EOF instead of crashing the shell.
                Console.WriteLine();
                return null;
            }

            try
            {

            // Floating-panel focus (P3): when focused, arrow/PgUp/PgDn/Enter/Esc drive the panel.
            // A non-panel key exits focus and is then handled normally below.
            if (_panelFocused)
            {
                if (TryHandlePanelKey(key)) continue;
                _panelFocused = false;
            }
            // ↓ with the panel visible (and not yet focused) moves focus INTO the panel.
            else if (key.Key == ConsoleKey.DownArrow && key.Modifiers == 0 && CurrentFlagHints().Count > 0)
            {
                _panelFocused = true;
                _panelSelected = 0;
                _panelScroll = 0;
                Redraw();
                continue;
            }

            // F1 — open the scrollable man-page browser for the command under the cursor.
            if (key.Key == ConsoleKey.F1)
            {
                ClearSuggestion();
                OpenHelpBrowserFromPrompt();
                continue;
            }

            // Ctrl+~ / Ctrl+^ opens command assist. The current line is passed
            // as context and restored on cancel/no provider, so the hotkey never
            // commits or corrupts the user's in-progress command.
            if (IsCommandAssistKey(key))
            {
                var assistedCommand = await HandleCommandAssistAsync(CancellationToken.None);
                if (assistedCommand is not null)
                {
                    Console.WriteLine();
                    AddToHistory(assistedCommand);
                    return assistedCommand;
                }
                continue;
            }

            // Tab completion
            if (key.Key == ConsoleKey.Tab && key.Modifiers == 0)
            {
                ClearSuggestion();  // Clear suggestion when tab completes
                // Drop the floating flag-doc panel and reprint the bare input line so the
                // completion list (if any) renders cleanly below, with no panel left behind.
                Console.Write("\r\x1b[0J");
                // Drop the floating flag-doc panel and reprint the bare input line so the
                // completion list (if any) renders cleanly below, with no panel left behind.
                Console.Write("\r\x1b[0J");
                Console.Write(_prompt);
                Console.Write(_buf.ToString());
                await HandleTabAsync();
                continue;
            }

            // Any non-Tab key clears the completion cycle
            ClearCompletion();

            // Keys that accept the current suggestion must see it before we clear.
            bool isAcceptSuggestionKey =
                (key.Key == ConsoleKey.RightArrow && key.Modifiers == 0) ||
                key.Key == ConsoleKey.End ||
                (key.Key == ConsoleKey.E && key.Modifiers == ConsoleModifiers.Control);
            if (!isAcceptSuggestionKey)
            {
                ClearSuggestion();  // Clear suggestion on any other key
            }

            // Ctrl-D: EOF on empty buffer, otherwise delete-char
            if (key.Key == ConsoleKey.D && key.Modifiers == ConsoleModifiers.Control)
            {
                if (_buf.Length == 0)
                {
                    Console.WriteLine();
                    return null;   // EOF
                }
                DeleteCharForward();
                await RefreshHintsAsync();
                Redraw();
                continue;
            }

            // Enter / newline
            if (key.Key == ConsoleKey.Enter)
            {
                // Erase any floating flag-doc panel below, reprint the committed line cleanly
                // (no ghost / no panel), then advance past it.
                Console.Write("\r\x1b[0J");
                Console.Write(_prompt);
                Console.Write(_buf.ToString());
                Console.WriteLine();
                var result = _buf.ToString();
                if (result.Length > 0)
                    AddToHistory(result);
                return result;
            }

            // Ctrl-R — reverse-i-search
            if (key.Key == ConsoleKey.R && key.Modifiers == ConsoleModifiers.Control)
            {
                var cmd = await HandleCtrlRAsync();
                if (cmd is not null)
                {
                    SetBuffer(cmd);
                }
                // Always redraw to restore the prompt line, whether or not a result was selected.
                Redraw();
                continue;
            }

            // Ctrl-C — caller handles SIGINT via CancelKeyPress; we just clear the line
            if (key.Key == ConsoleKey.C && key.Modifiers == ConsoleModifiers.Control)
            {
                // Erase any floating panel, reprint the abandoned line with a trailing ^C,
                // then drop to a fresh prompt.
                Console.Write("\r\x1b[0J");
                Console.Write(_prompt);
                Console.Write(_buf.ToString());
                Console.WriteLine("^C");
                _buf.Clear();
                _cursor = 0;
                Console.Write(_prompt);
                continue;
            }

            switch (key.Key)
            {
                // ── cursor movement ──────────────────────────────────────────
                case ConsoleKey.LeftArrow when key.Modifiers == 0:
                    MoveCursor(-1);
                    break;
                case ConsoleKey.RightArrow when key.Modifiers == 0:
                    if (_currentSuggestion is not null && _cursor == _buf.Length)
                    {
                        AcceptSuggestion();
                    }
                    else
                    {
                        MoveCursor(1);
                    }
                    break;
                case ConsoleKey.Home:
                case ConsoleKey.A when key.Modifiers == ConsoleModifiers.Control:
                    MoveCursorTo(0);
                    break;
                case ConsoleKey.End:
                case ConsoleKey.E when key.Modifiers == ConsoleModifiers.Control:
                    if (_currentSuggestion is not null)
                    {
                        AcceptSuggestion();
                    }
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
                    HistoryPrev();
                    break;
                case ConsoleKey.DownArrow:
                    HistoryNext();
                    break;

                // ── deletion ─────────────────────────────────────────────────
                case ConsoleKey.Backspace:
                    DeleteCharBack();
                    await RefreshHintsAsync();
                    Redraw();
                    break;
                case ConsoleKey.Delete:
                    DeleteCharForward();
                    await RefreshHintsAsync();
                    Redraw();
                    break;
                case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                    KillToEnd();
                    await RefreshHintsAsync();
                    Redraw();
                    break;
                case ConsoleKey.U when key.Modifiers == ConsoleModifiers.Control:
                    KillToStart();
                    await RefreshHintsAsync();
                    Redraw();
                    break;
                case ConsoleKey.W when key.Modifiers == ConsoleModifiers.Control:
                    KillWordBack();
                    await RefreshHintsAsync();
                    Redraw();
                    break;
                case ConsoleKey.Y when key.Modifiers == ConsoleModifiers.Control:
                    Yank();
                    await RefreshHintsAsync();
                    Redraw();
                    break;

                // ── misc ─────────────────────────────────────────────────────
                case ConsoleKey.L when key.Modifiers == ConsoleModifiers.Control:
                    // Clear screen
                    Console.Write("\x1b[H\x1b[2J");
                    Redraw();
                    break;

                default:
                    // Printable character
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        InsertChar(key.KeyChar);
                        await RefreshHintsAsync();
                        Redraw();
                    }
                    // Ignore other control sequences (F-keys, etc.)
                    break;
            }
            }
            catch (Exception ex)
            {
                // Per-keystroke guard: never let a bad completion provider,
                // history lookup, regex, etc. crash the shell. Reset visible
                // state to a clean prompt and continue reading.
                ClearCompletion();
                ClearSuggestion();
                Console.WriteLine();
                Console.Error.WriteLine($"ps-bash: line-editor error: {ex.GetType().Name}: {ex.Message}");
                Redraw();
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

    private void HistoryPrev()
    {
        if (_history.Count == 0) return;
        if (_historyIndex == _history.Count)
            _savedInput = _buf.ToString();   // stash current edit
        if (_historyIndex <= 0) return;
        _historyIndex--;
        _buf.Clear();
        _buf.Append(_history[_historyIndex]);
        _cursor = _buf.Length;
        ClearSuggestion();  // No suggestions while navigating history
        Redraw(showSuggestion: false);
    }

    private void HistoryNext()
    {
        if (_historyIndex >= _history.Count) return;
        _historyIndex++;
        var text = _historyIndex == _history.Count ? _savedInput : _history[_historyIndex];
        _buf.Clear();
        _buf.Append(text);
        _cursor = _buf.Length;
        ClearSuggestion();  // No suggestions while navigating history
        Redraw(showSuggestion: false);
    }

    private void AddToHistory(string line)
    {
        var last = _history.Count > 0 ? _history[^1] : null;
        if (last == line) return;

        _history.Add(line);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
    }

    private static List<string> LoadHistory(string path)
    {
        try
        {
            if (File.Exists(path))
                return [.. File.ReadAllLines(path).Where(l => l.Length > 0)];
        }
        catch (Exception) { }
        return [];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tab completion
    // ─────────────────────────────────────────────────────────────────────────

    private async Task HandleTabAsync()
    {
        if (_completer is null) return;

        if (_completions is null)
        {
            // First Tab: compute completions, bounded by a deadline so a slow runspace
            // round-trip cannot hang Tab — on timeout/failure we get whatever the static
            // providers returned (the engine swallows cancellation and falls back).
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionTimeoutMs));
            try
            {
                _completions = await _completer(_buf.ToString(), _cursor, cts.Token);
            }
            catch (Exception)
            {
                _completions = [];
            }
            _completionIndex = 0;

            if (_completions.Count == 0)
            {
                ClearCompletion();
                return;
            }

            // Determine base (text before the token) and token — use quote-aware split
            // so that completing inside a quoted string ("my fi → "my file") works.
            (_completionBase, _completionToken) = TabCompleter.SplitAtWordBoundaryQuoteAware(_buf.ToString(), _cursor);

            if (_completions.Count == 1)
            {
                // Unique match: complete immediately
                ApplyCompletion(_completions[0]);
                ClearCompletion();
                return;
            }

            // Multiple matches: show list below, apply first
            ShowCompletionList(_completions);
            ApplyCompletion(_completions[0]);
            _completionIndex = 1;
        }
        else
        {
            // Subsequent Tab: cycle through matches
            ApplyCompletion(_completions[_completionIndex]);
            _completionIndex = (_completionIndex + 1) % _completions.Count;
        }
    }

    private void ApplyCompletion(CompletionItem completion)
    {
        // Only ever insert InsertText. DisplayText (which may carry a flag description like
        // "-name  - name pattern") is for the list shown to the user and must never reach the buffer.
        var insertText = completion.InsertText;

        var suffix = _buf.ToString(_cursor, _buf.Length - _cursor);
        var addSpace = suffix.Length == 0
            && !insertText.EndsWith('/')
            && !insertText.EndsWith(Path.DirectorySeparatorChar);

        // If the completion base ends with an open double-quote, we are completing
        // inside a quoted argument. Wrap the completion in quotes so spaces in the
        // path are handled correctly.
        var isInsideQuote = _completionBase.Length > 0 && _completionBase[^1] == '"';
        string insertedCompletion;
        if (isInsideQuote && insertText.Contains(' '))
        {
            // Close the quote after the completion, no trailing space (shell adds it).
            insertedCompletion = insertText + "\"";
            addSpace = false;
        }
        else
        {
            insertedCompletion = insertText;
        }

        var newBuf = _completionBase + insertedCompletion + (addSpace ? " " : "") + suffix;
        SetBuffer(newBuf);
        _cursor = (_completionBase + insertedCompletion + (addSpace ? " " : "")).Length;
        Redraw();
    }

    private static void ShowCompletionList(IReadOnlyList<CompletionItem> completions)
    {
        Console.WriteLine();
        // The list shows DisplayText (e.g. "-name  - name pattern"); only InsertText is ever typed.
        var maxLen = completions.Max(c => c.DisplayText.Length) + 2;
        var cols = Math.Max(1, Console.WindowWidth / maxLen);
        int i = 0;
        foreach (var c in completions)
        {
            Console.Write(c.DisplayText.PadRight(maxLen));
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
        Redraw();
    }

    private void RestoreBuffer(string text, int cursor)
    {
        _buf.Clear();
        _buf.Append(text);
        _cursor = Math.Clamp(cursor, 0, _buf.Length);
        Redraw();
    }

    // Buffer mutators do NOT redraw — the keystroke handler in ReadLineAsync
    // calls UpdateSuggestionAsync then Redraw() once after, eliminating the
    // stale-suggestion flicker that came from drawing twice per keystroke.
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
        Redraw();
    }

    private void MoveCursorTo(int pos)
    {
        _cursor = Math.Clamp(pos, 0, _buf.Length);
        Redraw();
    }

    private void MoveCursorWordLeft()
    {
        while (_cursor > 0 && _buf[_cursor - 1] == ' ') _cursor--;
        while (_cursor > 0 && _buf[_cursor - 1] != ' ') _cursor--;
        Redraw();
    }

    private void MoveCursorWordRight()
    {
        while (_cursor < _buf.Length && _buf[_cursor] == ' ') _cursor++;
        while (_cursor < _buf.Length && _buf[_cursor] != ' ') _cursor++;
        Redraw();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Terminal rendering
    // ─────────────────────────────────────────────────────────────────────────

    private void Redraw()
    {
        Redraw(showSuggestion: true);
    }

    private void Redraw(bool showSuggestion)
    {
        // Strip ANSI from prompt to measure visual length
        var promptVisible = StripAnsi(_prompt);
        var text = _buf.ToString();

        // The floating flag-doc panel: rows shown below the input line for the flag-prefix
        // token under the cursor. Empty unless the cursor is on a "-flag" argument of a known
        // command. Suppressed when suggestions are off (history navigation) to avoid noise.
        var panel = showSuggestion ? ComputeFlagPanel() : Array.Empty<PanelRow>();

        // Erase the whole region we own: carriage-return to the input line's column 0, then
        // \x1b[0J wipes the input line AND everything below it (the previous panel). This
        // single erase replaces the old single-line clear and also cleans up a stale panel.
        Console.Write("\r\x1b[0J");

        Console.Write(_prompt);
        Console.Write(text);

        // Inline history ghost — only when there is no flag panel, so the two never compete.
        if (panel.Count == 0 && showSuggestion && _currentSuggestion is { Length: > 0 } && _cursor == _buf.Length)
        {
            Console.Write(SuggestionColor);
            Console.Write(_currentSuggestion);
            Console.Write(SgrReset);
        }

        // Draw the panel below. Each row goes on its own fresh line. Relative cursor moves
        // (the \x1b[{N}A afterwards) stay correct even if writing the rows scrolls the screen,
        // because the distance from the last panel row back up to the input line is always N.
        foreach (var row in panel)
        {
            Console.Write("\r\n\x1b[2K");
            if (row.Highlight)
            {
                Console.Write("\x1b[7m");   // reverse video for the focused selection
                Console.Write(row.Text);
                Console.Write("\x1b[27m");
            }
            else
            {
                Console.Write(SuggestionColor);
                Console.Write(row.Text);
                Console.Write(SgrReset);
            }
        }
        if (panel.Count > 0)
            Console.Write($"\x1b[{panel.Count}A"); // back up to the input line

        // Place the cursor at the logical column on the input line: column 0 then forward.
        var logicalCol = promptVisible.Length + _cursor;
        Console.Write("\r");
        if (logicalCol > 0)
            Console.Write($"\x1b[{logicalCol}C");
    }

    /// <summary>
    /// The flag/parameter hints for the token under the cursor: bash flag specs (synchronous,
    /// always fresh) when the command is a bash command; otherwise the cached async PowerShell
    /// parameter hints, but only while their key still matches the cursor. Empty when neither
    /// applies. Never throws.
    /// </summary>
    private IReadOnlyList<FlagHint> CurrentFlagHints()
    {
        try
        {
            var specs = TabCompleter.MatchingFlagSpecs(_buf.ToString(), _cursor, _aliases);
            if (specs.Count > 0)
            {
                return specs.Select(s => new FlagHint(
                    s.Flag,
                    s.Arg is { Length: > 0 } ? $"{s.Flag} {s.Arg}" : s.Flag,
                    s.Desc, s.Detail, s.Examples)).ToList();
            }

            // PowerShell-cmdlet params (async, cached) — only if the cache is for this exact token.
            if (_psPanelHints is { Count: > 0 } && _psPanelKey is not null && _psPanelKey == CurrentHintKey())
                return _psPanelHints;

            return Array.Empty<FlagHint>();
        }
        catch (Exception)
        {
            return Array.Empty<FlagHint>();
        }
    }

    /// <summary>One rendered panel line: its text and whether it is the focused selection.</summary>
    private readonly record struct PanelRow(string Text, bool Highlight);

    /// <summary>
    /// Rows for the floating flag-doc panel: each hint as "head  desc", two-space indented and
    /// column-aligned. When unfocused the list is capped at <see cref="MaxPanelRows"/> with a
    /// "press ↓" overflow hint; when focused it shows a scroll window around the highlighted
    /// selection. Empty when not on a documented flag/parameter token. Never throws — advisory.
    /// </summary>
    private IReadOnlyList<PanelRow> ComputeFlagPanel()
    {
        try
        {
            var hints = CurrentFlagHints();
            if (hints.Count == 0)
                return Array.Empty<PanelRow>();

            var headWidth = hints.Max(h => h.Head.Length);
            int width;
            try { width = Console.WindowWidth; } catch { width = 80; }
            var maxWidth = Math.Max(20, width - 1);

            string Format(FlagHint h)
            {
                var row = h.Desc.Length > 0
                    ? "  " + h.Head.PadRight(headWidth + 2) + h.Desc
                    : "  " + h.Head;
                return row.Length > maxWidth ? row[..maxWidth] : row;
            }

            var rows = new List<PanelRow>(MaxPanelRows + 1);

            if (!_panelFocused)
            {
                // Passive preview: first MaxPanelRows; if more, an overflow hint instead of scrolling.
                int take = Math.Min(MaxPanelRows, hints.Count);
                for (int k = 0; k < take; k++)
                    rows.Add(new PanelRow(Format(hints[k]), false));
                if (hints.Count > MaxPanelRows)
                    rows.Add(new PanelRow($"  ↓ {hints.Count - MaxPanelRows} more — press ↓ to scroll", false));
                return rows;
            }

            // Focused: clamp selection/scroll, render a window with the selection highlighted.
            int window = Math.Min(MaxPanelRows, hints.Count);
            int selected = Math.Clamp(_panelSelected, 0, hints.Count - 1);
            int scroll = Math.Clamp(_panelScroll, 0, Math.Max(0, hints.Count - window));
            for (int k = scroll; k < scroll + window && k < hints.Count; k++)
                rows.Add(new PanelRow(Format(hints[k]), k == selected));
            // A position indicator when the list overflows the window.
            if (hints.Count > window)
                rows.Add(new PanelRow($"  [{selected + 1}/{hints.Count}]  ↑↓ scroll · Enter insert · Esc back", false));
            return rows;
        }
        catch (Exception)
        {
            // The panel is advisory; never let it break the line editor.
            return Array.Empty<PanelRow>();
        }
    }

    // ── panel focus navigation (P3) ───────────────────────────────────────────

    /// <summary>
    /// Handle a key while the panel is focused. Returns true if the key was consumed (navigation,
    /// insert, or exit); false to let normal line editing handle it (which also exits focus).
    /// </summary>
    private bool TryHandlePanelKey(ConsoleKeyInfo key)
    {
        int total = CurrentFlagHints().Count;
        if (total == 0) { _panelFocused = false; return false; }
        int window = Math.Min(MaxPanelRows, total);

        switch (key.Key)
        {
            case ConsoleKey.DownArrow when key.Modifiers == 0:
                if (_panelSelected < total - 1) _panelSelected++;
                EnsurePanelScroll(window, total); Redraw(); return true;
            case ConsoleKey.UpArrow when key.Modifiers == 0:
                if (_panelSelected <= 0) { ExitPanelFocus(); Redraw(); return true; }
                _panelSelected--; EnsurePanelScroll(window, total); Redraw(); return true;
            case ConsoleKey.PageDown:
                _panelSelected = Math.Min(total - 1, _panelSelected + window);
                EnsurePanelScroll(window, total); Redraw(); return true;
            case ConsoleKey.PageUp:
                _panelSelected = Math.Max(0, _panelSelected - window);
                EnsurePanelScroll(window, total); Redraw(); return true;
            case ConsoleKey.Escape:
                ExitPanelFocus(); Redraw(); return true;
            case ConsoleKey.Enter:
                InsertFlagFromPanel(); return true;
            case ConsoleKey.RightArrow when key.Modifiers == 0:
                // Drill into the man-page detail for the selected option.
                OpenHelpBrowser(CurrentFlagHints(), _panelSelected);
                return true;
            default:
                return false; // not a panel key → caller exits focus and re-handles it
        }
    }

    private void EnsurePanelScroll(int window, int total)
        => _panelScroll = ComputeScroll(_panelSelected, _panelScroll, window, total);

    /// <summary>
    /// Pure scroll-window math: given the selected index, the current top-row offset, the visible
    /// window size, and the total row count, return the offset that keeps the selection visible
    /// (and never scrolls past the end). Extracted so the trickiest panel logic is unit-testable.
    /// </summary>
    internal static int ComputeScroll(int selected, int scroll, int window, int total)
    {
        if (selected < scroll) scroll = selected;
        else if (selected >= scroll + window) scroll = selected - window + 1;
        return Math.Clamp(scroll, 0, Math.Max(0, total - window));
    }

    private void ExitPanelFocus()
    {
        _panelFocused = false;
        _panelSelected = 0;
        _panelScroll = 0;
    }

    /// <summary>Insert the selected panel hint's bare flag at the cursor, then leave focus.</summary>
    private void InsertFlagFromPanel()
    {
        var hints = CurrentFlagHints();
        if (_panelSelected < 0 || _panelSelected >= hints.Count)
        {
            ExitPanelFocus();
            Redraw();
            return;
        }

        InsertFlagAtToken(hints[_panelSelected].Insert);
        ExitPanelFocus();
        _psPanelHints = null;
        _psPanelKey = null;
        Redraw();
    }

    /// <summary>Replace the partial flag token under the cursor with <paramref name="insert"/> + space.</summary>
    private void InsertFlagAtToken(string insert)
    {
        var line = _buf.ToString();
        var (baseText, _) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, _cursor);
        var suffix = _cursor <= line.Length ? line[_cursor..] : string.Empty;
        var newBuf = baseText + insert + " " + suffix;
        _buf.Clear();
        _buf.Append(newBuf);
        _cursor = (baseText + insert + " ").Length;
    }

    // ── man-page drill-down (P4) ───────────────────────────────────────────────

    /// <summary>
    /// Open the scrollable man-page browser for <paramref name="hints"/> (alt-screen). On Enter the
    /// chosen flag replaces the current token. Always restores the prompt afterward. No-op if empty.
    /// </summary>
    private void OpenHelpBrowser(IReadOnlyList<FlagHint> hints, int selected)
    {
        if (hints.Count == 0)
            return;

        var cmd = TabCompleter.GetCommandNameAtCursor(_buf.ToString(), _cursor, _aliases) ?? "help";
        string? insert = null;
        try
        {
            var browser = new FlagHelpBrowser($"Help: {cmd}", hints, selected);
            var (result, chosen) = browser.Run();
            if (result == FlagHelpBrowser.Result.Insert)
                insert = chosen;
        }
        catch (Exception)
        {
            // Advisory; never let the browser crash the shell.
        }

        ExitPanelFocus();
        _psPanelHints = null;
        _psPanelKey = null;
        if (insert is not null)
            InsertFlagAtToken(insert);
        Redraw();
    }

    /// <summary>
    /// Open the man-page browser for the command at the cursor from the prompt (F1): the matching
    /// flags if on a flag token, otherwise the command's full flag set (bash). No-op when there is
    /// nothing to show.
    /// </summary>
    private void OpenHelpBrowserFromPrompt()
    {
        var hints = CurrentFlagHints();
        if (hints.Count == 0)
        {
            // Not on a flag token (or PS with nothing cached): fall back to the command's full
            // bash flag set so F1 works even at "find ⎵".
            var all = TabCompleter.AllFlagSpecsForCommand(_buf.ToString(), _cursor, _aliases);
            hints = all.Select(s => new FlagHint(
                s.Flag,
                s.Arg is { Length: > 0 } ? $"{s.Flag} {s.Arg}" : s.Flag,
                s.Desc, s.Detail, s.Examples)).ToList();
        }
        OpenHelpBrowser(hints, 0);
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

    /// <summary>
    /// Refresh both keystroke-driven hints: the inline history ghost and the floating panel's
    /// async PowerShell-parameter cache. The bash flag panel is computed synchronously in Redraw
    /// (always fresh), so it is not refreshed here.
    /// </summary>
    private async Task RefreshHintsAsync()
    {
        await UpdateSuggestionAsync();
        await UpdatePsFlagHintsAsync();
    }

    /// <summary>
    /// The "commandtoken" identity of the flag token under the cursor, or null when the cursor is
    /// not on a <c>-</c>-prefixed argument token. Used to key the async PS-param cache so a result
    /// only paints while it still matches what the user is looking at.
    /// </summary>
    private string? CurrentHintKey()
    {
        var line = _buf.ToString();
        if (TabCompleter.IsFirstWord(line, _cursor))
            return null;
        var (beforeToken, token) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, _cursor);
        if (token.Length == 0 || token[0] != '-')
            return null;
        var cmd = TabCompleter.GetCommandNameAtCursor(beforeToken, beforeToken.Length, _aliases);
        if (cmd is null)
            return null;
        return cmd + "" + token;
    }

    /// <summary>
    /// Populate the async PowerShell-parameter panel cache for the current flag token. No-op (and
    /// clears the cache) when there is no provider, the token is bash-served (the sync panel wins),
    /// or the cursor is not on a flag. Bounded by a short deadline so a busy worker never blocks
    /// typing; on timeout / failure the cache is simply left empty.
    /// </summary>
    private async Task UpdatePsFlagHintsAsync()
    {
        if (_flagHintProvider is null)
            return;

        var key = CurrentHintKey();
        if (key is null)
        {
            _psPanelHints = null;
            _psPanelKey = null;
            return;
        }

        // The bash flag panel (synchronous, always fresh) takes precedence; don't double up.
        if (TabCompleter.MatchingFlagSpecs(_buf.ToString(), _cursor, _aliases).Count > 0)
        {
            _psPanelHints = null;
            _psPanelKey = null;
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionTimeoutMs));
            var hints = await _flagHintProvider(_buf.ToString(), _cursor, cts.Token).ConfigureAwait(false);
            _psPanelHints = hints.Count > 0 ? hints : null;
            _psPanelKey = key;
        }
        catch (Exception)
        {
            // Advisory; never let a slow/failed worker break the prompt.
            _psPanelHints = null;
            _psPanelKey = null;
        }
    }

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
        Redraw();
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
    private async Task<string?> HandleCtrlRAsync()
    {
        // Seed the reverse-i-search with whatever the user has already typed on the line.
        var search = new CtrlRSearch(_historyStore, _cwd, _prompt, initialQuery: _buf.ToString());
        var (result, command) = await search.RunAsync();

        if (result == CtrlRSearch.Result.Execute && command is not null)
        {
            return command;
        }

        return null;
    }

    internal static bool IsCommandAssistKey(ConsoleKeyInfo key)
    {
        if (key.KeyChar == '\u001e') return true;
        if (key.Key == ConsoleKey.Oem3 && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return true;
        if (key.Key == ConsoleKey.D6 && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return true;
        return false;
    }

    internal static (string Buffer, int Cursor) ApplyCommandAssistResponse(
        string buffer,
        int cursor,
        CommandAssistResponse response)
    {
        if (response.Action == CommandAssistResponseAction.Cancel || response.Command is null)
            return (buffer, cursor);
        return (response.Command, response.Command.Length);
    }

    private async Task<string?> HandleCommandAssistAsync(CancellationToken ct)
    {
        var originalBuffer = _buf.ToString();
        var originalCursor = _cursor;
        ClearCompletion();
        ClearSuggestion();
        ExitPanelFocus();

        try
        {
            Console.Write("\r\x1b[0J");
            Console.Write(_prompt);
            Console.Write(originalBuffer);
            Console.WriteLine();

            if (_commandAssist is null)
            {
                Console.WriteLine("ps-bash: command assist is not configured; returning to prompt.");
                RestoreBuffer(originalBuffer, originalCursor);
                return null;
            }

            var response = await _commandAssist(
                new CommandAssistRequest(originalBuffer, originalCursor),
                ct).ConfigureAwait(false);
            if (response.Action == CommandAssistResponseAction.Execute && response.Command is { Length: > 0 } command)
            {
                RestoreBuffer(command, command.Length);
                return command;
            }

            var (buffer, cursor) = ApplyCommandAssistResponse(originalBuffer, originalCursor, response);
            RestoreBuffer(buffer, cursor);
            return null;
        }
        catch (OperationCanceledException)
        {
            RestoreBuffer(originalBuffer, originalCursor);
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ps-bash: command assist failed: {ex.Message}");
            RestoreBuffer(originalBuffer, originalCursor);
            return null;
        }
    }
}

internal readonly record struct CommandAssistRequest(string Buffer, int Cursor);

internal enum CommandAssistResponseAction
{
    Cancel,
    Insert,
    Execute,
}

internal readonly record struct CommandAssistResponse(CommandAssistResponseAction Action, string? Command)
{
    public static CommandAssistResponse Cancelled { get; } = new(CommandAssistResponseAction.Cancel, null);
    public static CommandAssistResponse Insert(string command) => new(CommandAssistResponseAction.Insert, command);
    public static CommandAssistResponse Execute(string command) => new(CommandAssistResponseAction.Execute, command);
    public static CommandAssistResponse ReplaceWith(string replacement) => Insert(replacement);
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
        catch (Exception) { /* routine: legacy file history is best-effort */ }
    }
}
