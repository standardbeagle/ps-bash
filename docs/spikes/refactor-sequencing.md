# Refactor Sequencing + Dependency Graph

**Post-v0.9.7 architectural cleanup**

Tracking task: `FA4eeJEiCJQk` (META) — loop `dqjNTUoPZjCa`.

This document formalizes the wave-sequenced refactor plan, the inter-project
dependency graph, the per-wave risk gates, and a revised scope estimate. It is a
**live tracking artifact** — see the "Status as of 2026-05-14" section for what
has actually landed.

---

## 1. Sequencing Principle

Group refactors by independence; pick the smallest cuts first. Each wave is a
batch of tasks that can proceed in parallel; a wave only starts once the prior
wave's risk gate is green.

### Wave 1 — Foundational hygiene (independent, parallel-safe)

| Task | ID | Description |
|------|----|-----|
| REFACTOR-1 | `ODVeJZ6IWUvU` | SDK setup script as embedded `.ps1` file. |
| REFACTOR-6 | `HSd7a1lT8PZC` | psm1 `Set-StrictMode` at function scope, not file-wide. |
| RC-3a | `zsbBywo8pNb7` | psm1 partial-load bisect. |
| RC-5 | `YVzx6dZO0sy4` | macOS fd-walk fstat precision. |

### Wave 2 — Module load consolidation (depends on Wave 1)

| Task | ID | Description |
|------|----|-----|
| REFACTOR-5 | `hQzX7gAha41K` | Canonicalize module load to extracted resources only. |
| REFACTOR-5a | `Fh2WgjOx46dJ` | **(unplanned)** Extract `PsBash.Transpiler` leaf project to break the Core↔Cmdlets cycle. |
| REFACTOR-5a-2 | `0cx2VPwufzIJ` | **(unplanned)** Fix `EmitEval` bare type literal — assembly-qualify `BashTranspiler` after the type changed assemblies. |
| RC-3 | `mgdPfmblsu4Y` | Cmdlets.dll embedding build infra. *(Done prior to this loop.)* |

### Wave 3 — Output channel symmetry (depends on Wave 1)

| Task | ID | Description |
|------|----|-----|
| REFACTOR-4 | `dLoSV6zSQcAn` | All host→launcher output via IPC stream tags. Subsumes RC-1. |
| RC-1 | `0RhwDuwTPmUu` | Obsoleted by REFACTOR-4 — **recommend closing as superseded.** |

### Wave 4 — Lifetime + test harness (depends on Wave 3)

| Task | ID | Description |
|------|----|-----|
| REFACTOR-7 | `g5vzfVkq68J5` | Per-invocation host for `-c` / script modes. |
| REFACTOR-3 | `H8nhR08AT3Ws` | Unified test process-spawn helper. |

### Wave 5 — Runtime migration (largest scope)

| Task | ID | Description |
|------|----|-----|
| REFACTOR-2 | `DoIYh3RVWpCI` | psm1 → `PsBash.Cmdlets` migration, in phases. |
| RC-7 | `uYqZrG1n7UWX` | Emitter unquoted-empty-var elision. |

REFACTOR-2 follow-ons (filed during this loop):

| Task | ID | Description |
|------|----|-----|
| REFACTOR-2 Phase 2 | `i1Apd5OwULMQ` | Extract psm1 arg-helpers (`ConvertFrom-BashArgs`, `Emit-BashLine`, `New-FlagDefs`) to binary cmdlets. |
| REFACTOR-2 Phase 1b | `MjqPco7X45k4` | Remaining 8 leaf functions (echo/printf/cat/ls/head/tail/wc/pwd). **Blocked on Phase 2.** |
| REFACTOR-2 Phase 3 | `IAZZ5TsCj9kH` | Non-leaf / pipeline-consumer function migration. |

### Wave 6 — Differential parity sweep

| Task | ID | Description |
|------|----|-----|
| RC-8 | `7q2c4RafF5qb` | Windows Differential triage. |
| RC-9 | `0Gx9HutvWmEC` | Eval Fnm payload decision. |

---

## 2. Dependency Graph

### 2.1 Wave dependency graph (task-level)

```
Wave 1 ─┬─> Wave 2 ──> (Wave 2 internal: REFACTOR-5a -> REFACTOR-5a-2 -> REFACTOR-5)
        │
        └─> Wave 3 ──> Wave 4 ──> Wave 5 ──> Wave 6
```

- Wave 2 and Wave 3 both depend only on Wave 1; they are parallel-safe with
  respect to each other.
- Wave 4 depends on Wave 3 (test harness + lifetime build on the IPC stream-tag
  work).
