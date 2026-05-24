# Interactive Completion Specification

How the ps-bash interactive shell produces Tab completions and inline suggestions.
This is the map: what each file owns and how a keystroke becomes a candidate list.

Source files (all under `src/PsBash.Host/Shell/`, namespace `PsBash.Host.Shell`):

| File | Owns |
|------|------|
| `LineEditor.cs` | VT100 line editor: keybindings, redraw, history nav, and the **Tab** handler. Calls the completer (async, bounded by a deadline). |
| `CompletionEngine.cs` | The completion orchestrator. Composes the static base set with live, runspace-backed providers; the single place new completion sources are added. |
| `TabCompleter.cs` | The static/local base providers: command list, flag specs, path, history-sequence, plus the line-tokenizing helpers (`SplitAtWordBoundaryQuoteAware`, `IsFirstWord`, `GetCommandNameAtCursor`). |
| `BashCompletionRegistry.cs` | Bash programmable completion (`complete`/`compgen`). Parses `complete -W`/`-r` lines and holds the cmd→word-list registry the engine consults (P5, §7). |
| `FlagSpecs.cs` | Loads bash-command flag metadata from the embedded `Resources/FlagSpecs.json`. |
| `Suggester.cs` | Inline (greyed) autosuggestion from history. |
| `CtrlRSearch.cs` | Reverse-i-search (Ctrl-R) overlay. |
| `IHistoryStore` (+ `SqliteHistoryStore`) | History persistence the suggester / sequence completion query. |
| `Runtime/ICompletionWorker.cs` | Worker capability: run PowerShell's own `CommandCompletion.CompleteInput` against the live runspace (implemented by `SdkWorker`). |

---

## 1. Data flow

```
key = Tab
  └─ LineEditor.HandleTabAsync()                 (200ms CancellationToken)
       └─ CompletionEngine.CompleteAsync(line, cursor, ct)
            ├─ TabCompleter.Complete(...)         static base set (always)
            ├─ [arg + complete spec] word list    ─ BashCompletionRegistry (local; pre-worker)
            ├─ [command position] live command names  ─ worker Get-Command '<prefix>*'
            ├─ [PS cmdlet + "-tok"] parameter names   ─ worker Get-Command.Parameters
            └─ [PS cmdlet + value ] parameter values  ─ ICompletionWorker.CompleteInputAsync
                                                          (Register-ArgumentCompleter / ValidateSet
                                                           / enum / provider paths), else
                                                          ValidateSet/enum introspection
```

Every runspace round-trip is bounded by the caller's `CancellationToken`. On timeout,
cancellation, or any failure the engine returns the static base set — **Tab never hangs**.

## 2. Completion context

`CompletionEngine.CompleteAsync` classifies the token under the cursor (using
`TabCompleter` helpers) into one of:

| Context | Detected by | Source |
|---------|-------------|--------|
| Command position | `TabCompleter.IsFirstWord` | base command list ∪ live `Get-Command` |
| Argument of a `complete`-registered command | `BashCompletionRegistry.HasSpec(cmd)` | the command's `-W` word list (§7) — checked first, local |
| Parameter name (PS cmdlet, token starts `-`) | resolved command not a bash command (`!IsBashCommand`) | `Get-Command.Parameters` |
| Parameter value (PS cmdlet, after `-Flag`) | `PreviousParamFlag` | `CompleteInput` → ValidateSet/enum fallback |
| Flag (bash command) | `FlagSpecs.GetFlags(cmd) != null` | `FlagSpecs.json` |
| Path / redirect target | default | filesystem |

The command under the cursor is resolved from the text **before** the token
(`GetCommandNameAtCursor` walks back skipping flags; on a value token it would otherwise
return the partial value).

## 3. The PowerShell bridge (no cursor mapping)

The user types bash; PowerShell's engine wants PowerShell + a PS cursor offset. Rather than
map a bash cursor into transpiled PS, the engine **avoids mapping**:

- Command names and parameter names use **introspection** (`Get-Command` / `.Parameters`) —
  no `CompleteInput` needed.
