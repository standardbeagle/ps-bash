# Architecture Overview

This document provides a high-level overview of the ps-bash interactive shell architecture, including component relationships and data flow from user keystrokes to command execution and rendering.

---

## 1. Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────────────────┐
│                                  ps-bash Interactive Shell                          │
├─────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                     │
│  ┌─────────────────┐      ┌───────────────────────────────────────────────────┐   │
│  │ InteractiveShell│◄─────┤          LineEditor (VT100)                       │   │
│  │                 │      │  ┌─────────────────────────────────────────┐     │   │
│  │ - RunAsync()    │      │  │  ReadLoop(prompt)                        │     │   │
│  │ - BuildPrompt() │      │  │    ├─ Tab   → ICompletionProvider[]      │     │   │
│  │ - Aliases       │      │  │    ├─ Up/Down → IHistoryStore            │     │   │
│  │ - IsIncomplete()│      │  │    ├─ Right  → Suggester (autosuggest)   │     │   │
│  └────────┬────────┘      │  │    └─ Ctrl+R → CtrlRUI (planned)        │     │   │
│           │               │  └─────────────────────────────────────────┘     │   │
│           │               │                                                   │   │
│           │               │  History: file-based, upgrades to SQLite (P2)    │   │
│           │               └───────────────────────────────────────────────────┘   │
│           │                                                                           │
│           │                                                                           │
│           ▼                                                                           │
│  ┌─────────────────────────────────────────────────────────────────────────────┐   │
│  │                        BashTranspiler                                         │   │
│  │  ┌──────────────┐    ┌─────────────┐    ┌──────────────────────────────┐   │   │
│  │  │ BashLexer    │───▶│ BashParser  │───▶│ PsEmitter                    │   │   │
│  │  │ (tokenizer)  │    │ (AST build) │    │ (→ PowerShell)                │   │   │
│  │  └──────────────┘    └─────────────┘    └──────────────────────────────┘   │   │
│  └────────────────────────────┬──────────────────────────────────────────────┘   │
│                               │ PowerShell script                                 │
│                               ▼                                                   │
│  ┌─────────────────────────────────────────────────────────────────────────────┐   │
│  │                           PwshWorker                                         │   │
│  │  ┌──────────────────────────────────────────────────────────────────────┐  │   │
│  │  │  pwsh process (stdin/stdout IPC)                                       │  │   │
│  │  │                                                                        │  │   │
│  │  │  Embedded: PsBash.psm1 runtime module                                  │  │   │
│  │  │    ├── 76 Invoke-Bash* functions                                      │  │   │
│  │  │    ├── BashObject model (BashText pipeline)                           │  │   │
│  │  │    └── $script:BashFlagSpecs (completion data)                        │  │   │
│  │  └──────────────────────────────────────────────────────────────────────┘  │   │
│  └─────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                     │
│  ┌─────────────────────────────────────────────────────────────────────────────┐   │
│  │                        Plugin System (P7)                                    │   │
│  │                                                                              │   │
│  │  PluginLoader discovers DLLs in ~/.psbash/plugins/                          │   │
│  │                                                                              │   │
│  │  ┌─────────────────────┐    ┌──────────────────────────────────────────┐   │   │
│  │  │ IHistoryStore       │    │ ICompletionProvider                      │   │   │
│  │  │ ├── SqliteHistory   │    │ ├── TabCompleter (built-in)              │   │   │
│  │  │ ├── AtuinHistory    │    │ ├── FlagSpecCompletionProvider           │   │   │
│  │  │ └── FileHistory     │    │ ├── CommandCompletionProvider           │   │   │
│  │  └─────────────────────┘    │ ├── PathCompletionProvider              │   │   │
│  │                             │ ├── GitCompletionProvider (example)      │   │   │
│  │                             │ └── DockerCompletionProvider (example)  │   │   │
│  │                             └──────────────────────────────────────────┘   │   │
│  └─────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                     │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Component Responsibilities