- Wave 5 depends on Wave 4 (runtime migration needs the per-invocation host and
  the unified spawn helper for parity testing).
- Wave 6 depends on Wave 5 (parity sweep validates the migrated runtime).

The graph is acyclic: `W1 → W2`, `W1 → W3 → W4 → W5 → W6`. No back-edges.

### 2.2 REFACTOR-5 internal split (the unplanned prerequisite)

REFACTOR-5 could not embed `Cmdlets.dll` into `Core` because `Cmdlets` had a
compile-time `ProjectReference` to `Core` — a hard circular dependency. The fix
was an unplanned project-graph restructuring:

```
REFACTOR-5a  (extract PsBash.Transpiler leaf project)
     │
     v
REFACTOR-5a-2  (fix EmitEval assembly-qualified type literal)
     │
     v
REFACTOR-5  (retry: embed Cmdlets.dll into Core, remove Get-Module probe)
```

### 2.3 Project dependency graph (assembly-level, post-REFACTOR-5)

`PsBash.Transpiler` is a **new leaf node** introduced by REFACTOR-5a. Parser +
Transpiler source moved there; namespaces were kept as `PsBash.Core.*` for
zero using-churn.

```mermaid
graph TD
    Transpiler[PsBash.Transpiler<br/>NEW leaf — Parser + Transpiler<br/>namespaces still PsBash.Core.*]
    Cmdlets[PsBash.Cmdlets<br/>binary Invoke-Bash* cmdlets]
    Core[PsBash.Core<br/>embeds Cmdlets.dll as resource]
    Host[PsBash.Host<br/>ps-bash-host runspace]
    Testing[PsBash.Testing<br/>NEW — PsBashRunner + ProcessSpawn]
    Launcher[ps-bash launcher]

    Cmdlets --> Transpiler
    Core --> Transpiler
    Core -. embeds Cmdlets.dll .-> Cmdlets
    Host --> Core
    Launcher --> Core
    Launcher --> Host
```

Key edges:

- `PsBash.Transpiler` is a leaf — depends on nothing internal. This is what
  broke the cycle: both `Cmdlets` and `Core` now reference `Transpiler`
  instead of each other.
- `Core` embeds `Cmdlets.dll` as an **MSBuild-target-copied embedded resource**
  (not a `ProjectReference`), extracted at runtime by `ModuleExtractor`. This is
  a build-output dependency, not a compile-time reference — hence the dotted
  edge.
- `PsBash.Testing` is a **new dependency-free shared assembly** (REFACTOR-3). It
  deliberately does **not** reference `PsBash.Host` — the in-process `SdkWorker`
  harness (`HostWorkerFixture`) is a different shape and was split to a
  follow-on.

---

## 3. Risk Gates Between Waves

Each gate must be green before the next wave starts.

| After | Gate |
|-------|------|
| Wave 1 | CI baseline must be `<=` current OR better (no new failures). |
| Wave 2 | `Host.Tests` `SdkWorkerTests` should pass. |
| Wave 3 | Canary `StderrRedirect_StderrContainsMessage_SpawnModes` must pass. |
| Wave 4 | Escalation timeouts should fall (per-invocation host shortens lifetime). |
| Wave 5 | Differential parity baseline: zero new failures vs pre-migration. |
| Wave 6 | Windows Differential triage closed; Eval Fnm payload decision recorded. |

Additional guard added during this loop: REFACTOR-6 introduced
`ModulePartialLoadTests.cs`, a permanent CI guard against psm1 partial-load
regressions. This **substantially de-risked RC-3a** — the partial-load bisect
task — without a separate bisect run.

---

## 4. Scope Estimate — Original vs Revised

### 4.1 Original estimate (~7–9 weeks)

| Wave | Original estimate |
|------|-------------------|
| Wave 1 | ~1 week |
| Wave 2 | ~3 days |
| Wave 3 | ~1 week |
| Wave 4 | ~1 week |
| Wave 5 | ~3 weeks |
| Wave 6 | ~2 weeks |

### 4.2 Where reality diverged

**Wave 2 was NOT a ~3-day task.** The original estimate assumed REFACTOR-5 was a
straightforward "point the loader at extracted resources" change. In reality it
surfaced a hard `Core↔Cmdlets` circular dependency that blocked the work
outright. Resolving it required an **unplanned, mandatory project-graph
restructuring**:

- REFACTOR-5a — extract the new `PsBash.Transpiler` leaf project.
- REFACTOR-5a-2 — fix the runtime fallout (`EmitEval` emitted a bare
  `[PsBash.Core.Transpiler.BashTranspiler]` type literal the host runspace could
  no longer resolve once the type moved assemblies).
