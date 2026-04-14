# Fish-Style Autosuggestions

This document specifies the behavior and implementation of fish-style autosuggestions in the ps-bash interactive shell.

**Status:** DESIGNED — Feature specification complete. Implementation pending (P4 in `shell-implementation-phases.md`).

---

## 1. Overview

Autosuggestions are the fish shell's most praised feature. As you type, the shell suggests the most recent matching command from your history in gray (dimmed) text after your cursor. Press `Right` or `End` to accept the suggestion, or keep typing to ignore it.

### Why Autosuggestions Matter

- **Type less** — Frequently repeated commands appear instantly
- **Muscle memory** — Your workflow becomes faster without learning new shortcuts
- **Context-aware** — Suggestions adapt to your current directory
- **Non-intrusive** — Gray text is easy to ignore when unwanted

---

## 2. User Experience

### 2.1 Visual Appearance

```
andyb@pc:~/app $ git comm
                    it -m "fix: handle null ref"
```

The suggestion (`it -m "fix: handle null ref"`) appears in **ANSI dim** (gray) immediately after your cursor. The text you've typed (`git comm`) remains bright and visible.

#### ANSI Escape Sequence

The suggestion is rendered using the ANSI escape sequence for dimmed text:

```
ESC[2m...suggestion text...ESC[0m
```

- `ESC[2m` — Enables dim/bright mode (faint text)
- `ESC[0m` — Resets all attributes (returns to normal brightness)

### 2.2 Accepting Suggestions

#### Right Arrow

Pressing `Right` (with no modifiers) accepts the full suggestion:

```
Before:  git comm|                    (| = cursor)
Press:   Right Arrow
After:   git commit -m "fix: handle null ref"|
```

The suggestion text becomes real input and the cursor moves to the end.

#### End Key

Pressing `End` also accepts the full suggestion (standard readline behavior):

```
Before:  git comm|
Press:   End
After:   git commit -m "fix: handle null ref"|
```

### 2.3 Partial Acceptance (Not Implemented)

Fish supports accepting word-by-word with `Alt+Right` (forward-word). This is **not** in the initial spec but could be added later.

### 2.4 Ignoring Suggestions

You can ignore a suggestion by:

- **Keep typing** — Any keypress other than `Right`/`End` clears the suggestion
- **Press `Esc`** — Explicitly dismiss the current suggestion
- **Move cursor left** — Any cursor movement clears the suggestion

---

## 3. Suggestion Algorithm

### 3.1 Matching Strategy

**Substring prefix match** (not fuzzy):

- Your input must match the **beginning** of a history entry
- Match is case-sensitive
- No fuzzy matching or typo tolerance (performance consideration)

#### Examples

| Your Input | History Entry | Match? | Reason |
|------------|---------------|--------|--------|
| `git c` | `git commit -m "fix"` | Yes | Prefix match |
| `git c` | `git checkout main` | Yes | Prefix match (ties broken by recency) |
| `gi` | `git status` | No | Not a prefix (missing `t`) |
| `GIT` | `git status` | No | Case-sensitive |

### 3.2 Ranking Algorithm

Suggestions are ranked by the following criteria (in order):

1. **CWD match** — Commands run in the current working directory rank higher
2. **Recency** — More recent commands rank higher
3. **Frequency** (future) — Commands run more often rank higher

#### Tie-Breaking

When multiple history entries match the prefix:

1. Prefer entries from the **current working directory**
2. Among CWD matches, prefer **most recent** (by timestamp)
3. Among non-CWD matches, prefer **most recent**
4. If timestamps tie, prefer **most frequent** (future enhancement)

#### Examples

Given this history:

| Timestamp | CWD | Command |
|-----------|-----|---------|
| 10:00 | `~/app` | `git commit -m "fix"` |
| 09:55 | `~/app` | `git checkout main` |
| Yesterday | `~/docs` | `git commit -m "update docs"` |
| Yesterday | `~/tmp` | `git status` |

When typing `git c` in `~/app`:

1. First suggestion: `git commit -m "fix"` (CWD match, most recent)
2. If you type `git ch`, suggestion changes to: `git checkout main` (CWD match)

When typing `git c` in `~/tmp` (no CWD matches):

1. Suggestion: `git commit -m "fix"` (most recent overall)

### 3.3 CWD Filtering

CWD filtering is controlled by the config file:

```toml
[history]
cwd_filter = true   # Default: true
```

When `cwd_filter = true`:
- Commands from the current directory are **strongly preferred**
- Non-CWD commands only appear when no CWD match exists

When `cwd_filter = false`:
- All history is considered equally
- Ranking is purely by recency

