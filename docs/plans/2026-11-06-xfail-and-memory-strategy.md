# XFAIL backlog and memory: strategy

Status: proposal (2026-11-06), written after the v0.14.1 release. Nothing below is started. It replaces "just work through
the 727 XFAILs", which the release work showed does not converge.

## What the release taught us

1. **Most XFAIL cost is not code.** Cases checked by hand: "arguments to pwd" expects a recorded random `mktemp` path;
   "chdir is a synonym for cd" is busybox-only (bash has no `chdir`). A count of 727 mixes four different things and
   needs sorting before any fixing.
2. **A "fix" that reshapes every script is not a fix.** `$_` (only `echo` sets it) and `$BASHPID` (same in subshells)
   are real gaps, but both touch the emitter's hottest path for about 5 cases each.
3. **Per-OS differences hide in tests, not just code.** The four macOS-only Differential failures were: a test whose
   answer pairing depended on readdir order, BSD `od` layout, a real perf bug (`find` forked `/usr/bin/stat` per entry,
   17 s for 701 files), and a real design gap (native stdin writer, `docs/bugs/native-stdin-endless-producer-broken-pipe.md`).
4. **Memory pressure is already real.** The harness killed a full corpus run for low memory; a bash clone should run
   small (your requirement). `scripts/run-memcapped.ps1` (Job Object, per-process commit limit) exists but only wraps
   ad-hoc runs.

## Phase 1: classify, don't fix (cheap, no code)

Run the spec corpus ONE FILE AT A TIME (`PSBASH_SPEC_PAR=1`, under `run-memcapped.ps1 -LimitMB 2048`) and give every XFAIL
one of four labels in a table (`docs/specs/xfail-triage.md`), each backed by the bash oracle, never by reading the case:

| Label | Meaning | Action |
|---|---|---|
| XPASS | passes today | delete the marker (an honest reduction) |
| ARTIFACT | expectation is nondeterministic or other-shell-only (random temp path, busybox/zsh) | fix the case at the source (normalise the recorded value) or mark `N-I` with the reason, reviewed per case |
| WINDOWS | cannot hold under the Windows path model | document in `intentional-differences.md`, mark `N-I ps-bash` |
| GAP | real behaviour difference | goes to Phase 2 |

Exit criterion: every one of the 727 has a label. The ~460 target is then a statement about GAP vs the rest, not a guess.

## Phase 2: fix GAPs by root cause, grouped, cheapest first

Group GAPs by the mechanism they share, not by file (one fix clears a family). Order by cases cleared per unit of
emitter/runtime risk. Candidates already understood: `$_` (set only when the script mentions it; transpile-time flag),
`$BASHPID` (subshell depth, document the in-process limit), trap DEBUG, background jobs, nameref. Each fix needs a
regression test that fails pre-fix, plus a Windows + macOS check through `single-test.yml` (never a full publish run).

## Phase 3: memory

1. **Caps by default.** Run every test project under a Job Object cap without wrapping `tman` itself: `tman` sets the
   cap on the test host it spawns. Start at 2048 MB per process, lower it until something fails, then fix what failed.
2. **Baseline.** Measure idle `ps-bash-host` working set and per-command peak (private bytes), record the numbers in
   the repo, and add a guard test that fails when the idle set grows past a bound.
3. **Leaks.** Soak test: N thousand commands through one warm host; assert the working set returns to baseline
   (GC.Collect allowed in the test, per your direction: monitor the live set, not GC timing).
4. **Linux/macOS caps.** `systemd-run --user -p MemoryMax=` on Linux; macOS has no per-process cap, so measure only.

## Order and checkpoints

Phase 1 first (it makes every later number meaningful), Phase 3.1 in parallel (it protects the machine while Phase 1
runs the corpus), then Phase 2. Report the labelled table, the XPASS removals, and the new count before any Phase 2 code.

## Measured so far (2026-11-06)

Soak through ONE warm dev host (`tman dev script.sh`, working set sampled externally every 5-6 s):

| Workload | Commands | Host working set | Private bytes |
|---|---|---|---|
| 40 rounds x 500 iterations of `$(echo\|sed\|awk)`, `cat\|grep`, `sort\|uniq\|wc`, `head\|cut` | ~200,000 | 133-146 MB, flat | 57-72 MB, flat |
| 25 rounds over a 300,000-line file (`sort -rn`, `grep`, `awk`, `cat\|wc`, `find`, `sed`) | ~150 big pipelines | 167-182 MB, flat | 91-107 MB, flat |

No growth across rounds in either run: no leak in the command path, and the working set is bounded by the largest
single command (the 300k-line file), not by how many commands ran. The 1.1 GB installed host seen earlier in a
long agent session had exited by the next check, so it is not a standing leak; it was a session that ran very large
commands. Open follow-up: record peak private bytes per large command (the real "low memory" risk), not idle size.
