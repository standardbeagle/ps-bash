# Spec suite: a fast bash-conformance corpus and runner

Status: DESIGN. Nothing below exists yet except the pieces named as existing.
Goal: thousands of bash-behaviour cases that run on ps-bash in seconds, with expected output taken from real bash,
that replace the existing process-per-test differential tests wherever they overlap.

## 1. Why a new runner

Today a bash-behaviour check is a C# test that launches `ps-bash.exe` (`PsBashRunner`), often plus a second launch of
real bash. `Differential.Tests` has 1,394 tests and took about 14 minutes on the last full run (~0.4 s per test, parallel).
Almost all of that is process spawn, launcher start-up, IPC connect and transpiler start-up, none of which is the thing
being tested. The cost grows with every case added, so nobody adds cases.

Target on a warm machine: **5,000 cases in under 30 s**; median overhead per case under 5 ms beyond the command itself;
a single-file run (`--filter`) under 2 s including start-up.

## 2. The corpus

One `.spec.sh` file per area (`quoting`, `param-expansion`, `arrays`, `redirects`, `builtins-read`, ...) under
`spec/`. The format is the Oils spec-test format so Oils files import unchanged (see the Oils wiki, "Spec Tests"):

```
#### name of the case
echo hello
## stdout: hello
## status: 0

#### multi-line expectation
printf 'a\nb\n'
## STDOUT:
a
b
## END

#### where bash and the ideal differ
...
## OK bash STDOUT:
...
## END
```

- **Expectations come from real bash**, never hand-written. `spec record` runs every case in the pinned oracle
  (Ubuntu bash 5.x under WSL, one process per FILE, parallel across files; the 345-case probe built for this took 34 s) and
  writes `## stdout:` / `## status:`. Recorded values are committed, so a normal run needs no WSL.
- Stderr is compared only when a case says `## stderr:` (messages carry the shell name and line; the runner normalises
  `script: line N:` prefixes).
- **Per-shell annotations** (`## OK bash`, `## BUG bash`, `## N-I ps-bash`) are honoured so imported Oils cases keep their
  meaning. ps-bash's own gaps are `## XFAIL ps-bash <id>` (section 6).
- Cases are independent. Each gets a fresh scratch directory as its cwd (`ps-bash/spec-tmp/<run>/<case>`, removed after).
- Provenance: every imported file keeps its upstream header; check the Oils licence before vendoring and record it in
  `spec/THIRD-PARTY.md`.

## 3. The runner

New project `PsBash.Spec` (library + small console) and `PsBash.Spec.Tests` (one xunit fact per spec FILE that reports each
failing case by name). No new `tman` verb is needed: `tman test-proj src/PsBash.Spec.Tests --filter ...`; the console runs
via `tman dev`-style invocation for `record` and ad-hoc runs.

### 3.1 In-host execution, no process per case

The runner talks to `ps-bash-host` over the existing IPC (`HostProtocol`), one `Command` frame per case, exactly what the
launcher sends for `ps-bash script.sh`:

```
case text ──BashTranspiler (in the runner process, parallel, cached)──▶ PS text
          ──positional preamble (script path, BASH_SOURCE) + env block──▶ Command frame ──▶ leased executor
          ◀── STDOUT/STDERR frames + EXIT ──
```

- A warm host takes a case in single-digit milliseconds; the per-command reset (env block, shell globals, `Clear-BashFd`,
  fresh pooled runspace) already gives case isolation without a process.
- **Parallelism = executor slots** (`executor-slots.md`): the runner holds N leases (N = cores, default 8) so N cases run at
  once in N host processes. This is why Phase 6 comes first: with one global exec gate there is no parallelism to use.
- **The launcher pipeline is shared, not copied.** The code that turns `(script text, args)` into a framed request now lives
  in `Shell/Program.cs`. It moves into a Core class (`LauncherRequest`) used by both the launcher and the runner, so the fast
  path cannot drift from the real one.
