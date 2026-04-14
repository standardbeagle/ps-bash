# Tab Completion Providers Specification

This document specifies the built-in tab completion providers for the ps-bash interactive shell. Completion providers implement `ICompletionProvider` and are queried when the user presses Tab to get suggestions for the current input.

**Status:** PLANNED — FlagSpec, Path, and Command completion are partially implemented in `TabCompleter.cs`. History and Sequence completion are planned.

---

## 1. Overview

The ps-bash shell uses a multi-provider completion system. When Tab is pressed, all registered completion providers are queried, and their results are merged and de-duplicated according to priority rules.

### Provider Types

| Provider | Priority | Purpose | Data Source |
|----------|----------|---------|-------------|
| `SequenceCompletionProvider` | 1 (highest) | Suggest next command based on prior command patterns | `IHistoryStore` sequence pairs |
| `HistoryCompletionProvider` | 2 | Fuzzy match against command history | `IHistoryStore` entries |
| `FlagSpecCompletionProvider` | 3 | Complete command flags (`-`, `--`) | `BashFlagSpecs` from runtime |
| `CommandCompletionProvider` | 4 | Complete command names | `$PATH` scan + aliases |
| `PathCompletionProvider` | 5 (lowest) | Complete file/directory paths | `FileSystem` scan |

### Interface Definition

```csharp
namespace PsBash.Shell;

/// <summary>
/// A pluggable tab completion provider.
/// </summary>
public interface ICompletionProvider
{
    /// <summary>
    /// Returns completion candidates for the current line and cursor position.
    /// All providers are consulted; results are merged and de-duplicated.
    /// </summary>
    /// <param name="line">The full input line.</param>
    /// <param name="cursor">Cursor position (character offset) within <paramref name="line"/>.</param>
    /// <param name="context">Shell context: working directory, defined aliases, environment.</param>
    /// <returns>Completion candidates. Empty list if no matches.</returns>
    IReadOnlyList<CompletionItem> Complete(string line, int cursor, CompletionContext context);
}

/// <summary>
/// A completion result with display text and optional metadata.
/// </summary>
public sealed record CompletionItem
{
    public required string Text { get; init; }           // The text to insert
    public string? Description { get; init; }             // Optional description (for display)
    public string? DisplayText { get; init; }             // Optional alternate display text
    public CompletionKind Kind { get; init; } = CompletionKind.Text; // Kind for icon/filtering
}

public enum CompletionKind
{
    Text,           // Generic text
    Command,        // Command name
    Flag,           // Command flag (-x, --xxx)
    Path,           // File/directory path
    History,        // History entry
    Sequence,       // Sequence-predicted command
}
```

**Note:** The existing `TabCompleter.cs` returns `IReadOnlyList<string>`. The interface should be upgraded to return `IReadOnlyList<CompletionItem>` to support descriptions and kinds.

---

## 2. Provider Specifications

### 2.1 FlagSpecCompletionProvider

**Priority:** 3

**Purpose:** Complete command flags (`-` and `--`) based on `BashFlagSpecs` data from the runtime module.

**Trigger Condition:** The token being completed starts with `-` and the previous token is a recognized command name.

**Data Source:** `$script:BashFlagSpecs` hashtable in `PsBash.psm1`:

```powershell
$script:BashFlagSpecs = @{
    'ls' = @(
        @('-l', 'long listing'),
        @('-a', 'show hidden'),
        @('-h', 'human readable sizes'),
        @('-R', 'recursive'),
        @('-S', 'sort by size'),
        @('-t', 'sort by time'),
        @('-r', 'reverse sort'),
        @('-1', 'one per line')
    )
    'grep' = @(
        @('-i', 'ignore case'),
        @('-v', 'invert match'),
        @('-n', 'line numbers'),
        @('-c', 'count only'),
        # ... more flags
    )
    # ... 76 commands total
}
```

**Behavior:**

