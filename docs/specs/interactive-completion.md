# Interactive Completion Specification

How the ps-bash interactive shell produces Tab completions and inline suggestions.
This is the map: what each file owns and how a keystroke becomes a candidate list.

Source files (all under `src/PsBash.Host/Shell/`, namespace `PsBash.Host.Shell`):

| File | Owns |
|------|------|
| `LineEditor.cs` | VT100 line editor: keybindings, redraw, history nav, and the **Tab** handler. Calls the completer (async, bounded by a deadline). |
| `CompletionEngine.cs` | The completion orchestrator. Composes the static base set with live, runspace-backed providers; the single place new completion sources are added. |
| `TabCompleter.cs` | The static/local base providers: command list, flag specs, path, history-sequence, plus the line-tokenizing helpers (`SplitAtWordBoundaryQuoteAware`, `IsFirstWord`, `GetCommandNameAtCursor`). |
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
| P4 unify flag specs | planned | one source of truth for bash flags (see §6) |
| P5 bash `complete`/`compgen` | planned | programmable completion |

Tests: `PsBash.Host.Tests/Shell/CompletionEngineTests.cs` (engine logic, fake worker),
`PsBash.Host.Tests/Runtime/SdkWorkerTests.cs::CompleteInput_HonorsRegisterArgumentCompleter`
(live runspace), `PsBash.Shell.Tests` `Complete_*` (TabCompleter base set).

## 5. Adding a completion source

Add the provider logic in `CompletionEngine` (introspection / worker query) or a static
provider in `TabCompleter`, classify the context, and merge with `Merge` (append, preserve
base order) or `MergeFirst` (new source wins). Never throw — completion is advisory; bound any
runspace call by the passed `CancellationToken`.

## 6. Known maintenance hazard: dual flag-spec sources

Bash-command flag metadata is currently maintained in **two** places that drift:

- `src/PsBash.Host/Resources/FlagSpecs.json` — consumed by the interactive shell (`FlagSpecs.cs`).
- `src/PsBash.Module/PsBash.psm1` `$script:BashFlagSpecs` — consumed by `Register-BashCompletions`
  for module-mode PowerShell argument completers (`Import-Module PsBash` in a plain pwsh).

They hold the same data in different shapes (`{"flag","desc"}` vs `@('-n','desc')`) and have
already diverged (e.g. `find -print0/-exec`, `tail -f/-c/-s` exist only in the psm1 copy).
P4 unifies them onto a single canonical JSON consumed by both, with a parity test.
