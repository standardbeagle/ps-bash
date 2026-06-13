# Frecency Directory Jump (`z` / `zi`) Specification

A zoxide-style "smart cd" for the interactive shell: visited directories are scored
by **frecency** (frequency + recency) so `z <keyword>` jumps to the directory you
most likely mean, and cd/z completion + ghost text are sourced from the same data.

Interactive-mode only. Non-interactive `ps-bash -c "z foo"` is **not** supported
(a documented v1 gap — there is no `z` cmdlet; the jump is a prompt-side rewrite).

## Components

| Concern | File |
|---|---|
| Store interface + match type | `src/PsBash.Host/Shell/IFrecencyStore.cs` (`IFrecencyStore`, `FrecencyMatch`) |
| SQLite store (scoring, aging, pruning) | `src/PsBash.Host/Shell/SqliteFrecencyStore.cs` |
| `z` / `zi` interception, cd-visit recording, disposal | `src/PsBash.Host/Shell/InteractiveShell.cs` |
| Tab completion (cd/z/zi → dirs) | `src/PsBash.Host/Shell/CompletionEngine.cs` |
| Ghost-text suffix | `src/PsBash.Host/Shell/FrecencySuggester.cs` (+ `LineEditor` wiring) |

Tests: `PsBash.Shell.Tests/{FrecencyStoreTests,ZCommandParsingTests,FrecencySuggesterTests,ZJumpEndToEndTests}.cs`.

## Data model & scoring (zoxide-faithful)

`frecency.db` lives beside `history.db` under `{PSBASH_HOME}/.psbash/`. One table:
`dirs(path PRIMARY KEY, rank REAL, last_access INTEGER epoch-seconds)`, WAL mode.

- **Record:** each visit upserts `rank += 1` (insert at 1) and `last_access = now`.
- **Score:** `rank ×` a recency multiplier — within the hour ×4, the day ×2, the
  week ×0.5, else ×0.25.
- **Aging (self-bounding):** when `SUM(rank) > 9000`, every rank decays ×0.9 and
  rows with `rank < 1` are dropped.
- **Match:** keywords match in order (case-insensitive) within the path, and the
  **last** keyword must hit the final path component. Non-existent directories are
  skipped and pruned on query.

## Recording (shell-side, not transpiler-side)

The frecency DB is host-process state, but `cd` runs in the worker runspace. So
visits are recorded in `InteractiveShell.SyncWorkerCwdAsync` (which already reads
back the post-command cwd): on an actual directory change the new path is recorded.
The write is **awaited** on a change so a `z` issued immediately after a `cd` sees
the visit (otherwise the fire-and-forget write races the next prompt). This captures
every directory change regardless of mechanism (cd, pushd, scripts, z itself).

## `z` / `zi` (prompt-side interception, like `alias` / `complete`)

Recognized in the REPL after alias/complete handling, before the worker gate
(`TryParseZCommand`). Resolution (`ResolveZTargetAsync`) rewrites the line to
`cd '<path>'` and lets it flow through the normal cd path — so OLDPWD, chpwd hooks,
and cwd tracking all apply. The original `z …` line is what gets recorded in history.

- `z` (no args) → `cd ~`.
- `z <existing-path>` (token looks like a path AND resolves) → literal `cd` passthrough.
- `z <keywords>` → highest-frecency match, or `ps-bash: z: no match for '…'` on miss.
- `zi [keywords]` → numbered list of matches, read a selection, cd to it (no fzf dep).

## Completion & ghost text

- **Tab (`CompletionEngine`):** completing a cd/z/zi argument offers the
  highest-frecency directories whose final component matches the token (full paths,
  merged ahead of the base set). Local — runs during warmup. Skipped for literal-path
  tokens (base path completion owns those).
- **Ghost text (`FrecencySuggester`):** append-only, so conservative — empty arg
  (`cd `/`z `) previews the top directory's full path; `z <kw>`/`zi <kw>` complete
  the keyword to the matched basename (z re-resolves it via frecency). `cd <kw>` with
  a non-empty token defers to path/history completion. Tried before the history
  suggester; returns null for every non-jump line so history suggestion is unchanged.
