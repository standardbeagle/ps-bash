# Config File Format Specification

This document specifies the TOML configuration file format for the ps-bash interactive shell.

**Status:** PLANNED — The config file system is not yet implemented. This document is the design specification.

**Location:** `~/.psbash/config.toml`

---

## 1. Overview

The ps-bash shell reads configuration from `~/.psbash/config.toml` on startup. This file controls:

- History storage behavior and retention
- Tab completion and autosuggestion behavior
- Plugin loading and configuration

If the config file does not exist on first run, the shell creates it with default values.

---

## 2. TOML Parsing Library

**Library:** [Tomlyn](https://github.com/xoofx/Tomlyn)

**Rationale:**

- AOT-friendly — Compatible with Native AOT compilation used by ps-bash
- Pure C# implementation — No native dependencies
- Full TOML 1.0 spec compliance
- Strongly typed deserialization with `Toml.ToModel<T>()`
- MIT licensed

---

## 3. Config File Location

The config file is located at:

```
~/.psbash/config.toml
```

The path is resolved from `$HOME` (`Environment.SpecialFolder.UserProfile`) on all platforms (Windows, Linux, macOS).

---

## 4. Configuration Schema

### 4.1 Complete Example

```toml
[history]
default_store = "sqlite"          # or "atuin", "file"
cwd_filter = true                 # Up-arrow filters by CWD
max_entries = 100000              # SQLite retention limit

[completion]
enable_sequence_suggestions = true
autosuggestions = true            # fish-style gray text
flag_completion = true
path_completion = true

[plugins]
# directory = "~/.psbash/plugins/"
# atuin_db_path = "~/.local/share/atuin/history.db"
# disable_builtin_history = false
# disable_builtin_completion = false
```

### 4.2 Default Config (First Run)

When the shell starts for the first time and no config exists, it creates:

```toml
[history]
default_store = "sqlite"
cwd_filter = true
max_entries = 100000

[completion]
enable_sequence_suggestions = true
autosuggestions = true
flag_completion = true
path_completion = true

[plugins]
directory = "~/.psbash/plugins/"
atuin_db_path = "~/.local/share/atuin/history.db"
disable_builtin_history = false
disable_builtin_completion = false
```

---

## 5. Sections

### 5.1 `[history]` Section

Controls command history behavior.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `default_store` | string | `"sqlite"` | History backend: `"sqlite"`, `"atuin"`, or `"file"` |
| `cwd_filter` | boolean | `true` | When true, Up-arrow only shows commands from the current working directory |
| `max_entries` | integer | `100000` | Maximum entries to retain in SQLite history |

#### `default_store` Values

- **`"sqlite"`** — Built-in SQLite database at `~/.psbash/history.db`. Fast, indexed, supports CWD filtering.
- **`"atuin"`** — Atuin SQLite database at `~/.local/share/atuin/history.db`. Requires Atuin to be installed.
- **`"file"`** — Plain text file at `~/.psbash/history`. Bash-compatible, no indexing.

#### CWD Filtering Behavior

When `cwd_filter = true`, the shell groups history by working directory. Pressing Up only cycles through commands run in the current directory. This makes history context-aware — `git push` from `~/projects/ps-bash` doesn't appear when you're in `~/tmp`.

To see all history regardless of CWD, set `cwd_filter = false`.

---

### 5.2 `[completion]` Section

Controls tab completion and autosuggestions.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `enable_sequence_suggestions` | boolean | `true` | Enable sequence-based suggestions (e.g., after `git `, suggest branch names) |
| `autosuggestions` | boolean | `true` | Enable fish-style gray text autosuggestions as you type |
| `flag_completion` | boolean | `true` | Enable flag name completion after `-` or `--` |
| `path_completion` | boolean | `true` | Enable file and directory path completion |

#### Autosuggestions

When `autosuggestions = true`, the shell displays gray ghost text after your cursor showing a prediction from history. Press `Right` or `End` to accept the suggestion, or keep typing to ignore it.

Example:

```
$ git commit<ghost -m "fix parser bug">
```

**Behavior:**
- Gray inline suggestions appear as you type (ANSI dim text)
- Right arrow or End key accepts the full suggestion
- Ranking: CWD matches > global matches, then by recency
- Substring prefix match (not fuzzy) for performance
- Disabled when tab completion dropdown is active

See [autosuggestions.md](./autosuggestions.md) for complete specification.

#### Sequence Suggestions

When `enable_sequence_suggestions = true`, tab completion adapts to context:

- After `git checkout` — suggest branch names
- After `docker exec` — suggest container names
- After `kubectl` — suggest pods, services, etc.

This requires plugins to provide context-aware completions (see [plugin-architecture.md](./plugin-architecture.md)).

---

### 5.3 `[plugins]` Section

Controls plugin loading and behavior.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `directory` | string | `"~/.psbash/plugins/"` | Path to plugin DLL directory |
| `atuin_db_path` | string | `"~/.local/share/atuin/history.db"` | Path to Atuin history database |
| `disable_builtin_history` | boolean | `false` | Disable built-in file/SQLite history (use plugin only) |
| `disable_builtin_completion` | boolean | `false` | Disable built-in TabCompleter (use plugin only) |

#### Path Expansion

Tilde (`~`) in paths is expanded to `$HOME` automatically.

#### Plugin Discovery

Plugins are DLL files in the `directory` path. Each DLL is scanned for types implementing:

- `IHistoryStore` — Custom history backends
- `ICompletionProvider` — Custom completion sources

See [plugin-architecture.md](./plugin-architecture.md) for plugin development details.

#### Disabling Built-ins

Set `disable_builtin_history = true` when using a plugin that fully replaces history (e.g., Atuin). This prevents the shell from querying both the plugin and built-in store for every command.

Set `disable_builtin_completion = true` only when a plugin provides complete tab completion. Most users should leave this `false` to get both plugin and built-in completions.

---

## 6. Missing Key Behavior

Missing keys in `config.toml` use their default values. The shell never requires a key to be present.

**Example:** A minimal config file with only one override:

```toml
[history]
max_entries = 50000
```

All other values use defaults:
- `default_store` → `"sqlite"`
- `cwd_filter` → `true`
- All `[completion]` keys → defaults
- All `[plugins]` keys → defaults

**Invalid values** trigger warnings but do not prevent the shell from starting:

```
ps-bash: warning: config.toml: history.default_store = "invalid" is not valid, using "sqlite"
```

---

## 7. First-Run Behavior

On first launch, if `~/.psbash/config.toml` does not exist:

1. The shell creates `~/.psbash/` directory if missing
2. Writes the default config file with all keys and defaults
3. Logs a message: `ps-bash: created ~/.psbash/config.toml with defaults`

Users can edit the file immediately. Changes take effect on the next shell restart (live reload is not supported).

---

## 8. Config Class (C# Representation)

The config is deserialized into a C# class using Tomlyn:

```csharp
namespace PsBash.Shell;

public sealed record ShellConfig
{
    public HistoryConfig History { get; init; } = new();
    public CompletionConfig Completion { get; init; } = new();
    public PluginsConfig Plugins { get; init; } = new();
}

public sealed record HistoryConfig
{
    public string DefaultStore { get; init; } = "sqlite";
    public bool CwdFilter { get; init; } = true;
    public int MaxEntries { get; init; } = 100000;
}

public sealed record CompletionConfig
{
    public bool EnableSequenceSuggestions { get; init; } = true;
    public bool Autosuggestions { get; init; } = true;
    public bool FlagCompletion { get; init; } = true;
    public bool PathCompletion { get; init; } = true;
}

public sealed record PluginsConfig
{
    public string Directory { get; init; } = "~/.psbash/plugins/";
    public string AtuinDbPath { get; init; } = "~/.local/share/atuin/history.db";
    public bool DisableBuiltinHistory { get; init; } = false;
    public bool DisableBuiltinCompletion { get; init; } = false;
}
```

**Loading:**

```csharp
using Tomlyn;
using Tomlyn.Model;

public static ShellConfig LoadConfig(string path)
{
    if (!File.Exists(path))
        WriteDefaultConfig(path);

    var toml = Toml.ToModel(File.ReadAllText(path));
    return Toml.ToModel<ShellConfig>(toml);
}
```

---

## 9. Implementation Status

| Feature | Status |
|---------|--------|
| TOML config file | Not implemented |
| `[history]` section | Not implemented |
| `[completion]` section | Not implemented |
| `[plugins]` section | Not implemented |
| Tomlyn integration | Not implemented |
| First-run config creation | Not implemented |

This document serves as the specification for the implementation phase. See [plugin-architecture.md](./plugin-architecture.md) for the plugin system design.

---

## 10. Migration from `.psbashrc`

The existing `~/.psbashrc` file is still sourced on startup for shell commands (`alias`, `export`, etc.). The `config.toml` file is for settings only, not commands.

**Separation of concerns:**

- **`.psbashrc`** — Shell commands to execute on startup (aliases, env vars, functions)
- **`config.toml`** — Persistent settings for shell behavior

This matches the bash pattern of `.bashrc` (commands) vs separate config for tools (e.g., `.inputrc` for readline, `.gitconfig` for git).

---

## 11. Validation Rules

The enforcer validates keys at load time:

### `[history]` validation

- `default_store` must be `"sqlite"`, `"atuin"`, or `"file"` — invalid values default to `"sqlite"` with a warning
- `max_entries` must be `> 0` — invalid values default to `100000` with a warning

### `[completion]` validation

- All boolean keys are normalized to `true`/`false` (TOML spec handles this)
- No additional validation needed

### `[plugins]` validation

- `directory` must be a valid absolute path or `~/`-prefixed path — relative paths are rejected with a warning
- `atuin_db_path` must be a valid absolute path or `~/`-prefixed path
- Paths are expanded on load; invalid expansions log warnings but don't prevent startup

---

## 12. Example Configs

### Minimal Config (Power User)

```toml
[history]
cwd_filter = false   # Show all history, not just CWD
```

### Atuin-Only History

```toml
[history]
default_store = "atuin"
cwd_filter = false   # Atuin has its own filtering

[plugins]
disable_builtin_history = true   # Don't query built-in store
```

### Disable Autosuggestions

```toml
[completion]
autosuggestions = false
```

### Custom Plugin Directory

```toml
[plugins]
directory = "~/code/ps-bash-plugins/build/"
```

### No Completion (Bare Bones)

```toml
[completion]
enable_sequence_suggestions = false
autosuggestions = false
flag_completion = false
path_completion = true   # Keep file completion
```
