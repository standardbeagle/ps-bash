---
written_at: 2026-07-17T10:15:00Z
source_event: [task:01KXQ0JXSXT6GZAX4HA46M39BT, task:01KXQ0KMG5C26BWXNVPZXBVA6H, git:8f71872, git:747b5a7, git:45641d1, git:bb63d7d, git:6230833, git:566af23, git:9a670c4, git:bf764b7, git:0dd6a89, git:4a48981, git:64da305, git:f7b3981]
module: ps-bash
category: workflow-issues
confidence: high
sources:
  - task:01KXQ0JXSXT6GZAX4HA46M39BT
  - task:01KXQ0KMG5C26BWXNVPZXBVA6H
  - git:8f71872
  - git:747b5a7
  - git:45641d1
  - git:6230833
  - git:9a670c4
  - git:bf764b7
  - git:0dd6a89
  - git:4a48981
  - git:f7b3981
tags: [worktrack, perf, differential-tests, fused-pipeline, review-rewind, cassette]
status: steering
recurrence: 1
---

# Lessons — differential-cassette + fused-pipeline-perf pair, 2026-07-17

Two `slice-guarded` priority-leg tasks: `01KXQ0JXSXT6GZAX4HA46M39BT` (cassette-record the
differential bash oracle, commits 8f71872/747b5a7/45641d1) closed clean on review attempt 1
(4 low-severity advisories, no rewind). `01KXQ0KMG5C26BWXNVPZXBVA6H` (fused internal pipeline
perf, commits bb63d7d..f7b3981, 9 commits across phase-1 profiling / phase-2a fuse / phase-2b
streaming cores) took **two real review rewinds** — phase-2a attempt 1 (`tail -f` hang) and
phase-2b attempt 1 (certified-subset drift) — both caught before merge, not after.

## Lesson 1 — profile before design; the assumed bottleneck was not the dominant one

**What happened:** the task's own phase-1 profiling (`scripts/bench-pipeline.ps1`, warm
isolated daemon) ranked causes empirically instead of assuming: (1) per-output-line IPC
return framing — DOMINANT; (2) per-line `BashObject` allocation; (3) ~250ms warm floor. The
task narrative had opened expecting per-line object allocation to be the ceiling; the actual
data showed IPC framing cost 5–11x more. Phase-2a fixed bottleneck #1 first (batched-frame
fused lane) and got 5–11x before touching allocation at all.

**Apply when:** starting any "make X faster" task with a hypothesis about what's slow.

