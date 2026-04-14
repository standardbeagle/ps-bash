# Design Decisions and Rationale

This document explains key architectural decisions for the ps-bash interactive shell and the rationale behind them. These decisions shape the implementation across multiple phases (see `shell-implementation-phases.md`).

---

## 1. SQLite for History Storage

### Decision

Store command history in a SQLite database (`~/.psbash/history.db`) rather than a plain text file.

### Rationale

**Enables rich metadata queries without loading entire history into memory**

- SQLite supports indexed queries on timestamp, working directory, exit code, and duration
- Up/Down arrow history can be filtered by current CWD: `WHERE cwd = ? ORDER BY timestamp DESC LIMIT 10`
- Ctrl-R fuzzy search can rank matches by recency and CWD proximity without scanning all entries
- Sequence pair queries (what follows `git commit`?) run against a materialized view

**Proven at scale**

- Atuin uses SQLite for shell history and successfully handles 100k+ entries
- A single history table with proper indexes remains fast for common queries
- SQLite is embedded (no separate server process) and available on all platforms via `Microsoft.Data.Sqlite`

**Structured data enables future features**

- Exit code tracking: show failed commands in history
- Duration tracking: identify slow commands
- Session grouping: analyze shell sessions by `session_id`
- Sequence frequency: learn workflow patterns (`git commit` typically followed by `git push`)

### Trade-offs

| Pro | Con |
|-----|-----|
| Fast indexed queries | Requires SQLite dependency (already in .NET runtime) |
| Rich metadata | More complex than text file |
| ACID guarantees | Migration path from legacy text file required |
| Single file, easy backup | Cannot edit with a text editor |

### Implementation

```sql
CREATE TABLE history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    command TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    cwd TEXT NOT NULL,
    exit_code INTEGER,
    duration_ms INTEGER,
    session_id TEXT
);

CREATE INDEX idx_history_timestamp ON history(timestamp DESC);
CREATE INDEX idx_history_cwd ON history(cwd, timestamp DESC);
```

### Related Files

- `src/PsBash.Shell/HistoryStore.cs` -- SQLite history backend
- `src/PsBash.Shell/HistoryEntry.cs` -- History record model

---

## 2. VT100 Line Editor, Not Library Dependency

### Decision

Implement a custom VT100 line editor (`LineEditor.cs`) instead of using a third-party readline library.

### Rationale

**AOT-compatible**

- Native AOT compilation ahead of time (ahead-of-time) restricts library usage
- Many readline libraries depend on `DynamicMethod` or runtime code generation
- Custom implementation ensures full AOT compatibility without reflection tricks

**No native dependencies**

- `Readline` (Linux) and `EditLine` (macOS) require P/Invoke to native libraries
- Cross-platform builds become complex with native library shims
- Pure C# implementation works everywhere .NET runs

**Full control over UX**

- Emacs bindings are the default, but the shell can customize behavior
- Integration with `TabCompleter` is direct (no callback layer)
- History filtering by CWD is implemented in the navigation logic, not bolted on
- Async interfaces for future network stores (Atuin sync) are easier to add

**The prompt already uses ANSI escapes**

- `InteractiveShell.BuildPrompt()` emits VT100 color codes
- The shell already assumes a VT100-compatible terminal
- Line editor is in good company: `grep --color=ls`, `ls --color=auto`, etc.

### Trade-offs

| Pro | Con |
|-----|-----|
| AOT-compatible | Reinventing well-tested readline behavior |
| No native deps | Implementation and maintenance burden |
| Custom UX | Missing features: vi mode, multi-line editing |
| Direct integration | Edge cases: wide Unicode chars, bracketed paste |

### Mitigations

- Start with emacs mode only (most common for developers)
- Add vi mode later via user feedback
- Reference existing readline implementations for edge cases
- Test with real terminals across platforms

### Related Files

- `src/PsBash.Shell/LineEditor.cs` -- VT100 line editor implementation
- `src/PsBash.Shell/LineEditorTests.cs` -- Keybinding tests

---

## 3. FlagSpecs in C#, Not PowerShell

### Decision

Store command flag specifications as an embedded JSON resource in the AOT shell binary, not in the PowerShell runtime module.

### Rationale

**AOT shell has no access to PSM1 at runtime**

