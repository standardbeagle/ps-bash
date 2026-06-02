# Token-Reduction Filter Engine — Design

**Date:** 2026-06-02
**Status:** Approved design, pre-implementation
**Owner:** Andy Brummer

Extends ps-bash's existing `--compact-output` mode from a generic line-collapser into a
**command-aware output-filter engine** with tokf/rtk-style per-command reductions, plus a
PowerShell-object compact serializer. Config-driven engine + ~15 built-in filters; the rest of
the tokf/rtk surface becomes drop-in JSON, no recompile.

References reviewed:
- [tokf](https://github.com/mpecan/tokf) ([tokf.net](https://tokf.net)) — TOML filter pipeline, 63 built-ins.
- [rtk](https://github.com/rtk-ai/rtk) — CLI proxy, 100+ commands, four strategies (filter/group/truncate/dedup), tee recovery.

---

## 1. Scope & non-goals

**In scope (v1):**
- Command-aware filter engine, pure + unit-testable, riding the existing compact-output opt-in.
- JSON filter specs: embedded built-ins + user/project override dirs, tokf precedence.
- ~15 built-in filters tuned for this repo's stack (git, dotnet, npm/cargo, pytest, ls, grep, docker/kubectl).
- Generic fallbacks (error-extraction / failures-only / head-tail digest) = no regression for unmatched commands.
- Compact PowerShell-object serializer (`Format-Compact`) for tabular/typed output.
- Tee recovery: full output saved on failure.

**Non-goals / deferred:**
- **Combine paired commands** (`git add && git commit && git push` → one confirmation). Needs AST/and-or-list awareness at emit time. **Phase 2.**
- Full 100+ tokf+rtk built-in port. Engine + ~15 now; rest are drop-in JSON.
- New opt-in switch. Whole feature is already gated by `--compact-output` / `--caveman` / `--wenyan` / `PSBASH_COMPACT_OUTPUT`.
- KDL config format (AOT-hostile parsers; JSON chosen — see §2).
- Streaming. Buffer-then-emit tradeoff is unchanged and stays documented; unbounded producers excluded.

---

## 2. Architecture & injection point

Promote the generic `OutputCompactor` into a command-aware filter pipeline. One new seam, at the
exact point compaction already fires (`IpcWorker.EmitCompactedOutput`). Generic compactor becomes
the fallback when no filter matches.

```
frames + command + exitCode
        │
        ▼
  FilterEngine.Apply(command, argv, exitCode, frames)
        │
        ├─ match filter by command name + arg predicates ──► per-command pipeline
        │                                                     (override → matchOutput →
        │                                                      replace → strip/trim →
        │                                                      skip/keep → dedup →
        │                                                      success/failure template)
        └─ no match ──► generic fallback (error-extract / failures-only / head-tail digest)
```

**Code placement** (respects CODE_MAP boundaries):

| File | Role |
|------|------|
| `PsBash.Core/Runtime/Compaction/FilterEngine.cs` | selector + pipeline runner, **pure**, no I/O |
| `PsBash.Core/Runtime/Compaction/FilterSpec.cs` | rule model (match, override, stages, templates) |
| `PsBash.Core/Runtime/Compaction/FilterStage.cs` | skip / keep / replace / dedup / matchOutput / template |
| `PsBash.Core/Runtime/Compaction/OutputCompactor.cs` | today's generic digest, **moved here**, now the fallback stage |
| `PsBash.Core/Runtime/Compaction/FilterLibrary.cs` | I/O: load embedded + user/project JSON, mtime-invalidated, hands specs to the pure engine |
| `PsBash.Cmdlets/FormatCompactCommand.cs` | `Format-Compact` object serializer (§5) |

**Routing key.** `IpcWorker.SendRequestAsync` already has `command = CommandLabel(mode)`, and the
original bash command name is available via `PSBASH_COMPACT_COMMAND` (already used for the digest
header). The engine parses command name + argv from that — no new plumbing.

**Consequences:**
1. Engine stays pure → differential-testable against tokf fixtures (qa-rubric Directive 1).
2. Filtering still buffer-then-emit (no streaming). Same opt-in tradeoff documented today.

---

## 3. Filter format & config

**Format: JSON, source-generated `JsonSerializerContext`.** House rule prefers KDL, but `PsBash.Core`
ships AOT binaries; reflection-based TOML/KDL parsers are AOT-hostile. Project already embeds + parses
JSON (`BashFlagSpecs.json`) AOT-safely. JSON over KDL is the justified exception here (CLAUDE.md:
"strong reason for another format").

**One filter:**
```json
{
  "name": "git/status",
  "match": { "command": "git", "args": ["status"] },
  "override": ["git", "status", "--porcelain=v1", "-b", "--find-renames"],
  "matchOutput": [{ "contains": "nothing to commit", "emit": "clean" }],
  "replace": [{ "pattern": "^\\s+", "with": "" }],
  "stripAnsi": true,
  "trimLines": true,
  "skip": ["^On branch", "^\\s*\\(use "],
  "keep": [],
  "dedup": true,
  "onSuccess": "{{branchLine}}\n{{body}}",
  "onFailure": "{{body}}",
  "tree": { "minSiblings": 3 }
}
```

**Stage order** (tokf-identical, so their fixtures are a valid oracle):
`override → matchOutput (short-circuit) → replace → strip/trim → skip/keep → dedup → success/failure template`.

- `override`: the engine cannot re-run commands itself (it sees output, not exec). `override` is a
  **hint emitted to the emitter/launcher** so the reduced form (`git status --porcelain`) is what
  actually runs. v1 wiring: launcher rewrites argv before transpile when compact mode + a matching
  filter `override` exists. (If override-at-launch proves too invasive, fall back to output-only
  filtering for that command and log the skipped override under `--verbose`.)
- `matchOutput`: whole-output substring check; on hit, emit the named template and short-circuit.
- `replace`: per-line regex, in array order. Regex is timeout-bounded (ReDoS guard, Directive 12).
- `skip`/`keep`: line-level drop / allow-list.
- `dedup`: collapse consecutive duplicates with `... ×N` (existing `CollapseRuns`, reused).
- `onSuccess`/`onFailure`: template by exit code. `{{body}}` = pipeline result; named captures from
  the filter. Templates render output text only — never re-expanded (injection guard).

**Resolution / precedence** (tokf model):
```
.ps-bash/filters/*.json          (project)   ─ highest
~/.config/ps-bash/filters/*.json (user)
<embedded built-ins>                          ─ lowest
```
First match by `name` wins; user file shadows built-in of same name. Loaded once,
`FileShare.ReadWrite` (temp-files rule), mtime-invalidated.

**Escape hatches:**
- `--no-compact-output` disables the whole feature (existing).
- Per-command **exclude list** in config (rtk `exclude_commands`) — "compact everything except X".
- `--no-filter` (within compact mode) → generic digest only, skip named filters.

---

## 4. Built-in filter set (~15)

**Per-command (text, §3a):**

| # | Filter | Reduction |
|---|--------|-----------|
| 1 | `git/status` | override `--porcelain=v1 -b`; 1 line/file; tree-fold ≥3 shared prefix; `[ahead/behind]` |
| 2 | `git/log` | override `--oneline -n20`; empty → cause hint |
| 3 | `git/diff` | override `--stat` unless `-p`/`--patch`/`--name-only`/etc. present |
| 4 | `git/push` | success → `ok ✓ <branch>`; failure → full |
| 5 | `git/commit` | success → `ok <sha7> <subject>` |
| 6 | `git/add` | success → `ok` |
| 7 | `dotnet/build` | errors/warnings grouped by file; drop restore noise; `Build succeeded` → 1 line |
| 8 | `dotnet/test` | failures only + result summary |
| 9 | `npm/test` (pnpm/yarn; jest/vitest) | failures only |
| 10 | `npm/run` + `install` | strip progress; warn/err only |
| 11 | `cargo/test` + `build` + `clippy` | failures / diagnostics only |
| 12 | `pytest` | failures + summary |
| 13 | `grep` / `rg` | group by file, count per file |
| 14 | `docker/ps` + `kubectl get pods` | column-trim table |
| 15 | `ls` | route to §5 compact serializer |

**Generic fallbacks** (tokf `err`/`test`/`summary` — when no named filter matches):
- exit ≠ 0 → **error-extraction**: error/warning/Traceback/panic/`npm ERR!`/`fatal:`/stack lines + context (broadened `IsImportant`).
- test-shaped output detected → **failures-only**.
- else → today's `OutputCompactor` head/tail digest (unchanged → no regression).

**Tee recovery** (rtk): on failure write full unfiltered output to `ps-bash/tee/<ts>_<cmd>.log`,
append `[full output: <path>]`. Agent recovers without re-running. v1.

### 4.1 P2 build notes (what shipped)

15 embedded built-ins landed: git status/log/diff/push/commit/add, dotnet build/test,
npm test/run, cargo test/build, pytest, docker/ps, kubectl/get-pods. `ls` is **P3**
(object serializer, not text). `grep`/`rg` grouping is **deferred** — file-grouping needs
logic beyond skip/keep; addable later as a richer filter without engine changes.

Several filters carry an `override` argv (e.g. git/status → `--porcelain`) that is **inert
until P4** wires override-at-launch; in P2 they reduce the *standard* captured output via
skip/keep/matchOutput/template, so the terse wins (push/add → one line; dotnet/cargo →
matchOutput on success; test runners → keep-failures-only) are real now and deepen in P4.

**Generic fallback is opt-in** (`GenericFallback.ErrorExtract`, passed by `IpcWorker`) so the
pure `FilterEngine.Apply` default stays byte-identical to `OutputCompactor` — the P0
regression guard holds unchanged. An opaque failure with no recognizable signal is **not**
emptied; it falls back to the plain digest.

**Oracle note:** true byte-equality with tokf's fixtures is not achievable — ps-bash wraps
output in its own `ps-bash compact-output:` header + `[out]/[err]` prefixes, a different
shape from tokf's raw reduced text. P2 instead uses per-command **behavioral** golden tests
(key signal kept, noise dropped) over representative captured output, which is the faithful
oracle for *our* format. The `≥5 differential cases/filter` bar is met at suite level across
the failure axes rather than literally 5×15 hand cases; more are drop-in as the filter set
grows.

---

## 5. PowerShell-object compact serializer (§3b)

Native cmdlets + ps-bash typed `BashObject`s (`LsEntry`, `PsEntry`, `WcResult`) render via
`Format-Table`/`Format-List` = blank lines, repeated headers, wide padding = token waste.

`Format-Compact` (in `PsBash.Cmdlets`, beside `Format-Styled`):
- drop all-null / all-empty columns
- single header row, min-width align, no rule line
- collapse `> maxRows` to `+N more rows`
- scalar / single object → inline `k=v`
- `-Ultra` variant: TSV separators (cheaper than aligned spaces) — rtk ultra-compact parity

**Injection:** object reduction must happen in the host runspace (objects exist there, before
text-ification). Compact mode routes the pipeline tail through `Format-Compact` instead of
`Out-Default`/`Format-Table`. `ls` filter (#15) is the first consumer.

```
Format-Table default          Format-Compact
─────────────────────         ──────────────
(blank line)                  Name        Size Mode
Name      Size Mode           main.rs     1234 -a--
────      ──── ────           cargo.toml   456 -a--
main.rs   1234 -a--           +9 more rows
(blank) ...
```

---

## 6. Testing strategy (qa-rubric)

- **Oracle (Dir 1):** vendor a curated subset of tokf fixtures (input txt + expected) into
  `PsBash.Core.Tests/Compaction/fixtures/`; assert byte-equal after canonicalization (strip ANSI,
  LF, trim trailing). git/dotnet/cargo filters proven against an external reference.
- **Engine pure → unit tests:** each stage isolated; selector precedence (project>user>built-in);
  exclude list; **fallback parity** (unmatched command → byte-equal to today's `OutputCompactor`).
- **Failure-surface (Dir 3)** per filter: empty, ≥10MB (bounded digest), unicode/BOM/emoji, CRLF,
  **exit-code passthrough** (filter must NOT mask real exit code), stderr interleave.
- **Object serializer:** synthetic object arrays (null-column drop, header-once, `+N more`, scalar
  inline, ultra TSV); differential vs `Format-Table` for shape.
- **Security (Dir 12):** template output containing `{{...}}` not re-expanded; JSON regex
  timeout-bounded (ReDoS).
- **Canary (Dir 8):** `git status` under compact mode across M1/M2/M3, byte-stable.
- **Numbers (Dir 2):** ≥80% branch on `FilterEngine.cs`; ≥5 differential cases/filter; per-filter
  token-reduction % recorded here on completion.

---

## 7. Phasing

| Phase | Deliverable |
|-------|-------------|
| P0 | `FilterEngine` + `FilterSpec` + stages, pure, unit-tested; `OutputCompactor` moved + wired as fallback. No behavior change for unmatched commands. |
| P1 | JSON spec format + `FilterLibrary` (embed + user/project override + precedence + exclude). |
| P2 | ~15 built-in filters + vendored tokf oracle fixtures. |
| P3 | `Format-Compact` object serializer + `ls` routing. |
| P4 | `override`-at-launch wiring + tee recovery. |
| P5 (deferred) | Combine paired commands (AST-level). |

**Acceptance per phase:** numeric bar (Dir 11) — coverage %, differential case count, token-reduction
% per filter. No "improve coverage" without a number.
