# LineEditor VT100 Design

This document describes the VT100 rendering strategy, escape sequences, and platform compatibility approach for `LineEditor.cs` — the custom line editor that replaces `Console.ReadLine()` in the ps-bash interactive shell.

---

## 1. VT100 Escape Sequences

`LineEditor` assumes a VT100-compatible terminal and emits ANSI escape sequences for rendering. Modern terminals (Windows Terminal, iTerm2, GNOME Terminal, etc.) all support VT100.

### 1.1 Sequences Used

| Sequence | Purpose | Implementation |
|----------|---------|----------------|
| `\x1b[2K\r` | Clear entire line, move cursor to column 0 | `ClearLine` constant |
| `\x1b[{n}D` | Move cursor back n columns | `Redraw()` positions cursor after redraw |
| `\x1b[H\x1b[2J` | Clear screen (home + erase all) | Ctrl+L handler |

### 1.2 Rendering Strategy

**Full Line Redraw**

On each keystroke that modifies the buffer, `Redraw()` performs:

1. **Clear line**: Emit `\x1b[2K\r` to erase current line and move cursor to start
2. **Render prompt**: Write prompt (may contain ANSI color codes)
3. **Render buffer**: Write entire buffer contents
4. **Position cursor**: Move cursor back from end of buffer to actual cursor position

```csharp
private void Redraw(string prompt)
{
    var promptVisible = StripAnsi(prompt);
    var text = _buf.ToString();

    // Erase current line, reprint prompt + buffer
    Console.Write(ClearLine);
    Console.Write(prompt);
    Console.Write(text);

    // Move cursor back from end to correct position
    var charsAfterCursor = _buf.Length - _cursor;
    if (charsAfterCursor > 0)
        Console.Write($"\x1b[{charsAfterCursor}D");
}
```

**ANSI-Aware Prompt Length**

The prompt may contain color codes (e.g., `\x1b[32m$\x1b[0m`). To correctly position the cursor, visual length is computed by stripping ANSI sequences:

```csharp
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
```

---

## 2. Unicode and Wide Character Handling

### 2.1 Current Approach

`LineEditor` stores input in a `StringBuilder` and tracks cursor position as a **character index** (not a visual column position). This works correctly for:

- ASCII (1 char = 1 visual column)
- Multi-byte UTF-8 sequences (C# `char` is UTF-16 code unit, `StringBuilder` handles this)
- Combining characters (accents, diacritics) — each is a separate code unit

### 2.2 Limitations

**Wide Characters (CJK, Emoji)**

Some characters occupy 2 visual columns in terminal rendering:
- CJK ideographs (U+4E00–U+9FFF): Japanese, Chinese, Korean
- Emoji: many emoji render as 2 columns
- Some box-drawing characters

The current implementation **does not account for double-width characters**. Cursor positioning assumes 1 character = 1 visual column. For most use cases (English input, common programming), this is acceptable.

**Future Enhancement**

If wide character support is needed, the strategy is:

1. **Detect wide characters**: Use `System.Rune` and `EastAsianWidth` from `System.Text.Unicode`
2. **Track visual column**: Maintain separate `visualColumn` offset from `charIndex`
3. **Position cursor**: Use visual column for ANSI escape calculations

Example approach (not implemented):

```csharp
private int GetVisualWidth(string s)
{
    int width = 0;
    foreach (var rune in s.EnumerateRunes())
    {
        width += rune.GetEastAsianWidth() == EastAsianWidth.Fullwidth ? 2 : 1;
    }
    return width;
}
```

### 2.3 Combining Characters

Combining characters (e.g., `e` + combining acute accent = `é`) are stored as two `char` values in the buffer. Cursor movement treats them as two positions, which may feel slightly off but remains functional.

---

## 3. Platform Compatibility

### 3.1 Windows Console API Fallback

**When stdin is redirected** (piped input, test harness, non-interactive), `LineEditor` falls back to `Console.ReadLine()`:

```csharp
if (Console.IsInputRedirected)
{
    Console.Write(prompt);
    return Console.ReadLine();
}
```

This preserves compatibility with:
- Piped input: `cat commands.txt | ps-bash`
- Test harnesses: stdin redirected from a file
- CI environments: No TTY available

### 3.2 VT100 Support on Windows

**Windows 10+ (build 16257+)**
- Native VT100 support enabled by default
- No additional configuration needed
- `LineEditor` works out of the box

**Windows 7-8.1 and older Windows 10**
- VT100 support **not** available by default
- `LineEditor` will emit escape sequences that appear as garbage characters
- **Workaround**: Users should upgrade to Windows 10+ or use a third-party terminal with VT100 support (ConEmu, mintty)

**Detection strategy (not implemented)**

To detect VT100 support on Windows, query `Console.VirtualTerminalProcessing`:

```csharp
if (OperatingSystem.IsWindows())
{
    var handle = Console.Out.Handle;
    if (WindowsNative.GetConsoleMode(handle, out var mode))
    {
        if ((mode & WindowsNative.ENABLE_VIRTUAL_TERMINAL_PROCESSING) == 0)
        {
            // VT100 not available; fall back or warn user
        }
    }
}
```

### 3.3 Linux and macOS

All modern terminals on Linux and macOS support VT100. No special handling needed.

---

## 4. Key-by-Key Input Loop

### 4.1 ReadKey Intercept

`LineEditor.ReadLine()` blocks in a `while (true)` loop calling `Console.ReadKey(intercept: true)`:

- `intercept: true` prevents keys from echoing to terminal
- Control characters are delivered via `ConsoleKeyInfo.Key` and `ConsoleModifiers`
- Printable characters are in `key.KeyChar`

### 4.2 Key Processing

Each keypress dispatches through a `switch` statement:

```csharp
var key = Console.ReadKey(intercept: true);

// Special keys with their own handlers
if (key.Key == ConsoleKey.Tab) { HandleTab(prompt); continue; }
if (key.Key == ConsoleKey.Enter) { /* submit */ }

// Modifier + key combinations
switch (key.Key)
{
    case ConsoleKey.A when key.Modifiers == ConsoleModifiers.Control:
        MoveCursorTo(0);  // Ctrl-A: home
        break;
    case ConsoleKey.LeftArrow when key.Modifiers == ConsoleModifiers.Alt:
        MoveCursorWordLeft();  // Alt-B equivalent
        break;
    // ...
}
```

### 4.3 Escape Sequence Handling

**Alt key combinations** arrive as Escape prefix + key:

- `Alt+F`: ESC (0x1B) followed by `F` key
- `Console.ReadLine()` does NOT synthesize `ConsoleModifiers.Alt` automatically
- Current implementation relies on .NET's `ConsoleModifiers.Alt` synthesis for arrow keys

**Planned enhancement** for Alt+letter keys (not fully implemented):

```csharp
// Detect ESC prefix manually
if (key.Key == ConsoleKey.Escape)
{
    var next = Console.ReadKey(intercept: true);
    // Map to Alt+whatever
}
```

---

## 5. Multi-Line Input

### 5.1 Detection

Multi-line input is **not** handled by `LineEditor` directly. Instead, `InteractiveShell.IsIncomplete()` checks for unclosed constructs before calling `LineEditor`:

```csharp
// In InteractiveShell
private bool IsIncomplete(string input)
{
    // Check for unmatched quotes, unclosed if/for/while, etc.
    // Returns true if continuation prompt needed
}
```

When continuation is needed, `LineEditor.ReadLine()` is called with a `"> "` prompt instead of the normal prompt. Each line is accumulated in the shell until complete.

### 5.2 Future Enhancement

**Full multi-line editing** (treating multiple lines as a single buffer, navigating between lines) is planned but not implemented. This would require:

- Tracking buffer as a list of lines, not a single `StringBuilder`
- Rendering all lines on each redraw
- Cursor movement across lines (Up/Down arrows in editor mode)
- Line insertion/deletion

---

## 6. Kill Ring (Clipboard)

### 6.1 Design

The kill ring is a single string `_killRing` storing the most recently killed text:

- `Ctrl+K` (kill to end of line) appends to kill ring
- `Ctrl+U` (kill to start) replaces kill ring
- `Ctrl+W` (kill word back) replaces kill ring
- `Ctrl+Y` (yank) inserts kill ring content at cursor

### 6.2 Limitations

Current implementation stores **only the most recent kill**, not a ring of previous kills. True emacs kill ring behavior (cycling through previous kills with `Alt+Y`) is planned for P2.

---

## 7. Tab Completion Integration

### 7.1 Delegation Model

`LineEditor` does not implement completion logic itself. It delegates to a callback:

```csharp
private readonly Func<string, int, IReadOnlyList<string>>? _completer;

// First Tab: query completer
_completions = _completer?.Invoke(_buf.ToString(), _cursor);
```

This design allows:
- `TabCompleter` (file/command completion)
- Future plugin completion providers
- No hard dependency on completion implementation

### 7.2 Cycle Behavior

- **First Tab**: Compute completions, show list if multiple, apply first
- **Subsequent Tabs**: Cycle through completion list
- **Non-Tab key**: Clear completion state

---

## 8. References

| Document | Covers |
|----------|--------|
| `keybindings.md` | All keybindings, emacs behavior |
| `shell-implementation-phases.md` | P1 scope, LineEditor acceptance criteria |
| `design-decisions.md` | Rationale for custom VT100 editor vs library |
| `architecture-overview.md` | Component relationships, data flow |
| `src/PsBash.Shell/LineEditor.cs` | Implementation (481 lines) |
| `src/PsBash.Shell.Tests/LineEditorTests.cs` | Unit tests for word boundary splitting |