- `ps-bash` binary spawns a `pwsh` worker for transpiled bash execution
- Tab completion happens in the AOT process, before sending input to the worker
- Querying the PSM1 module's `$script:BashFlagSpecs` would require IPC on every Tab press

**Startup-time data access**

- Embedded JSON loads once at startup into a `Dictionary<string, FlagSpec[]>`
- Flag completion queries are in-memory lookups: `O(1)` average case
- No IPC latency, no JSON parsing on every Tab

**Single source of truth**

- Flag specs are generated from the runtime module during build
- AOT shell and PSM1 module stay in sync via CI
- Completion data is versioned with the binary (no drift)

### Trade-offs

| Pro | Con |
|-----|-----|
| Fast in-memory lookup | Build-time generation step required |
| No IPC overhead | Flags must be kept in sync manually |
| Works without worker process | Updates require rebuild, not script edit |

### Implementation

```csharp
// src/PsBash.Shell/FlagSpecs.cs
public static class FlagSpecs
{
    private static readonly Dictionary<string, FlagSpec[]> Data = Load();

    public static IReadOnlyList<FlagSpec>? GetFlags(string command) =>
        Data.TryGetValue(command, out var specs) ? specs : null;
}

// Embedded resource: PsBash.Shell.Resources.FlagSpecs.json
{
  "ls": [
    {"flag": "-a", "desc": "show hidden"},
    {"flag": "-l", "desc": "long listing"}
  ]
}
```

### Generation Script

A PowerShell script extracts FlagSpecs from `PsBash.psm1` and generates JSON at build time.

### Related Files

- `src/PsBash.Shell/FlagSpecs.cs` -- Flag spec loader
- `src/PsBash.Shell/Resources/FlagSpecs.json` -- Embedded flag data
- `scripts/generate-flagspecs.ps1` -- Build-time generator

---

## 4. Sequence Pairs as First-Class Concept

### Decision

Track command sequences (what follows what) as a materialized database table, not a derived query.

### Rationale

**Workflow awareness, not just recency**

- Traditional history: "What did I type before?"
- Sequence awareness: "What typically follows this command?"
- Example: After `git commit`, suggest `git push` (frequent sequence)

**Efficient queries**

- Materialized table `sequences(prev_command, next_command, count)` aggregates pair frequency
- Query: `SELECT next_command FROM sequences WHERE prev_command = ? ORDER BY count DESC LIMIT 5`
- No need to scan entire history to compute frequencies on every suggestion

**Post-command suggestions**

- After a command completes, show a ranked list of likely next commands
- Combines sequence frequency, recency, and CWD context
- Learn user workflow: `docker build` often followed by `docker push`

### Database Schema

```sql
CREATE TABLE sequences (
    prev_command TEXT NOT NULL,
    next_command TEXT NOT NULL,
    count INTEGER NOT NULL,
    PRIMARY KEY (prev_command, next_command)
);

CREATE INDEX idx_sequences_prev ON sequences(prev_command, count DESC);
```

### Update Logic

After each command executes:

```csharp
void RecordSequence(string? prev, string current)
{
    if (prev is null) return;
    // Upsert: INSERT OR REPLACE INTO sequences VALUES (?, ?, 1)
    // On conflict: count = count + 1
}
```

### Trade-offs

| Pro | Con |
|-----|-----|
| Fast sequence queries | Additional storage per pair |
| Workflow learning | Privacy concern for some users |
| Post-command suggestions | Requires opt-in config |

### Privacy Consideration

Sequence tracking reveals workflow patterns. Offer an opt-out config flag for users who prefer local-only history.

### Related Files

- `src/PsBash.Shell/SequenceStore.cs` -- Pair tracking and queries
- `src/PsBash.Shell/Suggester.cs` -- Uses sequences for ranking

---

## 5. Plugins Are Additive, Not Replacements

### Decision

The built-in SQLite history store and completion provider always exist. Plugins supplement or extend, they do not replace core functionality.

### Rationale

**Shell always works, even without plugins**

- First-run experience is functional: history, completion, Ctrl-R work out of the box
- Plugin failures are non-fatal (log error, continue with built-in)
- No "plugin required to use this feature" anti-pattern

**Built-in implementations are reference implementations**

- `FileHistoryStore` shows how `IHistoryStore` works
- `TabCompleter` demonstrates `ICompletionProvider`
- Plugin authors have working code to study

**Plugins stack and de-duplicate**

