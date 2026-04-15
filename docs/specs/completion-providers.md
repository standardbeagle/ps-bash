# Tab Completion Providers Specification

This document specifies the built-in tab completion providers for the ps-bash interactive shell. Completion providers implement `ICompletionProvider` and are queried when the user presses Tab to get suggestions for the current input.

**Status:** PLANNED — FlagSpec, Path, and Command completion are partially implemented in `TabCompleter.cs`. History and Sequence completion are planned.

---

## 1. Overview

The ps-bash shell uses a multi-provider completion system. When Tab is pressed, all registered completion providers are queried, and their results are merged and de-duplicated according to priority rules.

### Provider Types

| Provider | Name | Kind | Purpose | Data Source |
|----------|------|------|---------|-------------|
| `CommandCompletionProvider` | "Commands" | `Command` | Complete command names | `$PATH` scan + aliases |
| `FlagSpecCompletionProvider` | "Flags" | `Flag` | Complete command flags (`-`, `--`) | `BashFlagSpecs` from runtime |
| `VariableCompletionProvider` | "Variables" | `Variable` | Complete shell variable names | `Environment` + shell vars |
| `HistoryCompletionProvider` | "History" | `History` | Suggest commands from history | `IHistoryStore` entries |
| `PathCompletionProvider` | "Paths" | `File`/`Directory` | Complete file/directory paths | `FileSystem` scan |
| `SequenceCompletionProvider` | "Suggestions" | `Command` | Suggest next command based on prior patterns | `IHistoryStore` sequence pairs |

### Interface Definition

```csharp
namespace PsBash.Shell;

/// <summary>
/// A pluggable tab completion provider for the interactive shell.
/// Providers are queried when the user presses Tab, and results are merged
/// and de-duplicated according to priority rules.
/// </summary>
public interface ICompletionProvider
{
    /// <summary>
    /// Gets the display name of this provider (e.g., "Commands", "Files", "Flags").
    /// Used for UI display and debugging.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns completion candidates for the current input context.
    /// All providers are consulted; results are merged and de-duplicated.
    /// </summary>
    /// <param name="context">The completion context containing input, cursor position, and shell state.</param>
    /// <returns>Completion candidates. Empty list if no matches.</returns>
    Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(CompletionContext context);
}

/// <summary>
/// Context information for a completion request. Passed to all providers
/// to enable context-aware completion suggestions.
/// </summary>
/// <param name="Input">The full input line being edited.</param>
/// <param name="CursorPosition">Cursor position (character offset) within <see cref="Input"/>.</param>
/// <param name="Cwd">Current working directory for path resolution.</param>
/// <param name="LastCommand">The last executed command (for sequence-aware providers).</param>
/// <param name="Aliases">Currently defined shell aliases (for command expansion).</param>
public sealed record CompletionContext(
    string Input,
    int CursorPosition,
    string Cwd,
    string? LastCommand,
    IReadOnlyDictionary<string, string> Aliases
);

/// <summary>
/// A completion result with display text, description, and kind.
/// </summary>
/// <param name="Text">The text to insert when this completion is selected.</param>
/// <param name="Description">Optional description for display (e.g., flag help text).</param>
/// <param name="Kind">The kind of completion (determines icon and sorting priority).</param>
public sealed record CompletionItem(
    string Text,
    string? Description,
    CompletionKind Kind
);

/// <summary>
/// The kind of completion item. Determines sort order priority and UI icon.
/// Lower enum values have higher priority (e.g., Command > File > Directory).
/// </summary>
public enum CompletionKind
{
    /// <summary>Command name (highest priority for first-word completion).</summary>
    Command = 0,

    /// <summary>Command flag (-x, --xxx).</summary>
    Flag = 1,

    /// <summary>Variable reference ($VAR, ${VAR}).</summary>
    Variable = 2,

    /// <summary>History entry (previously executed command).</summary>
    History = 3,

    /// <summary>File path.</summary>
    File = 4,

    /// <summary>Directory path (lower priority than files in mixed results).</summary>
    Directory = 5,
}
```