- Per-case timeout (default 5 s; `## timeout:` overrides): cancel the connection; the host's disconnect watchdog stops the
  command; a host that does not recover in 2 s is killed and its slot respawned. A hung case costs one slot for one timeout,
  never the run.
- Stdin is `/dev/null` unless the case supplies a heredoc/pipe.

### 3.2 Making it fast

| Cost | Remedy |
|---|---|
| Transpile | Transpile all cases of a file up front, in parallel, once; cache on disk keyed by `hash(case) + transpiler build id`. An unchanged case in an unchanged transpiler costs a file read. |
| Host start-up | One warm host per slot, kept for the whole run; slots are spawned in parallel while the corpus is parsed. |
| Warm-up | First case on a slot pays runspace warm-up; the runner sends a no-op to every slot while it parses. |
| Output | Frames are compared as bytes (`RawBytes`), no decode unless the case fails. |
| Order | Failed-last-run cases first, then longest-last-time first (so the tail is short); `--fail-fast N`. |
| Re-runs | `--changed`: only files whose cases or whose transpiler/cmdlets build id changed; `--last-failed`. |
| Dedup | Identical (script, stdin, env) across files run once (hash); this is also how the absorbed tests collapse. |

### 3.3 Fidelity guard

The fast path is only trustworthy if it equals the real launcher. A sampled self-check (default 1 case in 50, always the
first of each file, plus every case tagged `## real-launcher`) re-runs through `PsBashRunner` (a real `ps-bash.exe`) and
fails the run if the two ever disagree. Cases that need a real process boundary (signals to the launcher, exit codes of the
launcher itself, `-c` vs file vs stdin modes, terminal behaviour) are tagged `## real-launcher` and run only that way.

## 4. Absorbing the existing tests

Inventory today (xunit attributes counted): Cmdlets.Tests 3,746; Core.Tests 1,550; Differential.Tests 1,394;
Shell.Tests 681; Host.Tests 263; Canary 38; Escalation 23; Pester `PsBash.Tests.ps1` 847 `It` blocks.

A test is **absorbed** (becomes a spec case, the C# test is deleted) when ALL hold:
1. it is "run this bash text, assert stdout/exit status (and optionally stderr)" with no access to internals;
2. it needs no real-launcher behaviour (section 3.3);
3. a bash oracle run reproduces the asserted value (a test whose assertion disagrees with real bash is an intentional
   difference: it becomes `## XFAIL ps-bash` plus an entry in `intentional-differences.md`, not silently dropped).

Candidates, in order of yield: `Differential.Tests` (nearly all), the command-behaviour half of `Cmdlets.Tests`
(`Invoke-BashXxx` called with text vs a real tool, only where a bash snippet expresses the same thing), the stdout
assertions in `Shell.Tests`, the behavioural Pester blocks. **Stay as C#**: parser/emitter unit tests (they assert the
generated PowerShell), protocol/lifecycle/IPC, host-must-survive, daemon and process-management tests, flag-spec and
guard tests, cmdlet unit tests of internals.

Mechanics: `spec absorb <test-class>` extracts the script literals (and `Theory` rows) from a test class, runs them in the
oracle, writes the cases (with the old test name in a `## from:` line), and prints the cases whose recorded value differs
from the old assertion. A class is deleted only when every extracted case passes or is explicitly XFAIL'd.
Order: one class at a time, suite green after each, so a regression is attributable.

## 5. Reporting

One line per file (`pass/xfail/xpass/fail`, ms), failures printed with a unified diff of expected vs actual and the case
text; machine-readable `spec-results.json` for the progress dashboard. `xpass` (an XFAIL case that now passes) is an error
so the gap list only shrinks. A coverage page (`docs/specs/bash-coverage.md`, generated) lists, per section of the GNU Bash
Reference Manual and per POSIX Shell Command Language chapter, the number of cases, passes and XFAILs.

## 6. Known gaps are data, not red tests

`## XFAIL ps-bash <id> <reason>` marks a case ps-bash is known to fail. `<id>` is a heading in
`intentional-differences.md` (a deliberate difference) or a `GAP-n` row in `spec/GAPS.md` (a bug or missing feature).
The suite is green with gaps listed; fixing one means deleting its XFAIL line (xpass enforces it).

