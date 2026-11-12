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

### Progress, 2026-11-10 (third pass)

Word splitting and stdin, mostly A6-style PowerShell text leaks:
- Unquoted `$*` / `$@`, alone or with text glued on (`=$*=`, `a$1`), and `for i in a$x`: the values are
  joined with IFS[0] (a space when IFS is unset), the affixes glued on, then split on IFS. When IFS is empty
  it is one field per value with empty fields dropped (`ConvertTo-BashWords -Prefix -Suffix -Fields`,
  `BashWordSplitter.AffixFields`). A for-list item `=$*=` / `a$x` was single-quoted PowerShell text.
- A variable that ends a `"…"` part, with text glued after the quote (`"$y"x`, `$y'x'`, `"$y""x"`), is
  braced: it emitted `"$env:yx"`, another (unset) variable. A for-list item `a"$y"b` is flattened into one
  string instead of quoting the command-mode text.
- `eval` reads the stdin it is given (`… | eval 'read x'`, `eval 'cat' <<< hi`, a runtime eval inside a
  stdin scope): it runs inside a stdin scope.
- awk keeps a native program's blank line as an empty record, and its `/dev/stderr`, `system()` and pipe
  stderr go to the error stream.
- `''` as a command and `time`'s report wrote to `[Console]::Error`, which `2>/dev/null` cannot drop.

Deferred: `p a$1` as a command argument with a value containing spaces (needs the same splitting for
command words; broad).

### Progress, 2026-11-11 (fourth pass)

Long gates now run on beagle-ab (`xfail-tools/fleet/fleet-gate.ps1`: Windows = build + `gate.ps1` +
Differential + Cmdlets; WSL Linux = build + Core/Security/Cmdlets/Differential), leaving this machine free to keep
building. The Linux side's remaining failures are the known `LsSortTimeDifferentialTests` ×6 and
`Find_UnsupportedPredicate_EmitsErrorThenContinues`.
- Every `Console.Error` site in `src/PsBash.Cmdlets` now writes to the error stream (xargs `-t`, `source <(…)`
  transpile errors), so `2>` redirects them. (The awk defaults are overridden by the cmdlet.)
- A7/A5 `set` forms: `set +` alone is a no-op and `set + a b` sets `$1 $2` (keeps `-x`, unlike `set -`); both were
  refused at parse time. `set -o NOSUCH` returns 2 (bash 5.2), not 1.
- A9: a command substitution in a stdin-ignoring pipe stage reads the pipe (`echo hi | echo "[$(cat)]"` is
  `[hi]`): the stage runs as `{ …; }` with a stdin scope. A bare `$BASH_SOURCE` / `${BASH_SOURCE##*/}` is element 0.
  `FUNCNAME` ends with `main` only in a script file (`bash -c`: `1 f`).
- Open design question, not fixed: `foreach x`, `where`, `%`, `gci` run PowerShell aliases (bash: 127). ps-bash
  deliberately lets scripts call PowerShell commands; decide whether built-in PowerShell aliases that are not bash
  commands should be hidden from bash scripts.
- The installed release in the Bash tool (0.13.0-dev) runs Windows' `timeout.exe` and fails
  `sed -n '/x/,$p'`; the dev build is correct for both.
- `set - x` / `set + x` anywhere among options (`set -x - -v`, `set -e + -- p q`) and `$-` now carries `x` and,
  under `-c`, a trailing `c` (bash order `efhuxBCc`).
- A8 launcher options: `-x` and `-n` (noexec: parse only, syntax error = 2), set options after `-c`
  (`-c -x -e 'cmd'`), `-c --` / `-c -`, `+c`, bundled `o` taking the next word (`-oo errexit noglob`, `-eo noglob`);
  an unknown option after `-c` is status 2 instead of running `-z` as the command. Still open: `-v` / `set -v`
  (needs a verbose state wired through `set -o`, `$-`, SHELLOPTS) and `bash -o` with no name (lists options).
- awk range patterns `start, end` (`/b/,/c/`, `NR==1,NR==2`) were a syntax error; now POSIX semantics.
- Still open: `$((BASH_LINENO))` is 0; the `shopt` invalid-option message lacks bash's `script: line N:` prefix.
- Spec files are eol=lf; a CRLF working copy makes `unmark.ps1` report NOT FOUND (it now refuses one). Pass several
  case names with `& ./unmark.ps1 -Cases @(…)` in-process: through `pwsh -File` only the first name binds.
- sed address regexes (`/a\+/p`, `/[[:digit:]]/p`) now get the BRE / POSIX-class translation the `s///` path had; a
  backslash inside `[...]` is literal (GNU keeps `\n \t`).
- An in-process background job no longer undoes the parent's writes: a subshell body inside the job's pooled
  runspace restored the PARENT's process-wide variables at its end (`(sleep 1) & x=1; wait` lost `x`).