**Note:** The existing `TabCompleter.cs` returns `IReadOnlyList<string>`. The interface should be upgraded to return `IReadOnlyList<CompletionItem>` to support descriptions and kinds.

---

## 2. Provider Specifications

### 2.1 CommandCompletionProvider

**Name:** `"Commands"`

**Kind:** `CompletionKind.Command`

**Purpose:** Complete command names from `$PATH` executables, built-in commands, and user-defined aliases.

**Trigger Condition:** The current token is the first word on the line (before any command arguments).

**Data Sources:**

1. **Aliases:** `context.Aliases` from `CompletionContext`
2. **Built-ins:** Hardcoded list of bash builtins (see `TabCompleter.KnownCommands`)
3. **$PATH executables:** `Directory.EnumerateFiles()` for each directory in `$PATH`
4. **Local executables:** `./script-name` from current working directory

**Behavior:**

1. Check if cursor is at the first word (see `IsFirstWord()` in `TabCompleter`)
2. Query aliases, built-ins, and $PATH directories
3. Filter entries that start with the partial token
4. Return results as `CompletionItem` with `Kind = CompletionKind.Command`

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

### 2.2 FlagSpecCompletionProvider

**Name:** `"Flags"`

**Kind:** `CompletionKind.Flag`

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
4. Return `CompletionItem` with `Kind = CompletionKind.Flag` and description from spec

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

### 2.3 VariableCompletionProvider

**Name:** `"Variables"`

**Kind:** `CompletionKind.Variable`

**Purpose:** Complete shell variable references (e.g., `$HOME`, `$USER`, `$PATH`).

**Trigger Condition:** The token being completed starts with `$`.

**Data Source:** `Environment.GetEnvironmentVariables()` plus shell-defined variables.

**Behavior:**

1. Check if current token starts with `$`
2. Strip the `$` prefix to get the variable name partial
3. Filter environment variables by prefix match
4. Return `CompletionItem` with `Kind = CompletionKind.Variable` and the full variable name including `$`

**Examples:**

| Input | Tab Result |
|-------|------------|
| `echo $H` [Tab] | `$HOME`, `$HOSTNAME`, `$HISTFILE` |
| `export $US` [Tab] | `$USER` |
| `$PAT` [Tab] | `$PATH` |

**Implementation Notes:**

- Include both environment variables and shell-defined variables
- Case-sensitive on Unix, case-insensitive on Windows
- The completion text should include the `$` prefix

---

### 2.4 PathCompletionProvider

**Name:** `"Paths"`

**Kind:** `CompletionKind.File` or `CompletionKind.Directory`

**Purpose:** Complete file and directory paths based on the current working directory and relative path prefixes.

**Trigger Condition:** The token being completed does not start with `$` or `-` and is not the first word on the line.

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
5. Return `CompletionItem` with `Kind = CompletionKind.File` or `CompletionKind.Directory`

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

### 2.5 HistoryCompletionProvider

**Name:** `"History"`

**Kind:** `CompletionKind.History`

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
4. Return top 10 matches as `CompletionItem` with `Kind = CompletionKind.History`

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

### 2.6 SequenceCompletionProvider

**Name:** `"Suggestions"`

**Kind:** `CompletionKind.Command`

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

1. Get the previous command from `context.LastCommand`
2. Query `IHistoryStore.GetSequences(prevCommand, limit: 10)`
3. Filter sequences by current line prefix (if any)
4. Return top matches ranked by sequence frequency as `CompletionItem` with `Kind = CompletionKind.Command`

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

### 3.1 Provider Stacking

All completion providers are queried for every Tab press. Providers do not "exit early" — each provider contributes its candidates, and results are merged together. This enables multiple providers to contribute complementary completions (e.g., flag and file completions can both appear in the same result set).

### 3.2 De-duplication Rules

When multiple providers return the same `Text`, only one result is kept. The winner is determined by the `CompletionKind` priority (lower enum value wins):

**Kind Priority (highest to lowest):**
1. `Command` (0)
2. `Flag` (1)
3. `Variable` (2)
4. `History` (3)
5. `File` (4)
6. `Directory` (5)

**Example:** If both a command provider and a file provider return `git`, the `Command` kind wins because `Command = 0 < File = 4`.

