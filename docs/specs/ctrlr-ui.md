# Ctrl-R Full-Screen Search UI

This document specifies the full-screen fuzzy search interface (Ctrl-R) for the ps-bash interactive shell.

**Status:** DESIGNED — UI mockup, fuzzy matching algorithm, and rendering strategy specified.

---

## 1. Overview

Ctrl-R invokes a full-screen incremental search interface over command history. Unlike bash's incremental reverse-i-search (which matches prefixes linearly), ps-bash provides:

- **Fuzzy matching** — Substring matches anywhere in the command, not just prefixes
- **Ranked results** — Sorted by CWD match > recency > frequency
- **Metadata display** — Timestamp, exit code, working directory
- **Dual mode** — CWD-filtered vs global history toggle

---

## 2. UI Mockup

```
┌─ History Search ──────────────────────────────────────────────────────┐
│ andyb@pc:~/app $                                                       │
│                                                                        │
│ (i-search) `docker` [CWD]                              12 matching    │
│                                                                        │
│  docker build -t myapp .            ~/app   2m ago    Exit 0   #145   │
│  docker run --rm -it myapp          ~/app   5h ago    Exit 0   #142   │
│  docker-compose up -d               ~/app   3d ago    Exit 0   #138   │
│  docker build -t myapp . --no-cache ~/docs  1w ago    Exit 1   #092   │
│  docker ps -a                       ~/app   2w ago    Exit 0   #087   │
│                                                                        │
│                                                                        │
│ [Enter] Execute  [Tab] Edit  [Ctrl-G] Toggle CWD/All  [Esc] Cancel    │
└────────────────────────────────────────────────────────────────────────┘
```

### UI Elements

| Element | Description |
|---------|-------------|
| **Title bar** | `History Search` centered, box-drawing characters |
| **Prompt line** | Original shell prompt preserved (for context) |
| **Search bar** | `(i-search) query [mode]                          count` |
| **Results list** | Matched commands, one per line |
| **Status bar** | Keybinding hints |

### Result Line Format

```
<command>  <cwd>  <relative-time>  <exit-code>  <id>
```

**Columns:**
- **Command** — Full command text (truncated with `...` if too long for width)
- **CWD** — Working directory, shortened with `~` for home
- **Relative time** — Human-readable: `2m ago`, `5h ago`, `3d ago`, `1w ago`
- **Exit code** — `Exit 0` (success) or `Exit 1` (failure), color-coded
- **ID** — History entry ID for reference

**Truncation:**
- Commands longer than `(terminal_width - 50)` characters are truncated
- Truncation preserves query match visibility (truncate opposite side of match)
- Example: Query `build` → `docker ...build...` shows match, truncates after

### Color Coding

| Element | Color |
|---------|-------|
| Search query text | Bold white (or terminal default) |
| Matched substring in results | Bright yellow (reverse video) |
| Exit 0 | Green |
| Exit non-zero | Red |
| CWD path | Cyan (dimmed) |
| Relative time | Gray |
| Selected line | Reverse video (entire line background) |

### Mode Indicator

The search bar shows `[CWD]` or `[All]`:
- **`[CWD]`** — Searching only commands from current working directory
- **`[All]`** — Searching entire history database

Press `Ctrl-G` to toggle between modes.

---

## 3. Fuzzy Matching Algorithm

### 3.1 Scoring Function

Each history entry receives a relevance score based on three factors:

```csharp
double ScoreFuzzyMatch(HistoryEntry entry, string query, string currentCwd)
{
    double score = 0;

    // Factor 1: Query match quality (0-100)
    score += QueryMatchScore(entry.Command, query) * 100;

    // Factor 2: CWD match boost (0-50)
    if (entry.Cwd == currentCwd)
        score += 50;

    // Factor 3: Recency boost (0-30)
    var hoursSince = (DateTime.UtcNow - entry.Timestamp).TotalHours;
    score += Math.Max(0, 30 - hoursSince);

    // Factor 4: Frequency boost (0-20)
    score += FrequencyBoost(entry.Command) * 20;

    return score;
}
```

**Final score range:** 0-200 points

### 3.2 Query Match Scoring

```csharp
double QueryMatchScore(string command, string query)
{
    var cmd = command.ToLowerInvariant();
    var q = query.ToLowerInvariant();

    // Exact match (highest score)
    if (cmd == q)
        return 1.0;

    // Prefix match (second highest)
    if (cmd.StartsWith(q))
        return 0.9;

    // Substring match (third highest)
    if (cmd.Contains(q))
        return 0.7;

    // Fuzzy subsequence match (lowest non-zero score)
    double fuzzyScore = FuzzySubsequenceScore(cmd, q);
    return fuzzyScore > 0 ? fuzzyScore * 0.5 : 0;
}
```

