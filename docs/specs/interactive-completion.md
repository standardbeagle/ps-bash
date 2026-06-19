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
| `Suggester.cs` | Inline (greyed) autosuggestion from history. Rendered gray (SGR 90), not faint (SGR 2) — faint is invisible on many terminals. |
| `CompletionItem.cs` | A candidate's `InsertText` (typed into the buffer) vs `DisplayText` (shown in lists). Keeping them apart is what stops a flag's description from being inserted. |
| `CtrlRSearch.cs` | Reverse-i-search (Ctrl-R) overlay. |
| `FlagHint.cs` | One unified panel/man-page row: `Insert` (typed), `Head`/`Desc` (shown), optional `Detail`/`Examples` (man-page). Spans bash flag specs + live PS params. |
| `FlagHelpBrowser.cs` | Scrollable alt-screen man-page browser for a command's flags (P4, §8). Opened by F1 / →. |
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
| `grep` regex pattern value (token after `-e`/`--regexp`) | `TabCompleter.TryGetGrepPatternValueContext` | basic / extended / fixed regex snippet sets (§9), local, no runspace |
| Parameter name (PS cmdlet, token starts `-`) | resolved command not a bash command (`!IsBashCommand`) | `Get-Command.Parameters` |
| Parameter value (PS cmdlet, after `-Flag`) | `PreviousParamFlag` | `CompleteInput` → ValidateSet/enum fallback, labeled with source + type (§9) |
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

## 8. Floating flag-doc panel (type-ahead, no Tab)

As the user types a flag token for a known bash command (`find -n`), `LineEditor` shows a **dim
panel below the prompt** listing the matching flags + descriptions — IDE-style parameter help, no
Tab required. It updates every keystroke and vanishes on space / Enter / a non-flag token.

- **Data** — `TabCompleter.MatchingFlagSpecs(line, cursor, aliases)`: pure/synchronous (no
  runspace), returns the `FlagSpec`s whose flag starts with the cursor's `-`-prefixed token for the
  command at the cursor (a lone `-` matches all; redirect targets and the command word are excluded;
  aliases are resolved). Source is the single `BashFlagSpecs.json` (§6).
- **Render** — `LineEditor.ComputeFlagPanel` formats the rows; `Redraw` draws them below the input
  line and returns the cursor with **relative** moves (`\x1b[{N}A`), which stay correct even if the
  panel scrolls the screen. The whole region is erased each redraw with `\r\x1b[0J` (also on Tab /
  Enter / Ctrl-C so the panel never lingers). `ComputeFlagPanel` never throws — the panel is advisory.
- **PowerShell cmdlet parameters** — for a non-bash command the panel ALSO shows the cmdlet's
  parameters with their type and any ValidateSet / enum value-set (`tnc -C` → `-CommonTCPPort
  <String>   HTTP, RDP, SMB, WINRM`). This needs a runspace round-trip, so it is async:
  `CompletionEngine.GetFlagHintsAsync` (returns `FlagHint`s) is wired into `LineEditor` as
  `_flagHintProvider`. `UpdatePsFlagHintsAsync` fetches on keystroke (bounded by the 200ms Tab
  deadline — typing never blocks) and caches the result keyed by `commandtoken`; `ComputeFlagPanel`
  renders the bash specs synchronously (always fresh) and falls back to the cached PS hints only
  while their key still matches the cursor. Bash takes precedence when both could apply.
- **Focus & scrolling (P3)** — with the panel visible, **↓ moves focus into it**: `↑↓`/`PgUp`/`PgDn`
  scroll a highlighted selection, **Enter** inserts the selected flag (the bare `Insert` text, never
  the arg/desc), **Esc** (or ↑ past the top) returns to typing. Unfocused, the panel shows the first
  `MaxPanelRows` with a "press ↓ to scroll" overflow hint; focused, it renders a scroll window with a
  `[i/N]` position line. The scroll-window math is the pure, unit-tested `LineEditor.ComputeScroll`;
  selection/scroll reset each prompt (`ExitPanelFocus`). `Right`/Enter→man-page is P4.
- **Man-page drill-down (P4)** — a scrollable alt-screen browser (`FlagHelpBrowser`, modeled on
  `CtrlRSearch`): ↑↓ switches option, PgUp/PgDn (or j/k) scrolls that option's man-page detail
  (head, description, the `detail` paragraph wrapped, and `examples` for bash; type + value-set for
  PS), Enter inserts the flag, Esc/q closes. Opened **both** ways: **F1** at the prompt (for the
  command under the cursor — the matching flags, or the command's full bash flag set when not on a
  flag token, via `TabCompleter.AllFlagSpecsForCommand`) and **→** on a focused inline-panel row.
  The format helpers (`DetailLines`, `WrapText`) are pure/unit-tested; the key loop has a `Simulate`
  seam. Tests: `PsBash.Shell.Tests/LineEditorTests.cs` `FlagHelpBrowserTests`.