- XFAIL count after this pass: 452 (commit d4c11d05).

### Progress, 2026-11-11 (fifth pass)

- `B=(b "x y") cmd` was a parse error; bash binds the temporary as the string `(b x y)` (2 `array` cases).
- `${s[1]-w}`, `${s[1]:-w}`, `${s[0]+x}` on a SCALAR threw "Cannot index into a null array"; a scalar is now a
  one-element array for the operator forms too.
- Fleet noise, not regressions: when both sides run at once on beagle-ab, launchers can miss the host connect
  window, so an awk `print | "cat"` child loses its output after ~60 s, and nested `$SH -c` cases (array.spec
  `strict_array`) pass the 10 s case limit. Each passes when re-run alone; check that before chasing one.
- A bare command name with characters no command name has (`{ls`, `a,b`, `x%y`, `@foo`) was parsed by
  PowerShell as a script block, array or splat; it now runs through `&` and is "command not found" (127).
- `time` is a keyword: `time [-p] [--] [!] pipeline` times the whole pipeline or compound command, keeps its
  status and assignments, and prints bash's report (TIMEFORMAT, `-p`, empty TIMEFORMAT = no report) to stderr.
  User/sys are the host's CPU time, so a native child's CPU is not counted.
- `gawk` runs ps-bash's awk, and `match(s, re, arr)` fills arr (groups plus `[n,"start"]`/`[n,"length"]`).
- Linux fleet: `LsSortTimeDifferentialTests` -g/-o/-n fail on beagle-ab in g9 and g10 alike (ps-bash creates
  files 0664, the oracle 0644: a umask difference), not a regression. The `wait -n` job test needed a wider
  gap: a cold Linux host took over 0.5 s to start the second background job.
- Windows fleet: nativestdiobytes, redirect "x=1>", toysh-posix "IFS 4" and bugs "full disk" XPASS in some of
  the 7 gate runs only; pipeline "Pipeline in eval", background "Builtin in background" and errexit row 21
  failed 2 of 7 runs under load and pass locally.
- Found while on `type -P` (dogfood-level, beyond the spec cases):
  - `PATH=_tmp:$PATH` on Windows made ONE entry `_tmp:C:\…`, hiding both directories. Every bash write to PATH
    is now followed by `BashRuntime.NormalizePathVariable` (`:` separators -> `;`, drive colons kept).
  - `echo x | bash s.sh` / `bash s.sh < in`: the launcher forwarded stdin for `-c` only, so a script FILE read
    nothing. File mode now uses the same launcher-stdin scope.
  - An extensionless `#!` script (`./configure`, a helper on PATH) opened Windows' "Pick an app" dialog and
    returned 0. The host's post-lookup hook now runs it through its interpreter (sh/bash = ps-bash), and any other
    non-PATHEXT file is "Permission denied" (126).
  - `type -P`/`-a` search PATH through `ExecutableLookup` (the lookup that runs commands) and print each hit as its
    entry was written (`_tmp/mv`). Default/`-p`/`-f` keep `mv is mv` (documented: the cmdlet is what runs), so the
    `/tmp/mv` and `/bin/pwd` cases stay XFAIL.
- `alias` (8 of 10):
  - A command word from an expansion (`cmd=myalias; $cmd`) is never an alias: `Resolve-BashCommand` skips the
    alias's function (127 unless a cmdlet or file of that name exists).
  - An alias defined in `$( )`, `( )`, `&` or a pipeline element ends with it: the subshell snapshot now restores the
    alias functions, and the transpile-time alias scan does not enter those bodies.
  - An alias holding part of a compound command (`alias LEFT={`, `alias e_='for i in 1 2 3; do echo '`, chained
    trailing-blank aliases) is spliced into the script as text before parsing (`PsEmitter.SpliceAliases`), when the
    script does not parse otherwise or such an alias is used.
  - Still XFAIL: "here doc inside alias" (bash reads the body from the next input line) and "quote in the middle"
    (bash: an alias leaving a quote open is a syntax error).
  - Not covered: a function and an alias of the same name. The alias function replaces the bash function, so
    `f() {…}; alias f=…; g=f; $g` is not found (bash runs the function).
- `var-ref` "${!undef:-}": `${!ref}` with ref unset is "ref: invalid indirect expansion", with ref empty
  ": invalid variable name"; both abandon the command, status 1, also under `set -u` (was "unbound variable").
  `${!undef:=x}` reported twice and threw assigning to an empty name.
