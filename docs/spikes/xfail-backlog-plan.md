# Plan: the remaining spec/oils XFAIL backlog

Written 2026-11-10 as a hand-off brief for an implementer with no session context. Read `CLAUDE.md`,
`CODE_MAP.md` and `docs/specs/xfail-triage.md` first; this file assumes their rules.

**Where things stand.** `spec/oils/*.spec.sh` holds **480** `## XFAIL ps-bash` markers at commit `f729b873`
(local, not pushed). It was 714 on 2026-11-06 and 555 on 2026-11-09. Every number below comes from one dump
of the still-marked cases (476 of the 480 were in it), taken on the tree just before `f729b873`.

**What is known and what is not.** The dump gives each case's script, expected and actual output. Clustering
by failure signature and first stderr line explains about 190 cases by a named cause. The other ~290 are in
the bucket "stdout differs, no telling stderr line": their causes are **not analysed**. Do not treat the
per-file counts as a list of bugs; expect the usual mix seen so far (a few shared mechanisms, stale markers,
expectations bash itself does not meet, real one-off gaps).

## Every marker ends in one of four states

1. **Fixed**: marker removed, regression test at the right layer (see `.claude/rules/testing.md`).
2. **Platform**: a Windows difference. Marker id `platform-windows-<topic>`, and a row under
   "Platform (Windows)" in `docs/specs/intentional-differences.md`.
3. **Won't fix**: documented in `docs/specs/intentional-differences.md` with the reason, marker id
   `wontfix-<topic>`.
4. **Artifact**: the expectation is not bash 5.2 behaviour (OSH-only, nondeterministic, recorded value).
   Fix the spec side (re-record from WSL bash with `PSBASH_SPEC_RECORD=1`, or guard with
   `case $SH in bash) exit ;; esac` as upstream does), then remove the marker.

Today about 450 of the 480 markers carry a generic id (`gap-oils-<file>`). Replace it with a root-cause id whenever
a case is looked at, even if it stays open: the id is what makes the next clustering pass cheap.

## Priority rule

Agents run non-interactive scripts and `-c` snippets. Order work by that, not by file size:

- **P0, any tier**: a case that hangs, times out, or takes the host process down. The host must survive any
  input (`docs/specs/host-lifecycle-contract.md`).
- **Tier 1**: scripting (variables, arrays, expansion, tests, redirects, pipelines, exit status, cd, parse).
- **Tier 2**: builtins used in scripts less often (`trap`, `type`, `kill`, background jobs).
- **Tier 3**: interactive-only (history, `fc`, `bind`, completion, prompt, line-editor idioms, `pushd`/`popd`).
  Fix only when a shared mechanism makes it free; otherwise classify and move on.

## Workstream A: named causes (do these first)