## 7. Phases and exit criteria

0. **Executor slots** (`executor-slots.md`): N parallel hosts. Exit: concurrent launchers run in parallel, suites green.
1. **Runner skeleton**: parse the format, `LauncherRequest` extracted from `Program.cs`, run a file in-host on one slot,
   `record` against WSL. Exit: the 345-case probe runs, matches the launcher on a fidelity sample, in under 10 s.
2. **Parallel + cache + reporting**: N slots, transpile cache, ordering, XFAIL/XPASS, `spec-results.json`.
   Exit: probe corpus under 3 s warm.
3. **Import** Oils cases for the sections ps-bash claims (quoting, expansions, arrays, redirects, builtins, control flow),
   record bash 5.x expectations, triage failures into XFAIL with ids. Exit: coverage page generated.
4. **Absorb** `Differential.Tests` class by class, then the other candidates. Exit: Differential.Tests contains only
   tests that need a real process; full-suite wall time drops accordingly (record before/after).
5. **Gate**: `Spec.Tests` joins the release gates (with Pester and Core.Tests) once it is stable for a week of runs.

## 8. Risks

- *Fast path drifts from the real launcher.* Mitigated by sharing `LauncherRequest` and the sampled fidelity check.
- *Host state leaks between cases.* The per-command reset is the contract; a leak is a host bug found by running 5,000
  cases back to back, which is itself a benefit. A `--isolate` mode gives every case a fresh host to separate a leak from a
  real failure.
- *Oils cases assume other shells' quirks.* Per-shell annotations carry that; only bash expectations are recorded here.
- *Absorbing hides an intentional difference.* Rule 3 in section 4: a disagreement with the oracle must become a documented
  XFAIL, never a deleted assertion.

## Phase 1 status (built)

`src/PsBash.Spec` holds the format parser (`SpecParser`), the judge (`SpecRunner`: pass / fail / xfail / xpass, stdout and
status compared, stderr only when the case states it), the oracle (`WslBashExecutor`) and `SpecRecorder`.
`src/PsBash.Spec.Tests` runs every `spec/*.spec.sh` against ps-bash, one launcher process per case
(`PsBashExecutor`; in-host execution and transpile caching are later phases).
Record expectations for new cases from real bash: `PSBASH_SPEC_RECORD=1 tman test-proj src/PsBash.Spec.Tests`
(only cases with no expectation are filled). Add `## XFAIL ps-bash <id> <reason>` for a known gap.

## Importing Oils cases

`PSBASH_SPEC_IMPORT=<oils .test.sh> tman test-proj src/PsBash.Spec.Tests` converts an Oils spec file into an empty
`spec/oils/NAME.spec.sh` (`SpecImporter`): expectations are stripped, cases that need the Oils harness (`$SH`,
`$REPO_ROOT`, `ysh`, ...) are dropped, and an Apache-2.0 attribution header is added. `PSBASH_SPEC_RECORD=1` then fills
expectations from bash 5.2 (oracle timeouts drop the case), and `PSBASH_SPEC_TRIAGE=1` marks failing cases XFAIL and
unmarks the ones that now pass. First import: `spec/oils/loop.spec.sh`.

## 9. Measured (Phase 2 in-host executor)

56 cases (`spec/**`), same machine, run to completion; wall time of the case runs only (`PSBASH_SPEC_TIMING`).

| Executor | Wall | Per case | Note |
|---|---|---|---|
| Launcher + fresh host per case (Phase 1) | 84 s | 1.5 s | `PSBASH_SPEC_MODE=perinv` |
| Launcher per case, one shared warm host | 30 s | 0.54 s | `PSBASH_SPEC_MODE=warm`; ~500 ms of each case is launcher start + connect (`PSBASH_TIMING=1`: transpile 125 ms, connect 55 ms, execute 100 ms) |
| **In-process transpile + N private hosts (default)** | **13 s** | **0.24 s** | `InHostExecutor`; 4 slots, one warm-up script per host; most remaining time is host start-up and two `sleep`-based cases |