1. Parse the line to identify the command name (first non-assignment word)
2. Check if the command exists in `BashFlagSpecs`
3. If the current token starts with `-`, filter matching flags
4. Return `CompletionItem` with flag text and description

**Examples:**

| Input | Tab Result |
|-------|------------|
| `ls -` [Tab] | `-a`, `-h`, `-l`, `-R`, `-r`, `-S`, `-t`, `-1` (with descriptions) |
| `grep --c` [Tab] | `--color` (if extended spec), `-c` |
| `cat -` [Tab] | `-b`, `-E`, `-n`, `-s`, `-T` |

**Implementation Notes:**

- Must query the `PwshWorker` to access `BashFlagSpecs` from the PowerShell runtime
- Cache the specs per worker session to avoid repeated queries
- Handle both short flags (`-x`) and long flags (`--xxx`)
- For flags with values (e.g., `--color=always`), the value completion is handled separately

---

### 2.2 PathCompletionProvider

**Priority:** 5 (lowest)

**Purpose:** Complete file and directory paths based on the current working directory and relative path prefixes.

**Trigger Condition:** The token being completed does not start with `-` (not a flag) and is not the first word on the line.

**Data Source:** `System.IO.Directory.EnumerateFileSystemEntries`

**Behavior:**

1. Extract the partial path from the current token
2. Resolve the directory component:
   - Empty token → use current working directory
   - `src/` → resolve `src/` relative to CWD
   - `/etc/pa` → resolve `/etc/pa` as absolute path
   - `~/doc` → expand `~` to `$HOME`
3. Enumerate entries in the resolved directory
4. Filter entries that match the filename prefix
5. Append `/` to directory names for visual distinction

**Examples:**

| Input | Tab Result |
|-------|------------|
| `cat src/` [Tab] | Files and directories under `src/` |
| `cd ~/do` [Tab] | `~/docs/`, `~/downloads/` |
| `ls /etc/pas` [Tab] | `/etc/passwd` |
| `grep pattern src/P` [Tab] | `src/Program.cs`, `src/PsBash.Shell/` |

**Implementation Notes:**

- Case-sensitive on Unix, case-insensitive on Windows
- Handle both `/` and `Path.DirectorySeparatorChar`
- Escape spaces and special characters if needed
- The existing `TabCompleter.CompletePath()` implements this behavior

---

### 2.3 CommandCompletionProvider

**Priority:** 4

**Purpose:** Complete command names from `$PATH` executables, built-in commands, and user-defined aliases.

**Trigger Condition:** The current token is the first word on the line (before any command arguments).

**Data Sources:**

1. **Aliases:** `IReadOnlyDictionary<string, string>` from `CompletionContext.Aliases`
2. **Built-ins:** Hardcoded list of bash builtins (see `TabCompleter.KnownCommands`)
3. **$PATH executables:** `Directory.EnumerateFiles()` for each directory in `$PATH`
4. **Local executables:** `./script-name` from current working directory

**Behavior:**

1. Check if cursor is at the first word (see `IsFirstWord()` in `TabCompleter`)
2. Query aliases, built-ins, and $PATH directories in parallel
3. Filter entries that start with the partial token
4. Return sorted results (alphabetical order)

**Examples:**

| Input | Tab Result |
|-------|------------|
| `gre` [Tab] | `grep` |
| `gi` [Tab] | `git` |
| `ls` (if aliased) [Tab] | `ls` (alias) |
| `./scr` [Tab] | `./script.ps1`, `./script.sh` |

**Implementation Notes:**

- On Windows, only include executable extensions: `.exe`, `.cmd`, `.bat`, `.ps1`
- Deduplicate across sources (e.g., `git` in both built-ins and $PATH)
- The existing `TabCompleter.CompleteCommand()` implements this behavior

---

### 2.4 HistoryCompletionProvider

**Priority:** 2

**Purpose:** Suggest commands from command history based on fuzzy prefix matching, ranked by working directory proximity and recency.