| Component | Responsibility | Key Methods |
|-----------|---------------|-------------|
| **InteractiveShell** | Main REPL loop, prompt building, alias management, multiline detection | `RunAsync()`, `BuildPrompt()`, `IsIncomplete()`, `ProcessAliasCommand()`, `ExpandAliases()` |
| **LineEditor** | VT100 line editing, emacs keybindings, history navigation, tab completion UI | `ReadLine()`, `HandleTab()`, `HistoryPrev()`, `HistoryNext()`, `Redraw()` |
| **TabCompleter** | Computes completions for commands, files, paths | `CompleteCommand()`, `CompletePath()`, `SplitAtWordBoundary()` |
| **IHistoryStore** | History storage abstraction (file or SQLite) | `RecordAsync()`, `SearchAsync()`, `GetSequenceSuggestionsAsync()` |
| **ICompletionProvider** | Tab completion providers (pluggable) | `GetCompletionsAsync(CompletionContext)` |
| **Suggester** | Fish-style autosuggestions from history | `Suggestion(prefix, cwd)` |
| **CtrlRUI** | Full-screen reverse incremental search (planned) | `SearchLoop(query)` |
| **BashLexer** | Tokenizes bash input into `BashToken[]` | `Tokenize()` |
| **BashParser** | Builds AST from tokens (Oils-style) | `Parse()` → `Command` |
| **PsEmitter** | Transpiles AST to PowerShell script | `Emit(Command)` → string |
| **PwshWorker** | Manages pwsh process, executes transpiled scripts, IPC | `ExecuteAsync()`, `QueryAsync()` |
| **PluginLoader** | Discovers and loads plugin DLLs | `LoadPlugins(config, pluginDir)` |
| **ShellConfig** | Configuration for plugins and features | `HistoryStores`, `CompletionProviders`, flags |

---

## 3. Data Flow: Keystroke to Rendering

### 3.1 Tab Key (Completion)

```
User presses Tab
    │
    ▼
LineEditor.HandleTab()
    │
    ├──► Query all ICompletionProvider.GetCompletionsAsync(context)
    │     ├── CommandCompletionProvider → $PATH executables, aliases
    │     ├── FlagSpecCompletionProvider → flags from BashFlagSpecs
    │     ├── PathCompletionProvider → files/directories
    │     ├── HistoryCompletionProvider → IHistoryStore entries
    │     └── SequenceCompletionProvider → command pairs
    │
    ▼
Merge and de-duplicate results (by CompletionKind priority)
    │
    ├──► If 0 results: beep, no change
    ├──► If 1 result: insert completion
    └──► If N results: show list, insert first, cycle on repeat Tab
    │
    ▼
LineEditor.Redraw() → VT100 output with completion applied
```

### 3.2 Up/Down Arrows (History)

```
User presses Up/Down
    │
    ▼
LineEditor.HistoryPrev() / HistoryNext()
    │
    ├──► If file-based: read _history list
    └──► If SQLite (P2): IHistoryStore.SearchAsync(query)
    │       ├── Filter by CWD (cwd_filter = true)
    │       ├── Order by timestamp DESC
    │       └── Return matching HistoryEntry[]
    │
    ▼
Replace buffer with history entry
    │
    ▼
LineEditor.Redraw() → VT100 output with history line
```

### 3.3 Right/End Key (Autosuggestion - P4)

```
User presses Right/End
    │
    ▼
LineEditor checks if suggestion exists
    │
    ├──► If yes: append suggestion text to buffer
    └──► If no: move cursor (normal readline behavior)
    │
    ▼
LineEditor.Redraw() → VT100 output with suggestion accepted
```

### 3.4 Typing (Autosuggestion Update)

```
User types character
    │
    ▼
LineEditor.InsertChar()
    │
    └──► If autosuggestions enabled:
         Suggester.Suggestion(prefix, cwd)
         │
         ├──► IHistoryStore.SearchAsync(prefix, cwd, limit: 1)
         │    ├── Try CWD-filtered query first
         │    ├── Fall back to global query if no CWD matches
         │    └── Return single best match or null
         │
         ▼
    Render gray text after cursor (ANSI dim: ESC[2m...ESC[0m)
```

### 3.5 Enter Key (Command Execution)

```
User presses Enter
    │
    ▼
LineEditor.ReadLine() returns input string
    │
    ▼
InteractiveShell.RunAsync()
    │
    ├──► ExpandAliases(input)
    ├──► BashTranspiler.Transpile(input)
    │         ├── BashLexer.Tokenize()
    │         ├── BashParser.Parse() → AST
    │         └── PsEmitter.Emit(AST) → PowerShell script
    │
    ▼
PwshWorker.ExecuteAsync(powershellScript)
    │
    ├──► Write to pwsh stdin
    ├──► Read output from pwsh stdout
    └──► Return exit code
    │
    ▼
IHistoryStore.RecordAsync(new HistoryEntry { ... })
    │
    └──► SQLite INSERT or file append
```

---

## 4. Data Flow: Startup