Raising launcher-per-case parallelism did not help: one host serialises, and spilling to more hosts costs a cold start each.
The in-host executor is the first to scale with the corpus, since the fixed cost is paid once per slot.
Fidelity guard: the first and every 25th case of each file is re-run through the real launcher and must give the same verdict.
Memory: 4 slots = 4 hosts (~0.4-0.8 GB each at peak); `PSBASH_SPEC_PAR` lowers it.

## 10. Status: Oils and Differential tests brought over

- **Oils**: every file in `oils-for-unix/oils` `spec/` except `builtin-fc` is in `spec/oils/` (bulk import: `PSBASH_SPEC_IMPORT_DIR=<dir>`
  with empty `spec/oils/NAME.spec.sh` placeholders, plus `PSBASH_SPEC_RECORD=1 PSBASH_SPEC_TRIAGE=1`). Cases that need the Oils harness or
  `ysh` are dropped by `SpecImporter`; `builtin-fc` is in with 3 of its 14 cases (the ones that do not use the harness's `$SH`); they are marked `## real-launcher`, so they run through the launcher instead of the shared in-host runspace (the two disagree on `fc`). The other 11 pipe a script into a nested `$SH --norc -i` (`## tags: interactive`) to check history output, which a corpus of non-interactive scripts cannot express; interactive behaviour is covered by the PTY tests.
- **Differential**: `PSBASH_ORACLE_CAPTURE=<file>` makes `AssertOracle.EqualAsync/GoldenAsync` and `PsBashRunner.RunScriptAsync` append
  `{class, method, script}` and return without running anything (`PsBash.Testing/ScriptCapture.cs`), so a `Differential.Tests` run takes
  seconds and yields the exact script of every test, including ones built by `Tree()`/`WithArchive()`. `tools/absorb-capture.ps1 -Capture <file>`
  merges them into `spec/differential/<class>.spec.sh` (existing recorded cases kept). `FsStateOracle` scripts already print the file-system
  snapshot to stdout, so state checks are part of the case. `tools/absorb-differential.ps1` is the older text-scanning route (literals only).
  A script naming a Windows drive path (a helper file the test wrote under `%TEMP%`) is skipped: the case cannot recreate it and bash under
  WSL cannot run it. `nativestdiobytes.spec.sh` was such a file (expectations were pwsh's usage text) and was removed; `NativeStdioBytesTests` covers it.
- Not part of the spec suite by nature: cmdlet tests (`CmdResult`, in `Cmdlets.Tests`), the host-memory budget and process-lifecycle tests.
- **Isolation**: each case gets its own scratch directory (`_tmp` only when the script mentions one). The in-host executor snapshots shell state
  once per host (`BashShellState.Save` plus the function table) and restores it after every case; `readonly` names are reset per invocation.
- Cost: the full suite is ~35 min on this machine (the restore step and the record pass dominate); a single file is seconds.

### 10.1 Bounded, resumable runs

A tman job is killed at its max-time and loses its summary, so the corpus runner records progress itself:
`PSBASH_SPEC_BUDGET_MIN=<n>` stops starting new files after n minutes (skipped, not failed);
`PSBASH_SPEC_RESULTS=<file>` appends `PASS|FAIL|SKIP<TAB>file<TAB>ms` per file as it finishes;
`PSBASH_SPEC_RESUME=1` skips files the results file already records as PASS. Re-run until no file is SKIP.
Full suite (301 files, 316 tests): clean, 32m47s.

### 10.2 Parallelism vs memory (measured)

`--filter "DisplayName~differential/"` (77 tests, all green at every setting), one tman job each:

| `PSBASH_SPEC_PAR` | wall | peak memory |
|---|---|---|
| 2 | 24m26s | 2.9 GB |
| 4 (default) | 18m19s | 4.2 GB |
| 6 | 16m24s | 6.0 GB |

4 stays the default: 6 buys 10% more speed for 1.8 GB more. On a shared machine use 2.