- Parameter values use a **synthesized fragment** the engine fully controls —
  `"<cmd> <-Param> <partial>"` with the caret pinned at the end — so the cursor is trivially
  the string length. This is the only path that calls `CommandCompletion.CompleteInput`, and
  it is where `Register-ArgumentCompleter` is honored.

## 4. Phases

| Phase | Status | Adds |
|-------|--------|------|
| P0 async/cancellable engine seam | done | `CompletionEngine`, 200ms Tab deadline |
| P1 live command names | done | runspace `Get-Command` merge |
| P2 PS parameter understanding | done | parameter names + ValidateSet/enum values (introspection) |
| P3 dynamic values via CompleteInput | done | `Register-ArgumentCompleter`, provider paths |
| P4 unify flag specs | done | one canonical source for bash flags (§6) |
| P5 bash `complete`/`compgen` | done (Tier 1) | static `-W` word lists (§7); `-F` Tier 2 deferred |

Tests: `PsBash.Host.Tests/Shell/CompletionEngineTests.cs` (engine logic, fake worker),
`PsBash.Host.Tests/Shell/BashCompletionTests.cs` (P5 registry + engine integration),
`PsBash.Host.Tests/Runtime/SdkWorkerTests.cs::CompleteInput_HonorsRegisterArgumentCompleter`
(live runspace), `PsBash.Shell.Tests` `Complete_*` (TabCompleter base set).

## 5. Adding a completion source

Add the provider logic in `CompletionEngine` (introspection / worker query) or a static
provider in `TabCompleter`, classify the context, and merge with `Merge` (append, preserve
base order) or `MergeFirst` (new source wins). Never throw — completion is advisory; bound any
runspace call by the passed `CancellationToken`.

## 6. Flag-spec source (one, enforced)

Bash-command flag metadata has **one** canonical source: `src/PsBash.Module/BashFlagSpecs.json`.

- The host embeds it (resource name `PsBash.Host.Resources.FlagSpecs.json`) and `FlagSpecs.cs`
  reads it for the interactive shell.
- The psm1 loads the same file from the module dir for `Register-BashCompletions` (module-mode
  PowerShell argument completers, `Import-Module PsBash` in a plain pwsh).

The old second copy (`src/PsBash.Host/Resources/FlagSpecs.json` as a hand-maintained file) is
gone. `FindabilityGuardTests.FlagSpecs_HaveExactlyOneSource` fails the build if a second source
reappears — do not reintroduce one (P4).

## 7. Programmable completion: `complete` / `compgen` (P5)

`BashCompletionRegistry` implements bash programmable completion, **Tier 1** (static word lists):

- **Register** — typing `complete -W '<words>' NAME...` at the prompt is intercepted by
  `InteractiveShell` (like `alias`; there is no `complete` cmdlet to transpile to) and stored as
  `NAME → word list`. `complete -r [NAME...]` removes (all when no NAME); bare `complete` / `-p`
  prints the specs.
- **Consume** — when completing an **argument** of a registered command, `CompletionEngine`
  consults the registry **before** any runspace round-trip (it is local state) and offers the word
  list filtered by the typed prefix (case-sensitive, matching bash), merged ahead of the base set.
  At the **command** position the registry is not consulted (that is command-name completion).

Example: `complete -W 'start stop restart' svc` then `svc st`⇥ → `start`, `stop`.

**Tier 2 (deferred):** function-based completion (`complete -F func`, with `COMP_WORDS` /
`COMP_CWORD` / `COMPREPLY`) is **not** implemented. A `-F` spec registers (so the line is consumed,
not transpiled to a missing command) but contributes no candidates. Standalone `compgen` output and
registering `complete` from a sourced rc file (the intercept is prompt-side only) are likewise out
of scope. Tier 1 is the 80/20: `complete -W` covers the common "fixed sub-command/keyword list" case.

Tests: `PsBash.Host.Tests/Shell/BashCompletionTests.cs` — registry parser (quoting, `-r`, `-F`
no-candidates, multi-name) and engine integration (argument vs command position, worker-independent).
