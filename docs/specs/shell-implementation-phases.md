# Shell Implementation Phases

This document describes the incremental implementation phases for the ps-bash interactive shell. Each phase is independently shippable and builds on the previous ones.

## Phase Overview

| Phase | Name | Scope | Effort | Status |
|-------|------|-------|--------|--------|
| P1 | LineEditor | VT100 key-by-key input, emacs bindings, basic line editing | Medium | Mostly Complete |
| P2 | Built-in history | SQLite store, Up/Down navigation, CWD filtering | Medium | Partial (file-based) |
| P3 | Ctrl-R UI | Full-screen fuzzy search with metadata display | Medium | Not Started |
| P4 | Autosuggestions | Fish-style gray inline suggestions from history | Small | Not Started |
| P5 | Tab completion | FlagSpecs (C#), path completion, command completion | Medium | Partial |
| P6 | Sequence awareness | Pair tracking, ranking, post-command suggestions | Small | Not Started |
| P7 | Plugin system | Interface loading, config, Atuin adapter | Small | Designed |

**Cumulative Value**: P1-P3 delivers a solid built-in shell with a nice TUI. P4-P6 adds CWD and sequence-aware features. P7 enables extensibility.

---

## P1: LineEditor

**Status**: Mostly Complete (VT100 editing, emacs bindings, tab completion wired in)

### Scope

- Replace `Console.ReadLine()` with a VT100-aware line editor
- Emacs-style keybindings (Ctrl-A, Ctrl-E, Ctrl-K, Alt-F, Alt-B, etc.)
- Persistent history to file (`~/.psbash/history`)
- Tab completion (wired to `TabCompleter`)
- Kill ring (Ctrl-K, Ctrl-Y, Alt-Y)
- Basic navigation (arrows, Home/End)

### Implementation Files

- `src/PsBash.Shell/LineEditor.cs` (existing)
- `src/PsBash.Shell/TabCompleter.cs` (existing)
- `src/PsBash.Shell.Tests/LineEditorTests.cs` (existing)

### Keybindings Implemented

| Key | Action |
|-----|--------|
| `Ctrl-A` | Move to beginning of line |
| `Ctrl-E` | Move to end of line |
| `Ctrl-B` | Move back one character |
| `Ctrl-F` | Move forward one character |
| `Alt-B` | Move back one word |
| `Alt-F` | Move forward one word |
| `Ctrl-K` | Kill to end of line |
| `Ctrl-U` | Kill to beginning of line |
| `Ctrl-Y` | Yank (paste) |
| `Ctrl-D` | EOF on empty line, delete-char otherwise |
| `Ctrl-L` | Clear screen |
| `Up/Down` | History navigation |
| `Tab` | Complete |

### Acceptance Criteria

- [x] VT100 codes for cursor positioning, line clearing
- [x] History file persists across sessions
- [x] Tab completion triggers on Tab key
- [x] Emacs bindings work in interactive shell
- [x] Fallback to `Console.ReadLine()` when stdin is redirected

### Remaining Work

- Add more emacs bindings (Ctrl-T transpose, Alt-C capitalize, etc.)
- Better multi-line input support (current implementation is basic)
- Optimize redraws for large buffers

---

## P2: Built-in History

**Status**: Partial (file-based history exists, needs SQLite upgrade)

### Scope

- Upgrade from plain text file to SQLite history store
- Store metadata: timestamp, working directory, exit code, duration
- Up/Down arrow navigation filters by current CWD
- History file path: `~/.psbash/history.db`

### Implementation Files

- `src/PsBash.Shell/HistoryStore.cs` (new)
- `src/PsBash.Shell/HistoryEntry.cs` (new)
- `src/PsBash.Shell/LineEditor.cs` (modify to use HistoryStore)
- `src/PsBash.Shell.Tests/HistoryStoreTests.cs` (new)

### Database Schema

```sql
CREATE TABLE history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    command TEXT NOT NULL,
    timestamp INTEGER NOT NULL,  -- Unix timestamp
    cwd TEXT NOT NULL,           -- Working directory when executed
    exit_code INTEGER,           -- 0-255, null if still running
    duration_ms INTEGER,         -- Command duration in milliseconds
    session_id TEXT              -- UUID for shell session
);

CREATE INDEX idx_history_timestamp ON history(timestamp DESC);
CREATE INDEX idx_history_cwd ON history(cwd, timestamp DESC);
```

### API Design

```csharp
public sealed class HistoryStore
{
    public HistoryStore(string dbPath);
    public void Append(string command, string cwd);
    public IReadOnlyList<HistoryEntry> Search(string prefix, string? cwd, int limit);
    public IReadOnlyList<HistoryEntry> GetRecent(string? cwd, int limit);
}

public sealed record HistoryEntry
{
    public long Id { get; init; }
    public string Command { get; init; }
    public DateTime Timestamp { get; init; }
    public string Cwd { get; init; }
    public int? ExitCode { get; init; }
    public long? DurationMs { get; init; }
}
```

### Acceptance Criteria

- [ ] SQLite database created on first run
- [ ] Commands stored with timestamp, CWD, exit code, duration
- [ ] Up arrow shows history filtered by current CWD first
- [ ] Full history available via Ctrl-R (when P3 lands)
- [ ] History import from legacy text file on upgrade

### Migration Path

1. Create `HistoryStore` wrapping SQLite
2. Add migration: read `~/.psbash/history` (text) and import to DB
3. Update `LineEditor` to use `HistoryStore`
4. Keep text file as fallback if SQLite fails

---

## P3: Ctrl-R UI

**Status**: Not Started

### Scope

- Full-screen reverse-i-search interface
- Fuzzy search as you type
- Display metadata: timestamp, CWD, exit code
- Up/Down to navigate matches, Enter to accept, Esc to cancel
- Highlights matching substring

### Implementation Files

- `src/PsBash.Shell/CtrlRUI.cs` (new)
- `src/PsBash.Shell/LineEditor.cs` (add Ctrl-R handler)
- `src/PsBash.Shell.Tests/CtrlRUITests.cs` (new)

### UI Design

```
andyb@pc:~/app $
(bck-i-search)`git`                                _                                   
git commit -m "fix: handle null ref"     ~/app   2s ago   Exit 0   
git checkout -b feature/new-ui            ~/app   5m ago   Exit 0   
git push origin main                      ~/app   1h ago   Exit 0   
```

- **Line 1**: Original prompt preserved at top
- **Line 2**: Search bar with query
- **Lines 3+**: Matches, ranked by recency and CWD proximity

### Keybindings

| Key | Action |
|-----|--------|
| `Ctrl-R` | Enter reverse-i-search mode |
| `Ctrl-R` (again) | Next match |
| `Up/Down` | Navigate matches |
| `Enter` | Accept selected match |
| `Esc` | Cancel, return to original line |
| `Ctrl-G` | Cancel (bash compatibility) |

### Acceptance Criteria

- [ ] Ctrl-R enters full-screen mode
- [ ] Typing filters matches in real-time
- [ ] Up/Down cycles through matches
- [ ] Enter inserts match into current buffer
- [ ] Esc cancels without modifying buffer
- [ ] CWD matches shown first
- [ ] Fallback to file-based history if SQLite unavailable

---

## P4: Autosuggestions

**Status**: Not Started

### Scope

- Fish-style inline gray suggestions
- Suggest from history based on prefix
- Right arrow accepts suggestion
- Suggestions update as you type

### Implementation Files

- `src/PsBash.Shell/LineEditor.cs` (add suggestion display)
- `src/PsBash.Shell/Suggester.cs` (new)
- `src/PsBash.Shell.Tests/SuggesterTests.cs` (new)

### UI Design

```
andyb@pc:~/app $ git comm
                    it -m "fix: handle null ref"  (gray, right-acceptable)
```

The suggestion appears in gray after the cursor. Pressing `Right` or `Ctrl-F` accepts the suggestion.

### Algorithm

```csharp
public sealed class Suggester
{
    public string? Suggestion(string prefix, string cwd)
    {
        // Query history for commands starting with prefix
        // Prefer:
        // 1. Same CWD
        // 2. Recent (last 7 days)
        // 3. High frequency (run count)
        // Return first match or null
    }
}
```

### Acceptance Criteria

- [ ] Gray text appears after cursor when history matches
- [ ] Right arrow accepts suggestion
- [ ] Suggestion updates as you type
- [ ] No suggestion when no matches
- [ ] Opt-in via config (some users dislike this feature)

---

## P5: Tab Completion

**Status**: Partial (basic file/path/command completion exists)

### Scope

- Use C# `FlagSpec` definitions for flag completion
- Complete command flags based on BashFlagSpecs in runtime
- Context-aware completion (git branches, docker containers)
- Cycle through multiple matches with Tab

### Implementation Files

- `src/PsBash.Core/Parser/BashFlagSpec.cs` (new - reflect runtime specs)
- `src/PsBash.Shell/TabCompleter.cs` (extend for flags)
- `src/PsBash.Shell.Tests/TabCompleterTests.cs` (extend)

### Current State

The existing `TabCompleter` handles:
- Command names (aliases, built-ins, $PATH)
- File and directory paths
- First-word detection

### Additions Needed

1. **Flag completion**: After command name, complete flags from FlagSpecs
2. **Flag value completion**: Some flags take specific values (`--color=always|never|auto`)
3. **Context completion**: `git checkout` → branches, `docker attach` → containers

### Integration with Runtime

The runtime module (`PsBash.psm1`) has `$script:BashFlagSpecs` with flag definitions. Either:

1. **Mirror in C#**: Parse the psm1 at build time, generate C# constants
2. **Query at runtime**: Ask worker for completion candidates

Option 2 is more flexible:

```csharp
private IReadOnlyList<string> CompleteFlags(string command, string partial)
{
    // Query worker: $completer = Get-CommandCompleter "grep"
    // Worker returns: ["--color", "--context", "--extended-regexp", ...]
    return worker.QueryCompletion(command, partial);
}
```

### Acceptance Criteria

- [ ] Tab after `ls -` completes to `--all`, `--long`, etc.
- [ ] Tab after `grep --color=` completes to `always`, `never`, `auto`
- [ ] Tab after `git checkout ` completes to branch names
- [ ] Successive Tab cycles through multiple matches
- [ ] Common prefix inserted automatically on first Tab

---

## P6: Sequence Awareness

**Status**: Not Started

### Scope

- Track command pairs (what you run after what)
- Rank history suggestions by sequence frequency
- Post-command suggestions (predict next command)

### Implementation Files

- `src/PsBash.Shell/SequenceStore.cs` (new)
- `src/PsBash.Shell/HistoryStore.cs` (add pair tracking)
- `src/PsBash.Shell/Suggester.cs` (use sequences for ranking)

### Database Schema Addition

```sql
CREATE TABLE sequences (
    prev_command TEXT NOT NULL,
    next_command TEXT NOT NULL,
    count INTEGER NOT NULL,
    PRIMARY KEY (prev_command, next_command)
);

CREATE INDEX idx_sequences_prev ON sequences(prev_command, count DESC);
```

### Algorithm

After each command, record the sequence:

```csharp
void RecordSequence(string? prev, string current)
{
    if (prev is null) return;
    // Upsert: INSERT OR REPLACE INTO sequences ... count = count + 1
}
```

Suggestion ranking combines:
- Sequence frequency (what usually follows this command)
- Recency (recently used)
- CWD context

### Acceptance Criteria

- [ ] Command pairs tracked in database
- [ ] Post-command suggestion shown (e.g., after `git commit`, suggest `git push`)
- [ ] Sequence-aware ranking used in Ctrl-R
- [ ] Opt-in via config (privacy concern for some users)

---

## P7: Plugin System

**Status**: Designed (see `plugin-architecture.md`)

### Scope

- Interface-based plugin loading
- `IHistoryStore` for custom history backends
- `ICompletionProvider` for custom completions
- Atuin adapter as proof-of-concept
- Config file support (`~/.psbash/config.json`)

### Implementation Files

- `src/PsBash.Shell/IHistoryStore.cs` (new interface)
- `src/PsBash.Shell/ICompletionProvider.cs` (new interface)
- `src/PsBash.Shell/PluginLoader.cs` (new)
- `src/PsBash.Shell/ShellConfig.cs` (new)
- `src/PsBash.Shell/Plugins/AtuinHistoryStore.cs` (new example)

### Design

See [`plugin-architecture.md`](./plugin-architecture.md) for full specification.

### Acceptance Criteria

- [ ] Plugin DLLs discovered in `~/.psbash/plugins/`
- [ ] `IHistoryStore` plugins can replace built-in history
- [ ] `ICompletionProvider` plugins extend completions
- [ ] Config file enables/disables built-in implementations
- [ ] Atuin adapter example works
- [ ] Plugin load failures are non-fatal

---

## Progressive Rollout

### Milestone 1: Basic Shell (P1 only)

**Value**: Replace `Console.ReadLine()` with a proper line editor

**Shippable**: Yes

**User-facing features**:
- Arrow key navigation
- Basic history (Up/Down)
- Tab completion for files/commands

### Milestone 2: Rich History (P1-P2)

**Value**: History becomes a powerful tool, not just a list

**Shippable**: Yes

**User-facing features**:
- CWD-aware history (Up arrow shows relevant commands)
- Metadata (when, where, exit code)
- Faster searches via SQLite

### Milestone 3: Power User Shell (P1-P3)

**Value**: Competitive with modern shells (fish, zsh)

**Shippable**: Yes

**User-facing features**:
- Ctrl-R reverse search with metadata
- Full-screen TUI
- Rich history display

### Milestone 4: Predictive Shell (P1-P4)

**Value**: Type less with intelligent suggestions

**Shippable**: Yes

**User-facing features**:
- Inline gray suggestions
- Right-arrow accept
- Context-aware ranking

### Milestone 5: Smart Completion (P1-P5)

**Value**: Tab completes everything, not just files

**Shippable**: Yes

**User-facing features**:
- Flag completion (`ls --[Tab]`)
- Value completion (`grep --color=[Tab]`)
- Context completion (`git checkout [Tab]`)

### Milestone 6: Sequences (P1-P6)

**Value**: Shell learns your workflow

**Shippable**: Yes

**User-facing features**:
- Post-command suggestions
- Sequence-based ranking
- Workflow optimization

### Milestone 7: Extensible (P1-P7)

**Value**: Community can extend the shell

**Shippable**: Yes

**User-facing features**:
- Atuin integration
- Custom completion plugins
- Config file

---

## Effort Summary

| Phase | Core Work | Testing | Total |
|-------|-----------|---------|-------|
| P1 | LineEditor, TabCompleter | VT100 tests | Medium |
| P2 | SQLite, HistoryStore | DB tests, migration | Medium |
| P3 | Ctrl-R UI, fullscreen | UI tests | Medium |
| P4 | Suggester, inline display | Suggestion logic | Small |
| P5 | FlagSpecs, worker queries | Completion tests | Medium |
| P6 | SequenceStore, ranking | Sequence tracking | Small |
| P7 | Interfaces, plugin loader | Plugin load tests | Small |

**Total Effort**: Medium-Large (but incremental value at each step)
