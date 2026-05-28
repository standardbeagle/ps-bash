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
