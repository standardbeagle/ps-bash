# Plugin Architecture Specification

This document describes the ps-bash plugin system, which allows extending the interactive shell with custom history stores and completion providers.

Target implementation files:
- `src/PsBash.Shell/ShellConfig.cs` -- configuration class
- `src/PsBash.Shell/IHistoryStore.cs` -- history store interface
- `src/PsBash.Shell/ICompletionProvider.cs` -- completion provider interface
- `src/PsBash.Shell/PluginLoader.cs` -- DLL discovery and loading

---

## 1. Overview

The ps-bash shell supports two plugin types:

| Plugin Type | Interface | Purpose |
|-------------|-----------|---------|
| History Store | `IHistoryStore` | Replace or augment the built-in file-based command history |
| Completion Provider | `ICompletionProvider` | Add custom tab completion logic (e.g., git branches, docker containers) |

Plugins are discovered as DLL files in `~/.psbash/plugins/` at shell startup. Each plugin DLL is scanned for types implementing the plugin interfaces. Registered plugins can either extend built-in functionality or replace it entirely via configuration flags.

---

## 2. ShellConfig Class

`ShellConfig` is the central configuration object passed to `InteractiveShell.RunAsync`. It controls which plugins are active and whether built-in implementations are enabled.

### 2.1 Class Definition

```csharp
namespace PsBash.Shell;

public sealed class ShellConfig
{
    /// <summary>
    /// History stores to query. Order matters: first match wins.
    /// If DisableBuiltInHistory is false, the built-in file store is prepended.
    /// </summary>
    public List<IHistoryStore> HistoryStores { get; } = new();

    /// <summary>
    /// Completion providers to query. All are consulted (union of results).
    /// If DisableBuiltInCompletion is false, the built-in file/path completer is included.
    /// </summary>
    public List<ICompletionProvider> CompletionProviders { get; } = new();

    /// <summary>
    /// When true, the built-in file-based history store (~/.psbash/history) is not added.
    /// Use this when a plugin fully replaces history (e.g., Atuin).
    /// </summary>
    public bool DisableBuiltInHistory { get; set; }

    /// <summary>
    /// When true, the built-in TabCompleter is not included.
    /// Use this when a plugin handles all completion (rare).
    /// </summary>
    public bool DisableBuiltInCompletion { get; set; }
}
```

### 2.2 Default Configuration

When `ShellConfig` is not provided by the user, the shell uses a default configuration:

```csharp
var config = new ShellConfig
{
    // Built-in file history at ~/.psbash/history is always included unless disabled
    DisableBuiltInHistory = false,
    // Built-in file/path/command completion is always included unless disabled
    DisableBuiltInCompletion = false,
};
// PluginLoader discovers and adds plugin DLLs to HistoryStores/CompletionProviders
PluginLoader.LoadPlugins(config, "~/.psbash/plugins/");
```

---

## 3. Plugin Interfaces

### 3.1 IHistoryStore

History stores are queried during:

- **Up-arrow history navigation** (`LineEditor.HistoryPrev`)
- **Down-arrow history navigation** (`LineEditor.HistoryNext`)
- **Reverse-i-search** (`Ctrl-R`) — when implemented

```csharp
namespace PsBash.Shell;

/// <summary>
/// A pluggable command history store.
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Returns history entries matching the query, ordered newest-first.
    /// The shell may request all entries (empty prefix) or filter as the user types.
    /// </summary>
    /// <param name="prefix">Text prefix to filter by. Empty string = all entries.</param>
    /// <param name="limit">Maximum number of entries to return. Avoid unbounded queries.</param>
    /// <returns>History entries, newest first.</returns>
    IReadOnlyList<string> Search(string prefix, int limit = 100);

    /// <summary>
    /// Adds a command to the history store. Called after command execution.
    /// Implementations should deduplicate and trim to avoid unbounded growth.
    /// </summary>
    /// <param name="command">The command line to store.</param>
    void Append(string command);
}
```

### 3.2 ICompletionProvider

Completion providers are queried during:

- **Tab key press** (`LineEditor.HandleTab`)
- **Automatic suggestion** — when implemented (fish-style autosuggestions)

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
    IReadOnlyList<string> Complete(string line, int cursor, CompletionContext context);
}

