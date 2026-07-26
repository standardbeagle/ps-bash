# Real-world `.sh` corpus sweep (2026-07-25)

Method: transpile every `.sh` file on the dev machine (Git-for-Windows +
`~/.claude` plugin scripts, 100 files) and check two things per file —
`BashTranspiler.Transpile` does not throw, and `[Parser]::ParseInput` on the
result reports zero errors. See the `real-world-sh-corpus-sweep` memory for the
runner snippet.

**Result: 72 → 99 files transpile to valid PowerShell, and PowerShell parse
errors are ZERO.** The single remaining failure is `sqlite3_analyzer.sh`, which
is not shell at all (see Open, below) and which real `bash -n` rejects too — so
every valid bash file in the corpus now transpiles. Failures that do occur are
clean bash-stage `ParseException`s with accurate messages, which is the intended
contract: valid PowerShell or an honest error, never a crash and never broken
output.

The sweep also led — via oracle-diffing each fix rather than stopping at
"it parses now" — to three RUNTIME bugs it does not itself measure, the
largest being that POSIX bracket classes never worked in any command.

Every fix below was confirmed byte-identical against the bash oracle before
landing. Note how many were **silent wrong output** rather than parse errors —
the parse-error count badly understates the damage this sweep found.

## Fixed

| Shape | Symptom | Commit |
|---|---|---|
| `case x in a) ;; *) …` | empty arm body swallowed its own `;;` → parse error on EVERY Bash-tool command (the shell snapshot's `pkill` guard uses it) | `ba91e36` |
| `[[ $x =~ ^(a\|b)$ ]]` | regex truncated to `^` on the `&&` path (**silent**); "Expected `then`" inside `if` | `f501957` |
| `A=(\n x\n y\n)` | `)` unconsumed; `A=(x\ny)` dropped an element and ran `y` as a command (**silent**) | `355677b` |
| `declare -a A=(x y)`, `readonly A=(x y)` | array dropped AND the following command swallowed (**silent**) | `355677b` |
| `local -a A=(…)` | attribute flag taken as a variable name → junk `$-a = $-a` | `355677b` |
| `done <<< "$var"` | here-string dropped, loop read nothing (**silent**) | `2926419` |
| `for x in one` | emitted `foreach ($x in one)` → "one: command not found" | `2926419` |
| `while read a b` | only the LAST name bound; `$a` unset (**silent**) | `2926419` |
| `${value%\'}` | `\X` compiled to `\\X`, so the quote-strip idiom never matched (**silent**) | `986a6d4` |
| `[ -z "${V:-}" ]` | tested the literal text `($env:V ?? "")` — always false (**silent**) | `5ab55e5` |
| `cmd \|\| case … esac` | compound command is not a valid pipeline-chain operand → parse error | `5ab55e5` |
| `[[ ( a && b ) ]]` | grouping parens dropped the group's operands (**silent**) | `d0f9d7a` |
| `[ -o NAME ]` | unimplemented operator emitted adjacent values → parse error; now `$false` + stderr diagnostic | `d0f9d7a` |
| `X="$(grep "a:b" f)"` | assignment split at the colon; `:b f)` torn out of the command (**silent**) | `9af314a` |
| `PATH=~/bin:~/x` | emitted `$HOMEbin` — not a valid variable reference | `9af314a` |
| `grep "^${field}:"` | suffix-less `${name}` before `:` not braced → PS drive-reference misparse | `803645e` |
| `eval "…" \| sort` | a statement-list SIMPLE command is not a valid pipeline stage | `803645e` |
| `X="$(grep "a\"b" f)"` | backtick escape eaten by the enclosing string → unparseable | `ce1dd7d` |
| `X="$(false \|\| echo "")"` | a nested EMPTY `""` closes the outer string → unparseable | `2d21944` |
| `x=$(<file)` | bash's read-a-file shorthand emitted a dangling `\|` | `2d21944` |
| `$(( $(date +%s) + 60 ))` | command substitution as an arithmetic operand rejected the WHOLE file ("invalid arithmetic parameter") | (this sweep's last gap) |

### Runtime bugs found while oracle-diffing the above

| Shape | Symptom | Commit |
|---|---|---|
| `grep '[[:digit:]]'`, `sed 's/[[:space:]]\+//'`, `awk '/[[:digit:]]/'` | POSIX bracket classes never worked in ANY command — .NET reads `[[:digit:]]` as the set `[:digt`, so they matched nothing and reported no error (**silent**) | `ce1dd7d` |
| `printf … \| grep 'a'; printf … \| grep -E 'b'` | binder-swallowed-flag recovery read a DIFFERENT statement's text, so the second grep lost its `-E` and ran as basic regex (**silent**) | `ce1dd7d` |

The POSIX-class bug masked the flag-recovery one: with the classes matching
nothing, a basic-vs-extended regex mix-up made no visible difference.

## Open

One file still fails, and it is not a ps-bash gap.

1. ~~**`run-worktrack-vite-smoke.sh`** — `$(( $(date +%s) + 60 ))`~~ **FIXED.**
   A command substitution used as an arithmetic operand was rejected by the typed
   arithmetic parser ("invalid arithmetic parameter"), failing the whole file.
   `BashArithmeticParser` now lexes `$( … )` (quote- and nesting-aware) and
   `` ` … ` `` into an opaque `ArithmeticExpr.CommandSub` — the `${#arr[@]}`
   pattern — and the emitter's `BuildArithFragments` splices each substitution's
   runtime value in before the string reaches `Invoke-BashArith`, matching bash's
   expand-then-evaluate order. `$((…))` nested inside arithmetic keeps its
   existing paren-grouping path rather than being mistaken for a subshell.
   Four differential cases recorded against the bash oracle
   (`CommandSubstitutionDifferentialTests`, section 7b).

2. **`sqlite3_analyzer.sh`** — NOT a ps-bash bug. The file is a Tcl script
   behind a `#!/bin/sh` shim (`exec tclsh "$0" ${1+"$@"}`), so its body is Tcl,
   not shell. `bash -n` rejects it too ("unexpected EOF while looking for
   matching `''", line 887) — only the message differs. Nothing to fix; the
   corpus is effectively 99 valid files, all of which transpile.

## Test-suite observations

### Fixed (`3d5907a`)

- `FilterLibraryTests.Load_CachesUntilFileMtimeChanges` — asserted `Assert.Same`
  across two `FilterLibrary.Load` calls, but the library keeps a SINGLE-entry
  static cache and xUnit runs classes in parallel, so another class (notably
  `BuiltinFiltersTests`, which loads from a static field initializer) evicted the
  entry in between. ~1 failure in 5 runs, with a misleading diff: the lists have
  EQUAL content, so an identity failure reads as a data mismatch. Every class
  reaching FilterLibrary now shares one non-parallel collection.
- `HangingCommand_TimesOutWithin35Seconds_AndKillsProcessTree` — diffed EVERY
  `pwsh` on the machine against a pre-run snapshot, so any concurrent test project
  or developer shell counted as a leak. Now records only hosts parented by its own
  launcher, collected while that launcher is alive. Verified against four
  deliberately-spawned concurrent `pwsh` processes.

### Timeouts widened (`InteractiveShellHarness`, `CommandAssistProviderTests`)

Four *different* interactive tests each failed exactly once across seven full
runs, always with the same signature — `WaitForPromptAsync timed out after 8.0s`,
`stdout received (0 chars)` — and always passed when re-run alone:
`CtrlC_MidInput_ShellRemainsAlive`, `Unalias_RemovesAlias`, and others. One
systemic cause, not four bugs: the harness's per-command prompt wait was a hard
8 s, while a command's round-trip goes through the IPC socket to the SDK host and
several shell-spawning suites can be competing for the machine.

These bounds are not assertions — every loop exits the instant its condition
holds, so the bound only caps how long a genuine failure takes to REPORT, and the
harness dumps a full transcript when it fires. `DefaultPromptTimeout` is now 25 s
with a `PSBASH_TEST_PROMPT_TIMEOUT_SEC` override, matching the pattern
`DefaultStartTimeout` already used. `CommandAssistProviderTests`' two
process-lifecycle waits went 5 s → 30 s for the same reason (its "fake provider"
is itself a `pwsh` spawn, which alone can exceed 5 s on a saturated box).

Verified by running the whole Shell suite against six deliberately-spawned
CPU-saturating processes — the condition that produced every one of these flakes.

### Fixed (2026-07-26)

- **The escalation-suite flake family** — ~1 failure per full run, a different test each
  time, always `ps-bash.exe did not exit within 30s` with zero partial output. Root cause
  was a PRODUCT bug, not a tight bound: a command whose launcher had been killed kept
  running in the daemon and held the process-wide exec gate, so an unrelated test's
  launcher queued behind it. Full diagnosis and fix in
  [`abandoned-command-holds-exec-gate.md`](./abandoned-command-holds-exec-gate.md).
  Escalation suite is now 22/22 across three consecutive runs, and its runtime dropped
  from ~76 s to ~25–34 s.
- **The `PsBash.Host.Tests`-after-another-suite hang** — `ProcessSpawn` bounded the wait
  for process exit but NOT the stdout/stderr drain that follows it, so a surviving
  grandchild holding the pipe blocked forever with no diagnostic. Now bounded by
  `ProcessSpawn.DrainGrace` and reported as `SpawnDrainTimeoutException`. Same doc.

### Fixed (2026-07-26) — `Harness_IsolatesHome`

Not a flake: a deterministic failure on the sanctioned route. The test read HOME with
`printenv`, which on Windows with Git Bash's `usr/bin` on PATH resolves to the MSYS
`printenv.exe`; that binary rewrites Windows paths through its POSIX mount table, so the
injected `C:\…\Temp\ps-bash-harness-<guid>` came back as `/tmp/ps-bash-harness-<guid>`.
`scripts/test.sh` IS bash, so this fired on every Windows dev run while `dotnet test`
from PowerShell passed. Now read via `Invoke-BashEnv HOME` (no PATH lookup → nothing can
intercept it) and asserted against `harness.TempHome` rather than a second
`Path.GetTempPath()` read. Note the pre-existing `PSBASH_UNIX_PATHS=0` pin in the harness
is NOT the mechanism — the failure reproduces with that variable unset entirely.

### Fixed (2026-07-26) — `WorkerPoolTests.Dispose_DisposesIdleWorkers`

"expected idle workers disposed, got 2". The mechanism was a real (if small) product gap,
not just a test race: `WorkerPool.DisposeAsync` drained only the workers already in
`_idle`. A warmer still in flight observes `_disposed` under `_warmGate` and disposes its
own worker — but on ITS task, after `DisposeAsync` had already returned. So a PowerShell
runspace could briefly outlive its pool, and the count the test read was nondeterministic
(`CreatedCount` is incremented at construction, before the worker reaches the idle queue).

`DisposeAsync` now tracks in-flight warm tasks and awaits them, bounded by
`WarmDrainGrace` (10 s) so a warmer caught mid-`psm1`-import cannot wedge host shutdown.
The contract is now "when `DisposeAsync` returns, every worker the pool created has been
disposed", asserted directly. New test `Dispose_DisposesWorkerStillBeingWarmed` holds a
warmer mid-create, disposes the pool, then releases it; verified red before the fix.

### Fixed (2026-07-26) — `FrecencyStoreTests.Query_SkipsAndPrunesDeletedDirectory`

Full-suite-only failure ("Collection was not empty … `projects\gone`, Score = 4"). The
product is behaving as designed: `SqliteFrecencyStore.DirectoryExistsBounded` deliberately
FAILS OPEN — it probes on a thread-pool task and reports "exists" if that probe misses its
200 ms bound, so a dead network path can neither freeze the keystroke path nor prune a real
directory. Under a loaded box the probe can miss 200 ms purely from thread-pool scheduling,
so the skip-and-prune is best-effort per query by design. The test asserted it as immediate.
It now retries within a bounded window (30 s, 25 ms poll), asserting the real contract —
EVENTUALLY skips and prunes — plus an explicit precondition that the directory is really
gone, so a genuine regression still fails with a clear message.

### Widened with a measurement (2026-07-26) — `SixteenConcurrentConnections`

`HostServerTests.SixteenConcurrentConnections_AllCompleteWithoutInterleaving` hit its own
60 s CTS at 1m8 s in a full-suite run. Not a product hang — every connection was still
progressing, and the exception is the test's `CancelAfter`, not an assertion.

Measured before touching the bound: **9-11 s standalone**, and still only **12 s under six
CPU hogs** — so raw CPU load is not the cause. The cause is that `dotnet test` on the
solution dispatches **one vstest worker per project in parallel** (stated in
`scripts/test.sh`'s own comment), so a full-suite run has all seven suites spawning
processes and runspaces simultaneously. That is creation/IO contention, which CPU hogs do
not reproduce. The test is intrinsically expensive: each connection gets an isolated pooled
worker that is DISCARDED on release, so it is 16 psm1 imports capped at
`Clamp(ProcessorCount, 2, 8)` concurrent, with execution serialized by the exec gate.

Raised to 180 s with a `PSBASH_TEST_CONCURRENCY_TIMEOUT_SEC` override (the
`PSBASH_TEST_PROMPT_TIMEOUT_SEC` pattern). `Task.WhenAll` returns the instant the last
connection completes, so this costs no coverage — it only caps how long a genuine hang
takes to report. Also confirms my WorkerPool change is not implicated: Host.Tests took
1m52 s inside the full suite BEFORE it and 1m58 s after.

### Still open
- **Flakes seen once in a back-to-back suite run, not reproduced since.**
  `CommandAssistProviderTests.GenerateAsync_CallerCancellationKillsProviderProcess`
  (the known family whose waits were already widened to 30 s in `017ffe3`), and three
  differential cases — `Differential_AnsiCQuote_Newline`,
  `Differential_AdjacentQuotes_DoubleThenDouble`,
  `Differential_DoubleBracket_GlobMatch_Matches`.

  Status of the differential three: **unreproduced, mechanism unknown.** Since seen they
  have passed a clean full-suite run (288/288) and six consecutive filtered runs. They
  appeared only when `PsBash.Shell.Tests` had run immediately before in the same shell —
  which points at cross-suite contamination (a leftover host, or process-global `$env:` /
  cwd state) rather than a bound, since these compare OUTPUT rather than waiting on a
  timeout. Do NOT widen anything here.

  Note there is no observability gap to close first: `AssertOracle.AssertMatches` already
  throws a full bundle (input script, expected vs actual stdout/stderr, exit code,
  transpiled PowerShell, and a line diff). That detail WAS emitted in the failing run and
  was lost only because the console output was grep-filtered and then overwritten — so the
  next occurrence is diagnosable as-is, provided the raw log is kept.

## Not a product bug

- `[[ $x =~ ^a\sb ]]` — bash's ERE has no `\s`; .NET regex does. An engine
  difference, same class as the `PIPESTATUS` entries in `known-issues.md`.
- `$HOME` renders as a Windows path (`C:\Users\…`), so a value built from `~`
  differs textually from bash even when structurally correct.
