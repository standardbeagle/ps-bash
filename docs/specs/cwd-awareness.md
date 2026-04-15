# CWD-Aware History Design

This document specifies how the ps-bash interactive shell uses the current working directory (CWD) to provide contextually relevant command history. All history features respect CWD to make commands more discoverable and workflow-aware.

**Status:** DESIGNED — Feature specification complete. Implementation pending (P2-P4 in `shell-implementation-phases.md`).

---

## 1. Overview

Traditional shell history is global — `git push` from `~/projects/ps-bash` and `git push` from `~/tmp` are indistinguishable. ps-bash makes history **directory-aware**:

- **Up-arrow** defaults to commands from the current directory
- **Ctrl-R** toggles between CWD-filtered and global search
- **Autosuggestions** prefer commands run in this directory
- **Ranking** balances exact CWD match, parent directory match, and recency

This mirrors real workflows: commands you run in `~/app` are usually relevant when you're back in `~/app`, but not when you're in `~/docs`.

---

## 2. History Storage

### 2.1 CWD Column

Every history entry records the working directory at execution time:

```sql
CREATE TABLE history (
    id INTEGER PRIMARY KEY,
    command TEXT NOT NULL,
    cwd TEXT NOT NULL,           -- Working directory
    timestamp TEXT NOT NULL,
    exit_code INTEGER,
    duration_ms INTEGER,
    session TEXT
);
```

**Storage details:**
- `cwd` is stored as an absolute path
- No normalization or symlinks resolution (raw path from OS)
- Indexed for fast CWD-scoped queries

### 2.2 Index

```sql
CREATE INDEX idx_history_cwd ON history(cwd);
CREATE INDEX idx_history_cwd_timestamp ON history(cwd, timestamp DESC);
```

The compound index supports the common query pattern: "get recent commands from this directory."

---

## 3. CWD Filtering Modes

### 3.1 Modes

| Mode | Description | Scope |
|------|-------------|-------|
| **CWD** | Only commands from exact current directory | `HistoryQuery.Cwd = Directory.GetCurrentDirectory()` |
| **CWD + Parents** | Commands from current directory and any ancestor | Hierarchical match (see Section 4) |
| **Global** | All commands, no CWD filter | `HistoryQuery.Cwd = null` |

### 3.2 Default Behavior

By default, history features use **CWD mode**:

- **Up/Down arrows** — Only cycle through commands from exact CWD
- **Autosuggestions** — Prefer exact CWD matches, fall back to global
- **Ctrl-R** — Starts in CWD mode, toggleable to global via `Ctrl-G`

Configuration:

```toml
[history]
cwd_filter = true   # Default: true
```

When `cwd_filter = false`, all features use **Global mode** — CWD is ignored.

---

## 4. Parent Directory Matching

When `cwd_filter = true` and **CWD + Parents mode** is enabled (default for Ctrl-R), history search includes commands from ancestor directories. This enables useful workflows:

### 4.1 Hierarchy Example

```
~/projects/ps-bash/
  ├── src/
  │   ├── PsBash.Core/
  │   │   └── Parser/     # CWD: ~/projects/ps-bash/src/PsBash.Core/Parser
  │   └── PsBash.Shell/
  └── docs/
```

When in `~/projects/ps-bash/src/PsBash.Core/Parser`, parent directory matching includes commands from:
- `~/projects/ps-bash/src/PsBash.Core/Parser` (exact CWD)
- `~/projects/ps-bash/src/PsBash.Core/` (parent)
- `~/projects/ps-bash/src/` (grandparent)
- `~/projects/ps-bash/` (great-grandparent)
- `~/projects/` (great-great-grandparent)
- `~/` (root, rarely useful)

### 4.2 Implementation

Parent matching is done via path prefix comparison:

```csharp
bool IsParentOrCurrent(string historyCwd, string currentCwd)
{
    // Exact match
    if (historyCwd == currentCwd)
        return true;

    // Parent: history CWD is a prefix of current CWD + path separator
    // e.g., historyCwd = "/home/user/projects"
    //      currentCwd = "/home/user/projects/ps-bash/src"
    // Result: true (projects is an ancestor)
    return currentCwd.StartsWith(historyCwd + Path.DirectorySeparatorChar);
}
```

**SQL Implementation** (SQLite):

```sql
-- CWD + Parents query
SELECT * FROM history
WHERE ? LIKE cwd || '/%'   -- ? is current CWD, matches if cwd is a prefix
   OR cwd = ?              -- Exact match
ORDER BY
    -- Prefer exact match, then closest parent (longest path)
    LENGTH(cwd) DESC,
    timestamp DESC
LIMIT 100;
```

The `LENGTH(cwd) DESC` sort puts exact matches (longest path) first, then closer parents before distant ancestors.

### 4.3 Use Cases

| Scenario | Why Parent Matching Helps |
|----------|---------------------------|
| `cd src/Parser` then `Up` | Shows `dotnet build` from project root |
| `cd tests/Unit` then `Up` | Shows `dotnet test` from parent test dir |
| Deeply nested project | Still reach high-level commands (`git push`, `docker build`) |
| Monorepo navigation | Commands from repo root are accessible in subdirs |