---

## 4. Integration with Tab Completion

### 4.1 Mutual Exclusion

Autosuggestions are **disabled** when the tab completion dropdown is active:

```
# User types and presses Tab
git c[Tab]

# Completion menu appears:
git checkout  git commit  git config  git cherry-pick

# During this state, autosuggestions are OFF
# User selects "git commit" or presses Esc to close menu

# After menu closes, autosuggestions resume
```

### 4.2 Rationale

- Tab completion shows **all possible matches** (multiple options)
- Autosuggestions show **the single best match** (prediction)
- Showing both simultaneously creates visual clutter
- Tab completion is **explicit** (user pressed Tab), autosuggestions are **implicit** (always on)

### 4.3 State Machine

```
Idle typing → autosuggestions ON
    |
    v
User presses Tab
    |
    v
Completion menu active → autosuggestions OFF
    |
    v
User selects completion OR presses Esc
    |
    v
Idle typing → autosuggestions ON
```

---

## 5. Configuration

### 5.1 Enable/Disable

Control autosuggestions via `~/.psbash/config.toml`:

```toml
[completion]
autosuggestions = true   # Default: true
```

Set to `false` to disable completely. Some users dislike autosuggestions because:
- Visual clutter
- Muscle memory conflicts
- Privacy concerns (history-based suggestions)

### 5.2 Related Settings

Autosuggestions interact with other completion settings:

```toml
[completion]
autosuggestions = true              # Must be true for suggestions
enable_sequence_suggestions = true  # Use sequence predictions (future)
flag_completion = true              # Tab completes flags
path_completion = true              # Tab completes paths

[history]
cwd_filter = true                   # Suggestions prefer CWD matches
max_entries = 100000                # Larger history = better suggestions
```

---

## 6. Performance Considerations

### 6.1 Query Frequency

Suggestions are updated on **every keystroke**. The query must be fast enough to avoid lag:

- Target: < 10ms per query
- History size: up to 100,000 entries
- Requires indexed database (SQLite)

### 6.2 Database Index

To support fast prefix queries, the history table needs an index:

```sql
CREATE TABLE history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    command TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    cwd TEXT,
    exit_code INTEGER
);

CREATE INDEX idx_history_prefix_cwd ON history(command, cwd DESC, timestamp DESC);
CREATE INDEX idx_history_prefix_global ON history(command, timestamp DESC);
```

Query strategy:
1. Try CWD-scoped query first: `WHERE command LIKE 'prefix%' AND cwd = ?`
2. If no results, try global query: `WHERE command LIKE 'prefix%'`
3. Return top result

### 6.3 Why Not Fuzzy Matching?

Fuzzy matching (e.g., `gcm` → `git commit`) is **not** used because:

1. **Performance** — Fuzzy search over 100k entries is slow
2. **False positives** — Suggests irrelevant commands
3. **User expectations** — Fish uses prefix matching, users expect that behavior

Prefix matching on an indexed column is O(log n). Fuzzy matching requires scanning all entries or complex indexes.

---

## 7. Implementation Architecture

### 7.1 Components

```
LineEditor
    ├── On every keystroke
    │   ├── Query Suggester.Suggestion(prefix, cwd)
    │   ├── Render suggestion in gray (ANSI dim)
    │   └── Redraw line with suggestion appended
    │
    ├── On Right arrow / End key
    │   ├── If suggestion exists: accept it (append to buffer)
    │   └── Move cursor to end
    │
    └── On any other key
        └── Clear suggestion, redraw

Suggester
    ├── Query IHistoryStore.Search(prefix, cwd, limit: 1)
    ├── Return single best match or null
    └── Ranking: CWD > recency > frequency

IHistoryStore
    ├── Search(prefix, cwd, limit)
    │   ├── Try CWD-scoped query first
    │   ├── Fall back to global query if needed
    │   └── Return ranked results
    └── Uses SQLite with prefix index
```

### 7.2 Files

| File | Purpose |
|------|---------|
| `src/PsBash.Shell/LineEditor.cs` | Add suggestion display, Right/End handling |
| `src/PsBash.Shell/Suggester.cs` | New: suggestion query logic |
| `src/PsBash.Shell/HistoryStore.cs` | Extend: prefix search with CWD filtering |
| `src/PsBash.Shell.Tests/SuggesterTests.cs` | New: ranking logic tests |

### 7.3 Rendering

The suggestion is rendered using ANSI escape sequences:

```csharp
private void RedrawWithSuggestion(string prompt, string? suggestion)
{
    var promptVisible = StripAnsi(prompt);
    var text = _buf.ToString();

    // Clear line, reprint prompt + buffer
    Console.Write("\x1b[2K\r");  // Clear line
    Console.Write(prompt);
    Console.Write(text);

    // Append suggestion in dim (gray) if present
    if (suggestion is not null && suggestion.Length > 0)
    {
        Console.Write("\x1b[2m");      // Dim on
        Console.Write(suggestion);
        Console.Write("\x1b[0m");      // Reset
    }

    // Move cursor back to correct position
    var charsAfterCursor = _buf.Length - _cursor;
    if (charsAfterCursor > 0)
        Console.Write($"\x1b[{charsAfterCursor}D");
}
```

---

## 8. Edge Cases

### 8.1 Empty Input

No suggestion when input is empty — would be too noisy.

```
$ |           (no suggestion)
```

### 8.2 No Matches

No suggestion when no history entry matches the prefix.

```
$ xyzzy|     (no suggestion)
```

### 8.3 Exact Match

If your input exactly matches a history entry, no suggestion is shown (nothing to add).

```
$ git status|  (if "git status" is in history, no suggestion)
```

### 8.4 Multiline Input

Autosuggestions only work for the **first line** of multiline input. Once you're in continuation mode (`> `), suggestions are disabled.

```bash
$ if true; then
>            (no suggestions in continuation mode)
```

### 8.5 Suggestion Longer Than Screen

If the suggestion would wrap to the next line, it's truncated to avoid visual glitches:

```csharp
var maxWidth = Console.WindowWidth - promptVisible.Length - _buf.Length - 1;
if (suggestion.Length > maxWidth)
    suggestion = suggestion.Substring(0, maxWidth);
```

---

## 9. Future Enhancements

### 9.1 Word-by-Word Acceptance

Fish supports `Alt+Right` to accept one word at a time:

```
git commit -m "fix: handle null"
  ↑-------↑
  Alt+Right accepts "commit ", cursor moves there
```

This is **not** in P4 scope but could be added later.

### 9.2 Sequence-Aware Suggestions

After implementing `SequenceStore` (P6), suggestions could predict the **next command** based on what you usually run:

```
$ docker build -t myapp .
[press Enter, command succeeds]
$ |                   (suggest: docker run myapp)
```

This requires tracking command pairs in history.

### 9.3 Frequency Ranking

Currently ranking is by recency only. Frequency ranking (how often you run a command) could improve suggestions:

```sql
CREATE TABLE history (
    ...
    run_count INTEGER DEFAULT 1  -- Incremented on each run
);

-- Ranking: (cwd_boost * 10) + (recency_score) + (frequency_score * 2)
```

---

## 10. Comparison with Other Shells

| Feature | ps-bash | fish | zsh (autosuggestions) |
|---------|---------|------|----------------------|
| Gray inline suggestions | Yes | Yes | Yes (via plugin) |
| Right/End to accept | Yes | Yes | Yes |
| CWD-aware ranking | Yes | No | Plugin-dependent |
| Substring prefix match | Yes | Yes | Varies |
| Fuzzy matching | No | No | Some plugins |
| Word-by-word accept | Future | Yes | Some plugins |
| Sequence predictions | Future | No | No |

### Why ps-bash Is Different

- **CWD-aware** — Suggestions adapt to your current directory (unique to ps-bash)
- **SQLite-backed** — Fast queries over large history (100k+ entries)
- **Configurable** — Easy to disable if you don't like it

---

## 11. References

- [Fish shell autosuggestions documentation](https://fishshell.com/docs/current/interactive.html#autosuggestions)
- [Fish shell implementation (C++)](https://github.com/fish-shell/fish-shell/blob/master/src/autosuggest_suggestion.cc)
- [zsh-autosuggestions plugin](https://github.com/zsh-users/zsh-autosuggestions)
- Related specs:
  - `config-format.md` — Configuration file format
  - `shell-implementation-phases.md` — P4 implementation plan
  - `completion-providers.md` — Tab completion system

---

## 12. Acceptance Criteria

Implementation is complete when:

- [ ] Gray text appears after cursor when history matches prefix
- [ ] Right arrow accepts the full suggestion
- [ ] End key accepts the full suggestion
- [ ] Suggestion updates on every keystroke
- [ ] No suggestion when no matches exist
- [ ] CWD matches are preferred over global matches
- [ ] Suggestions are disabled when tab completion menu is active
- [ ] `autosuggestions = false` in config disables the feature
- [ ] Performance is acceptable (< 10ms per query over 100k history)
- [ ] Multiline continuation mode disables suggestions