**Trigger Condition:** The current token is the first word on the line (similar to command completion).

**Data Source:** `IHistoryStore.Search(string prefix, int limit)`

**Ranking Algorithm:**

Entries are ranked by:

1. **CWD match boost:** Commands run in the current working directory rank higher
2. **Recency:** More recent commands rank higher
3. **Frequency:** Commands run more often rank higher (optional future enhancement)

**Behavior:**

1. Extract the prefix (current token text)
2. Query `IHistoryStore.Search(prefix, limit: 100)`
3. Rank results by (CWD, timestamp)
4. Return top 10 matches as `CompletionItem`

**Examples:**

| Context | Input | Tab Result |
|---------|-------|------------|
| CWD: `~/app` | `git c` [Tab] | Previous `git checkout`, `git commit` from this directory |
| CWD: `~/app` | `gi` [Tab] | `git push`, `git status` (recent in this dir) |
| Any | `npm` [Tab] | `npm install`, `npm test` (most recent) |

**Implementation Notes:**

- Requires SQLite-based `HistoryStore` (see P2 in `shell-implementation-phases.md`)
- CWD filtering must be enabled via config (`history.cwd_filter = true`)
- For file-based history, all entries have the same CWD (null/empty)

---

### 2.5 SequenceCompletionProvider

**Priority:** 1 (highest)

**Purpose:** Suggest the next command based on command pair sequences from history. After running `docker build`, pressing Tab on an empty line suggests `docker run`.

**Trigger Condition:** The previous command (last executed) is known, and the current line is empty or starts with a prefix matching the predicted next command.

**Data Source:** `IHistoryStore.GetSequences(string prevCommand, int limit)` — sequence pairs table

**Database Schema:**

```sql
CREATE TABLE sequences (
    prev_command TEXT NOT NULL,
    next_command TEXT NOT NULL,
    count INTEGER NOT NULL,
    PRIMARY KEY (prev_command, next_command)
);

CREATE INDEX idx_sequences_prev ON sequences(prev_command, count DESC);
```

**Behavior:**

1. Get the previous command from shell state (last executed)
2. Query `IHistoryStore.GetSequences(prevCommand, limit: 10)`
3. Filter sequences by current line prefix (if any)
4. Return top matches ranked by sequence frequency

**Examples:**

| Previous Command | Current Line | Tab Result |
|------------------|--------------|------------|
| `docker build -t myapp .` | `` (empty) [Tab] | `docker run myapp` |
| `git commit -m "fix"` | `` (empty) [Tab] | `git push` |
| `kubectl apply -f deployment.yaml` | `` (empty) [Tab] | `kubectl get pods` |
| `git checkout -b feature` | `git p` [Tab] | `git push` (matches prefix) |

**Implementation Notes:**

- Requires `SequenceStore` (see P6 in `shell-implementation-phases.md`)
- Sequences are recorded after each command execution
- Privacy concern: Some users may disable this via config
- Only triggers on empty or prefix-matching lines

---

## 3. Merge and De-duplication

### 3.1 Priority Order

Providers are queried in priority order, but all are consulted. The priority determines which result wins when multiple providers return the same text.

**Priority (highest to lowest):**

1. `SequenceCompletionProvider` — Sequence predictions
2. `HistoryCompletionProvider` — History entries
3. `FlagSpecCompletionProvider` — Flag names
4. `CommandCompletionProvider` — Command names
5. `PathCompletionProvider` — File paths

### 3.2 De-duplication Rules

When multiple providers return the same `Text`, the higher priority wins:

**Example:** If both `CommandCompletionProvider` and `PathCompletionProvider` return `git`, the `CommandCompletionProvider` result wins (priority 4 > 5).

**De-duplication Algorithm:**