- `var-ref` "a[key]" and "tilde in array index": through `${!ref}`, an assoc subscript's quotes are removed wherever
  they are (`assoc["al"e]`), and an indexed subscript is arithmetic (`a[i+1]`; it was read as unset unless a
  literal number); one that does not parse (`a[~+]`) abandons the command.
  - Still XFAIL: LINENO through a ref (LINENO is substituted at lex time), the `check_indir` case (needs bash's
    "expansion error abandons the whole function call"), and the code-injection case (the expected output is
    Oils', which refuses the `$( )`; bash runs it, ps-bash refuses).
- Found on the way:
  - A failed word on a command with a file redirect (`echo "$((1/0))" 2>>f`) still ran the command: the redirect is
    a later pipeline element, began first, and cleared the failure mark. A later element now leaves a mark it does
    not own (`FileSystemHelpers.TakeExpansionFailed`).
  - Linux: a file without an execute bit (`./doc`) went to xdg-open with status 0; now "Permission denied", 126
    (`GetVerbFallbackGuard`, Unix branch).
  - `toysh-posix` "IFS 4" passes now (unmarked).
- Gaps seen, not fixed:
  - bash does not open a redirect target when a word of the command fails to expand; ps-bash creates the file.
  - `set -u; ref=nope; ${!ref}` (target unset) is empty; bash: "!ref: unbound variable".
  - bash drops the rest of the input LINE after an expansion error (`echo ${!undef}; echo next` prints nothing);
    ps-bash runs `echo next`.
  - `builtin-kill` flakes (seen in g8w, g12): the first in-host background job takes ~650 ms to return to the
    parent, so `sleep 0.1 &` can finish before `kill`.
  - `fc -l 0 1 2` "passes" by accident: PowerShell's `fc` (Format-Custom) errors with status 0. Left XFAIL.
- XFAIL count after this pass: 435.

### Progress, 2026-11-11 (sixth pass)

- Run-time command name (`$x`, `"$x"`; `Resolve-BashCommand`):
  - An unquoted name splits first (`-Words`). `SUDO=; $SUDO rm f` runs `rm f`; an empty `$x` with no operands runs
    nothing, status 0 (it leaked PowerShell's "expression after '&'" error, with or without a redirect or heredoc);
    `x="echo a"; $x b` prints "a b" (it looked up `echo a`). A quoted empty name is `bash: : command not found`, 127.
  - In a compound command's stdin scope the queue was piped into EVERY run-time name (since v0.14.1): `printf`/`test`
    never ran on an empty queue and failed to bind a queued line. `-Fed` now lets the runtime feed only a command
    that reads stdin, by the emitter's own rule (`StdinReaders.ShouldFeed`, now public), a function always.
- `[[ ]]` operand: a literal word starting with `$` (`[[ $ =~ "$" ]]`) was emitted bare (`$: command not found`, or a
  parse error before `&&`). regex "Quoted { and +" unmarked.
- BASH_REMATCH is an indexed array in group order (`BashPatternMatch.Rematch`), not PowerShell's `$Matches`
  hashtable: `"${BASH_REMATCH[0]}"` was empty (string key '0' since 217cddd3c), `${BASH_REMATCH[@]}` was in hash
  order, bare `$BASH_REMATCH` was empty, a non-participating group was missing. regex "make a lisp example" unmarked.
- `cd` honours CDPATH (`BashRuntime.CdPathLookup`): a relative target not starting with `.` is searched there first,
  and echoed when a non-empty entry (even `.`) found it. On Windows the entries split like PATH (drive colons kept).
  builtin-cd "CDPATH is respected" now differs only in path spelling: retagged platform-windows-paths.
- Gaps seen, not fixed:
  - Design question: `chdir`, `sl`, `gci`, `iex`, `foreach`, `Get-Date` run as PowerShell aliases/cmdlets. bash
    has no `chdir` (127; busybox ash has it). Should an unknown-to-bash name that PowerShell resolves be 127, or keep
    the PowerShell passthrough? builtin-cd "chdir is a synonym for cd" stays marked until that is decided.
  - `printf 'in\n' | "$c"` (c=cat) prints an extra blank line on the terminal (the bytes into a file are right).
    Also in the release.
  - The installed 0.13 release's awk has no range patterns (`/a/,/b/`), fixed in dev.
  - A nested `bash` launch costs ~3.7 s here (cold host per nested shell); a case with three of them
    (regex "unquoted [a  b]", array strict_array) passes its 10 s budget only on an idle machine. Seen while another
    project's test jobs ran, and on beagle-ab in g13.
  - builtin-kill "kill -KILL kills the process with SIGKILL" fails in-host (fidelity guard: in-host Fail, launcher
    Pass): `sleep 0.1 &` can finish before the kill when the in-host job start is slow. Timing-bound case.
- builtin-fc "fc ignores too many args" and "... with strict_arg_parse" XPASS in g13 and locally: unmarked.
- builtin-type-bash: 9 cases retagged. 2 are `intentional-psbash-command` (`type -p`/`-P`/`-f` on a name that is a
  ps-bash builtin and also a file); 7 are `platform-windows-unix-bin` (they expect `/bin/…` paths).
- `trap … DEBUG` (in a script of 3+ lines; a lone one stays the interactive prompt hook) now runs as bash does:
  - before every simple command with its own `$LINENO`, `[[ ]]`, `(( ))`, assignments and each `&&`/`||` operand
    included (the and/or list is lowered to status tests in DEBUG mode so the action can sit between operands);
  - before each `for` pass, each arithmetic-for clause (init, test, step), and `case`;
  - for every simple pipeline stage, in the parent before the pipeline; never inside `$( )`, a subshell, a compound
    pipeline stage or a function (it ran inside `$( )` and subshells);
  - before `trap - DEBUG` itself; under `set -e` a failing action ends the script.
  - The parser records the start line of `[[`, `((`, `for` and `case` (`Line` on the AST nodes).
  - builtin-trap-bash: 12 DEBUG cases unmarked. Still different: two cases that signal the shell itself (intentional:
    no signals to the host) and "DEBUG trap with ERR trap inside actions".
- The spec recorder put `SH=bash` on a line of its own, so every recorded `$LINENO` in a `$SH` case was one too high.
  It now prefixes `SH=bash; ` on the script's first line, as PsBashExecutor does. 6 builtin-trap-bash and 2
  builtin-trap-err (errtrace, still XFAIL) expectations re-recorded.
- XFAIL count after this pass: 419.

### Progress, 2026-11-11 (seventh pass)

- Unquoted positionals (`$1`, `${11}`, `$0`) are word-split and globbed like any unquoted `$x`
  (`IsPureUnquotedVarWord`): argument lists, `for` lists, array literals, `local $1`, `return $4`. They were passed as
  ONE word: `for w in $1` looped once over `'x=1 y=2'`, and `local $1` made a variable named `x=1 y=2`.
- readonly at run time. The emitter's static set cannot follow `readonly $names` (the names are data) or a
  `readonly` inside a function (its set ended with the function). After either, a plain assignment to a name the
  static set does not hold asks `BashShellState.FirstReadonly` first (`PsBuild.RuntimeReadonlyGuard`).
- `readonly a=(1 2) nv c=(3 4)` marks every name. `declare -p nv` of an unset readonly name prints `declare -r nv`.
- `readonly 1a=3` / `export 1x=2` / `readonly 'a b'` report "not a valid identifier", and the command's redirect
  applies to the report.
- Injection fixed: `names='a;echo pwned'; readonly $names` ran `echo pwned`. The run-time word reached the eval
  unquoted. Each field is now `printf %q`-quoted before the eval.
- `export PATH` (no value) no longer rewrites PATH: the `;`-normalization runs only on a write. An inherited PATH
  that was not in `;` form changed, so `old=$PATH; export PATH; [ "$old" = "$PATH" ]` failed.
- The xcmp scratch tool ran every case in the repo root and left `_tmp/`, `foo.txt`, `F`, `G`, `hello.c`, and others
  there. It now uses a directory per case. The leftovers are untracked and not committed.
- Gaps seen, not fixed:
  - The readonly refusal prefix is `ps-bash: x:`; bash says `bash: line N: x:`.
  - `readonly "x-y+=1"` takes the dynamic-assignment path and reports no identifier error.
- Unmarked 4: builtin-vars "Make an existing local variable readonly", explore-parsing "readonly +", assign
  "readonly $x where x='b c'" and "readonly a=(1 2) no_value c=(3 4) makes 'no_value' readonly". 22 spec files that
  hold readonly, assignment and positional cases show no other change.
- XFAIL count after this pass: 415.

### Progress, 2026-11-11 (eighth pass)

- `command -v`/`-V` resolve names in bash's order (alias, keyword, function, builtin, file) through `type`'s own
  `InvokeBashTypeCommand.Resolve`:
  - `command -v true` / `eval` printed nothing, status 1. They have no PowerShell command behind them, and the
    lookup used `Get-Command` alone, which is now only the `-v` fallback.
  - `-v` prints an alias as `alias ll='ls -l'`.
  - `-V` is `type`'s sentence. It follows "f is a function" with the body, and reports a miss on stderr
    (`command: NAME: not found`).
  - `command -V ls` is now `ls is ls`, as `type ls` prints.
- `builtin NAME` refuses a name that is not a bash builtin ("builtin: ls: not a shell builtin", status 1); it ran
  the file. The emitter and the runtime share one list, `BashBuiltins` (Transpiler).
- builtin-meta: 4 cases retagged.
  - "command -v executable, builtin" is `intentional-psbash-command`: grep and ls are ps-bash's own commands.
  - "doesn't find executable dir" is `platform-windows-unix-bin`: it expects /usr/bin/cat.
  - "builtin" is `platform-windows-paths`.
  - "doesn't find non-executable file" is the new `platform-windows-exec-bit`: chmod +x leaves no mark on Windows.
- Unmarked 3: builtin-meta "command -V", "command -V nonexistent", "builtin ls not found".
- builtin-kill: either "kill -s TERM" or "kill -KILL" fails in-host on a given run (`sleep 0.1 &` ends before the
  kill). This is the timing case from the sixth pass, not a regression.
- XFAIL count after this pass: 412.

### Progress, 2026-11-11 (ninth pass)

- An array assignment now takes the kind bash gives it where the emitter cannot see a declaration:
  - `s=world; s+=(x)` keeps the string as element 0, and `declare -A s+=([a]=x)` keeps it as key "0". The append
    started from an empty array because the scalar lives in `$env:s`. `PsBuild.ArrayAppendBase` reads
    `($s ?? ${env:s})`, and `BashAssoc.Merge` promotes a string.
  - `a=([k1]=v1 [k2]=v2)` with no `declare -A` is an indexed array with arithmetic subscripts (`[0]=v2`); it was an
    associative table. A variable that is associative at run time (declared in a sourced file) still takes string
    keys: `PsBuild.AssocOrIndexed` picks the branch at run time, so a subscript's side effect (`[i++]`) runs once.
  - Mixed subscripts `b=([n]=x [0]=z)` stored the text `[n]=x` (`HasNonNumericSubscript`).
  - `declare -A a; eval 'a=(1 2 3 4)'` takes the words as key/value pairs; eval'd text cannot see the declare
    (`BashAssoc.ListOrPairs`).
- Gap, by design: `ArrayAppendBase` reads `${env:NAME}`, so on Windows `path+=(x)` starts from PATH. This is the
  scalar-read env-case collision in `windows-env-case-collision-plan.md`, not a new one.
- Not done: brace expansion inside a keyed value (`[k]=-{a,b}-`), the indexed↔assoc conversion errors, and
  `a+=([k]=v)` with a non-numeric key on an indexed array.
- Unmarked 4 in array-literal: "[k1]=v1 (BashArray)", "(strict_array) s+=()", "(strict_array) declare -A s+=()",
  "(strict_array) assoc=(key value ...) is not allowed". The other array/assign spec files show no new failures.
- XFAIL count after this pass: 408.

### Progress, 2026-11-11 (tenth pass)

- printf:
  - `%c` writes the first BYTE of its argument (`%c` of "μ" is CE), a NUL for an empty or missing argument, and
    honours the width.
  - `%u %o %x %X` read unsigned 64-bit (strtoumax: `%u -18446744073709551615` is 1) and `%d %i` signed. Out of
    range clamps with `printf: warning: …` and status 0; it was an error with status 1, and `%u` stopped at 2^63-1.
    An invalid number is still the only message, with status 1.
  - `%(fmt)T` honours width and precision.
- A pipeline's status is its last stage's. sed (pipeline mode) and streaming head set their 0 only in
  BeginProcessing, which runs before any upstream stage, so `printf %d x 2>&1 | sed …` was 1. head also stops its
  upstream by throwing, after which its EndProcessing is not the last word, so it sets 0 before the throw. A sweep
  of 30 filters found no other leak. (`fmt` is not implemented: 127.)
- comm reads the pipeline for a `-` operand; it refused pipeline input. `comm - -` follows GNU (second `-` is
  "Bad file descriptor", reads as empty, status 1).
- Unmarked 4 in builtin-printf: "%10.5(strftime format)T", the two integer-overflow cases, "%c unicode".
- Left: `%(fmt)T` and TZ (bash reads TZ once per process: both lines of c43 print the 16th), array-assign
  subscript side effects (`a[a[0]=1]=X` drops `a[0]`) and `declare a[i]=v`, trap-err errtrace in functions and
  LINENO for `x=$(false)`/`[[`/`((` in an ERR trap.
- XFAIL count after this pass: 404.
- Dogfood: on Windows, a glob behind a quoted backslash path (`ls 'C:\dir\'*.txt`) never expanded, so `ls` got
  the literal and listed bare names. `C:\dir\` is escaped into the pattern as `C:\\dir\\`, and BashGlob matched
  the literal backslash as a name character. A literal backslash is now a separator on Windows (no file name can
  hold one), a drive root `C:\` is a root, and the matches keep the separators as typed. Not a spec case.

### Progress, 2026-11-11 (eleventh pass)

- errtrace:
  - `set -o errtrace` reached only the shopt table and `set -eE` dropped the `E` as inert, so the ERR trap never
    ran inside functions, subshells or jobs. Every spelling now sets `__BashErrtrace`, which `$-` (E), `$SHELLOPTS`,
    `shopt -po`, `[[ -o errtrace ]]`, subshell state and job state all see.
  - `[[ -o NAME ]]` was refused as an unsupported operator; the test cmdlet owns it, as for `-v`/`-R`.
  - Inside `$( )` (scripts with an ERR trap and errtrace only), the body turns errexit off in its own scope and keeps
    per-statement checks, so the trap fires there; other scripts keep the unchecked form.
- The ERR trap's `$LINENO` is the line of an assignment, `[[ ]]` and `(( ))` (they kept the previous simple
  command's), and inside `$( )` lines count from where the body starts (`_lineBase`; the body is parsed alone).
  bash reports a multi-line `x=$(\n…\n)` at its last line; ps-bash uses the first.
- A background job is a subshell. Its body had no errexit check (`{ false; echo x; } &` under `set -e` printed x,
  status 0), and the in-process job's pooled runspace had none of the parent's options. The emitter now raises the
  subshell depth for the body (exit/errexit leave the job only). Both job paths get the options from
  `BashStateSnapshot.BuildJobOptions`, written on or off because pooled runspaces are reused, plus the ERR trap
  under errtrace. A `set -u` failure in a job was reported, then rethrown out of EndInvoke at `wait` ("Exception
  calling EndInvoke") and the job's output was lost; the job wrapper now catches it, with status 1.
- Unmarked 6: trap-err c13/c14/c15/c19/c20, errexit "background processes respect errexit".
- Left in trap-err: c7 (subshell pipeline doubling), c17 (redirect failure runs the trap), c21 (PIPESTATUS in the
  trap). fleet g16: Windows background-job spec timed out at 30 s (10 process-mode jobs take ~15 s here: at most 2
  warm spares, ~2.5 s cold start each); Linux GrepStreamingTests hung once (the 1-minute family).
- XFAIL count after this pass: 398.

### Progress, 2026-11-11 (twelfth pass)

- A compound command (`{ }`, `if`, loops, `case`) whose stdout file cannot be opened fails with status 1 and its body
  never runs. bash then runs the ERR trap (and errexit), with `$LINENO` left at the command before it. ps-bash
  never checked a compound's status. `Invoke-BashRedirect` now sets `$global:__BashRedirectFailed` on a failed
  open and clears it when an opened redirect finishes, so a nested failure inside a body that ran doesn't count.
  Under an ERR trap or errexit, a compound redirected to a real file (not `/dev/null`, which emits `>$null`)
  is followed by `PsBuild.RedirectFailureChecked`. With an ERR trap set, the cmdlet no longer throws under
  `set -e`; the statement check runs the trap and then exits.
- A plain command's `PIPESTATUS` (recorded when the script reads it) came after the ERR trap check, so the trap saw
  the previous pipeline's. It is now recorded first.
- Not fixed: a redirect's open error ignores an earlier `2>/dev/null` on the same command
  (`{ :; } 2>/dev/null > /nodir/f` prints it; bash doesn't).
- bash runs the ERR trap twice for a failed multi-stage pipeline whose last stage is a `( )` subshell (also under
  pipefail when an earlier stage failed). A `{ }` or plain last stage runs it once. The statement check now
  repeats the trap check in that case.
- `kill` resolved only its 13 listed names, so `kill -URG $!` / `kill -23` sent TERM and killed the job, and
  `kill -URG $$` with no trap ended the script with 143. Every Linux name and number now resolves.
  SIGCHLD/SIGURG/SIGWINCH are ignored by default: for a job or another process they only check that the target
  exists, and for the shell they do nothing unless a trap is set.
- Unmarked 4: trap-err "trap ERR with redirect", "trap ERR and pipelines - PIPESTATUS difference", "trap ERR
  pipelines without simple commands"; trap "traps are cleared in subshell (started with &)".
- builtin-kill "kill -s TERM specifies the signal name" failed once in a spec run and passed 4 of 4 alone: the
  known `sleep 0.1 &` timing flake.
- XFAIL count after this pass: 394.

### Progress, 2026-11-11 (thirteenth pass: fleet g17 follow-up)

- Machine-dependent results traced to this box having a `C:\dev` folder with stray files `null`, `stdout`, `stderr`
  and `full`. Redirects to `/dev/stdout`, `/dev/stderr`, `/dev/fd/1|2` and `/dev/full` were opened as files under
  `C:\dev`: elsewhere "No such file" (status 1), here the output silently went into a file. `ls /dev/null` worked
  only where `C:\dev\null` existed. `Invoke-BashRedirect` now routes the stream devices to the command's
  stdout/stderr on every platform (on Linux, opening them reached the host process's descriptors). `/dev/full`
  fails the first write with ENOSPC and status 1. `ls` lists the null device on Windows as `crw-rw-rw- root`
  (bash's `-l` size column is `1, 3`; ours prints 0).
- Unmarked bugs "'echo' and printf fail on writing to full disk" and "other builtins fail on writing to full
  disk". Marked redirect "Parsing of x={myvar}": its expectation needs stdout to be a regular file, where each
  `>/dev/stdout` reopens with O_TRUNC; through a pipe bash prints every echo, as ps-bash now does.
- Test harness: on Windows `~`/`$HOME` follow USERPROFILE, so the interactive harness sets it as well as HOME.
  `Interactive_CdHomeRelativeThenPwd` passed only where the real `~/work/beagle-term` existed. A GrepStreamingTests
  timeout now stops the pipeline instead of failing the rest of the class (fleet g16/g17, under load only).
- `dirs`/`pushd`/`popd` print `/` separators on Windows like `pwd`.
- XFAIL count after this pass: 393.

### Progress, 2026-11-12 (fourteenth pass: arrays)

- `${!ref}` naming a computed variable (LINENO, RANDOM, SECONDS, BASHPID, UID, ...) expands to its value.
- An element of an unset array is empty: `${u[1]}` aborted the whole command line with "Cannot index into a null
  array". `${!a[@]-d}` is indirection through the joined elements. A failing indirect expansion abandons any
  command with status 1.
- A subscript that assigns (`a[a[0]=1]=X`, `b[i++]=p`) keeps its write.
- Element initializers (`declare b[2]=y`, `typeset a[3]=4`, `declare -A m[k]+=v`) declare the array and assign the
  element. A redirected compound (`{ ...; } 2>/dev/null`, `if ... fi >f`) runs dot-sourced, so array assignments in
  it survive.
- A subscript is a word: quotes, nested `${...}` and nested `[...]` stay inside it, and its expansions expand
  before the lookup (`${a[$i+1]}`, `${m["$k b"]}`, `ref='A[$key]'; ${!ref}`). A `$( )` in an indirect subscript is
  still never run (policy). A scalar and element 0 are one variable (`a=z` on an array writes `a[0]`;
  `c=s; c[1]=t` keeps `s` as `c[0]`). Arithmetic on an array's bare name (`(( a++ ))`, `let b=7`) writes element 0.
- `declare -A` on an indexed array and `declare -a` on an associative one are refused (status 1). array-literal
  "BashArray cannot be changed to BashAssoc" stays XFAIL: it uses `a` and `A`, one variable in ps-bash
  (intentional-differences.md). A plain `declare` now honors its own redirects.
- The emitter's `_arrayVars` lacked `[ThreadStatic]`: concurrent transpiles (the Core test host) saw each other's
  names, which made the new ArraySubscriptWordTests flaky.
- Not fixed: `declare` inside a function writes the global array (bash makes a local); `declare -a q=3` makes a
  scalar; `declare -ai w; w[0]=1+2` is not arithmetic; keys like `["]"]`; array-literal "[k1]=v1 looking like brace
  expansions"; array-sparse `$0` and `compgen -F` cases. builtin-cd / builtin-dirs XFAILs are Windows path forms.
- Fleet g19 (de8efbcf): Linux Core and Cmdlets green; Security had two 15 s spawn timeouts under load (pass
  locally). Windows real-launcher spec cases in array / var-op-slice timed out under load (pass locally).
- XFAIL count after this pass: 377.

### Progress, 2026-11-12 (fifteenth pass: word expansion)

- Behind `builtin`/`command` a declaration's operands are ordinary words (`builtin export z=$x` exports `z=a` and
  `b`); `declare $y` / `export $y` declare each field.
- An unquoted variable glued to text is field-split in command arguments (`a$x`, `$x.c`, `"q"$x`, `f=$x`); only
  for-lists split it before. `/tmp/a$x` and `/c/...$x` keep their path mapping. A glob word keeps its old form.
- Text glued to a quoted `"${a[@]}"` / `"$@"` goes on the first and last element (`-"${a[@]}"`, `"p${a[@]}q"`,
  `k"$@"`); ps-bash joined the elements into one field. Also in for-lists and declarations (`typeset -"${a[@]}"`).
- A command name set at run time to `builtin` or a declaration (`$b declare f=$x`, `${b}uiltin ...`,
  `myvar=typeset; $myvar x=$x`) runs it; `declare 'b'` declares (it printed the variable).
- ls tests that print the mode column `chmod 644` first (beagle-ab's umask 002 failed G_NoOwnerColumn).
- Not fixed: an arithmetic syntax error (`b[0+]=y`, `$(( 1+ ))`, `(( 1+ ))`) should abandon the rest of the input
  line, status 1 (bash discards the current command list); ps-bash reports it and carries on (assign-extended c36).
  `readonly -p NAMES` / `export -p NAMES` set the attribute and `local -p NAMES` prints locals (c18).
  builtin-trap-err "trap ERR with redirect" fails on an elevated Windows runner, where `> /zz` (the drive root)
  succeeds.
- Fleet g20 (b423e1ce): Linux all green (Differential 2484/0); Windows green but that one case.
- XFAIL count after this pass: 366.
- Fleet g21 (80c30a61) caught a regression on both platforms: `n='a;echo pwn'; builtin declare k=$n` left k empty.
  The cause was older: the declare emitter took any operand containing the letter `n` for a flag (meant for
  `typeset +n`), so `declare k=ab pwn` never split its operands and `pwn` replaced the declaration. Fixed (leading
  `-`/`+` only). A declaration that assigns a name known to be read-only (`declare r=2`, `typeset +r r=2`) now
  reports `declare: r: readonly variable`, status 1, value kept, and the line goes on; it assigned. ble-idioms
  "Issue #1069 [40]" (two cases) pass since the run-time declaration change.
- g21 Windows Cmdlets: five time/streaming tests failed that passed in g19/g20 with no change to that code
  (LsTimeSortTests x3 atime/ctime order, FindPredicateTests.NewerXY, GrepStreamingTests 60 s timeout): suspect the
  runner. builtin-read "read -u 3 -d b -N 6" passes 3 of 5 Windows runs (flaky XPass).
- Not fixed: `typeset -n ref=x; typeset +n ref; echo $ref` prints nothing (bash: x).
- `declare -A d; declare -p d` prints `declare -A d` (it printed `=()`): a table built by `BashAssoc.Unset()` is
  declared-unset until its first write. assign "declare -a arr ... creates an empty array".

### Next workstream: bash's variable-context stack (`[bash_unset]`, about 12 cases)

assign "Test above without 'local'", temp-binding "Temp bindings with local", ble-features x5, ble-unset x5. ps-bash
parks a local's caller value in `$__lsd_NAME` (function scope) and a temp binding's in `$__saved_NAME`; neither is
visible to `unset`, so `unset` always just clears. bash 5.2 behaviour, checked against all 12 cases:

- Each name has a stack of frames: LOCAL or TEMPENV, each with the function depth that owns it and the value it hides.
- A function call's temp binding (`v=t f`) is a TEMPENV frame owned by the CALLED function (depth+1); an eval's or a
  builtin's (`v=t eval ...`) is owned by the current depth.
- `local v` at depth d: top frame LOCAL at d -> nothing new. Top frame TEMPENV at d -> it becomes LOCAL (value kept).
  Top frame TEMPENV below d -> push LOCAL, value inherited. Otherwise push LOCAL, value unset (or the given value).
- `unset v` at depth d: top LOCAL at d -> value unset, frame kept (local-unset). Top LOCAL below d (`unlocal`, a
  helper that unsets its caller's local) or top TEMPENV -> pop the frame and reveal the value it hid. No frame ->
  plain unset.
- Leaving a function or a temp binding restores a frame only if it is still on the stack (a popped frame's value is
  already revealed, and later writes go to the outer variable).
- Subshells `( )` keep the depth: `( unset v )` inside f is a local-unset.

Done (11 of 12; temp-binding "Temp bindings with local" still XFAIL): `PsBash.Cmdlets.VarFrameStack`, one per
runspace, reset by the host per command (`SdkWorker.ResetVarFrames`).
- The function wrapper counts the depth (`PsBuild.FunctionPrologue/Epilogue`).
- `local` registers its frame (`PsBuild.DeclareLocal`; a value-less local takes the value the stack gives it).
- A temp binding on a known function (`_unitFunctions`) or on eval/source/. pushes a frame (`TempFrameKind`).
- `Invoke-BashUnset` pops a frame and reveals what it hid.
- A function's local, and a binding, are restored on exit only while their frame is there
  (`RestoreLocal`/`RestoreTempBinding`/`UnbindSameScope`).
- eval/source/. bindings keep their saved value on the stack's LIFO, not in `$__saved_v`: nested evals share one
  PowerShell scope, and `v=t0 eval 'v=t1 eval ...'` left t0 behind.
- Also fixed: an inlined literal eval body read the pre-binding shadow (`v=t eval 'echo $v'` printed the old v,
  since 82c715f3).

Known gaps:
- A function that only a run-time `source` defines gets no frame for `v=t f`.
- Locals that stay lexical (IFS, loop variables, PS-builtin names) have no frames.

## Suggested order

1. Land A1 and the crash fix once the gate is green.
2. A4 (hangs and host exits), then A6 (PowerShell text leaks).
3. A3 (`argv.py` not found): 17 cases likely behind one or two causes.
4. A2 (environment-name case), as its own gated commit.
5. A7 (decide, then retag) and A8 (launcher options): mostly classification, large marker count.
6. A5 parse groups, largest first.
7. Workstream B Tier 1 by file, then Tier 2; classify Tier 3 last.