- REFACTOR-5 — only then could the original task be retried and completed.

Revised Wave 2 estimate: **~1.5–2 weeks**, not 3 days. Any future wave that
touches the project graph should budget for cycle discovery.

**Wave 5 phase ordering was inverted.** The original plan implied Phase 1 (leaf
function migration) before Phase 2 (helper extraction). REFACTOR-2 Phase 1
revealed that only `basename` + `dirname` are truly leaf — the other 8 Phase-1
functions (echo/printf/cat/ls/head/tail/wc/pwd) all depend on psm1-only helpers
(`ConvertFrom-BashArgs`, `Emit-BashLine`, etc.). Migrating them before the
helpers would force a fragile C#→PS→C# callback.

**Corrected ordering: Phase 2 (helper extraction) must precede Phase 1b
(remaining leaf functions).** Phase 1b (`MjqPco7X45k4`) is now explicitly
blocked on Phase 2 (`i1Apd5OwULMQ`).

### 4.3 Revised total

Original: ~7–9 weeks. Revised: **~8–11 weeks**, with the variance concentrated in
Wave 2 (project-graph prerequisite) and Wave 5 (re-sequenced into Phase 2 → 1b →
3, three tracked sub-tasks instead of a monolithic phase).

---

## 5. Status as of 2026-05-14

Loop `dqjNTUoPZjCa` executed Waves 1–5. Wave 6 and several RC-* tasks were not
touched.

### 5.1 Landed (commits on `main`)

| Task | Status | Commit | Notes |
|------|--------|--------|-------|
| REFACTOR-1 | Done | `7249693` | Embedded `SdkRunspaceSetup.ps1` + `RunspaceSetupExtractor.cs`. |
| REFACTOR-6 | Done | `e04e463` | File-scope `StrictMode` removed; function-scoped on `ConvertFrom-BashArgs` / `New-FlagDefs`; added `ModulePartialLoadTests.cs` (de-risks RC-3a). |
| REFACTOR-5a | Done | `1f23039` | Extracted `src/PsBash.Transpiler/` leaf project (Parser + Transpiler source, namespaces kept `PsBash.Core.*`). WIP commit — hit a sub-blocker. |
| REFACTOR-5a-2 | Done | `09cf8a3` | Fixed `PsEmitter.EmitEval` bare type literal — assembly-qualified `BashTranspiler` type names. |
| REFACTOR-5 | Done | `34d3845` | `Cmdlets.dll` embedded in `Core` via MSBuild target, extracted by `ModuleExtractor`; `Get-Module` probe removed. (Retried after the 5a/5a-2 split.) |
| REFACTOR-4 | Done | `c55d5b5` | `HostProtocol` gained STDOUT/STDERR stream tags; `PsEmitter` rewrites `>&2` to `Write-BashHostStderr`; `IpcWorker` routes by tag. |
| REFACTOR-7 | Done | `701502e` | `IpcWorker` `Lifetime {PerInvocation, Daemon}`; non-interactive modes spawn per-invocation hosts on process-local sockets. |
| REFACTOR-3 | Done | `61d58de` | New `PsBash.Testing` shared assembly (`PsBashRunner` builder + `ProcessSpawn`); Escalation/Canary/Differential migrated. |
| REFACTOR-2 Phase 1 | Partial | `3c12c3d` | Only `basename` + `dirname` migrated to binary cmdlets. Remaining 8 Phase-1 functions deferred (depend on Phase-2 helpers). |
| RC-3 | `mgdPfmblsu4Y` | Done | Cmdlets.dll embedding build infra — landed **before** this loop (commits `6f264eb`, then refined by `34d3845`). Listed here for completeness. |

### 5.2 Remaining

| Task | ID | Status | Notes |
|------|----|--------|-------|
| RC-1 | `0RhwDuwTPmUu` | Recommend close | Genuinely superseded by REFACTOR-4. |
| RC-3a | `zsbBywo8pNb7` | To-do | Substantially de-risked by `ModulePartialLoadTests.cs`; may be closeable without a bisect run. |
| RC-5 | `YVzx6dZO0sy4` | To-do | macOS fd-walk fstat precision — not executed. |
| RC-7 | `uYqZrG1n7UWX` | To-do | Emitter unquoted-empty-var elision — not executed. |
| RC-8 | `7q2c4RafF5qb` | To-do | Windows Differential triage (Wave 6). |
| RC-9 | `0Gx9HutvWmEC` | To-do | Eval Fnm payload decision (Wave 6). |
| REFACTOR-2 Phase 2 | `i1Apd5OwULMQ` | To-do | Helper extraction — **must precede Phase 1b.** |
| REFACTOR-2 Phase 1b | `MjqPco7X45k4` | Blocked | Remaining 8 leaf functions — blocked on Phase 2. |
| REFACTOR-2 Phase 3 | `IAZZ5TsCj9kH` | To-do | Non-leaf / pipeline-consumer migration. |
| HostWorkerFixture follow-on | (filed) | To-do | `Host.Tests` in-process `SdkWorker` harness — split from REFACTOR-3. |