### 3.3 Fuzzy Subsequence Algorithm

Based on Smith-Waterman-like alignment:

```csharp
double FuzzySubsequenceScore(string text, string query)
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
```

**Scoring examples:**

| Command | Query | Score | Reason |
|---------|-------|-------|--------|
| `docker build` | `docker build` | 1.00 | Exact match |
| `docker build -t app` | `docker build` | 0.90 | Prefix match |
| `docker build --no-cache` | `build` | 0.70 | Substring match |
| `docker build` | `db` | 0.14 | Fuzzy (`d`...`b`) |
| `docker-compose up` | `dcp` | 0.12 | Fuzzy (`d`...`c`...`p`) |

### 3.4 Frequency Boost

Frequency is computed from the `command_sequences` view (see `sqlite-history-schema.md`):

```csharp
double FrequencyBoost(string command)
{
    // Count how many times this command appears in last 30 days
    var count = GetCommandFrequency(command, days: 30);

    // Normalize: 10+ occurrences = max boost
    return Math.Min(1.0, count / 10.0);
}
```

---

## 4. Sort Order

Results are sorted by descending total score:

```sql
-- Pseudocode for final ordering
ORDER BY
    -- CWD match (current directory first)
    (cwd = @current_cwd) DESC,
    -- Recency (newest first)
    timestamp DESC,
    -- Frequency (most common first)
    frequency DESC
```

**Sort priority:**
1. **CWD match** — Commands from current directory rank higher
2. **Recency** — Recent commands rank higher (within CWD tier)
3. **Frequency** — Frequently-run commands rank higher (within recency tier)

**Example ranking for query `docker` in `~/app`:**

| Rank | Command | CWD | Time | Score | Why |
|------|---------|-----|------|-------|-----|
| 1 | `docker build -t myapp .` | `~/app` | 2m ago | 180 | CWD + recent |
| 2 | `docker run --rm -it myapp` | `~/app` | 5h ago | 175 | CWD + recent |
| 3 | `docker build -t myapp .` | `~/app` | 3d ago | 150 | CWD + older |
| 4 | `docker-compose up -d` | `~/app` | 1w ago | 140 | CWD + frequent |
| 5 | `docker build -t myapp . --no-cache` | `~/docs` | 1w ago | 90 | Different CWD |

---

## 5. VT100 Rendering Strategy

### 5.1 Alternate Screen Buffer

The Ctrl-R UI uses the xterm alternate screen buffer:

```
Enter mode:  \x1b[?1049h  (enable alt screen, save cursor)
Exit mode:   \x1b[?1049l  (disable alt screen, restore cursor)
```

**Benefits:**
- Original terminal content is preserved
- No redraw needed on exit
- Clean "overlay" appearance

### 5.2 Save/Restore State

On entry:
```
\x1b[s        (save cursor position)
\x1b[?1049h   (switch to alt screen)
\x1b[?25l     (hide cursor in TUI, use selection instead)
```

On exit:
```
\x1b[?1049l   (switch back to main screen)
\x1b[u        (restore cursor position)
\x1b[?25h     (show cursor)
```

### 5.3 Clear and Redraw

```
\x1b[2J       (clear entire screen)
\x1b[H        (move cursor to 1,1)
```

On each query change or selection change:
1. Clear screen
2. Redraw title bar
3. Redraw search bar with updated query
4. Redraw results list with new selection highlight
5. Redraw status bar

### 5.4 Result Line Rendering

Each result line:

```
\x1b[2K                   (clear entire line)
\x1b[0m                   (reset attributes)
<command text>            (with matched substrings highlighted)
\x1b[36m                  (cyan for CWD)
<cwd>
\x1b[90m                  (gray for time)
<relative time>
\x1b[32m or \x1b[31m      (green/red for exit code)
Exit <code>
```

**Selected line reverse video:**
```
\x1b[7m                   (reverse video for entire line)
<entire line content>
\x1b[27m                  (reverse off)
```

### 5.5 Terminal Size Handling

On startup and on `SIGWINCH`:

```csharp
(int width, int height) = GetTerminalSize();
int resultRows = height - 4; // Reserve 2 for title, 1 for search, 1 for status
```

If the terminal is too small (height < 10), Ctrl-R refuses to activate:
- Display error: `"Terminal too small for Ctrl-R (min 10 rows required)"`
- Stay in normal line editor mode

---

## 6. Keybindings

### 6.1 Mode Entry/Exit

| Key | Action |
|-----|--------|
| `Ctrl-R` | Enter Ctrl-R mode (from normal shell) |
| `Esc` | Exit, return to original line (no changes) |
| `Ctrl-G` | Exit, return to original line (bash compat) |

### 6.2 Navigation

