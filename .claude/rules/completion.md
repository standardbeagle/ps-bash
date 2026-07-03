---
paths:
  - "src/PsBash.Host/Shell/**"
  - "src/PsBash.Host/FlagSpecs.cs"
  - "src/PsBash.Module/BashFlagSpecs.json"
---

# COMPLETION CONVENTIONS. Ref: @docs/specs/interactive-completion.md

文言：補全居CompletionEngine，運行時呼必設限，旗譜唯一源，名以內省、值以CompleteInput，不映游標。

## INVARIANTS. DO NOT BREAK.

- CANDIDATES ARE `CompletionItem(InsertText, DisplayText)`, never bare strings. Apply path inserts `InsertText`; the list shows `DisplayText`. A description/annotation goes in `DisplayText` only (use `CompletionItem.Labeled`) — NEVER glue it onto the inserted text. (The flag-completion bug: `"-name  - name pattern"` got typed into the buffer because candidate = display = insert.)
- ALL completion logic behind `CompletionEngine`. Providers compose there. `LineEditor` stays dumb (calls one async completer).
- EVERY runspace call bounded by the passed `CancellationToken`. Tab MUST NEVER hang. On timeout/cancel/throw → return the static base set. Completion is advisory; never throw.
- ONE flag-spec source: `PsBash.Module/BashFlagSpecs.json`. Host embeds it (resource name `PsBash.Host.Resources.FlagSpecs.json` → `FlagSpecs.cs`); psm1 loads it from the module dir. NEVER reintroduce a second flag-spec table.
- PowerShell bridge avoids bash→PS cursor mapping: command names + parameter NAMES via introspection (`Get-Command.Parameters`, `[ValidateSet]`/enum); parameter VALUES via `ICompletionWorker.CompleteInput` on a synthesized fragment `"<cmd> <-Param> <partial>"` with the caret pinned at end. Honors `Register-ArgumentCompleter`.
- ps-bash-mapped BASH commands keep their flag specs (`FlagSpecs.GetFlags`). Do NOT run PS parameter completion on them (`IsBashCommand` skip).
- Merge results through `CompletionMerge.Append` (the one dedup). No new hand-rolled merge.

## ADDING A SOURCE

Use the `add-completion-source` skill. Classify context, add a provider in `CompletionEngine`, bound any worker call by ct, merge with `CompletionMerge.Append`, test (`CompletionEngineTests`, fake worker), update the spec.

## LINE RENDERING (LineEditor, multi-row + wide chars)
`Redraw` OWNS a multi-row region (prompt + wrapped input + flag panel). Invariants:
- Track `_renderCursorRow`/`_renderRows`; erase by walking UP to the region's first row (`ESC[{n}A`) then `\r\x1b[0J`. NEVER erase only from the cursor's bottom row — that re-renders one row PER keystroke on a wrapped line.
- All column math via `LineEditor.DisplayWidth` (wcwidth: wide CJK/emoji = 2, combining/format/control = 0), NEVER `string.Length`. ZWJ emoji are approximated.
- The pure builder is `ComputeRender` (side-effect-free; test via a terminal-grid sim — `LineEditorRenderTests`). Redraw is a thin wrapper.
- After ANY bare `Console.Write(_prompt)` or a completion-list print, reset geometry (`ResetRenderStateForPrompt`, or set `_renderCursorRow=0`) so the next erase starts on the right row. Alt-screen overlays (Ctrl-R / FlagHelpBrowser) restore the primary buffer, so geometry stays valid across them.
- The reprint-and-advance paths (Tab / Enter / Ctrl-C / command-assist) use `ReprintBareLine` (region-erase first), not a bare `\r\x1b[0J`.
