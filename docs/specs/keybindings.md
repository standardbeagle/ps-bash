# Interactive Shell Keybindings

This document specifies all keybindings for the ps-bash interactive shell. Keys are grouped by functional category: navigation, editing, history, completion, and control.

**Implementation Status:**
- Implemented: Already working in the current shell
- Planned: Specified, not yet implemented (see linked phase)
- Designed: Conceptual design exists, pending prioritization

---

## Navigation

Move the cursor within the current input line.

| Key | Action | Status | Notes |
|-----|--------|--------|-------|
| `Left Arrow` | Move back one character | Implemented | |
| `Right Arrow` | Move forward one character | Implemented | |
| `Ctrl+B` | Move back one character | Implemented | Emacs-style |
| `Ctrl+F` | Move forward one character | Implemented | Emacs-style |
| `Home` | Move to start of line | Implemented | |
| `Ctrl+A` | Move to start of line | Implemented | Emacs-style |
| `End` | Move to end of line | Implemented | |
| `Ctrl+E` | Move to end of line | Implemented | Emacs-style |
| `Alt+B` | Move back one word | Implemented | |
| `Alt+F` | Move forward one word | Implemented | |

---

## Editing

Delete, yank (paste), and modify text.

| Key | Action | Status | Notes |
|-----|--------|--------|-------|
| `Backspace` | Delete character backward | Implemented | |
| `Delete` | Delete character forward | Implemented | |
| `Ctrl+D` | Delete char forward / EOF | Implemented | Deletes char if buffer non-empty; exits shell if empty |
| `Ctrl+H` | Delete character backward | Planned | Emacs-style (same as Backspace) |
| `Ctrl+W` | Delete word backward | Implemented | Kills word to left of cursor |
| `Alt+D` | Delete word forward | Planned | Emacs-style |
| `Alt+Backspace` | Delete word backward | Planned | Same as Ctrl+W |
| `Ctrl+K` | Delete to end of line | Implemented | Text saved to kill ring |
| `Ctrl+U` | Delete to start of line | Implemented | Text saved to kill ring |
| `Ctrl+Y` | Yank (paste) | Implemented | Paste from kill ring |
| `Alt+Y` | Yank pop | Planned | Cycle through kill ring history |
| `Ctrl+T` | Transpose characters | Planned | Swap current and previous char |
| `Alt+T` | Transpose words | Planned | Swap current and previous word |
| `Alt+U` | Uppercase word | Planned | Convert current word to uppercase |
| `Alt+L` | Lowercase word | Planned | Convert current word to lowercase |
| `Alt+C` | Capitalize word | Planned | Capitalize current word |

### Kill Ring

The kill ring is a clipboard that accumulates deleted text:

- `Ctrl+K` / `Ctrl+U` / `Ctrl+W` append killed text to the ring
- `Ctrl+Y` yanks the most recently killed text
- `Alt+Y` cycles through previous kills (after `Ctrl+Y`)

---

## History

Navigate and search command history.

| Key | Action | Status | Notes |
|-----|--------|--------|-------|
| `Up Arrow` | Previous command | Implemented | CWD-filtered by default |
| `Down Arrow` | Next command | Implemented | Returns to current input |
| `Ctrl+P` | Previous command | Planned | Emacs-style (same as Up) |
| `Ctrl+N` | Next command | Planned | Emacs-style (same as Down) |
| `Ctrl+R` | Reverse search | Planned | Full-screen fuzzy search (P3) |
| `Ctrl+S` | Forward search | Planned | Reverse direction of Ctrl+R |
| `Alt+<` | First history entry | Planned | Jump to oldest command |
| `Alt+>` | Last history entry | Planned | Jump to newest command |
| `Ctrl+O` | Execute and advance | Planned | Run current, show next in history |

### CWD-Filtered History

By default, Up/Down arrows prioritize commands run in the **current working directory**. This makes history contextually relevant:

```
# In ~/app, pressing Up shows:
git commit -m "fix: ..."
npm test
dotnet build

# After cd ~/docs, pressing Up shows:
pdftk document.pdf output doc.pdf
markdown-toc README.md
```

**Configuration:** Disable CWD filtering via `~/.psbash/config.toml`:

```toml
[history]
cwd_filter = false
```

### Reverse Search (`Ctrl+R`)

Full-screen incremental search with metadata display:

```
(bck-i-search)`git`                                    
git commit -m "fix: handle null ref"     ~/app   2s ago   Exit 0   
git checkout -b feature/new-ui            ~/app   5m ago   Exit 0   
git push origin main                      ~/app   1h ago   Exit 0   
```

**Keybindings in Ctrl+R mode:**

| Key | Action | Status |
|-----|--------|--------|
| `Ctrl+R` | Next match | Planned |
| `Ctrl+S` | Previous match | Planned |
| `Up/Down` | Navigate matches | Planned |
| `Enter` | Accept match | Planned |
| `Esc` / `Ctrl+G` | Cancel | Planned |

**See:** [`shell-implementation-phases.md`](./shell-implementation-phases.md#p3-ctrl-r-ui)

---

## Completion

Tab completion and autosuggestions.

| Key | Action | Status | Notes |
|-----|--------|--------|-------|
| `Tab` | Complete | Implemented | Cycle through matches |
| `Shift+Tab` | Previous completion | Implemented | Cycle backwards |
| `Ctrl+I` | Complete | Planned | Same as Tab (emacs) |
| `Ctrl+X` / `Ctrl+V` | List possible completions | Planned | Show all candidates without cycling |
| `Alt+?` | List possible completions | Planned | Same as Ctrl+X / Ctrl+V |
| `Alt+/` | Complete filename | Planned | Alternate completion trigger |
| `Right Arrow` | Accept autosuggestion | Planned | Fish-style inline suggestion (P4) |
| `End` | Accept autosuggestion | Planned | Fish-style inline suggestion (P4) |

### Tab Completion

The shell supports context-aware tab completion:

- **First word**: Commands (aliases, built-ins, `$PATH` executables)
- **Arguments**: Files and directories
- **After commands**: Flags and values (P5 - planned)

```
$ ls --[Tab]
--all       --almost-all       --author       --escape       --directory
...
$ git checkout [Tab]
main          feature/new-ui   feature/fix-bug
```

**See:** [`completion-providers.md`](./completion-providers.md)

### Autosuggestions (Fish-Style)

Gray inline text suggests the most recent matching history entry:

```
andyb@pc:~/app $ git comm
                    it -m "fix: handle null ref"
                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^ gray suggestion
```

- Suggestion updates as you type
- Press `Right` or `End` to accept
- Any other key clears the suggestion
- CWD-aware: prefers commands from current directory

**Configuration:**

```toml
[completion]
autosuggestions = true   # Enable (default)
```

**See:** [`autosuggestions.md`](./autosuggestions.md)

---

## Control

Control the shell, input state, and screen.

| Key | Action | Status | Notes |
|-----|--------|--------|-------|
| `Enter` | Submit line | Implemented | Execute command |
| `Ctrl+C` | Cancel input / abort | Implemented | Clear line or send SIGINT |
| `Ctrl+D` | EOF (exit) | Implemented | Exit shell if line empty |
| `Ctrl+L` | Clear screen | Implemented | |
| `Ctrl+G` | Cancel | Planned | Abort current operation (bash compat) |
| `Ctrl+J` | Submit line | Planned | Same as Enter (newline) |
| `Ctrl+M` | Submit line | Planned | Same as Enter (carriage return) |
| `Ctrl+Q` | Resume output | Planned | Unpause `Ctrl+S` flow control |
| `Ctrl+S` | Pause output | Planned | XON/XOFF flow control |
| `Ctrl+Z` | Suspend shell | Planned | Background shell (job control) |
| `Esc` | Cancel / clear | Planned | Clear autosuggestion, exit modes |

### Multi-Line Input

The shell detects incomplete constructs and shows a continuation prompt (`> `):

```
$ if [ -f file.txt ]; then
> echo "found it"
> fi
```

**Detected constructs:**
- `if` / `fi`
- `for` / `do` / `done`
- `while` / `do` / `done`
- `until` / `do` / `done`
- `case` / `esac`
- `{` / `}`
- `(` / `)`
- Unclosed quotes (`'`, `"`)

---

## Special Modes

### Ctrl+R Mode (Reverse Search)

Full-screen incremental search interface:

```
andyb@pc:~/app $
(bck-i-search)`git`                                _                                   
git commit -m "fix: handle null ref"     ~/app   2s ago   Exit 0   
git checkout -b feature/new-ui            ~/app   5m ago   Exit 0   
git push origin main                      ~/app   1h ago   Exit 0   
```

**Behavior:**
- Typing filters matches in real-time
- CWD matches shown first
- Metadata displayed: timestamp, directory, exit code
- Press `Ctrl+R` again to cycle through matches

**Toggle CWD/Global:**
- Press `Tab` in Ctrl+R mode to toggle between CWD-filtered and global search

**See:** [`shell-implementation-phases.md`](./shell-implementation-phases.md#p3-ctrl-r-ui)

### Autosuggestion Mode

When autosuggestions are enabled, gray text appears inline:

```
$ git comm
            it -m "fix: handle null ref"  (gray)
```

**Keybindings:**
- `Right Arrow` / `End` — Accept suggestion
- Any other key — Clear suggestion and insert character
- `Esc` — Explicitly dismiss suggestion

**Disabled when:**
- Tab completion menu is active
- In continuation mode (`> ` prompt)
- Input is empty

---

## Configuration

Keybindings can be customized via `~/.psbash/config.toml`:

```toml
[keybindings]
# Emacs mode (default) or vi mode
mode = "emacs"   # | "vi"

# Feature toggles
autosuggestions = true
cwd_filter = true

# Future: custom keybinding overrides
# [keybindings.custom]
# CtrlK = "custom-function"
```

---

## Implementation Status Summary

| Category | Implemented | Planned |
|----------|-------------|---------|
| Navigation | 9 keys | 0 keys |
| Editing | 7 keys | 10 keys |
| History | 2 keys | 8 keys |
| Completion | 2 keys | 6 keys |
| Control | 5 keys | 8 keys |
| **Total** | **25 keys** | **32 keys** |

**Progress:** P1 (LineEditor) complete; P3 (Ctrl+R), P4 (autosuggestions), P5 (flag completion) planned.

---

## Comparison with Other Shells

| Key | ps-bash | bash | fish | zsh |
|-----|---------|------|------|-----|
| `Ctrl+A` | Start of line | Yes | Yes | Yes |
| `Ctrl+E` | End of line | Yes | Yes | Yes |
| `Ctrl+R` | Reverse search | Planned | Yes | Yes |
| `Right` (accept suggestion) | Fish-style | No | Yes | Plugin |
| CWD-filtered history | Yes | No | No | Plugin |
| `Ctrl+L` | Clear screen | Yes | Yes | Yes |

**Unique to ps-bash:**
- CWD-filtered history by default
- SQLite-backed history (fast, metadata-rich)
- Typed object pipeline integration

---

## References

- [`shell-implementation-phases.md`](./shell-implementation-phases.md) — Implementation roadmap
- [`autosuggestions.md`](./autosuggestions.md) — Fish-style suggestion spec
- [`completion-providers.md`](./completion-providers.md) — Tab completion system
- [`config-format.md`](./config-format.md) — Configuration file format
- [`shell-guide.md`](../shell-guide.md) — User-facing shell documentation
