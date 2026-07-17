---
written_at: 2026-07-16T22:45:00Z
source_event: [task:01KX8V6FBPDW078CQPT9R6TRVT, task:01KX8V6FC0G3XHTD6QMWPFKN2M, git:e2f5784, git:78d100f, doc:.worktrack/context.md]
module: ps-bash
category: workflow-issues
confidence: high
sources:
  - task:01KX8V6FBPDW078CQPT9R6TRVT
  - task:01KX8V6FC0G3XHTD6QMWPFKN2M
  - git:e2f5784
  - git:78d100f
  - doc:.worktrack/context.md
tags: [worktrack, psm1, line-endings, test-gate, scope-matcher, review-batch]
status: steering
recurrence: 1
---

# Lessons — review-batch pair (find multi-root, yq robustness), 2026-07-16

Two `review-batch` epic children (01KX8V6FBPDW078CQPT9R6TRVT "find ignores search-path
operands after the first", commit e2f5784; 01KX8V6FC0G3XHTD6QMWPFKN2M "yq crashes on large
integers / control chars / comma-decimal locales", commit 78d100f) both ran the
`slice-guarded` template clean: scope-check pass, `compound-review:correctness-reviewer`
pass attempt 1 (2 low advisory findings each, none blocking), test gate force-passed with
evidence. No rewinds on either task — audit below is durable process signal, not
defect-recurrence signal.

## Lesson 1 — PsBash.psm1 is CRLF-dominant but not uniform; verify before large Edits

**What happened:** mid-task on the yq fix, the Edit tool flattened psm1 line endings;
implementer caught it via `git diff --stat` blowing up far past the expected ~48-line diff
and hand-restored per-line-ending convention from the git blob before committing. Final
commit 78d100f is clean (48 psm1 lines changed, exactly the 3 touched functions) — the
review verdict explicitly confirms "no line-ending/whitespace churn from the reported
Edit-tool incident."

**Verified independently:** a live byte-scan of the current `PsBash.psm1` shows 4097 CRLF
line endings vs 50 LF-only lines — the file is CRLF-dominant but genuinely mixed, so an
Edit-tool normalization pass is a real, silent risk, not a one-off fluke.

**Apply when:** about to make a large/multi-line Edit to `PsBash.psm1` (or any file with a
similar history of mixed committed line endings).

**Prevention:** before a large edit, check line-ending composition of the target region
(or just diff line-count sanity after editing — a 48-line intended change producing a
1000+-line diff is the tripwire) and restore from the git blob if the tool normalized
endings it shouldn't have. Small, single-function edits (as both these tasks did) sidestep
the risk almost entirely — prefer minimal, localized `Edit` calls in this file over
broad rewrites.

## Lesson 2 — full-suite `test-all-with-log` gate is env-flaky here; force-pass-with-evidence is the accepted gate pattern

**What happened:** both tasks' `test-all-with-log` command steps show `status: passed`
with `outcomeJson: null` — i.e., forced rather than a captured green command result — and
the coordinator's checkpoint records this was because the full suite is flaky in this
environment (a tracking issue, 01KXPGK4V6WV1ZWXPNEYCWXGS3, was filed rather than
re-running to green).

**Apply when:** the `test-all-with-log` (or equivalent full-suite) step is the last gate
before closing a `slice-guarded` task and the environment is under load (e.g., other
suites/agents running concurrently).

**Prevention:** don't loop retrying a flaky full-suite gate. Capture filtered/targeted
test evidence for the touched area (both tasks did — 63/63 `InvokeBashFind` tests, 16/16
`InvokeBashYq` tests, cited in the reviewer verdicts) plus a background full-suite log
path, then `step_force` the gate with that evidence attached and file a tracking issue for
the flakiness itself rather than treating it as this task's blocker.

## Lesson 3 — server-side git/`run_until_gate` times out in this environment; client-side evaluation is the working pattern

**Source:** `.worktrack/context.md` checkpoint note (server-side git operations time out
here) is corroborated by both tasks' actual step shapes: neither used a server-side
git-diff evaluation step — the review step instead ran as a `subagent` step with an
explicit `reviewer_verdict_v1` outcome pushed by the review subagent itself, and the test
step ran as a local `command` step (`pwsh` invoking `scripts/test.sh`, not a server-managed
git/test integration).

**Apply when:** driving a `slice-guarded` (or similar) workflow in this workspace.

**Prevention:** default to client-side `task_workflow_step_evaluate`/explicit outcome
pushes and locally-run (possibly backgrounded) test commands rather than
`run_until_gate`/server-side git evaluation, which has been observed to time out.

## Lesson 4 (flagged, not fully corroborated) — scope-matcher `**` globs

**Source:** single source — `.worktrack/context.md` checkpoint note claims the worktrack
scope matcher does not support `**` globs, only exact paths. Both audited tasks' fileScope
*did* include a `**` entry (`artifacts/worktrack/**`), and both scope-checks passed — but
neither task's diff actually touched a file that would need that glob to match (the
`checkedFiles` in both scope-check outcomes list only the exactly-named files that were
actually changed). This audit neither confirms nor refutes the glob-matcher claim; it just
didn't get exercised here.

**Prevention (until confirmed):** when a task's file scope needs to cover a directory of
generated/output paths, list the exact paths actually expected to change rather than
relying on a `**` glob to cover them — cheap insurance either way.

## What worked (durable, not just this pair)

Both tasks: single-purpose commit, 1-2 files touched, tight `fileScope` matching the
actual diff, `slice-guarded` template — zero rewinds, only low-severity advisory findings
on first review attempt. Keeping `review-batch` epic children this narrowly scoped is
worth preserving as the default slice size for this kind of correctness-fix batch.
