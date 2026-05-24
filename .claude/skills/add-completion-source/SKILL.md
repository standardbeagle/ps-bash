---
name: add-completion-source
description: Add an interactive-shell Tab completion source (provider) to ps-bash
disable-model-invocation: true
---

文言：分上下文，置provider於CompletionEngine，呼運行時必設限，併以CompletionMerge，測之，更新spec。

Add a completion source for: $ARGUMENTS

Ref: @docs/specs/interactive-completion.md · rule: @.claude/rules/completion.md

## STEPS

1. **Classify context.** Decide WHERE the new candidates apply: command position
   (`TabCompleter.IsFirstWord`), parameter name (`-tok`, PS cmdlet via `GetCommandNameAtCursor`
   + `!IsBashCommand`), parameter value (after `-Flag`, `PreviousParamFlag`), path, or a bash flag.

2. **Add the provider in `CompletionEngine.CompleteAsync`** (`src/PsBash.Host/Shell/`). NOT in
   `LineEditor`. One branch per context. Pure introspection where possible (no cursor mapping).

3. **Bound every runspace call by the passed `CancellationToken`.** Use `_worker.QueryAsync(expr, ct)`
   or `ICompletionWorker.CompleteInputAsync`. Catch all; return empty on cancel/throw. Tab never hangs.

4. **Merge with `CompletionMerge.Append`** (`sortSecondary` true = append+sort; false = source-first).
   Do NOT hand-roll a merge.

5. **Single flag-spec source.** If it's bash-command flags, it comes from
   `PsBash.Module/BashFlagSpecs.json` via `FlagSpecs`. NEVER add a second flag table.

6. **Test** in `src/PsBash.Host.Tests/Shell/CompletionEngineTests.cs` with the `FakeWorker`
   (`IWorker` [+ `ICompletionWorker`]): assert the merge, prefix re-filter, and a deterministic
   cancel/fallback case (no sleeps). For live `CompleteInput`, add an `SdkWorkerTests` case.

7. **Run:** `./scripts/test.sh --filter CompletionEngine`. **Deploy:** `pwsh install-local.ps1`.

8. **Update the spec** (`docs/specs/interactive-completion.md`): add the source to the context table.