| # | Cause | Cases | State | First step |
|---|---|---|---|---|
| A1 | `chmod` did not exist | 15 report "command not found"; 17 scripts use it | Implemented, **uncommitted** (`InvokeBashChmodCommand.cs`, tests pass) | Land it, then unmark what passes. Some cases assert real mode bits and will become `platform-windows-*`. |
| A2 | Lowercase variable clobbers a system variable (`path=`, `tmp=`) | 2 marked `platform-windows-env-case`, plus hidden damage in other cases | Not started | Separate brief: `docs/spikes/windows-env-case-collision-plan.md`. |
| A3 | `argv.py` / `printenv.py`: command not found | 14 still marked | **Analysed 2026-11-10: the premise was wrong** | The helpers exist NOWHERE: not in the repo, not in the WSL oracle. The expectations were recorded from bash with them missing, so every passing case simply prints nothing for them. In these 14 cases bash stops BEFORE reaching the helper (an expansion error: `${a[0][0]}` bad substitution, `${!ref}` to an unset name, a syntax error, `typeset a[3]=4` rejected), giving status 1 or 2. ps-bash misses that error, runs the helper, and gets 127. So each case is its own missing-error gap; group them by the error bash raises (`git grep` the case, run it in the oracle with `2>&1`), not by the helper. Oracle run: bash errors first in array "Multiple subscripts" (`${a[0][0]}: bad substitution`, 1), var-sub-quote (`unexpected EOF while looking for matching '`, 2), array-literal (`declare -A` on an indexed array: `cannot convert indexed to associative array`), nix-idioms "string var is fatal" (`!hookSlice: unbound variable` under `set -u`), var-ref `${!a[@]-…}` (`invalid indirect expansion`). The other 7 (array-compat, nix-idioms "undefined array", assign-extended, word-split ×2, `typeset a[3]=4`, var-ref suffix operators) get "not found" in bash too: their diff is in other lines. assign-deferred `readonly/export a[7]=8` are fixed. |
| A4 | Timeout or host exit | 11 | **Partly done (2026-11-10, second pass)**. **P0** | Fixed: `loop` continue-in-subshell (subshell is a loop barrier; the errexit case also needed the next fix), `process-sub` ×2 (`> >(body)` on simple AND compound commands: temp-file hand-off), `trap 'echo <'`. Root causes found: (1) a bare word matching a PowerShell module export only by case (`should` → Pester's `Should`) auto-imported the module: slow, then a PowerShell error. Now exact case for dash-less words (`SdkRunspaceSetup.ps1`). (2) `sh -c "kill -USR1 $$"` hard-killed the enclosing host (a nested shell runs in another host, so `$$` is not its own pid). `kill` now never kills a `ps-bash-host`; the signal is not delivered (`gap-signal-enclosing-shell`, row in intentional-differences). **Follow-up:** a cross-host signal channel so the enclosing script's trap runs. Open: `builtin-trap-bash` ×2, `background`, `builtin-special` `exec` (not a hang standalone; real gap: `pre1=pre1 readonly x=x` makes `x` visible to a child, bash does not export it), `builtin-bind` ×2, `array-assign` "More fragments like a[". |
| A5 | Valid bash rejected at parse time | 29 | Not analysed | Groups: alias-driven grammar (alias ×6, Tier 3 unless non-interactive `shopt -s expand_aliases` is involved); `ble-unset` / `ble-features` ×8 (same construct, likely one fix); `set` forms `set -`, `set +`, `set - +` ×5 (see A7); `$((` vs `$( (` ambiguity ×3 (`paren-ambiguity`); arrays as env bindings ×2; then singles (`[[` over lines, `{abc}<<<`, `time { … }`, `trap ERR` pipeline, `a[@]` nameref, arithmetic `?:` in a slice). Two are the opposite problem: bash rejects, ps-bash accepts (`parse-error-wrong-shape`). |
| A6 | PowerShell error text reaches the user | 7 | Not analysed | Any `Exception calling`, `Cannot bind`, `The term …` in stderr is a bug by itself, whatever the case tests. Cases: redirect-multi, builtin-history ×2, builtin-trap, array-assign, alias, divergence. |
| A7 | OSH-only options and `set` forms | 12 use `shopt --set/-s <oils option>`; 4 hit "set: option not supported" | Decision needed, then mechanical | bash prints `shopt: X: invalid shell option name` and carries on. Check ps-bash does the same (same message, status 1, script continues); cases that then match bash are stale, the rest are Artifact. Do not implement Oils options. |
| A8 | `$SH` nested-shell forms | 139 scripts mention `$SH`; flags seen: `-c` 22, `--norc -i` 14, `--rcfile` 13, `-i -c` 4, `-i` 3, `-o` 2, and one each of `-x -v`, `-l -c`, `-n`, `-oo`, `+c`, `-c -x -e` | Not analysed | The harness sets `SH=bash`, and `bash` inside ps-bash is the launcher. Tabulate which launcher options are unsupported (`src/PsBash.Shell/Args.cs`), implement the non-interactive ones (`-o opt`, `-x`, `-e`, `-n`, `-v`, `+o`), classify `-i` / `--rcfile` cases as Tier 3. |
| A9 | Smaller known gaps (found, not fixed) | about 8 | Reproduced earlier | `echo hi \| echo "$(cat)"` (a command substitution in a pipe stage's arguments does not see the pipe); `${#FUNCNAME[@]}` is 2, not 1, under `-c`; bare `$BASH_SOURCE` is empty; `foreach` resolves to a PowerShell alias. |

## Workstream B: the unanalysed files

Counts are markers per file in the dump. Work a file with the per-file loop in the method section.

**Tier 1 (about 230 markers).** builtin-cd 12, var-ref 9, bugs 8, vars-special 7, sh-usage 7, builtin-meta 7,
assign 7, builtin-special 7, builtin-process 7, builtin-printf 6, extglob-files 6, nul-bytes 6, pipeline 6,
array-assign 6, array-literal 6, redirect 6, command_ 6, regex 6, sh-options 5, redirect-multi 5, arith 5,
extglob-match 5, word-split 5, var-op-bash 5, builtin-meta-assign 5, builtin-bracket 5, builtin-set 5,
toysh 5, builtin-eval-source 5, arith-context 5, loop 5, assign-extended 5, array-assoc 5, and about 30 files
with 4 or fewer.

Already understood within Tier 1 (from `docs/specs/xfail-triage.md` and the memory notes):
- `pipeline`: `PIPESTATUS` family and SIGPIPE 141 are a process-model limit. Decide won't-fix and document.
- `nameref` 4, `var-ref` 9: namerefs resolve at emit time; forwarding a name through a second function or
  targeting `a[2]` needs a run-time nameref. One mechanism, medium size.
- `vars-special`: `$PPID`, `$RANDOM`, HOSTNAME / OSTYPE are recorded values (Artifact or Platform); the `$_`
  family needs a transpile-time flag so only scripts that mention `$_` pay for it.
- `builtin-cd` 12 and most of the 23 `platform-windows-paths` markers: symlinked and `-P` paths. Check each is
  truly the filesystem and not `cd` logic.

**Tier 2 (about 50).** builtin-trap-bash 15 and builtin-trap-err 8 (DEBUG / ERR / RETURN traps: one design
question, answer it once), builtin-type-bash 10, background 9, builtin-kill 6, builtin-trap 4.

**Tier 3 (about 100).** builtin-dirs 13, alias 11, builtin-fc 11, interactive 10, builtin-completion 9,
prompt 8, builtin-bind 8, builtin-history 6, ble-idioms 5, ble-unset 5, ble-features 5, and the history /
readline files. Expected end state for most: `wontfix-interactive-<topic>` with one documented row per topic,
not one per case. `ble-*` and `builtin-dirs` are worth a quick look first: `pushd` / `popd` are used in
scripts, and the ble files failed on parse (A5), which is not interactive at all.

## Method

Tools are in `docs/spikes/xfail-tools/`:
- `gate.ps1 [-Tag n] [-OutDir dir]`: Core.Tests, then spec slices until all 222 spec files have run, then
  Pester. Writes `gate<n>.log`, `spec-results<n>.tsv`, `xfail<n>.jsonl` (the dump) to the output directory.
- `cluster.ps1 -Jsonl <dump> -Out <tsv>`: signature and first-stderr-line clusters.
- `unmark.ps1 -File <spec> -Cases 'name', …`: removes markers byte-safely (some spec files hold raw bytes).

Per cause (workstream A): reproduce with `tman dev -c '…'` or `tman dev <file.sh>` (run from the repo
directory; `PSBASH_DEBUG=1` prints the emitted PowerShell), confirm bash with
`wsl.exe -d Ubuntu-24.04 -e bash -c '…'`, fix the mechanism, add the regression test, then re-run the spec
files that mention the construct and unmark every `[XPass]`.

Per file (workstream B): run `tman test-proj src/PsBash.Spec.Tests --filter "DisplayName~oils/<file>.spec"`
with `PSBASH_SPEC_DUMP_XFAIL_JSONL=<file>` set, read each case's expected vs actual, confirm against WSL bash,
label it with one of the four end states. When three cases in a file share a cause, stop and treat it as a
workstream-A item.

Facts that cost time when forgotten:
- One spec run covers only about 35 of 222 files inside the time budget and still reports "Passed 222". A
  gate is complete only when every file has a non-SKIP row in the results TSV (`gate.ps1` loops for this;
  about 70 minutes).
- The Bash tool is the **installed release**, not the checkout. Use `tman dev`.
- Count markers with `git grep -c '^## XFAIL ps-bash' HEAD -- spec/oils` (ripgrep skips one file).
- `PsEmitter.cs` is Latin-1 with **LF** endings (checked 2026-11-10; an anchor written with CRLF never matches). Edit it with a script that round-trips through ISO-8859-1 and throws
  if an anchor is missing. Other sources are UTF-8.
- Differential tests compare stderr; wrap with `{ …; } 2>/dev/null` when only the message prefix differs.
  Record with `PSBASH_ORACLE_RECORD=1`, then `git checkout -- src/PsBash.Differential.Tests/Cassettes` and add
  only the new cassettes. The oracle's working directory reports mode 0777 for every file and runs any file
  as executable, so do not put permission-dependent assertions there.
- Clear stray one-letter environment variables (`D`, `S`, `F`) from the shell that launches a gate: they
  leak into cases (`s+=foo` printed `331foo`).
- `tman` refuses a second `test-proj` while one is active; it does not queue it.

## Gate and commits

Each batch: Core.Tests, the Differential classes touched, the affected spec files. Before committing a batch
that changes shared behaviour (parser, emitter, host): the full `gate.ps1` run locally and full
Differential + Cmdlets + Host suites. `build2` (Linux, usually idle) can run those from a git bundle while
the spec loop runs locally. Only these may fail: `serialize` case `set` (depends on the caller's environment)
and `core.spec.sh` case `wait -n returns the status of the next job to finish` (fails under suite load,
passes standalone). Commit locally in coherent batches with the XFAIL count in the message; do not push
unless asked.

## Open items inherited with this plan

- Uncommitted: `chmod` (A1) and a transpiler crash fix in `FusedLane.IsFusablePipeline`
  (`[ a = b -a c |= ddd ]` threw NullReferenceException; test `Transpile_TestWithPipeInside_DoesNotCrash`
  written, not yet run). The full gate for `f729b873` plus these was still running.
- Unconfirmed: 13 Linux-only failures seen on `build2` (`LsSortTimeDifferentialTests` ×6, `InvokeBashKill*` ×4,
  `Find*` ×3). Run the previous commit there to learn whether they predate this work.
- Spec harness, fixed 2026-11-10: the in-host executor's per-case restore never ran (the host gives each
  request a fresh runspace, so the snapshot it kept in a runspace variable was gone, and the error was
  ignored). It did little harm, because the host already resets the environment and the readonly table for
  every request; the restore now works and a failure is reported in the case's stderr.
- Gate trap, fixed in `xfail-tools/gate.ps1`: `[Environment]::SetEnvironmentVariable($n, $null)` from
  PowerShell passes an empty string, which creates an EMPTY variable instead of removing it. Empty `D`, `S`
  and `F` in the test process made `unset f` treat `f` as a variable and `declare -p d` print `d=""`. Use
  `[NullString]::Value`.
- `fc` is not a builtin (`gap-fc-builtin`, 2 cases plus the 11 already marked in `builtin-fc`): PowerShell's
  `fc` alias runs. Without history bash prints nothing and returns 0 for `fc -l`, and
  `fc: no command found` with status 1 for `fc -s`.
- Unconfirmed: 8 `PsBash.Shell.Tests` failures on `build2` (`ProgramEndToEndTests` parse-error cases ×6,
  one PTY signal test, one worktrack logging test). Run them on Windows and on the previous commit.
- `docs/specs/xfail-triage.md` still lists the 2026-11-09 clusters; update it as workstream A items close.
- Fixed 2026-11-10: a nested `bash -c`'s stderr was lost by every route (console, `2> f`, `2>&1`):
  `Invoke-BashBash` wrote it with `Console.Error`, which in the host is the host's own fd 2. It now uses
  `FileSystemHelpers.WriteStderrBytes`. As a safety net the host points `Console.Error` at the running command's
  stderr frames (`SdkWorker.RunCommand`), so awk `"/dev/stderr"`, `time` and xargs messages now print, but they
  still bypass `2>` redirection. **Follow-up:** move those `Console.Error` call sites (`git grep Console.Error
  src/PsBash.Cmdlets`) to the error stream.
- Fixed 2026-11-10 (an A2 symptom inside the host): a script's `tmp=x.txt` IS `TMP` on Windows, and the first
  unknown command in a cold host wrote its autoload-index cache under `Path.GetTempPath()`, creating a
  directory `x.txt/ps-bash/` in the cwd (the `bash-NNNN.txt/` folders littering the repo root came from
  this). Redirect case `>&word redirects stdout and stderr` then failed with "Is a directory". The host now
  captures its temp dir at startup (`PsBash.Core.HostTempPath`, used by the setup script, psm1
  `Get-BashHostTempPath`, `less`, and the proc-sub dir). User-visible temp (`mktemp` default, `/tmp`) still
  follows the live `TMP`: that is A2 proper.
- serialize `set` / `declare` cases grepped `zz` over the whole environment and matched a machine-specific
  variable whose value contained `zz`; the spec now greps `^zz=` (same bash output).
- Seen in the Bash tool (installed `0.13.0-dev`, older than v0.14.2), not yet checked on the dev build:
  `awk '/a/,/b/' file`, `python3 - <<'EOF'` and `sed -n 128,145p file` each "hung". Not those commands:
  every time, a long `run_in_background` Bash command (tman tests) was running, and the installed host ran
  the foreground command only after it. Check whether the current build still serializes a session's
  commands behind one long command (WorkerPool size / exec gate) before calling it a bug.
  Also: `grep -n 'grep "^zz="' file` found nothing although the line exists (`^` not first in a BRE is
  literal); confirm on the dev build.

### Progress, 2026-11-10 (second pass)

480 → 467 markers. Landed the previous session's uncommitted batch (subshell `continue` barrier, `> >(…)`,
`trap 'echo <'`, `( … ) N>&1` high-fd scoping, `local "$1"` via eval, `[[` across newlines, `{1..10}` as a
command word, `export/readonly a[1]=x` errors, `$PPID`/`$RANDOM` spec shape checks, HostMemoryGuard reclaim
backoff) plus the A4 root causes and the nested-stderr fix above.

467 → 462: `history` is a builtin in scripts (`Invoke-BashHistory`: `-c -d -s -p -a -n -r -w`, `[n]` with
bash's numeric / too-many / invalid-option errors; replaces PowerShell's `Get-History` alias, whose binder
errors leaked). The interactive shell still handles a typed `history` itself. `${a[0][0]}` and `${a[0]x}`
are a bad substitution (was element 0 plus literal text). An alias whose expansion is a syntax error
reports it with status 2 instead of throwing out of the alias function.

## Suggested order

1. Land A1 and the crash fix once the gate is green.
2. A4 (hangs and host exits), then A6 (PowerShell text leaks).
3. A3 (`argv.py` not found): 17 cases likely behind one or two causes.
4. A2 (environment-name case), as its own gated commit.
5. A7 (decide, then retag) and A8 (launcher options): mostly classification, large marker count.
6. A5 parse groups, largest first.
7. Workstream B Tier 1 by file, then Tier 2; classify Tier 3 last.