**De-duplication Algorithm:**

```csharp
public IReadOnlyList<CompletionItem> MergeAndDeduplicate(
    IEnumerable<IReadOnlyList<CompletionItem>> providerResults)
{
    var byText = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);

    foreach (var results in providerResults)
    {
        foreach (var item in results)
        {
            // If we've seen this text, keep only the higher-priority kind
            if (byText.TryGetValue(item.Text, out var existing))
            {
                if (item.Kind < existing.Kind)
                    byText[item.Text] = item; // New item has higher priority
                // else: keep existing (higher or equal priority)
            }
            else
            {
                byText[item.Text] = item;
            }
        }
    }

    return byText.Values.ToList();
}
```

### 3.3 Sorting

After de-duplication, results are sorted by:
1. **Primary:** `CompletionKind` (lower values first)
2. **Secondary:** Alphabetical by `Text`

```csharp
return merged
    .OrderBy(item => item.Kind)           // Group by kind priority
    .ThenBy(item => item.Text, StringComparer.Ordinal)
    .ToList();
}
```

This ordering ensures that commands appear before flags, which appear before files, making the completion list predictable and easy to navigate.

### 3.4 Exact Text Match

De-duplication is based on **exact text match** of the `Text` property. `Description` is ignored for comparison.

**Example:** Two providers returning `CompletionItem` with `Text = "main"` but different descriptions are considered duplicates; the one with lower `Kind` value wins.

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
| `CommandCompletionProvider` | Implemented | `TabCompleter.CompleteCommand()` |
| `FlagSpecCompletionProvider` | Not implemented | `BashFlagSpecs` exists in runtime; needs C# bridge |
| `VariableCompletionProvider` | Not implemented | Needs implementation |
| `HistoryCompletionProvider` | Not implemented | Requires `HistoryStore` with CWD metadata |
| `PathCompletionProvider` | Implemented | `TabCompleter.CompletePath()` |
| `SequenceCompletionProvider` | Not implemented | Requires `SequenceStore` |

### Roadmap

1. **Phase 5** (see `shell-implementation-phases.md`): Implement `FlagSpecCompletionProvider`
2. **Phase 2**: Implement `HistoryStore` → enables `HistoryCompletionProvider`
3. **Phase 6**: Implement `SequenceStore` → enables `SequenceCompletionProvider`
4. **Phase 7**: Implement `VariableCompletionProvider`
5. **Phase 8**: Refactor `TabCompleter` to use `ICompletionProvider` interface for all providers

---

## 6. Configuration

Completion behavior is controlled via `~/.psbash/config.toml` (see `config-format.md`):

```toml
[completion]
# Enable/disable specific providers
command_completion = true       # Enable CommandCompletionProvider
flag_completion = true          # Enable FlagSpecCompletionProvider
variable_completion = true      # Enable VariableCompletionProvider
history_completion = true       # Enable HistoryCompletionProvider
path_completion = true          # Enable PathCompletionProvider
sequence_suggestions = true     # Enable SequenceCompletionProvider

# Global completion settings
autosuggestions = true           # Enable fish-style inline suggestions
max_results = 100               # Maximum completions to return per provider
case_sensitive = false          # Case-sensitive matching (false on Windows by default)
```

---

## 7. Extension via Plugins

Third-party plugins can provide custom completion providers by implementing `ICompletionProvider`:

```csharp
using PsBash.Shell;

public sealed class DockerCompletionProvider : ICompletionProvider
{
    public string Name => "Docker";

    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(CompletionContext context)
    {
        var results = new List<CompletionItem>();

        // After "docker attach ", suggest container names
        if (context.Input.StartsWith("docker attach "))
        {
            var partial = ExtractPartialToken(context.Input, context.CursorPosition);
            foreach (var container in ListContainers())
            {
                if (container.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new CompletionItem(
                        container,
                        $"Running container: {container}",
                        CompletionKind.Command
                    ));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<CompletionItem>>(results);
    }
}
```

Plugin providers are loaded via `PluginLoader` and merged with built-in providers (see `plugin-architecture.md`).