| Key | Action |
|-----|--------|
| `Ctrl-R` | Cycle to next match (wrap to bottom) |
| `Ctrl-S` | Cycle to previous match (wrap to top) |
| `Up Arrow` | Move selection up one line |
| `Down Arrow` | Move selection down one line |
| `Page Up` | Move selection up one page |
| `Page Down` | Move selection down one page |
| `Home` | Jump to first result |
| `End` | Jump to last result |

### 6.3 Edit Mode

Press `Tab` to enter "edit mode" before executing:

```
┌─ History Search ──────────────────────────────────────────────────────┐
│ (i-search) `docker` [CWD]                              12 matching    │
│                                                                        │
│  docker build -t myapp .            ~/app   2m ago    Exit 0   #145   │
│  > docker run --rm -it myapp        ~/app   5h ago    Exit 0   #142   │
│                                                                    ^  │
│                                                                        │
│ [Enter] Execute edited  [Esc] Cancel edit  [Arrows] Move cursor       │
└────────────────────────────────────────────────────────────────────────┘
```

In edit mode:
- `>` prefix shows this entry is being edited
- Cursor appears in command text (normal line editor)
- Edit keybindings active (`Ctrl-A`, `Ctrl-E`, `Ctrl-K`, etc.)
- `Enter` executes the **edited** command
- `Esc` returns to selection mode (discards edits)

### 6.4 Execution

| Key | Action |
|-----|--------|
| `Enter` | Execute selected command (or edited command if in edit mode) |
| `Tab` | Enter edit mode for selected command |

---

## 7. Query Integration

### 7.1 IHistoryStore.SearchAsync Usage

```csharp
var results = await _historyStore.SearchAsync(new HistoryQuery
{
    Filter = query,           // Prefix/substring filter
    Cwd = cwdFilterMode ? currentCwd : null,  // CWD or all
    Limit = 100,              // Fetch 100 candidates
    Reverse = false           // Newest first
});

// Then re-rank by fuzzy match score client-side
var ranked = results
    .Select(e => new { Entry = e, Score = ScoreFuzzyMatch(e, query, currentCwd) })
    .OrderByDescending(x => x.Score)
    .ThenByDescending(x => x.Entry.Timestamp)
    .Take(maxResults)
    .ToList();
```

### 7.2 Performance Optimization

To keep search responsive under 50ms:

1. **SQLite prefix search** is fast (indexed)
2. **Client-side fuzzy scoring** on 100 candidates is fast (in-memory)
3. **Debounce queries** — wait 100ms after last keystroke before searching
4. **Incremental results** — Show previous results while loading new ones

---

## 8. CWD Filter Toggle

The mode indicator shows current filter:

```
(i-search) `docker` [CWD]    <- Only commands from ~/app shown
(i-search) `docker` [All]    <- All commands in history shown
```

Toggle with `Ctrl-G` (re-purposed from bash's "cancel" in this mode):

```csharp
void ToggleFilterMode()
{
    _cwdFilterEnabled = !_cwdFilterEnabled;
    RefreshResults();
}
```

**Visual feedback:**
- Mode indicator updates immediately
- Result list refreshes with new filter
- Match count updates

---

## 9. Exit Code Indicators

| Exit Code | Indicator | Color | Meaning |
|-----------|-----------|-------|---------|
| 0 | `Exit 0` | Green | Success |
| 1-127 | `Exit N` | Red | Command failed |
| 128+N | `Exit N` | Red | Terminated by signal N |
| null | `Exit ?` | Gray | Still running or unknown |

**Visual indicator:**
- Success: `★` or `✓` character before command (optional, configurable)
- Failure: No symbol, just red text

---

## 10. Acceptance Criteria

Implementation is complete when:

- [ ] Ctrl-R enters full-screen mode with alternate screen buffer
- [ ] Search bar shows query, mode, and match count
- [ ] Results display: command, CWD, relative time, exit code
- [ ] Fuzzy matching matches substrings and subsequences
- [ ] Sort order: CWD > recency > frequency
- [ ] CWD/All toggle works via `Ctrl-G`
- [ ] All keybindings work (Up/Down, Ctrl-R/Ctrl-S, Enter, Tab, Esc)
- [ ] Edit mode allows modifying command before execution
- [ ] Terminal resize handling is correct
- [ ] Performance: Search results appear within 100ms of keystroke

---

## 11. References

- Related specs:
  - `shell-implementation-phases.md` — P3 (Ctrl-R UI) implementation plan
  - `keybindings.md` — Interactive shell keybindings reference
  - `history-store-interface.md` — `IHistoryStore.SearchAsync` query API
  - `sqlite-history-schema.md` — Database indexes for history queries
  - `lineeditor-vt100-design.md` — VT100 rendering patterns
