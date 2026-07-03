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
    // In-memory cache for fast navigation. `volatile` because the background
    // LoadHistoryAsync PUBLISHES the loaded history by swapping this reference; the
    // key-loop thread reads it for ↑/↓ navigation. All in-place mutations (append)
    // happen on the single key-loop thread, so the only cross-thread event is that
    // one atomic swap — a reader therefore always sees a COMPLETE list.
    private volatile List<string> _history;  // In-memory cache for fast navigation
    private int _historyIndex;         // points into _history; _history.Count = current input
    private string _savedInput = "";   // stashed current input while navigating history
    private readonly SemaphoreSlim _historyLock = new(1, 1);

    // ── autosuggestion ────────────────────────────────────────────────────────
    private readonly Suggester _suggester;
    private string? _currentSuggestion;  // Suffix to append (null = no suggestion)
    private readonly string _cwd;  // Current working directory for suggestions

    // ── async, debounced prediction ───────────────────────────────────────────
    // The prediction (inline history ghost + PowerShell-parameter panel) NEVER gates the glyph echo:
    // a keystroke mutates the buffer and Redraw()s the typed character immediately, THEN awaits the
    // prediction inline (single flow — no background thread touches the console, which .NET's Console
    // does not allow concurrently). The lookup is skipped while more input is queued and re-checked
    // after it completes (MaybeRenderPredictionAsync), so fast typing / paste never blocks on it and a
    // result for a line the user has moved past is discarded rather than painted.

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
    // keystroke (ComputeHintsAsync) and rendered by ComputeFlagPanel only while the key still
    // matches the cursor — so a stale async result never paints under a changed token.
    private IReadOnlyList<FlagHint>? _psPanelHints;
    private string? _psPanelKey;

    // Ctrl+~ command assist. Most terminals encode Ctrl+~ as ASCII 0x1e, the
    // same control character as Ctrl+^; keep both as supported entry points.
    private readonly Func<CommandAssistRequest, CancellationToken, Task<CommandAssistResponse>>? _commandAssist;

    // Optional frecency ghost-text provider (cd/z/zi → directory suffix). Tried
    // before the history suggester so a jump command previews its target.
    private readonly Func<string, Task<string?>>? _frecencySuggest;

    // Optional background cache of live PowerShell command names (loaded modules + session-defined
    // functions/aliases). Read synchronously on the keystroke path by the command-position panel so
    // it surfaces real PS commands without a runspace round-trip. Null when no live worker is wired.
    private readonly CommandNameCache? _commandNameCache;

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

    // ── multi-row render tracking ──────────────────────────────────────────────
    // Where the last render left the terminal cursor, measured as row/rows within
    // the region WE own (prompt + wrapped input + flag panel). Redraw uses
    // _renderCursorRow to walk BACK UP to the region's first row before erasing, so a
    // line that wraps to multiple rows is cleared in full. Without this the erase only
    // cleared from the cursor's (bottom) row down, so each keystroke on a wrapped line
    // re-rendered one row lower (the "adds a row per character" bug).
    private int _renderRows = 1;
    private int _renderCursorRow;

    // ── ANSI sequences ───────────────────────────────────────────────────────
    // Color for the fish-style inline autosuggestion ("ghost" text). Use an explicit gray
    // (bright-black, SGR 90) rather than faint (SGR 2): faint is widely unsupported and on many
    // terminals/color-schemes renders identically to the background, making the ghost vanish.
    // fish and zsh-autosuggestions both default to an explicit gray for exactly this reason.
    private const string SuggestionColor = "\x1b[90m";
    private const string SgrReset = "\x1b[0m";

    // ── constants ────────────────────────────────────────────────────────────
    private const int MaxHistory = 5000;

    // Kill switch: PSBASH_NO_PREDICT=1 disables the inline history ghost + flag panel entirely.
    // Insurance against a prediction-path regression ever degrading input — set it and the line
    // editor is a plain echo loop you can always type into. Read once at process start.
    private static readonly bool PredictionDisabled =
        Environment.GetEnvironmentVariable("PSBASH_NO_PREDICT") is "1" or "true";

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
        Func<CommandAssistRequest, CancellationToken, Task<CommandAssistResponse>>? commandAssist = null,
        Func<string, Task<string?>>? frecencySuggest = null,
        CommandNameCache? commandNameCache = null)
    {
        _historyStore = historyStore;
        _completer = completer;
        _flagHintProvider = flagHintProvider;
        _commandAssist = commandAssist;
        _frecencySuggest = frecencySuggest;
        _commandNameCache = commandNameCache;
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
        // Fetch OUTSIDE any lock (async I/O), then PUBLISH by an atomic reference swap
        // rather than an in-place Clear()+AddRange(). The key loop reads _history
        // unsynchronized for ↑/↓ navigation; the old in-place mutation exposed a
        // window where the list was empty (between Clear and AddRange), so pressing ↑
        // early threw (index out of range) and wiped the in-progress line. A swap means
        // a concurrent reader always observes a COMPLETE list — the old one or the new.
        var entries = await _historyStore.SearchAsync(new HistoryQuery { Limit = MaxHistory });
        var loaded = new List<string>(entries.Select(e => e.Command).Reverse());
        _history = loaded;
        _historyIndex = loaded.Count;
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
        ResetRenderStateForPrompt();

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
            else if (key.Key == ConsoleKey.DownArrow && key.Modifiers == 0 && CurrentFlagHints(_buf.ToString(), _cursor).Count > 0)
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
                // Drop the floating flag-doc panel and reprint the bare input line (fully
                // erasing any wrapped rows first) so the completion list renders cleanly below.
                ReprintBareLine();
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
                Redraw();                            // echo the edit immediately
                await MaybeRenderPredictionAsync();   // prediction renders after; never blocks the echo
                continue;
            }

            // Enter / newline
            if (key.Key == ConsoleKey.Enter)
            {
                // Erase the whole (possibly wrapped) region, reprint the committed line
                // cleanly (no ghost / no panel), then advance past all of its rows.
                ReprintBareLine();
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
                // Erase the whole (possibly wrapped) region, reprint the abandoned line with
                // a trailing ^C, then drop to a fresh prompt.
                ReprintBareLine();
                Console.WriteLine("^C");
                _buf.Clear();
                _cursor = 0;
                Console.Write(_prompt);
                ResetRenderStateForPrompt();
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
                    Redraw();                            // echo the edit immediately
                    await MaybeRenderPredictionAsync();   // prediction renders after; never blocks the echo
                    break;
                case ConsoleKey.Delete:
                    DeleteCharForward();
                    Redraw();                            // echo the edit immediately
                    await MaybeRenderPredictionAsync();   // prediction renders after; never blocks the echo
                    break;
                case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                    KillToEnd();
                    Redraw();                            // echo the edit immediately
                    await MaybeRenderPredictionAsync();   // prediction renders after; never blocks the echo
                    break;
                case ConsoleKey.U when key.Modifiers == ConsoleModifiers.Control:
                    KillToStart();
                    Redraw();                            // echo the edit immediately
                    await MaybeRenderPredictionAsync();   // prediction renders after; never blocks the echo
                    break;
                case ConsoleKey.W when key.Modifiers == ConsoleModifiers.Control:
                    KillWordBack();
                    Redraw();                            // echo the edit immediately
                    await MaybeRenderPredictionAsync();   // prediction renders after; never blocks the echo
                    break;
                case ConsoleKey.Y when key.Modifiers == ConsoleModifiers.Control:
                    Yank();
                    Redraw();                            // echo the edit immediately
                    await MaybeRenderPredictionAsync();   // prediction renders after; never blocks the echo
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
                        // Echo the glyph BEFORE any prediction lookup — the visible character must
                        // never wait on history/runspace I/O (the suggestion was already cleared
                        // above, so this paints no stale ghost). The prediction is then computed and
                        // rendered inline (debounced), still without ever gating this echo.
                        Redraw();
                        await MaybeRenderPredictionAsync();
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
                return [.. File.ReadLines(path).Where(l => l.Length > 0).TakeLast(MaxHistory)];
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

            // Multiple matches: show list below, apply first. ShowCompletionList advances
            // the cursor onto a fresh line beneath the list, so reset the region geometry —
            // the following redraw draws a new prompt HERE, it must not walk up into the list.
            ShowCompletionList(_completions);
            _renderCursorRow = 0;
            _renderRows = 1;
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
        // Only reached from the command-assist flow, which has just printed output and
        // left the cursor on a fresh line. Treat that line as the region origin so the
        // redraw does not walk up into the printed assist output.
        _renderCursorRow = 0;
        _renderRows = 1;
        Redraw();
    }

    // Buffer mutators do NOT redraw — the keystroke handler in ReadLineAsync calls Redraw() once
    // immediately after to echo the edit, then awaits MaybeRenderPredictionAsync() to compute and
    // paint the prediction (debounced, inline on the same flow) without ever gating that echo.
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

    private void Redraw() => Redraw(showSuggestion: true);

    // Renders the current line. Only ever called on the single key-loop flow (the printable/edit
    // handlers call it before awaiting the prediction, and MaybeRenderPredictionAsync calls it after),
    // so there is never a second thread writing the console.
    private void Redraw(bool showSuggestion)
    {
        var promptVisible = StripAnsi(_prompt);
        var text = _buf.ToString();
        var cursor = Math.Clamp(_cursor, 0, text.Length);
        int cols = Math.Max(1, SafeWindowWidth());

        // The floating flag-doc panel: rows shown below the input line for the flag-prefix
        // token under the cursor. Empty unless the cursor is on a "-flag" argument of a known
        // command. Suppressed when suggestions are off (history navigation) to avoid noise.
        var panel = showSuggestion ? ComputeFlagPanel(text, cursor) : Array.Empty<PanelRow>();
        string? ghost = panel.Count == 0 && showSuggestion
                        && _currentSuggestion is { Length: > 0 } && cursor == text.Length
            ? _currentSuggestion : null;

        var r = ComputeRender(_prompt, DisplayWidth(promptVisible), text, cursor, ghost, panel, cols, _renderCursorRow);
        Console.Write(r.Sequence);
        _renderRows = r.TotalRows;
        _renderCursorRow = r.CursorRow;
    }

    internal readonly record struct RenderResult(string Sequence, int TotalRows, int CursorRow);

    /// <summary>
    /// Pure builder for one redraw's terminal byte sequence, given the display geometry.
    /// Extracted from <see cref="Redraw(bool)"/> so the wrap / wide-character / cursor math
    /// is unit-testable (feed the result through a terminal-grid simulator) without a live
    /// console. Erases the whole region (walking up <paramref name="prevCursorRow"/> rows
    /// first — the multi-row wrap fix), rewrites prompt + text + ghost + panel, and
    /// repositions to the logical cursor by display column. Returns the sequence plus the
    /// new region geometry the caller must persist for the next erase.
    /// </summary>
    internal static RenderResult ComputeRender(
        string promptRaw, int promptWidth,
        string text, int cursor,
        string? ghost,
        IReadOnlyList<PanelRow> panel,
        int cols, int prevCursorRow)
    {
        cols = Math.Max(1, cols);
        cursor = Math.Clamp(cursor, 0, text.Length);

        int cursorW = promptWidth + DisplayWidth(text.AsSpan(0, cursor));
        int contentW = promptWidth + DisplayWidth(text)
                       + (ghost is { Length: > 0 } ? DisplayWidth(ghost) : 0);

        var sb = new StringBuilder();

        // 1. Erase the ENTIRE region: walk up to the first (prompt) row, then clear to end of
        //    screen. The old `\r\x1b[0J` cleared only from the cursor's current (bottom) row
        //    down, leaving rows above intact, so each keystroke on a wrapped line re-rendered
        //    one row lower ("adds a row per character").
        if (prevCursorRow > 0) sb.Append($"\x1b[{prevCursorRow}A");
        sb.Append("\r\x1b[0J");

        // 2. Prompt + text + optional inline history ghost.
        sb.Append(promptRaw);
        sb.Append(text);
        if (ghost is { Length: > 0 })
        {
            sb.Append(SuggestionColor);
            sb.Append(ghost);
            sb.Append(SgrReset);
        }

        // Exact-fill guard: when content ends exactly on a column boundary the terminal leaves
        // the cursor pending at the row's right edge rather than on the next row, desyncing the
        // row math. Force the wrap deterministically.
        bool forcedNewline = contentW > 0 && contentW % cols == 0;
        if (forcedNewline) sb.Append("\r\n");

        int contentRows = (contentW == 0 ? 1 : (contentW - 1) / cols + 1) + (forcedNewline ? 1 : 0);

        // 3. Flag panel, each row on its own fresh line below the input.
        foreach (var row in panel)
        {
            sb.Append("\r\n\x1b[2K");
            if (row.Highlight)
            {
                sb.Append("\x1b[7m");
                sb.Append(row.Text);
                sb.Append("\x1b[27m");
            }
            else
            {
                sb.Append(SuggestionColor);
                sb.Append(row.Text);
                sb.Append(SgrReset);
            }
        }

        int totalRows = contentRows + panel.Count;

        // 4. Reposition to the logical cursor (row, col). After the writes above the terminal
        //    cursor sits on the LAST rendered row, so move up to the cursor's row then set the
        //    column — both from display columns, so wide glyphs land right.
        int cursorRow = cursorW / cols;
        int cursorCol = cursorW % cols;
        int afterRow = totalRows - 1;
        if (afterRow > cursorRow) sb.Append($"\x1b[{afterRow - cursorRow}A");
        sb.Append('\r');
        if (cursorCol > 0) sb.Append($"\x1b[{cursorCol}C");

        return new RenderResult(sb.ToString(), totalRows, cursorRow);
    }

    /// <summary>
    /// The flag/parameter hints for the token under the cursor: bash flag specs (synchronous,
    /// always fresh) when the command is a bash command; otherwise the cached async PowerShell
    /// parameter hints, but only while their key still matches the cursor. Empty when neither
    /// applies. Never throws.
    /// </summary>
    private IReadOnlyList<FlagHint> CurrentFlagHints(string line, int cursor)
    {
        try
        {
            // Command position: the live command-doc panel — the first-word counterpart to the
            // flag panel. As the user types a command prefix, show the matching commands/aliases,
            // including PowerShell commands from the background cache (loaded modules + functions /
            // aliases the session has defined). The cache read is synchronous (no runspace round-trip).
            var psCommands = _commandNameCache?.Names;
            var commands = TabCompleter.MatchingCommandNames(line, cursor, _aliases, psCommands);
            if (commands.Count > 0)
            {
                return commands.Select(c => new FlagHint(
                    c, c, CommandHintDesc(c))).ToList();
            }

            var specs = TabCompleter.MatchingFlagSpecs(line, cursor, _aliases);
            if (specs.Count > 0)
            {
                return specs.Select(s => new FlagHint(
                    s.Flag,
                    s.Arg is { Length: > 0 } ? $"{s.Flag} {s.Arg}" : s.Flag,
                    s.Desc, s.Detail, s.Examples)).ToList();
            }

            // PowerShell-cmdlet params (async, cached) — only if the cache is for this exact token.
            if (_psPanelHints is { Count: > 0 } && _psPanelKey is not null && _psPanelKey == HintKeyFor(line, cursor))
                return _psPanelHints;

            return Array.Empty<FlagHint>();
        }
        catch (Exception)
        {
            return Array.Empty<FlagHint>();
        }
    }

    /// <summary>
    /// The desc shown beside a command-position panel row: a user alias shows its expansion
    /// (<c>→ ls -la</c>); a PowerShell command shows its kind from the live cache ("PowerShell
    /// cmdlet/function/alias", or the static-fallback "PowerShell cmdlet" during warmup); a bash
    /// command shows nothing.
    /// </summary>
    private string CommandHintDesc(string command)
    {
        if (_aliases.TryGetValue(command, out var exp))
            return $"→ {exp}";
        return _commandNameCache?.DescribeKind(command)
            ?? (TabCompleter.IsKnownPowerShellCommand(command) ? "PowerShell cmdlet" : string.Empty);
    }

    /// <summary>One rendered panel line: its text and whether it is the focused selection.</summary>
    internal readonly record struct PanelRow(string Text, bool Highlight);

    /// <summary>
    /// Rows for the floating flag-doc panel: each hint as "head  desc", two-space indented and
    /// column-aligned. When unfocused the list is capped at <see cref="MaxPanelRows"/> with a
    /// "press ↓" overflow hint; when focused it shows a scroll window around the highlighted
    /// selection. Empty when not on a documented flag/parameter token. Never throws — advisory.
    /// </summary>
    private IReadOnlyList<PanelRow> ComputeFlagPanel(string line, int cursor)
    {
        try
        {
            var hints = CurrentFlagHints(line, cursor);
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
                    rows.Add(new PanelRow($"  ↓ {hints.Count - MaxPanelRows} more · F1 details · ↓ focus", false));
                else
                    rows.Add(new PanelRow("  F1 details · ↓ focus", false));
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
                rows.Add(new PanelRow($"  [{selected + 1}/{hints.Count}]  ↑↓ scroll · → details · Enter insert · Esc", false));
            else
                rows.Add(new PanelRow("  ↑↓ select · → details · Enter insert · Esc", false));
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
        int total = CurrentFlagHints(_buf.ToString(), _cursor).Count;
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
                OpenHelpBrowser(CurrentFlagHints(_buf.ToString(), _cursor), _panelSelected);
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
        var hints = CurrentFlagHints(_buf.ToString(), _cursor);
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
        var hints = CurrentFlagHints(_buf.ToString(), _cursor);
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

    // ── display-width + region helpers (multi-row rendering) ────────────────────

    /// <summary>Current terminal width in columns, never throwing and never zero.</summary>
    private static int SafeWindowWidth()
    {
        try { int w = Console.WindowWidth; return w > 0 ? w : 80; }
        catch { return 80; }
    }

    /// <summary>
    /// Display width (terminal columns) of <paramref name="s"/>, accounting for wide
    /// East-Asian / emoji code points (2 columns) and zero-width combining / format /
    /// control characters (0 columns). This is what fixes the CJK/emoji cursor drift:
    /// the old code used <c>string.Length</c> (UTF-16 code units), which over- or
    /// under-counts every non-ASCII glyph. ZWJ emoji sequences are still approximated
    /// (each component counted) — a documented residual, not a crash.
    /// </summary>
    internal static int DisplayWidth(ReadOnlySpan<char> s)
    {
        int w = 0;
        int i = 0;
        while (i < s.Length)
        {
            if (System.Text.Rune.DecodeFromUtf16(s[i..], out var rune, out int consumed)
                == System.Buffers.OperationStatus.Done)
            {
                w += RuneWidth(rune.Value);
                i += consumed;
            }
            else
            {
                w += 1;   // lone surrogate / invalid unit → assume 1 column
                i += 1;
            }
        }
        return w;
    }

    internal static int DisplayWidth(string s) => DisplayWidth(s.AsSpan());

    private static int RuneWidth(int cp)
    {
        if (cp == 0) return 0;
        switch (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(cp))
        {
            case System.Globalization.UnicodeCategory.Control:
            case System.Globalization.UnicodeCategory.NonSpacingMark:
            case System.Globalization.UnicodeCategory.EnclosingMark:
            case System.Globalization.UnicodeCategory.Format:
                return 0;
        }
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) ||   // Hangul Jamo
        (cp >= 0x2E80 && cp <= 0x303E) ||   // CJK radicals, Kangxi
        (cp >= 0x3041 && cp <= 0x33FF) ||   // Hiragana … CJK symbols
        (cp >= 0x3400 && cp <= 0x4DBF) ||   // CJK Ext A
        (cp >= 0x4E00 && cp <= 0x9FFF) ||   // CJK Unified
        (cp >= 0xA000 && cp <= 0xA4CF) ||   // Yi
        (cp >= 0xAC00 && cp <= 0xD7A3) ||   // Hangul syllables
        (cp >= 0xF900 && cp <= 0xFAFF) ||   // CJK compatibility
        (cp >= 0xFE10 && cp <= 0xFE19) ||   // vertical forms
        (cp >= 0xFE30 && cp <= 0xFE6F) ||   // CJK compatibility forms
        (cp >= 0xFF00 && cp <= 0xFF60) ||   // fullwidth forms
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||
        (cp >= 0x1F300 && cp <= 0x1FAFF) || // emoji & pictographs
        (cp >= 0x20000 && cp <= 0x3FFFD);   // CJK Ext B+

    /// <summary>
    /// Erase the ENTIRE region we own: walk up to the region's first (prompt) row,
    /// carriage-return to column 0, then <c>ESC[0J</c> (clear to end of screen). Leaves
    /// the cursor at the region origin and resets the tracked geometry to a single row.
    /// </summary>
    private void EraseRegion()
    {
        var sb = new StringBuilder();
        if (_renderCursorRow > 0) sb.Append($"\x1b[{_renderCursorRow}A");
        sb.Append("\r\x1b[0J");
        Console.Write(sb.ToString());
        _renderCursorRow = 0;
        _renderRows = 1;
    }

    /// <summary>
    /// Record that only the (freshly written) prompt occupies the region — the buffer is
    /// empty and the cursor sits right after the prompt. Called after every raw
    /// <c>Console.Write(_prompt)</c> that (re)starts a line so the next Redraw walks back
    /// up the correct number of rows.
    /// </summary>
    private void ResetRenderStateForPrompt()
    {
        int cols = Math.Max(1, SafeWindowWidth());
        int promptW = DisplayWidth(StripAnsi(_prompt));
        _renderCursorRow = promptW / cols;
        _renderRows = _renderCursorRow + 1;
    }

    /// <summary>
    /// Reprint the bare input line (prompt + buffer, no ghost / no panel) after fully
    /// erasing the current multi-row region, leaving the cursor at the end of the
    /// buffer. Used by the Tab / Enter / Ctrl-C paths that need a clean line before
    /// handing off (Tab), or before advancing past it (Enter / Ctrl-C).
    /// </summary>
    private void ReprintBareLine()
    {
        EraseRegion();
        var text = _buf.ToString();
        Console.Write(_prompt);
        Console.Write(text);

        int cols = Math.Max(1, SafeWindowWidth());
        int endW = DisplayWidth(StripAnsi(_prompt)) + DisplayWidth(text);
        _renderRows = endW == 0 ? 1 : (endW - 1) / cols + 1;
        _renderCursorRow = _renderRows - 1;   // cursor left at end, on the last row
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
    /// Compute the prediction for a snapshot of the line — the inline history ghost (a history
    /// prefix lookup) plus the floating PowerShell-parameter panel (a bounded runspace round-trip).
    /// Pure with respect to editor state (reads only the passed snapshot + immutable fields), so it
    /// runs entirely off the key loop and never touches <see cref="_buf"/>. Never throws.
    /// </summary>
    private async Task<(string? Suggestion, IReadOnlyList<FlagHint>? PsHints, string? PsKey)>
        ComputeHintsAsync(string line, int cursor, CancellationToken ct)
    {
        string? suggestion = null;

        // Frecency ghost first: for a cd/z/zi line it previews the jump target.
        // Returns null for every other line, so history suggestion is unaffected.
        if (_frecencySuggest is not null)
        {
            try
            {
                var frec = await _frecencySuggest(line).WaitAsync(ct).ConfigureAwait(false);
                suggestion = string.IsNullOrEmpty(frec) ? null : frec;
            }
            catch (Exception) { suggestion = null; }
        }

        if (suggestion is null)
        {
            try
            {
                var suffix = await _suggester.SuggestAsync(line, _cwd).WaitAsync(ct).ConfigureAwait(false);
                // SuggestAsync returns "" for an exact match (nothing to append) and null for no match —
                // both mean "no ghost".
                suggestion = string.IsNullOrEmpty(suffix) ? null : suffix;
            }
            catch (Exception) { suggestion = null; }
        }

        IReadOnlyList<FlagHint>? psHints = null;
        string? psKey = null;
        if (_flagHintProvider is not null)
        {
            try
            {
                var key = HintKeyFor(line, cursor);
                // The synchronous bash flag panel (computed in Redraw) takes precedence; only
                // fetch PS-cmdlet params when the cursor is on a flag token with no bash spec.
                if (key is not null && TabCompleter.MatchingFlagSpecs(line, cursor, _aliases).Count == 0)
                {
                    var hints = await _flagHintProvider(line, cursor, ct).ConfigureAwait(false);
                    if (hints.Count > 0) { psHints = hints; psKey = key; }
                }
            }
            catch (Exception) { psHints = null; psKey = null; }
        }

        return (suggestion, psHints, psKey);
    }

    /// <summary>
    /// Compute and render the prediction for the line as it stands now, having already echoed the
    /// keystroke. Runs INLINE on the single key-loop flow — never on a second thread — because .NET's
    /// <see cref="Console"/> tolerates no concurrent access (a background writer corrupts the cursor
    /// and reorders/drops input). It never gates the glyph (the caller redrew the character first),
    /// and it is debounced both ways: skipped up front while more input is queued, and discarded after
    /// the lookup if a key arrived meanwhile or the line moved on. So fast typing / paste never waits
    /// on it, and on an IO/memory-bound system the only cost is that a key pressed DURING a drained-
    /// input lookup waits for that one lookup — never a corrupted prompt. Advisory: never throws.
    /// </summary>
    private async Task MaybeRenderPredictionAsync()
    {
        if (PredictionDisabled) return;   // kill switch — keep the editor a plain echo loop

        // Still typing — the result would be superseded immediately and the lookup would sit in front
        // of the next keystroke. Defer; the next keystroke runs this again once input drains.
        if (KeyAvailableSafe()) return;

        var line = _buf.ToString();
        var cursor = _cursor;

        (string? Suggestion, IReadOnlyList<FlagHint>? PsHints, string? PsKey) result;
        try
        {
            using var cts = new CancellationTokenSource(CompletionTimeoutMs);
            result = await ComputeHintsAsync(line, cursor, cts.Token).ConfigureAwait(true);
        }
        catch (Exception) { return; }

        // A key landed during the lookup, or the buffer moved on — discard rather than paint a stale
        // prediction. The buffered keystroke will echo and re-run this on the next loop turn.
        if (KeyAvailableSafe()) return;
        if (_buf.ToString() != line || _cursor != cursor) return;

        _currentSuggestion = result.Suggestion;
        _psPanelHints = result.PsHints;
        _psPanelKey = result.PsKey;
        Redraw();
    }

    /// <summary>
    /// Whether another key is already waiting in the console buffer. Used to debounce the prediction
    /// while the user types fast / pastes. Called only on the key-loop flow (never a second thread —
    /// concurrent Console.KeyAvailable corrupts input). Safe against a redirected/closed console.
    /// </summary>
    private bool KeyAvailableSafe()
    {
        try { return Console.KeyAvailable; }
        catch { return false; }
    }

    /// <summary>
    /// The "commandtoken" identity of the flag token under the cursor, or null when the cursor is
    /// not on a <c>-</c>-prefixed argument token. Used to key the async PS-param cache so a result
    /// only paints while it still matches what the user is looking at.
    /// </summary>
    private string? HintKeyFor(string line, int cursor)
    {
        if (TabCompleter.IsFirstWord(line, cursor))
            return null;
        var (beforeToken, token) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, cursor);
        if (token.Length == 0 || token[0] != '-')
            return null;
        var cmd = TabCompleter.GetCommandNameAtCursor(beforeToken, beforeToken.Length, _aliases);
        if (cmd is null)
            return null;
        return cmd + "" + token;
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
            ReprintBareLine();
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
                _history.AddRange(File.ReadLines(_historyPath).Where(l => l.Length > 0).TakeLast(5000));
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

    public Task<IReadOnlyList<SequenceSuggestion>> GetSequenceSuggestionsAsync(string? lastCommand, string cwd, CancellationToken ct = default)
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