- Multiple completion providers can coexist: built-in + GitCompletion + DockerCompletion
- Results are merged and de-duplicated (union of all candidates)
- Users get the best of all worlds

**Opt-in replacement when desired**

- Atuin users can disable built-in history: `DisableBuiltInHistory = true`
- Advanced users can opt into custom workflows
- Default behavior is sensible for 95% of users

### Architecture

```
ShellConfig
├── HistoryStores (ordered, first match wins)
│   ├── Built-in FileHistoryStore (if not disabled)
│   ├── Plugin: AtuinHistoryStore
│   └── Plugin: CustomHistoryStore
├── CompletionProviders (union of results)
│   ├── Built-in TabCompleter (if not disabled)
│   ├── Plugin: GitCompletionProvider
│   └── Plugin: DockerCompletionProvider
```

### Trade-offs

| Pro | Con |
|-----|-----|
| Always works | Cannot fully replace built-ins via plugin alone |
| Non-fatal plugin errors | Config flag required to disable built-in |
| Reference implementations | Slightly more complex config |

### Related Files

- `src/PsBash.Shell/IHistoryStore.cs` -- History store interface
- `src/PsBash.Shell/ICompletionProvider.cs` -- Completion provider interface
- `src/PsBash.Shell/PluginLoader.cs` -- DLL discovery and loading

---

## 6. Async Interfaces for I/O

### Decision

History store and completion provider interfaces use async methods (`Task<IReadOnlyList<T>>`), even for local SQLite.

### Rationale

**SQLite I/O shouldn't block keystroke handling**

- History queries happen on Up/Down arrows (user waits)
- Tab completion triggers on Tab key (user expects instant response)
- Blocking I/O causes noticeable lag: `ReadLine()` blocks, `SQLite.Open()` blocks
- Async queries keep the UI responsive

**Future network stores require async**

- Atuin sync queries a remote SQLite database or HTTP API
- Custom plugins may query cloud services (GitHub, Jira, etc.)
- Async interfaces today enable remote stores tomorrow without breaking changes

**Consistent with .NET patterns**

- `System.Data.SQLite` supports async commands
- `Microsoft.Data.Sqlite` has async methods
- Async all the way is idiomatic C#

### Interface Design

```csharp
public interface IHistoryStore
{
    Task<IReadOnlyList<string>> Search(string prefix, int limit = 100);
    Task Append(string command);
}

public interface ICompletionProvider
{
    Task<IReadOnlyList<string>> Complete(string line, int cursor, CompletionContext context);
}
```

### Usage in LineEditor

```csharp
private async void HistoryPrev(string prompt)
{
    if (_historyIndex <= 0) return;
    var entries = await _historyStore.Search("", _historyIndex + 10);
    // Update buffer from entries
}
```

### Trade-offs

| Pro | Con |
|-----|-----|
| Non-blocking UI | More complex async/await in LineEditor |
| Ready for network stores | Cancellation tokens required for responsive UI |
| Idiomatic .NET | Testing async code is slightly harder |

### Cancellation

Queries accept `CancellationToken` to abandon stale requests:

- User types "git" then quickly hits Tab
- Two suggestion queries race: "g" prefix, "gi" prefix, "git" prefix
- Only the latest query wins; previous ones are cancelled

### Related Files

- `src/PsBash.Shell/IHistoryStore.cs` -- Async history interface
- `src/PsBash.Shell/ICompletionProvider.cs` -- Async completion interface
- `src/PsBash.Shell/LineEditor.cs` -- Async keystroke handling

---

## Summary Table

| Decision | Key Benefit | Trade-off |
|----------|-------------|-----------|
| SQLite over file | Rich metadata queries, proven scale | Migration complexity |
| VT100 custom editor | AOT-compatible, no native deps | Implementation burden |
| FlagSpecs in C# | Fast lookup, no IPC | Build-time generation |
| Sequence pairs | Workflow learning | Privacy opt-out needed |
| Plugins additive | Always works, non-fatal | Config flag for replacement |
| Async interfaces | Non-blocking UI, remote-ready | Async complexity |

---

## Related Documents

- `shell-implementation-phases.md` -- Incremental implementation plan
- `plugin-architecture.md` -- Plugin system specification
- `flagspec-extraction.md` -- FlagSpecs generation plan
- `completion-providers.md` -- Completion provider design