```csharp
public IReadOnlyList<CompletionItem> MergeCompletions(
    IReadOnlyList<ICompletionProvider> providers,
    string line,
    int cursor,
    CompletionContext context)
{
    var all = new List<CompletionItem>();
    var seen = new Dictionary<string, int>(); // Text -> priority

    foreach (var provider in providers)
    {
        var priority = GetProviderPriority(provider);
        var items = provider.Complete(line, cursor, context);

        foreach (var item in items)
        {
            // If we've seen this text before, keep only the higher priority
            if (seen.TryGetValue(item.Text, out int existingPriority))
            {
                if (existingPriority <= priority) continue; // Existing has higher/equal priority
            }

            // Remove lower-priority duplicate
            if (seen.ContainsKey(item.Text))
            {
                all.RemoveAll(i => i.Text == item.Text);
            }

            all.Add(item);
            seen[item.Text] = priority;
        }
    }

    return all;
}
```

### 3.3 Exact Text Match

De-duplication is based on **exact text match** of the `Text` property. `DisplayText` and `Description` are ignored for comparison.

**Example:** Two providers returning `CompletionItem` with `Text = "main"` but different descriptions are considered duplicates.

---

## 4. Completion Flow

### 4.1 Tab Press Handling

When the user presses Tab:

```
User presses Tab
    |
    v
Get current line and cursor position
    |
    v
Query all ICompletionProvider providers in parallel
    |                               |
    +-- SequenceCompletionProvider  |
    +-- HistoryCompletionProvider   |
    +-- FlagSpecCompletionProvider  |
    +-- CommandCompletionProvider   |
    +-- PathCompletionProvider      |
    |
    v
Merge and de-duplicate results
    |
    v
If 0 results: beep, no change
If 1 result: insert completion
If N results: show list, insert first (cycle on repeat Tab)
```

### 4.2 Cycling Through Matches

When multiple results exist:

1. **First Tab:** Show completion list, insert first result
2. **Subsequent Tab:** Cycle to next result, replace inserted text
3. **Non-Tab key:** Clear cycle state, accept current text

This is implemented in `LineEditor.HandleTab()`.

---

## 5. Implementation Status

| Provider | Status | Notes |
|----------|--------|-------|
| `FlagSpecCompletionProvider` | Not implemented | `BashFlagSpecs` exists in runtime; needs C# bridge |
| `PathCompletionProvider` | Implemented | `TabCompleter.CompletePath()` |
| `CommandCompletionProvider` | Implemented | `TabCompleter.CompleteCommand()` |
| `HistoryCompletionProvider` | Not implemented | Requires `HistoryStore` with CWD metadata |
| `SequenceCompletionProvider` | Not implemented | Requires `SequenceStore` |

### Roadmap

1. **Phase 5** (see `shell-implementation-phases.md`): Implement `FlagSpecCompletionProvider`
2. **Phase 2**: Implement `HistoryStore` → enables `HistoryCompletionProvider`
3. **Phase 6**: Implement `SequenceStore` → enables `SequenceCompletionProvider`
4. **Phase 7**: Refactor to use `ICompletionProvider` interface for all providers

---

## 6. Configuration

Completion behavior is controlled via `~/.psbash/config.toml` (see `config-format.md`):

```toml
[completion]
enable_sequence_suggestions = true  # Enable SequenceCompletionProvider
autosuggestions = true              # Enable fish-style inline suggestions
flag_completion = true              # Enable FlagSpecCompletionProvider
path_completion = true              # Enable PathCompletionProvider
```

---

## 7. Extension via Plugins

Third-party plugins can provide custom completion providers by implementing `ICompletionProvider`:

```csharp
public sealed class DockerCompletionProvider : ICompletionProvider
{
    public IReadOnlyList<CompletionItem> Complete(
        string line,
        int cursor,
        CompletionContext context)
    {
        // After "docker attach ", suggest container names
        // After "docker run ", suggest image names
        // ...
    }
}
```

Plugin providers are loaded via `PluginLoader` and merged with built-in providers (see `plugin-architecture.md`).