```
ps-bash process starts
    │
    ▼
InteractiveShell.RunAsync()
    │
    ├──► PwshWorker.StartAsync()
    │     ├── Extract embedded PsBash.psm1 to temp
    │     ├── Spawn pwsh process with worker script
    │     └── Establish stdin/stdout IPC
    │
    ├──► SourceRcFileAsync() → ~/.psbashrc transpiled and executed
    │
    ├──► PluginLoader.LoadPlugins(config)
    │     ├── Scan ~/.psbash/plugins/*.dll
    │     ├── Load types implementing IHistoryStore
    │     └── Load types implementing ICompletionProvider
    │
    └──► Enter REPL loop:
         BuildPrompt() → LineEditor.ReadLine() → Transpile → Execute → Repeat
```

---

## 5. Integration Points

### 5.1 AOT Shell ↔ PowerShell Runtime

The AOT shell (C#) and the PowerShell runtime (PsBash.psm1) communicate through:

| Interface | Direction | Purpose |
|-----------|-----------|---------|
| **Transpiled script** | AOT → pwsh | Bash commands converted to PowerShell |
| **stdout/stderr** | pwsh → AOT | Command output |
| **Query response** | pwsh → AOT | Single-value queries (e.g., working directory) |
| **FlagSpecs** | pwsh → AOT (via build) | Tab completion flag definitions |

### 5.2 Plugin Interfaces

Plugins extend the shell without modifying core code:

- **IHistoryStore**: Replace/augment command history (SQLite, Atuin, file)
- **ICompletionProvider**: Add custom tab completions (Git, Docker, kubectl)

Both interfaces are async to support remote backends without blocking the UI.

### 5.3 History ↔ Autosuggestions ↔ Completions

Three features share the same data source:

```
┌─────────────────────────────────────────────────────────────┐
│                    IHistoryStore                            │
│  ┌─────────────────────────────────────────────────────┐   │
│  | Table: history (command, timestamp, cwd, ...)       |   │
│  | Table: sequences (prev_command, next_command, count)|   │
│  └─────────────────────────────────────────────────────┘   │
└────────┬────────────────────────────────────────────────────┘
         │
         ├──► HistoryStore.SearchAsync() ──► Up/Down navigation
         ├──► Suggester.Suggestion() ────────► Inline autosuggestions
         ├──► HistoryCompletionProvider ─────► Tab completion
         └── SequenceCompletionProvider ──────► Post-command suggestions
```

---

## 6. Data Models

### 6.1 Command Flow

```
Bash input (string)
    │
    ▼
BashToken[] (lexer)
    │
    ▼
Command AST (parser)
    │
    ▼
PowerShell script (emitter)
    │
    ▼
Execution output (pwsh)
```

### 6.2 Completion Item

```
CompletionItem
├── Text: string           // What gets inserted
├── Description: string?   // Help text (optional)
└── Kind: CompletionKind   // Command | Flag | Variable | History | File | Directory
```

### 6.3 History Entry

```
HistoryEntry
├── Command: string         // The command line
├── Timestamp: DateTime     // When executed
├── Cwd: string            // Working directory
├── ExitCode: int?         // 0 = success
├── DurationMs: long?      // Execution time
└── SessionId: string      // Shell session UUID
```

---

## 7. File Organization

```
src/PsBash.Shell/
├── InteractiveShell.cs      # Main REPL loop
├── LineEditor.cs            # VT100 line editor
├── TabCompleter.cs          # Tab completion logic
├── FlagSpecs.cs             # Embedded flag definitions
└── Plugins/ (future)
    ├── IHistoryStore.cs
    ├── ICompletionProvider.cs
    └── PluginLoader.cs

src/PsBash.Core/Parser/
├── BashLexer.cs             # Tokenization
├── BashParser.cs            # AST construction
├── PsEmitter.cs             # Bash → PowerShell
└── Ast/
    ├── Commands.cs          # AST node types
    ├── Words.cs
    └── Redirects.cs

src/PsBash.Core/Transpiler/
└── BashTranspiler.cs        # Public API: Parse/Transpile

src/PsBash.Core/Runtime/
├── PwshWorker.cs            # pwsh process management
└── ModuleExtractor.cs       # Embedded resource extraction

src/PsBash.Module/
└── PsBash.psm1              # PowerShell runtime module
```

---

## 8. References

For deeper detail on specific components, see:

- **Parser/Emitter**: `parser-grammar.md`, `emitter-strategy.md`
- **Runtime**: `runtime-functions.md`
- **History**: `sqlite-history-schema.md`, `history-store-interface.md`
- **Completion**: `completion-providers.md`, `flagspec-extraction.md`
- **Suggestions**: `autosuggestions.md`
- **Keybindings**: `keybindings.md`
- **Plugins**: `plugin-architecture.md`
- **Phases**: `shell-implementation-phases.md`
- **Design Decisions**: `design-decisions.md`