---

## 5. Ranking Algorithm

History features use a **tiered ranking** that balances directory match quality with recency:

### 5.1 Score Tiers (Highest to Lowest)

| Tier | Condition | Description |
|------|-----------|-------------|
| 1 | **Exact CWD match** | Command run in this exact directory |
| 2 | **Parent directory match** | Command run in an ancestor (closer = higher) |
| 3 | **Global match** | Command run anywhere else |

Within each tier, entries are sorted by **recency** (newest first).

### 5.2 Pseudocode

```csharp
int RankScore(HistoryEntry entry, string currentCwd)
{
    // Tier 1: Exact match (highest score)
    if (entry.Cwd == currentCwd)
        return 1000 + RecencyScore(entry.Timestamp);

    // Tier 2: Parent match (score based on proximity)
    if (IsParentOrCurrent(entry.Cwd, currentCwd))
    {
        // Closer parent = higher score
        int depth = PathDepth(entry.Cwd, currentCwd);
        return 500 - (depth * 10) + RecencyScore(entry.Timestamp);
    }

    // Tier 3: Global match (baseline score)
    return RecencyScore(entry.Timestamp);
}

int RecencyScore(DateTime timestamp)
{
    // 0-100 points based on how recent (100 = just now, 0 = very old)
    var hoursAgo = (DateTime.UtcNow - timestamp).TotalHours;
    return Math.Max(0, 100 - (int)hoursAgo);
}
```

### 5.3 Example

History:
| Command | CWD | Timestamp |
|---------|-----|-----------|
| `git push` | `~/projects/ps-bash` | 1 week ago |
| `git commit` | `~/projects/ps-bash` | 1 week ago |
| `dotnet build` | `~/projects/ps-bash/src` | 2 days ago |
| `dotnet test` | `~/projects/ps-bash/src/PsBash.Core` | 1 hour ago |
| `git status` | `~/tmp` | 5 minutes ago |

**When pressing Up in `~/projects/ps-bash/src/PsBash.Core`:**

1. `dotnet test` (exact CWD, recent) — **shown first**
2. `dotnet build` (parent `~/src`, recent) — **shown second**
3. `git push` (parent root, old) — **shown third**
4. `git commit` (parent root, old) — **shown fourth**
5. `git status` (different CWD, very recent but global) — **shown last** (or not shown if CWD-filter excludes global)

---

## 6. Feature-Specific Behavior

### 6.1 Up/Down Arrow Navigation

**Behavior:**
- Default: Cycle through exact CWD matches only
- If no exact CWD matches: Fall back to parent matches, then global
- Wrapping: Oldest CWD entry → newest CWD entry → back to oldest

**Configuration:**
```toml
[history]
cwd_filter = true   # Enable CWD filtering
```

**Disabling:** Set `cwd_filter = false` to use global history for Up/Down.

### 6.2 Ctrl-R Full-Screen Search

**Behavior:**
- Starts in **CWD + Parents mode** (exact + ancestors)
- Mode indicator shows `[CWD]` or `[All]`
- Toggle with `Ctrl-G` to switch between modes
- Results ranked by the algorithm in Section 5

**UI:**
```
(i-search) `docker` [CWD]                              12 matching

  docker build -t myapp .            ~/app   2m ago    Exit 0   #145
  docker run --rm -it myapp          ~/app   5h ago    Exit 0   #142
  docker-compose up -d               ~/      1d ago    Exit 0   #138

[Enter] Execute  [Tab] Edit  [Ctrl-G] Toggle CWD/All  [Esc] Cancel
```

**See:** [`ctrlr-ui.md`](./ctrlr-ui.md) for full UI specification.

### 6.3 Autosuggestions

**Behavior:**
- Substring prefix match only (not fuzzy)
- Exact CWD matches preferred over all others
- If no exact CWD match, shows best global match (by recency)
- Updates on every keystroke

**Configuration:**
```toml
[completion]
autosuggestions = true   # Enable autosuggestions

[history]
cwd_filter = true       # Prefer CWD matches
```

**See:** [`autosuggestions.md`](./autosuggestions.md) for full specification.

---

## 7. Configuration

### 7.1 Enable/Disable CWD Filtering

```toml
[history]
cwd_filter = true   # Default: true
```

| Value | Behavior |
|-------|----------|
| `true` | Up/Down/autosuggestions/ctrl-r prefer CWD matches |
| `false` | All history is global (traditional bash behavior) |

### 7.2 Per-Feature Control

Individual features can be controlled independently:

```toml
[completion]
autosuggestions = true   # Autosuggestions respect cwd_filter

[history]
cwd_filter = true        # Up/Down and Ctrl-R respect this
```

There is no separate "autosuggestions_cwd_filter" — autosuggestions inherit the global `cwd_filter` setting.

---

## 8. Implementation Notes

### 8.1 IHistoryStore Integration

The `IHistoryStore.SearchAsync` method supports CWD filtering:

```csharp
var results = await _historyStore.SearchAsync(new HistoryQuery
{
    Filter = prefix,
    Cwd = cwdFilterMode ? Directory.GetCurrentDirectory() : null,
    Limit = 100
});
```

**Parent matching** is implemented client-side or via LIKE queries (see Section 4.2).

### 8.2 Performance

CWD filtering must not slow down history navigation:

| Query | Index Used | Target Time |
|-------|------------|-------------|
| Exact CWD match | `idx_history_cwd_timestamp` | < 2 ms |
| Parent match (LIKE) | `idx_history_cwd` (prefix scan) | < 5 ms |
| Global match | `idx_history_timestamp` | < 3 ms |

**Optimization:**
- Start with exact CWD query (fastest, indexed)
- If no results, run parent query (still fast)
- Only query global as fallback

### 8.3 Caching Strategy

To reduce database queries:

```csharp
// Cache per CWD
private readonly Dictionary<string, List<HistoryEntry>> _cwdCache = new();

List<HistoryEntry> GetCwdHistory(string cwd)
{
    if (_cwdCache.TryGetValue(cwd, out var cached))
        return cached;

    var entries = QueryHistory(cwd);
    _cwdCache[cwd] = entries;
    return entries;
}

// Invalidate on cd command
void OnDirectoryChange(string newCwd)
{
    // Preload new CWD history asynchronously
    _ = Task.Run(() => GetCwdHistory(newCwd));
}
```

---

## 9. Edge Cases

### 9.1 Symlinks

Paths are stored as-is (no symlink resolution):

```
$ ln -s ~/projects/ps-bash ~/快捷方式
$ cd ~/快捷方式
$ dotnet build   # Stored with cwd = "~/快捷方式", not "~/projects/ps-bash"
```

**Rationale:** Preserves user intent, avoids confusing behavior when symlinks change.

### 9.2 Case Sensitivity

On case-insensitive filesystems (Windows, macOS), path comparison is case-insensitive for parent matching:

```csharp
bool IsParentOrCurrent(string historyCwd, string currentCwd)
{
    var hc = _platform.IsCaseSensitive ? historyCwd : historyCwd.ToLowerInvariant();
    var cc = _platform.IsCaseSensitive ? currentCwd : currentCwd.ToLowerInvariant();
    return cc.StartsWith(hc + Path.DirectorySeparatorChar) || hc == cc;
}
```

### 9.3 Network Drives

Network paths (UNC on Windows, mounted drives on Unix) work identically to local paths:

```
$ cd /mnt/server/projects/app
$ git push    # Stored with cwd = "/mnt/server/projects/app"
```

**Caveat:** Network latency may increase query time. Consider local caching for slow mounts.

### 9.4 Very Deep Paths

For deeply nested directories (> 200 characters), path comparison may slow down:

```
~/very/deeply/nested/path/that/goes/and/goes/and/goes/.../project
```

**Mitigation:** The `LENGTH(cwd)` sort in parent matching limits how many ancestors are checked (usually < 20 levels deep in practice).

---

## 10. Future Enhancements

### 10.1 Project Root Detection

Automatically detect project roots (`.git`, `package.json`, `Cargo.toml`, etc.) and treat the entire subtree as one "project scope" for history:

```
~/projects/ps-bash/src/PsBash.Core/Parser  # CWD
~/projects/ps-bash/                         # Detected project root
# History from any ~/projects/ps-bash/* subdirectory is considered "local"
```

### 10.2 Frequency Boosting

Add a frequency factor to ranking:

```csharp
score += GetCommandFrequency(command, cwd) * 10;
```

Commands run frequently in this directory rank higher than one-off commands.

### 10.3 Session Grouping

Group history by shell session (based on `session_id`) and prefer recent sessions:

```
# You opened a new shell 10 minutes ago
# Commands from that session rank slightly higher (session recency boost)
```

---

## 11. Acceptance Criteria

Implementation is complete when:

- [ ] History entries record `cwd` at execution time
- [ ] Up/Down arrows default to CWD-filtered history
- [ ] Up/Down arrows fall back to parent matches when no exact CWD matches
- [ ] Ctrl-R starts in CWD mode, toggleable to global via `Ctrl-G`
- [ ] Ctrl-R mode indicator shows `[CWD]` or `[All]`
- [ ] Autosuggestions prefer exact CWD matches
- [ ] Ranking algorithm: exact CWD > parent > global > recency
- [ ] `cwd_filter = false` disables CWD filtering globally
- [ ] CWD queries complete in < 5ms over 100k history
- [ ] Parent directory matching works via prefix comparison

---

## 12. References

- Related specs:
  - [`sqlite-history-schema.md`](./sqlite-history-schema.md) — CWD column and indexes
  - [`history-store-interface.md`](./history-store-interface.md) — `HistoryQuery.Cwd` API
  - [`ctrlr-ui.md`](./ctrlr-ui.md) — Ctrl-R CWD toggle
  - [`autosuggestions.md`](./autosuggestions.md) — CWD-aware suggestions
  - [`config-format.md`](./config-format.md) — `cwd_filter` setting
  - [`keybindings.md`](./keybindings.md) — Up/Down arrow CWD filtering