/// <summary>
/// Context passed to completion providers.
/// </summary>
public sealed record CompletionContext
{
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;
    public IReadOnlyDictionary<string, string> Aliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string?> Environment { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}
```

---

## 4. DLL Discovery and Loading

### 4.1 Plugin Directory

Plugins are discovered from:

```
~/.psbash/plugins/
    ├── AtuinHistory.dll
    ├── GitCompletion.dll
    └── DockerCompletion.dll
```

The plugin path is resolved from `$HOME` (`Environment.SpecialFolder.UserProfile`) on all platforms.

### 4.2 AssemblyLoadContext

Each plugin DLL is loaded into an **isolated** `AssemblyLoadContext` to:

- Allow unloading (for future plugin reload support)
- Prevent dependency conflicts between plugins
- Avoid version conflicts with the host ps-bash assembly

```csharp
internal static class PluginLoader
{
    private static readonly string PluginDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".psbash", "plugins");

    public static void LoadPlugins(ShellConfig config, string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
            return;

        foreach (var dllPath in Directory.EnumerateFiles(pluginDirectory, "*.dll"))
        {
            using var context = new AssemblyLoadContext(dllPath, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(dllPath);
                RegisterPlugins(assembly, config);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ps-bash: plugin load failed: {dllPath}: {ex.Message}");
            }
        }
    }

    private static void RegisterPlugins(Assembly assembly, ShellConfig config)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsInterface || type.IsAbstract)
                continue;

            // Register history stores
            if (typeof(IHistoryStore).IsAssignableFrom(type))
            {
                var store = (IHistoryStore?)Activator.CreateInstance(type);
                if (store is not null)
                    config.HistoryStores.Add(store);
            }

            // Register completion providers
            if (typeof(ICompletionProvider).IsAssignableFrom(type))
            {
                var provider = (ICompletionProvider?)Activator.CreateInstance(type);
                if (provider is not null)
                    config.CompletionProviders.Add(provider);
            }
        }
    }
}
```

### 4.3 Type Activation

Plugins are instantiated via **parameterless constructors**. The host does not perform dependency injection. If a plugin requires configuration, it should:

1. Read from a known config file (e.g., `~/.psbash/plugin-name/config.json`)
2. Read environment variables (e.g., `$PSBASH_ATUIN_DB_PATH`)
3. Use sensible defaults

### 4.4 Error Handling

Plugin load failures are **non-fatal**:

- Failed DLLs log to stderr and are skipped
- Invalid types are skipped
- Exceptions during completion queries are caught and logged

A malformed plugin cannot crash the shell.

---

## 5. Registration Order

Plugins are registered in a specific order that determines precedence.

### 5.1 History Stores

History stores are queried in list order; the first match wins.

Default order:

```
1. Built-in file store (if DisableBuiltInHistory = false)
   - ~/.psbash/history
2. Plugin history stores (in DLL filename order)
   - AtuinHistory.dll → AtuinHistoryStore
   - CustomHistory.dll → CustomHistoryStore
```

**Example**: If both the built-in store and Atuin have a matching entry, the built-in store wins (it's first). To make Atuin the primary store, set `DisableBuiltInHistory = true`.

### 5.2 Completion Providers

All completion providers are queried; results are merged and de-duplicated.

Default set:

```
1. Built-in TabCompleter (if DisableBuiltInCompletion = false)
   - Commands, aliases, $PATH executables, file paths
2. Plugin completion providers
   - GitCompletionProvider → branch names, tags
   - DockerCompletionProvider → container names, image names
```

The shell displays the union of all candidates. If multiple plugins return the same candidate (e.g., "main" as both a git branch and a local directory), it appears once.

---

## 6. Plugin Examples

### 6.1 Atuin History Plugin

Atuin is a shell history manager that stores commands in a SQLite database. This plugin bridges ps-bash to the Atuin database.

**DLL**: `AtuinHistory.dll`

**Implementation**:

```csharp
using Microsoft.Data.Sqlite;

namespace PsBash.Plugins;

public sealed class AtuinHistoryStore : IHistoryStore
{
    private readonly string _dbPath;

    public AtuinHistoryStore()
    {
        // Default Atuin database location
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        _dbPath = Path.Combine(dataHome, "atuin", "history.db");

        if (!File.Exists(_dbPath))
            throw new InvalidOperationException($"Atuin database not found: {_dbPath}");
    }