### 5.3 Recommended next actions

1. Close RC-1 as obsoleted-by-REFACTOR-4.
2. Re-evaluate RC-3a — the partial-load CI guard may make a separate bisect
   unnecessary; downgrade or close.
3. Resume Wave 5 in corrected order: REFACTOR-2 Phase 2 (`i1Apd5OwULMQ`) → Phase
   1b (`MjqPco7X45k4`) → Phase 3 (`IAZZ5TsCj9kH`).
4. Wave 6 (RC-8, RC-9) remains gated on Wave 5 completion.

---

## 6. Replan as of 2026-05-14 (post-loop)

Loop `dqjNTUoPZjCa` closed. This section replans the **17 remaining To-do tasks**
into execution phases. Supersedes the Wave 5/6 ordering above where they differ.

### Phase A — Cheap closures (no code) — DONE

| Task | Action | Result |
|------|--------|--------|
| RC-1 `0RhwDuwTPmUu` | Close superseded | Done — REFACTOR-4 subsumed the stderr-IPC channel. |
| RC-3a `zsbBywo8pNb7` | Verify guard, close | Done — `ModulePartialLoadTests` green (2/2) on `main`; REFACTOR-6 removed the file-scope StrictMode that caused the partial load. Permanent CI guard now in place. |

### Phase B — Finish Wave 5 (REFACTOR-2 chain, strict order)

1. **REFACTOR-2 Phase 2** `i1Apd5OwULMQ` — extract psm1 arg-helpers
   (`ConvertFrom-BashArgs`, `Emit-BashLine`, `New-FlagDefs`) to binary cmdlets.
   Pair with **RC-7** `uYqZrG1n7UWX` (emitter unquoted-empty-var elision) — the
   emitter is already being touched in this phase.
2. **REFACTOR-2 Phase 1b** `MjqPco7X45k4` — remaining 8 leaf functions
   (echo/printf/cat/ls/head/tail/wc/pwd). Blocked on B1.
3. **REFACTOR-2 Phase 3** `IAZZ5TsCj9kH` — non-leaf / pipeline-consumer functions.
4. **HostWorkerFixture follow-on** — `Host.Tests` in-process `SdkWorker` harness,
   split off from REFACTOR-3. File as a Dart task before scheduling.

### Phase C — Independent RC fixes (parallel-safe)

| Task | Scope | Note |
|------|-------|------|
| RC-5 `YVzx6dZO0sy4` | macOS fd-walk may close .NET runtime fds | host code, self-contained |
| RC-2 `ki3HRN90Sb2p` | bg-job hangs — `Invoke-BashBackground` spawns full pwsh child | runtime, self-contained |
| RC-6 `bPajrz2XY4zJ` | golden file regen on canonical env | run **after** RC-7/emitter settles — needs a clean baseline |

### Phase D — Wave 6 Differential sweep (gated on B + C)

1. RC-6 golden regen → clean baseline.
2. RC-8 `7q2c4RafF5qb` — Windows Differential 97-fail triage.
3. RC-9 `0Gx9HutvWmEC` — Eval Fnm payload decision.

### Phase E — PTY epic (separate track, parent `INvNldqseYdz`)

Sequential: PTY-5 signal forwarding → PTY-6 fg/bg handoff → PTY-7 crash recovery →
PTY-8 test harness → PTY-9 TUI parity (needs PTY-8) → PTY-11 revive
BrowseInteractive → PTY-12 arch doc.

**PTY-10 `Xin0M76kAytE` conflict:** "drop daemon-reuse for interactive sessions"
contradicts REFACTOR-7, which *kept* the daemon for interactive mode by design.
Re-grill PTY-10 against REFACTOR-7's landed lifetime model before scheduling.

### Standalone

`E84SFIVCE6zL` (stale ps-bash-host processes on Windows) — likely shrunk by
REFACTOR-7 (per-invocation hosts self-clean on client disconnect). Re-evaluate
scope; may be near-closeable.

### Critical path

`A → B(1→2→3→4) → C → D`. Phase E runs in parallel; PTY-10 blocked on reconcile.