**Prevention:** measure the ranked bottleneck list before choosing what to fix first. Fix
the dominant cost first — a correct-but-wrong-priority optimization (fixing #2 before #1)
burns a full implementation+review cycle for a fraction of the available speedup.

## Lesson 2 — reviewer value concentrated on delivery-layer and unbounded-stream divergences

**What happened:** both phase-2a and phase-2b rewinds were NOT about the core algorithm
(fusing pipeline stages) — they were about edge behavior the implementer's parity tests
didn't cover:

- Phase-2a: `IsFusablePipeline`/`FusePipelineAllowlist` let `tail -f | grep err` fuse, but
  the fused cmdlet buffers the *entire* inner pipeline before its first `WriteObject`
  (`InvokeCommand.InvokeScript` returns a completed `Collection<PSObject>`), while the
  unfused lane streams live via `SdkWorker`'s `DataAdded` delivery path. A bounded-chain
  parity suite (36/36 green) never exercises an unbounded producer, so the hang was invisible
  to the implementer's own tests.
- Phase-2b: the certified-argv subset (what `LineStreamRegistry` accepts as streamable) had
  drifted wider than the streamed-vs-unfused parity test matrix actually covered — e.g. `grep`
  flag bundles that prefix-collide with the cmdlet's own binder decoys were silently accepted
  by the streaming path without a parity test proving byte-identical output.

**Apply when:** reviewing (or writing acceptance criteria for) any change that adds a fast
path alongside an existing slow-but-correct path — caching, fusing, batching, an internal
short-circuit.

**Prevention:** for a new fast path, explicitly enumerate (a) unbounded/streaming inputs
(anything with `-f`/`--follow`/infinite generators) and (b) the exact argv surface the fast
path accepts vs. what its parity tests exercise. A parity suite that is 100% green while
covering only bounded finite chains proves nothing about live-follow correctness.

## Lesson 3 — review rubric line: "certified subset must equal tested subset, shrink or test"

**What happened:** phase-2b's fix (commit f7b3981) didn't widen the test matrix to match the
accepted argv surface — it **shrank the accepted surface to match what was actually tested**
(decline flag bundles for grep/sed, decline `+`-prefixed head counts) rather than writing more
parity tests to cover the wider surface. This is the cheaper and safer direction when the
untested surface is an edge case with a plausible failure mode (binder collision), not a
common case.

**Apply when:** a reviewer finds "this accepts more argv shapes than the parity tests prove."

**Prevention:** as a review rubric line for any certified/allowlisted-subset design: the
accepted subset and the tested subset must be provably equal. When they diverge, the fix is
either (a) shrink the accepted subset to what's tested, or (b) add tests for the gap — pick
whichever is cheaper and doesn't sacrifice the feature's value. Don't ship a subset wider than
its proof.

## Lesson 4 — shared-helper extraction beats duplicated logic for parity-critical code

**What happened:** phase-2b's first pass had `GrepStage`'s streaming path re-implementing the
regex-assembly ladder (fixed/BRE/extended + word/line wraps + ignore-case) as a second copy of
`InvokeBashGrepCommand`'s logic — two independent places that had to agree on regex semantics
forever. The review-attempt-1 fix extracted the ladder into a shared
`InvokeBashGrepCommand.TryBuildRegexes`, called by both the cmdlet and `GrepStage`. Commit
message: "the duplicated ladder is gone, drift is structurally impossible."

**Apply when:** adding a second call path (fast path, cache, batch mode) that must stay
byte-identical to an existing implementation's semantics.

**Prevention:** default to extracting the shared core as a named, tested static method the
instant a second caller needs the same logic — don't let "just copy it, it's a small ladder"
stand, even under review-fix time pressure. A structural guarantee (one function, two callers)
is strictly stronger than a parity test suite (which only proves today's inputs match).

## Lesson 5 — differential-oracle cassette-record: low-risk, advisory-only close

**What happened:** the companion cassette task (replacing per-test live WSL bash oracle spawns
with checked-in record/replay cassettes, 235 cases @ bash 5.2.21) passed review attempt 1 with
4 low-severity advisories only (eager class-load probe still spawns one bash even in replay
mode; `TryLoad` conflates corrupt-vs-missing cassette; no CI re-record/drift-detection job yet;
acceptance evidence — before/after timing — lives in task comments, unverified from the diff
alone). None were blocking. This is the same "narrowly scoped, single-purpose, zero rewind"
shape documented in the 2026-07-16 review-batch lessons doc (Lesson at end, "What worked") —
worth reinforcing as the default target shape, in contrast to the perf task's two-rewind path
which was appropriately larger/riskier (new execution lane, not a test-harness swap).

**Apply when:** scoping a task that swaps an external live dependency (a spawned process, a
network call) for a recorded/replayed fixture.

**Prevention:** file the follow-up items an advisory-only review surfaces (here:
01KXQMS5JM tracks the 4 advisories) rather than blocking on them — but do file them; "no CI
re-record job yet" is a real drift risk for a cassette-based test oracle and should not be
silently dropped once the task closes.

## What worked (durable, not just this pair)

- Phase-based delivery for a risky perf change (profile → phase-2a dominant-bottleneck fix →
  phase-2b secondary-bottleneck fix) let review catch two independent, unrelated correctness
  gaps at the point they were introduced, each with a small enough diff to root-cause fast.
- Both tasks' `test-all-with-log` gate again shows `outcomeJson: null` (forced pass) — same
  environment flakiness pattern as the 2026-07-16 batch; targeted suite evidence (fused suite
  54/54, grep/sed/wc/seq/head cmdlet suites 113/113, 36/36 emitter tests) was cited in the
  review verdicts instead, consistent with Lesson 2 of the prior lessons doc.

## Refactor-leg + epic-close lessons (later same day)

```yaml
written_at: 2026-07-17T13:10:00Z
source_event: [task:01KXPT3WT6RTZAGDHN363TB1TF, task:01KXPT3WTQXZQP6KFE2A6SXVBD, task:01KX8V6FG9ECBJ9YZED2PGSDRK, git:94bf481, git:b570854]
```

Closing leg of the `SMELL: break up god classes / long methods` epic
(`01KX8V6FG9ECBJ9YZED2PGSDRK`, parent `01KX8V3NPYDVY0PM21TJKAGWE0`): the last two subtasks —
special-var mapping table (`01KXPT3WT6RTZAGDHN363TB1TF`, commit `94bf481`) and EmitSimple
decomposition (`01KXPT3WTQXZQP6KFE2A6SXVBD`, commit `b570854`) — both closed on review attempt
1, no rewinds. Both touch `PsEmitter.cs`, the file the whole epic targeted.

### Lesson 1 — byte-identity as the pure-refactor tripwire, with named deliberate deviations

**What happened:** both tasks were scoped as pure code motion (dedupe a mapping table;
decompose a 400-line dispatch method into an early-return ladder) with "emitted text
byte-identical" as the acceptance bar. The reviewer didn't take that claim on faith — it
**independently re-derived** byte-identity for every special var in plain and braced contexts
(commit `94bf481`) and reconstructed the original dispatch-precedence ladder from the diff's
deletions to confirm fall-through semantics were preserved exactly (commit `b570854`, incl. the
declare-without-varname and null-`EmitSet` fall-through edge cases). Both tasks also named a
spot where unification was deliberately *not* done and said why: `94bf481` documents the
inherited braced-form asymmetry (PWD/RANDOM/SECONDS/PPID/BASH_VERSION/BASH_VERSINFO/multi-digit
positionals fall through to `${env:}` in braced context, unfixed, out of scope) and keeps
`PositionalRefExpr` (arithmetic-substitution context) untouched as a genuinely distinct shape
rather than folding it into the new helper. `b570854` explicitly calls out that the
`EmitGeneralCommand`-adjacent `EnvPairs` blocks were dead code before the refactor too, and kept
them verbatim rather than pruning them as a drive-by cleanup.

**Apply when:** scoping or reviewing any refactor whose acceptance criterion is "behavior
unchanged" (dedupe, extract-method, code-motion).

**Prevention:** (a) make the reviewer re-derive byte-identity from the diff, not just re-run the
test suite — tests only prove today's covered inputs match; (b) require the PR/task to name any
spot where the refactor stops short of full unification and state why (a real semantic
difference vs. scope discipline); (c) leave pre-existing dead code as-is under a pure-motion
refactor — pruning it is a separate, reviewable decision, not a freebie.

### Lesson 2 — defer-with-rationale is a legitimate way to close an epic, not a stall

**What happened:** the epic listed 6 findings; finding 4 (arithmetic clauses stored as raw
strings — primitive obsession) went through grill and came out **deferred**, not done: parked
as a standalone backlog task (`01KXPT3WW4`) with recorded rationale (L-size, not a live defect,
should ride the shape of the `Invoke-BashArith` evaluator rather than be redesigned standalone).
The epic then closed at 5/6 with that gap named explicitly in its closing comment, rather than
staying open waiting for a task that had no urgent forcing function.

**Apply when:** an epic/batch has one heavier or lower-value finding among several independent,
already-closable ones.

**Prevention:** treat "defer with a written rationale + a tracked follow-up task" as a normal,
first-class grill outcome — not a failure to finish. Closing the other 5/6 promptly is strictly
better than blocking the whole epic on the one finding that needs a different shape of work.
Verify the rationale actually says *why* (size, risk, dependency) and *where* it's tracked, not
just "deferred."

### Lesson 3 — `test-all-with-log` forced-pass (`outcomeJson: null`) recurring a third time

**What happened:** both tasks in this leg show the same `test-all-with-log` command step outcome
as the 2026-07-17 perf pair and the 2026-07-16 batch before it: `outcomeJson: null`, i.e. the
gate step recorded a pass without a structured result. Reviewers again substituted targeted
in-diff suite evidence (e.g. `PsBash.Core.Tests` net10.0 = 1241 passed / 0 failed / 1 skipped,
independently reproduced) rather than relying on the gate's own output.

**Apply when:** auditing the `slice-guarded` template's `test-all-with-log` step, or deciding
whether to trust a green gate at face value.

**Prevention:** this is now a 3-for-3 recurrence in one day — worth promoting from "reviewers
work around it" to a template fix: either make the command step capture and persist a structured
`command_result_v1` outcome reliably (the Windows/pwsh `Tee-Object` + exit-code plumbing is the
likely gap), or drop the null-outcome step and require the reviewer's targeted-suite citation as
the documented gate. Leaving it as a silently-null "passed" step is a false-confidence signal for
anyone who reads the workflow record without also reading the review comments.