    public IReadOnlyList<string> Search(string prefix, int limit = 100)
    {
        var results = new List<string>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        if (string.IsNullOrEmpty(prefix))
        {
            command.CommandText = @"
                SELECT command
                FROM history
                ORDER BY timestamp DESC
                LIMIT @limit";
            command.Parameters.AddWithValue("@limit", limit);
        }
        else
        {
            command.CommandText = @"
                SELECT command
                FROM history
                WHERE command LIKE @prefix || '%'
                ORDER BY timestamp DESC
                LIMIT @limit";
            command.Parameters.AddWithValue("@prefix", prefix);
            command.Parameters.AddWithValue("@limit", limit);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));

        return results;
    }

    public void Append(string command)
    {
        // Atuin manages its own database via the atuin binary.
        // ps-bash does NOT write to the database directly.
        // Users should run 'atuin import' or rely on the Atuin CLI.
    }
}
```

**Usage**:

```bash
# Install the plugin DLL
mkdir -p ~/.psbash/plugins
cp AtuinHistory.dll ~/.psbash/plugins/

# Restart ps-bash
ps-bash

# Up-arrow now queries Atuin history
# Ctrl-R will search the Atuin database
```

**Disable built-in history** (recommended for Atuin):

```csharp
// In future shell config (~/.psbash/config.json or similar):
{
  "disableBuiltInHistory": true
}
```

### 6.2 Git Branch Completion Plugin

This plugin provides git branch name completion after `git checkout` or `git switch`.

**DLL**: `GitCompletion.dll`

**Implementation**:

```csharp
using System.Diagnostics;

namespace PsBash.Plugins;

public sealed class GitCompletionProvider : ICompletionProvider
{
    public IReadOnlyList<string> Complete(string line, int cursor, CompletionContext context)
    {
        // Only complete after "git checkout ", "git switch ", or "git co "
        var beforeCursor = line[..cursor];
        if (!IsGitCheckoutCommand(beforeCursor))
            return Array.Empty<string>();

        // Run git to get branches
        try
        {
            var psi = new ProcessStartInfo("git", "branch --format='%(refname:short)'")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = context.WorkingDirectory,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return Array.Empty<string>();

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
                return Array.Empty<string>();

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim('\''))
                .Where(b => b.Length > 0)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsGitCheckoutCommand(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.StartsWith("git checkout ", StringComparison.Ordinal)
            || trimmed.StartsWith("git switch ", StringComparison.Ordinal)
            || trimmed.StartsWith("git co ", StringComparison.Ordinal);
    }
}
```

---

## 7. Implementation Roadmap

This plugin architecture is **not yet implemented**. This document serves as the design specification.

### 7.1 Phase 1: Core Interfaces and Loader

1. Create `IHistoryStore` and `ICompletionProvider` interfaces
2. Create `ShellConfig` class
3. Implement `PluginLoader` with `AssemblyLoadContext`
4. Wire `ShellConfig` into `InteractiveShell.RunAsync`

### 7.2 Phase 2: Built-In Implementations

1. Refactor `LineEditor` to use `IHistoryStore` instead of direct file I/O
2. Refactor `TabCompleter` to implement `ICompletionProvider`
3. Ensure built-in implementations work without changes

### 7.3 Phase 3: Example Plugins

1. Implement `FileHistoryStore` (current `LineEditor` behavior)
2. Implement `AtuinHistoryStore` as a proof-of-concept
3. Implement `GitCompletionProvider` as a proof-of-concept

### 7.4 Phase 4: Configuration File

1. Define `~/.psbash/config.json` schema
2. Add `DisableBuiltInHistory` and `DisableBuiltInCompletion` flags
3. Allow per-plugin configuration (e.g., Atuin database path)

---

## 8. Security Considerations

- **Plugin DLLs run with the same permissions** as the ps-bash process
- Users should only install plugins from trusted sources
- Consider code signing or hash verification in the future
- Plugin errors are caught and logged, but malicious plugins can still:
  - Read all user data
  - Execute arbitrary code
  - Modify the filesystem

**Recommendation**: Treat `~/.psbash/plugins/` like `PATH` — only add DLLs you trust.

---

## 9. Future Enhancements

| Feature | Description | Priority |
|---------|-------------|----------|
| Plugin reload | Reload plugins without restarting the shell | Low |
| Dependency injection | Allow plugins to request services via constructor injection | Low |
| Plugin marketplace | Discover and install plugins via a `ps-bash plugin install` command | Medium |
| Sandboxing | Run plugins in a restricted sandbox | Low |
| Async I/O | Support async history queries for remote stores | Low |
| Completion context | Pass more context to plugins (git status, kubernetes context) | Medium |