- **Command position (first word)** — the panel is the first-word counterpart to the flag panel:
  as the user types a command prefix, `TabCompleter.MatchingCommandNames(line, cursor, aliases)`
  (pure/synchronous — aliases first, then the static bash builtin/`$PATH` snapshot, then the live
  PowerShell command snapshot, prefix-filtered) feeds `CurrentFlagHints`, which renders one row per
  matching command. An alias row shows `→ {expansion}`; a PowerShell command shows its kind
  (`PowerShell cmdlet` / `function` / `alias` / …, from the cache's `DescribeKind`). PowerShell
  prefixes match case-insensitively (`get-c…` → `Get-ChildItem`), matching PS command resolution;
  bash names stay case-sensitive. It is suppressed for empty / flag (`-…`) / path-like (`./x`, `/x`,
  `~/x`) tokens, so the path and flag providers still own those. Focus/scroll/Enter-insert reuse the
  flag-panel machinery unchanged (`InsertFlagAtToken` replaces the partial command word). Tests:
  `LineEditorTests.cs` `MatchingCommandNames_*`.
  - **PowerShell command snapshot (dynamic, background-loaded)** — `CommandNameCache` holds the set
    of command names resolvable in the live runspace (cmdlets / functions / filters / aliases from
    every loaded module, **plus anything the session has defined** — a `function foo {}` or
    `Set-Alias` the user just ran). It is **preloaded in the background** once the worker is ready
    (after rc sourcing) and **refreshed after each executed command** that may have added a
    function/alias/module, and after a `source`. The panel reads `CommandNameCache.Names` /
    `DescribeKind` **synchronously** (no runspace round-trip, no keystroke-budget cost). `RefreshAsync`
    is single-flighted and coalesces a burst of triggers into one trailing query. During the brief
    warmup before the first snapshot lands, `MatchingCommandNames` falls back to the curated static
    `TabCompleter.KnownPowerShellCommands` set (label via `IsKnownPowerShellCommand`). This replaces
    the old "panel is deliberately runspace-free / no PS commands" rule — the panel is still
    keystroke-synchronous, but now reads a background-maintained live snapshot. The Tab Phase-1
    `Get-Command` merge (§1) remains the on-demand, fully-live path.
- **Scope** — command names (sync, first word) + bash flag specs (sync) + PS-cmdlet params (async).
  `complete -W` lists are not shown in the panel (still Tab-driven). PS man-page detail is
  type/value-set only (no `Get-Help` prose yet).

Tests: `PsBash.Shell.Tests/LineEditorTests.cs` `MatchingFlagSpecs_*` (bash panel data) and
`PsBash.Host.Tests/Shell/CompletionEngineTests.cs` `FlagHints_*` (PS-param hints via fake worker);
the ANSI rendering / caching itself is not unit-tested.

## 9. Value providers (argument values, not names)

Two providers complete the **value** of an argument (vs the command/parameter name). Both
emit `CompletionItem.Labeled(insert, display)`, keeping the inserted text free of the
description (the §invariant in `.claude/rules/completion.md`).

### 9.1 `grep` regex pattern snippets (local, `TabCompleter`)

When the cursor's token is the value **after `-e`/`--regexp`** for a `grep` command,
`TryGetGrepPatternValueContext` classifies the regex dialect from the other flags on the
line and `TryCompleteGrepPatternValue` offers a small curated snippet set:

| Dialect | Trigger flag | Snippet set |
|---------|--------------|-------------|
| basic (BRE) | default | `GrepBasicRegexSnippets` (`'^TODO'`, `'[0-9][0-9]*'`, …) |
| extended (ERE) | `-E`/`--extended-regexp` | `GrepExtendedRegexSnippets` (`'TODO\|FIXME'`, …) |
| fixed (literal) | `-F`/`--fixed-strings` | `GrepFixedPatternSnippets` |

This is **local** (no runspace) and consulted before the worker. It is deliberately scoped
to the post-`-e` token only — the ambiguous positional `grep <pattern> <file>` operand is
*not* completed (can't tell a pattern from a path). Candidates are filtered by
`InsertText.StartsWith(token, OrdinalIgnoreCase)` (case-insensitive). Tests: `PsBash.Shell.Tests/LineEditorTests.cs`
`MatchingFlagSpecs_*` / grep-snippet cases.

### 9.2 PowerShell parameter-value detail labels (`CompletionEngine`)

For a PS cmdlet parameter value, after the `CompleteInput` path returns nothing,
`QueryParameterValueItemsAsync` introspects the parameter's `[ValidateSet]` values (else
enum names for an enum-typed parameter) and `BuildParameterValueItems` renders each as
`CompletionItem.Labeled(value, "value  - {ValidateSet|Enum} value for -Param <Type>")` —
the bare value is inserted, the source + type show only in the list.

The worker expression emits one row per value as `value<sep>source<sep>type`, joined by the
**ASCII unit separator U+001F**, not `|`. This is a deliberate collision fix: a ValidateSet
value (or type name) that itself contains `|` would be truncated/mis-split by the row parser.
`BuildParameterValueItems` splits on the `ParameterValueFieldSeparator` const (`'\u001f'`).
**Do not revert to `|`.** Tests: `CompletionEngineTests.cs`
`ParameterValue_FallbackDisplaysValidateSetDetail*`, `BuildParameterValueItems_*` (incl.
`_PreservesValueContainingPipe`).
