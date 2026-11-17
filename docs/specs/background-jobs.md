# Executors: hub daemon + process-isolated execution (background jobs, foreground commands)

Status: **DESIGN; implementation started** (2026-10-22): phase 1 = `JobProcess` (§6). Originally written for
`&` only; generalised to *all* execution in §4.2 (the daemon becomes a pure hub). Supersedes the in-process runspace-pool runner
(`Invoke-BashBackground`, `src/PsBash.Module/PsBash.psm1`). Companion to
[host-lifecycle-contract.md](host-lifecycle-contract.md) (daemon, execution lease, executor slots) and
[pty.md](pty.md) (interactive hosts).

## 1. Problem

bash implements `cmd &` with `fork()`: the child gets a **copy** of the whole shell (variables,
functions, cwd, options, traps, open fds) and nothing it does is visible to the parent. ps-bash runs the
job as a PowerShell pipeline in a **second runspace of the same process**. Everything a bash job is
supposed to have a private copy of is shared or missing:

| Behaviour | bash | ps-bash today (after the 2026-10 patch: env restored at job end) |
|---|---|---|
| Job writes `x` ; parent reads after `wait` | parent's value | parent's value (patched) |
| Parent reads `x` while job runs | parent's value | **job's value** (shared `$env:`) |
| Parent changes `x` after launching | job still sees the old value | **job sees the new value** |
| Job calls a shell function | works | **fails**: "f is not recognized" (pool runspace never saw it) |
| `{ x=1; } \| cat` | `x` unchanged | **changed** (same mechanism, pipeline stages) |
| `$!`, `kill %1`, `kill $!` | real pid, real signal | synthetic id 1000+, `PowerShell.Stop()` |
| Job output | live, interleaved | live and interleaved for a job whose output is the shell's own (§12); **buffered until `wait`** for a job under a redirect, pipe, capture, function, or eval |
| `wait`/`$?` | job's status | job's status (patched) |
| `cmd &` then script/`-c` ends | job keeps running | **killed** (`Remove-BashBgState` at runspace discard) |

### 1.1 Why the daemon made this a correctness bug, not a quirk

The daemon (`ps-bash-host`, `Lifetime.Daemon`) serializes command execution (originally one process-wide gate in `SdkWorker`, now the per-host
execution lease in `HostServer`), **because env and cwd are process-global** (host-lifecycle-contract.md,
"Isolated runspaces, one command per host"). A background runspace runs **outside that lease**, so it:

1. mutates `$env:` / cwd **while another connection's command is executing under the gate** — a job from
   session A can change the variables session B's command is reading (cross-session corruption, not just
   a parent/child race);
2. outlives its command's gate hold, so its writes land between unrelated later commands;
3. is torn down by `Remove-BashBgState` when the pooled runspace is discarded — so the daemon's
   "discard on release" isolation silently turned every unwaited `&` into a kill.

The daemon's own isolation story (one runspace per connection, discarded on release) is only true for
foreground work. `&` was never moved onto the same footing. This design does that.

## 2. Goals / non-goals

Goals
- A job has **fork semantics**: a snapshot of shell state at the `&`, with no write path back.
- A job never executes inside the daemon process, so it needs no exec gate and cannot touch any other
  session's state.
- Real OS pids for `$!`/`jobs -p`/`kill`; signals and tree-kill work on Windows, Linux and macOS.
- Live output relay; correct `wait`, `$?`, `set -e`, `wait -n`, `jobs`, `disown`, `fg`, `bg`.
- **No orphaned or leaked processes**: on command end, client disconnect, daemon exit, daemon crash,
  launcher kill, host restart. Each of these has a defined, tested outcome (§6).
- Common cases (`sleep 5 &`, `./server &`, `make &`) stay cheap.

Non-goals
- Sharing live fds/pipes across the fork (`exec 3>f; cmd 3>&- &` subtleties) beyond redirections the
  emitter can express; `coproc` (separate work item).
- Making the *foreground* subshell/pipeline-stage case (`( x=2 )`, `{ x=2; } | cat`) concurrent. Subshells
  already work by save/restore (`PsBuild.ShellStateScope`) because nothing runs at the same time. The
  pipeline-stage leak in §1 is a separate, smaller fix that reuses that mechanism.

## 3. Semantics contract (what "correct" means)

1. **Snapshot at `&`.** State is captured on the thread that executes the `&` statement, before it
   returns. Later parent writes are invisible to the job and vice versa.
2. **Identity.** `$$` stays the *parent shell's* id in the job; `$BASHPID` is the job's own. `$!` in the
   parent is the job's OS pid.
3. **stdin.** A non-interactive job's stdin is `/dev/null` (NUL) unless redirected; an interactive one
   inherits the terminal (and gets SIGTTIN semantics only where the OS has them).
4. **Status.** `wait N` returns the job's exit status (128+signal on kill); unknown pid → 127;
   `wait` with no args → 0; `wait -n` → first finisher. `set -e` sees that status.
5. **Lifetime.** A job is not waited for at command end (bash behaviour). Output produced after the
   launching connection is gone is discarded (as with a closed pipe), unless the job redirected it. This
   is a deliberate improvement over today's kill; see Open questions Q1.
6. **Signals.** `kill`, `kill %n`, `disown`, `trap ... EXIT` in the job, SIGHUP on shell exit only for
   interactive hosts with `huponexit`.

## 4. Architecture

```
parent runspace (inside daemon or interactive host)
   │  `&` statement (emitted: Invoke-BashBackground { body })
   │   1. BashStateSnapshot.Capture()  ── text prelude + env block         (§5)
   │   2. ExecutorPool.Lease()        ── idle, scrubbed executor process (§4.2, §7)
   │   3. send Mode.Command(prelude + body, environment, session) over a private AF_UNIX endpoint
   ▼
job host  = ps-bash-host --job --launcher-pid=<parent host pid>      (own process, own runspace,
   │          own env/cwd, own exec gate; never touches the parent)
   │   runs the body, streams STDOUT/STDERR/EXIT frames
   ▼
JobRegistry (parent host)  ── per-job: OS pid + start-time identity, connection, TaskCompletionSource<int>,
                              relay pump → the owning connection's output sinks (live)
```

### 4.2 Generalisation: the daemon is a pure hub, every command runs in an *executor*

A job host is just an **executor**: a `ps-bash-host --executor` process that runs one command at a time
in its own env/cwd. Once that exists, nothing needs to run inside the daemon:

```
launcher ──ACQUIRE(session, buildId)──▶ HUB (daemon: no user state, no exec gate)
                                          │ picks an idle executor (or spawns one), leases it
launcher ◀──── executor endpoint ─────────┘
launcher ══ Command / STDIN / output frames, directly ══▶ EXECUTOR  (own process)
                                          hub: pool, health, idle shutdown, job registry, reaper
```

- **Broker, not proxy.** The hub never relays command traffic; it hands the launcher a private endpoint.
  Warm path cost = one tiny `ACQUIRE` round trip on a local socket (sub-millisecond) plus one connect.
  No fd passing (Windows AF_UNIX fd duplication is not something to depend on).
- **The exec gate disappears.** The process-wide gate existed only because env/cwd are process-global;
  with one command per host process the host keeps just a one-permit lease. Concurrent launchers run in
  parallel on different hosts. The nested-host machinery (`PSBASH_INSIDE_HOST`, `-nested<d>`
  endpoints) existed to dodge that gate and is deleted. **As built (Phase 6):** no hub/ACQUIRE broker; the
  host itself refuses a command while busy and the launcher spills to the next slot endpoint
  (`executor-slots.md`).
- **Crash containment.** A runaway or poisoned command costs one executor (kill tree, respawn). The hub
  holds no user state, so "host must survive any input" reduces to "the hub must survive"; the
  `NestingGuard`/watchdog logic stays in the executor as defence in depth.
- **Executors are reusable, not single-use.** A process has exactly one user at a time, so state cannot
  interleave. After a command the executor **scrubs** (environment replaced by the next request's block —
  the existing "R06 environment reset" in `SdkWorker.RunCommand` — cwd reset, shell globals reset, a fresh
  runspace taken from the executor's own in-process warm spare) and returns to the hub's idle list. It is
  **retired** (killed, not recycled) after: `PSBASH_EXECUTOR_MAX_COMMANDS` (default 200), any
  cancel/timeout/stuck/OOM/unclean exit, RSS above `PSBASH_EXECUTOR_MAX_MB`, a build-identity change, or a
  command that started a detached job (the job keeps the executor, see §6.2). Retire-and-respawn happens off
  the critical path.
- **Fresh state per command by default** (bash `-c` semantics; fixes the documented leak where `export X`
  survives into the next `-c`). Opt-in `PSBASH_SESSION_EXECUTOR=1` pins one executor to a session and skips
  the scrub for harnesses that rely on cwd/export persistence.
- **Jobs are executors.** `&` leases an executor exactly like a foreground command, preloaded with the
  snapshot (§5); when the job ends the executor is scrubbed and returned to the pool.

Why a **separate host process** and not a thread/runspace/AppDomain: env and cwd are process-global on
every OS (`Environment.SetEnvironmentVariable`, `chdir`), and bash scalar variables *are* env vars here
(`BashVariableStore`). Only a process boundary gives a private env and cwd. A per-job `Env:` provider
overlay was evaluated and rejected: native children and the C# cmdlets read the real process
environment, so the overlay would be invisible to exactly the code that needs it.

Reuse, not new machinery: the job host is the existing `ps-bash-host` binary started with
`Lifetime.PerInvocation`-style semantics (private endpoint, `--launcher-pid`, owned by its spawner),
speaking the existing framed protocol (`Mode.Command` already carries an `environment` block, a session
id, and `StdinFollows`). New protocol surface is limited to: a `--job` flag (suppresses interactive
setup, applies the snapshot), and an optional `STATE-FILE:` request field for large snapshots (the wire
line cap is `HostProtocol.MaxLineBytes` = 1 MB).

### 4.1 Native fast path (phase 2)

If the job body is one simple command that resolves (at run time, via command resolution, after
considering functions/aliases) to an **external program**, skip the job host: spawn the program directly
with the snapshot environment, cwd and redirections, under the same process-management primitives (§6).
`sleep`, servers, `make`, `ffmpeg` etc. then cost one `CreateProcess`/`posix_spawn`, not a host. The
snapshot still comes from the same capture code, so behaviour is identical by construction.

## 5. State snapshot

`BashShellState` (`src/PsBash.Cmdlets/BashShellState.cs`) already enumerates what a *subshell* must
not leak; the snapshot is the same inventory, serialised instead of saved in memory. Inventory
(extends `BashShellState.GlobalNames` and is guarded by a test that fails when new global shell state
appears without an explicit snapshot/not-snapshotted decision — the `BashVariableStoreGuardTests` pattern):

| State | Carried by |
|---|---|
| Scalars + exports | `environment` block (`BashVariableStore.Enumerate()`); host-config vars (`PSBASH_*`) are excluded/overridden, as `SdkWorker`'s environment reset does today |
| Arrays, associative arrays, `declare` attributes (`-i -r -x -l -u -n`) | prelude statements built with `PsBuild.SingleQuote` (never hand concatenation) |
| Functions | `function name { <ScriptBlock.Ast.Extent text> }` for every non-module function; **all** are sent (static analysis cannot see `eval`, indirect calls, `source`d files) |
| Aliases (`shopt expand_aliases` honoured) | prelude |
| Positional parameters, `$0`, `$-`, `IFS`, `set` options (`errexit`, `nounset`, `pipefail`, `xtrace`), `shopt` table | prelude via the `GlobalNames` list |
| cwd | request field; job host `chdir`s before the body |
| `$?`, `$_`, `$$`, `$BASHPID` | prelude; `$$` fixed to the parent's id |
| Traps | **not** inherited (bash resets caught traps in subshells); ignored (`trap '' SIG`) are |
| Open fds beyond 0/1/2 | not supported v1; emitter rejects with a clear error rather than silently dropping |

Size: functions + arrays can exceed 1 MB for large scripts → spill to a 0700 temp file in the runtime
dir (`PsBashRuntimeDirectory`), referenced by `STATE-FILE:`; deleted by the job host after load and by
the registry on job end.

A snapshot that cannot be serialised is a hard error (`bash: cannot run in background: <reason>`, exit
1) — never a silent drop. During rollout `PSBASH_BG_MODE=inproc` keeps the legacy runner as an escape
hatch (§10).

## 6. Process management (cross-platform)

This is the part that has historically gone wrong in this repo (see memory notes: Windows has no SIGHUP
or process-group inheritance; `Process.Start` always inherits handles on Windows; daemon pipe-inheritance
hang). Rules below are requirements, each with a test (§9).

### 6.1 Primitive matrix

| Concern | Windows | Linux | macOS |
|---|---|---|---|
| Spawn | `WindowsHostSpawn` (CreateProcessW + `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`, no handle inheritance); flags `CREATE_NO_WINDOW`, `CREATE_NEW_PROCESS_GROUP`, `CREATE_SUSPENDED` | `posix_spawn` with `POSIX_SPAWN_SETPGROUP` (own process group); CLOEXEC everywhere | same as Linux |
| Contain the tree | **one Job Object per job**, assigned while suspended, then resumed (no race where a grandchild escapes before assignment); `KILL_ON_JOB_CLOSE`; `BREAKAWAY` *not* allowed | process group (`killpg`); optional cgroup-v2 / `PR_SET_CHILD_SUBREAPER` is a future hardening | process group (`killpg`) |
| Die with the daemon | Job Object held by the daemon (closing the handle kills the job) **and** `ParentDeathWatcher` in the job host (`--launcher-pid`) | `prctl(PR_SET_PDEATHSIG)` is unavailable through `posix_spawn`; use `ParentDeathWatcher` (poll 200 ms) + daemon graceful-shutdown `killpg` | `ParentDeathWatcher` + graceful-shutdown `killpg` (no PDEATHSIG on macOS) |
| Graceful stop (`kill`, TERM) | `CTRL_BREAK_EVENT` to the job's process group, grace period, then `TerminateJobObject` | `killpg(pgid, SIGTERM)`, grace, `SIGKILL` | same |
| Hard stop | `TerminateJobObject` | `killpg(pgid, SIGKILL)` | same |
| Stop/continue (`Ctrl-Z`, `bg`) | no OS equivalent: `NtSuspendProcess` over the job's processes (documented limitation: not for protected processes) | `SIGSTOP`/`SIGCONT` | same |
| Exit status | `GetExitCodeProcess`; killed-by-us reported as 128+15/137 to match shells | `waitpid` via .NET `Process`; signal → 128+n | same |
| Reap | `Process` handle disposed on completion; no zombies on Windows | `Process.WaitForExitAsync` on every job so SIGCHLD is consumed — **no zombies** | same |
| Identity (PID reuse) | `ProcessIdentity` (pid + creation time) checked before every signal/kill and on registry reload | pid + `/proc/<pid>/stat` starttime | pid + `proc_pidinfo` start time |
| Window flashes | `CREATE_NO_WINDOW` (see memory: codex-window-flashes) | n/a | n/a |

Everything above is wrapped in one `JobProcess` abstraction (`PsBash.Transpiler/Processes/JobProcess*.cs`,
namespace `PsBash.Core.Runtime.Processes`; it lives in the leaf assembly so `PsBash.Cmdlets` can use it) with
`Spawn / Signal(sig) / Kill(tree) / Suspend / Resume / WaitForExit / Identity`, one implementation per OS
behind a shared contract test-suite. It is THE process-tree primitive: every child a cmdlet starts
(`ChildStdio.Start`, so `BashRuntime.RunChildProcess` and `StartChildProcessStreaming`) goes through it too, with
pipes as its stdio, `BREAKAWAY` allowed and no new console process group. The foreground-child invariant - a stop
ends the whole tree and no wait depends on a pipe - is `stdio-messaging.md` §4.6.

### 6.2 Lifecycle table — every way a job can end, and what happens

| Event | Outcome |
|---|---|
| Job exits | status stored; registry entry kept until `wait`/`jobs` reports it (bash "Done"); handles/state-file released at once |
| Launching **command ends** (script `exit`, `-c` returns) | job keeps running, detached from the connection; output discarded; stays owned by the daemon (daemon idle-shutdown is deferred while jobs are alive, bounded by `PSBASH_JOB_MAX_DETACHED_SECS`) |
| **Client disconnects** (Connection watchdog fires) | same as above — a dead launcher is not a dead job (matches `nohup`-less bash on a closed pipe: job gets EPIPE on write) |
| **Command cancelled/timed out** (`StuckCommandWatchdog`, tool timeout) | jobs of that connection are tree-killed: a cancelled command must not leave work behind |
| Session anchor exits (`SessionAnchorWatcher`) | the 5 s idle shortening applies only when no jobs are alive; with jobs, daemon waits up to the detached cap, then tree-kills and exits |
| `ps-bash host restart` / obsolete-build replacement | graceful: SIGTERM/CTRL_BREAK to all jobs, grace 5 s, hard kill; registry cleared |
| Daemon **crash / kill -9 / TerminateProcess** | Windows: Job Object handle closes → OS kills the trees. POSIX: job hosts see the parent gone via `ParentDeathWatcher` (≤200 ms) and tree-kill themselves; plus the reaper below |
| Job host **crash** | parent sees EXIT-less EOF → status 255, "job host terminated unexpectedly" on stderr, entry completed, tree-killed |
| Reboot / logout | nothing survives; sidecars are stale and reaped |

**Orphan reaping.** Each live job writes `<runtime dir>/jobs/<daemon-endpoint-hash>.<jobid>.json`
(pid, start-time identity, owner uid/SID, parent daemon pid+identity), 0600, beside the host sidecars.
`StaleArtifactReaper` is extended with the same compare-then-delete rule it uses for host sidecars: a job
whose recorded parent daemon is provably dead **and** whose own pid still matches the recorded identity
**and** whose owner is the current user is killed (tree) and its sidecar removed; anything that fails
a check is left alone ("Unsafe to touch"). `ps-bash host reap` runs it on demand; daemon startup runs it.

### 6.3 Handle, fd and stdio hygiene

- Job hosts are spawned through `WindowsHostSpawn` on Windows (explicit handle list). Never
  `Process.Start` for a long-lived child (documented leak: pipe-inheritance hang).
- Host stdio of a job host is redirected to pipes owned by the daemon and drained to null, exactly as
  `IpcWorker.SpawnAndWaitAsync` does for daemons; job I/O travels on the framed IPC connection, not on
  the process's stdio.
- The daemon's listening sockets and the launcher's std handles must be CLOEXEC / non-inheritable at
  spawn time; a test spawns a job and asserts the job host holds none of them.

### 6.4 Resource limits

- `PSBASH_JOBS_MAX` (default 32) concurrent job hosts per daemon; the 33rd `&` fails like a failed fork
  (`bash: fork: Resource temporarily unavailable`, status 1) instead of exhausting memory
  (a host is tens of MB of RSS). `HostMemoryGuard` already exists for the host process; the pool
  consults it before warming a spare.
- Output relay is back-pressured per job (bounded queue, same `IpcOutputQueue` rules): a job producing
  output with nobody reading cannot grow the daemon without bound; the existing stall timeout applies.
- Detached-job cap `PSBASH_JOB_MAX_DETACHED_SECS` (default 0 = unlimited while the daemon lives, but the
  daemon exits with its session anchor).

## 7. Performance — warm execution must not regress

### 7.1 Baseline (2026-10-22, Windows 11, installed release, machine at ~80 % CPU — treat as upper bounds)

| Measurement | min / p50 / p90 (ms) |
|---|---|
| floor: `cmd /c exit` | 15 / 19 / 59 |
| floor: `pwsh -NoProfile -c 1` (cold PowerShell) | 500 / 571 / 599 |
| launcher alone: `ps-bash --version` | 97 / 123 / 220 |
| warm daemon, back-to-back: `ps-bash -c 'echo hi'` | 430 / 516 / 578 |
| warm daemon, 2 s idle gap between calls | ≈ 246–370 |

Reading: the launcher costs ~100–120 ms; the rest (~130–250 ms) is connect + transpile + execute + release.
**Back-to-back calls are 150–250 ms slower than spaced calls**: discarding the used runspace starts
re-warming a replacement (psm1 import) *while the next command runs*, so warm-up competes with the
foreground command for CPU. That is the thing to fix, and the executor model must not make it worse.

### 7.2 Design rules that protect the warm path

1. **Reuse, don't respawn** (§4.2). A recycled executor has zero warm-up cost; process spawn + psm1 import
   (~1 s+ CPU) only happens when concurrency exceeds the idle pool or an executor is retired.
2. **Spares are paid for off the critical path and at low priority.** Replacement warm-up runs at
   below-normal priority (`SetPriorityClass` / `nice`), is delayed until the pool has been quiet for
   ~50 ms, and never starts a spare while a lease is in flight. Burst arrivals are served from the idle
   list (`PSBASH_EXECUTOR_WARM`, default 2) before any spawn.
3. **In-executor warm runspace.** Each executor keeps one pre-imported spare runspace, so the scrub is a
   swap, not an import. This removes the back-to-back penalty in §7.1 even for the current design.
4. **Broker round trip only.** `ACQUIRE` reply carries the endpoint *and* the executor's build identity, so
   the launcher skips the separate health handshake it does today.
5. **Launcher-side:** no change in this design; the ~100 ms launcher floor is tracked separately (AOT
   startup, module probe) and is where the next big win is.
6. **Native fast path** (§4.1) for single external-program jobs: one `CreateProcess`/`posix_spawn`.
7. **Memory guard.** `HostMemoryGuard` gates spare creation; idle executors above `PSBASH_EXECUTOR_WARM`
   are reaped after `PSBASH_EXECUTOR_IDLE_SECS` (default 60) so a quiet session returns to hub + 1 spare.

### 7.3 Acceptance bars (measured with the harness `scripts/bench-warm.ps1`, same machine, A/B vs the release)

- warm `-c 'echo hi'`, back-to-back p50 **≤ 0.8×** today's; spaced p50 **≤ 1.05×** today's;
- 4 concurrent launchers: wall time ≤ 1.3× a single command (today: ≥ 4× because of the gate);
- `{ :; } & wait` within the same budget as a foreground command; `sleep 1 & wait` ≤ `sleep 1` + 50 ms;
- steady-state RSS: hub + `WARM` executors ≤ today's single host + 60 MB;
- cold first call (no daemon) not worse than today.

## 8. Shell surface changes

| Item | Change |
|---|---|
| `Invoke-BashBackground` | becomes a thin call into `JobRegistry` (C#); emitter output unchanged |
| `$!`, `$BASHPID`, `jobs -p` | real pids |
| `wait [-n] [-f] [id…]`, `wait %n`, `wait` | registry-backed; real exit statuses; `127` for unknown |
| `jobs [-lp]`, `fg`, `bg`, `disown [-ah]`, `kill [-s sig] %n \| pid` | `kill` of a registry pid routes through `JobProcess.Signal` (tree-aware); other pids use the OS directly |
| Pipeline stages with side effects (`{ x=1; } \| cat`) | out of scope here; fix with `ShellStateScope` per stage (separate task) |
| `Remove-BashBgState` / pool teardown in `SdkRunspace` | replaced by `JobRegistry.OnConnectionEnd(policy)` per §6.2 |

## 9. Test plan (every bullet is a named test)

Differential (bash = oracle, `AssertOracle`): the table in §1 verbatim; function-in-job; array/assoc in
job; `set -e` + `wait`; `wait -n`; `$!`/`kill $!`/`kill %1` observable effects; subshell-with-`&`;
`cd` in job; positional params in job; IFS in job; `trap` inheritance; job stdin is /dev/null;
`$$` vs `$BASHPID`.

Process management (run on Windows, Linux, macOS CI; each asserts via OS process queries, not logs):
- kill tree: job spawning grandchildren; `kill $!` leaves **zero** descendants (poll with deadline).
- daemon killed hard (`TerminateProcess`/`SIGKILL`): all job trees gone within 2 s.
- launcher killed / client disconnect: job survives; command cancel: job tree dies.
- no zombies after 100 short jobs (POSIX: scan `/proc` or `ps` for `Z`); handle count flat on Windows.
- PID reuse: forged registry entry with a live-but-different process is **not** killed.
- orphan reaper: stale sidecar for a dead parent kills the orphan; foreign-owner sidecar is untouched.
- handle hygiene: job host holds no daemon socket / launcher std handle (extends
  `Daemon_Spawn_DoesNotInheritStrayLauncherHandles`).
- fork-bomb guard: `for i in $(seq 100); do sleep 30 & done` → exactly `PSBASH_JOBS_MAX` succeed, rest
  fail with status 1, daemon RSS bounded.
- cross-session isolation (the §1.1 bug): session A job loops writing `x`; session B's command asserts
  `x` unchanged for its whole run.
- exec-gate independence: a 30 s job does not delay another connection's command.
- Windows: no console window created; `CTRL_BREAK` graceful path and `TerminateJobObject` fallback.
- Inventory guard: new `$global:` shell state without a snapshot decision fails the build.

Pester (release gate): direct-cmdlet `Invoke-BashWait`/`Invoke-BashJobs` surface in module (non-daemon) mode,
which needs the in-process fallback (§10) or a module-mode job-host spawner.

## 10. Rollout

0. **Measure**: baseline recorded in §7.1; add `scripts/bench-warm.ps1` and re-run after every phase.
1. `JobProcess` + contract tests on all three OSes (no behaviour change).
2. `BashStateSnapshot` + inventory guard; `--job` host mode; `JobRegistry`; flag
   `PSBASH_BG_MODE=process` opt-in; differential tests green.
3. Warm spare + native fast path; perf bar met.
4. Orphan reaper + lifecycle table tests; interactive-host integration (fg/bg/Ctrl-Z).
5. Default flips to `process`; `inproc` retained one release behind `PSBASH_BG_MODE=inproc`, then deleted
   along with the job runspaces (`BashJobRunspaces`) and `Remove-BashBgState`.
6. **Foreground migration** (§4.2) — DONE as `executor-slots.md` (host lease + BUSY + launcher slot spill
   instead of an `ACQUIRE` broker): `_globalExecGate`, the nested-host endpoints and `PSBASH_INSIDE_HOST`/
   `PSBASH_NEST_DEPTH` are deleted. Done before the §7.3 bars were met, on the owner's explicit approval; the
   default background mode stays `inproc`.

Module mode (plain `Import-Module PsBash`, no daemon): the module spawns job hosts through the same
`JobProcess`; if the host binary is not discoverable it falls back to `inproc` with a one-time warning.

## 11. Open questions

- **Q1 — detach vs kill at command end.** bash lets the job continue; today's ps-bash kills it. Agent
  harnesses may *rely* on cleanup. Proposal: detach (bash-correct) with the daemon's session-anchor
  shutdown as the backstop, plus `PSBASH_BG_ON_EXIT=kill` for harnesses that want the old behaviour.
- **Q2 — fd inheritance (`3>file` into jobs).** Rejecting at emit time is safe but may break scripts that
  pass fds to jobs; survey the real-world `.sh` corpus before deciding.
- **Q3 — cgroup/subreaper hardening on Linux** for jobs that `setsid` out of the process group.
  Windows Job Objects already prevent escape; POSIX double-fork daemons can. Worth it?
- **Q4 — Windows suspend for `Ctrl-Z`.** `NtSuspendProcess` is undocumented; acceptable, or limit
  `Ctrl-Z` job control to POSIX and document it in `intentional-differences.md`?

## 11. Implementation status

Shipped (uncommitted at time of writing):

- **Phase 1** `JobProcess` (Windows `CreateProcessW` + kill-on-close Job Object; POSIX `posix_spawnp` + own pgroup), contract tests.
- **Phase 2/3 (jobs only)** `cmd &` runs in a private `ps-bash-host` (`BashJobProcess`), seeded by `BashStateSnapshot`
  (cwd, shell globals, shopt, functions, arrays; scalars travel as the request environment). `$!` is the real pid,
  `kill $!` kills the tree and `wait` returns 128+signal, `wait` returns the job status and honours errexit,
  parent/child assignments are isolated both ways. `JobHostSpares` keeps up to 2 warm hosts (adaptive, 60 s expiry),
  pre-warmed when a command contains `Invoke-BashBackground`.
- **Opt-in, default NOT flipped**: `PSBASH_BG_MODE=process` selects it; the default stays `inproc`.
  `scripts/bench-warm.ps1` (Debug build copied to a scratch dir, loaded machine, p50 ms, inproc -> process), after the
  prelude fix below: `sleep 1 & wait` 1391 -> 1484 (bar: fg `sleep 1` 1292 + 50), `{ :; } & wait` 320 -> 2595
  (min 388: bimodal, a miss when back-to-back jobs outrun the ~2 s spare refill), back-to-back echo 276 -> 278,
  spaced echo 210 -> 273, 4 concurrent 1761 -> 2164 (1.23x). The cold-miss case still fails the section 7.3 bar.
- **Prelude fix (the big one)**: the job prelude carried every runtime helper function (115 KB, ~1 s to execute in the
  job host). `$global:__BashBaselineFunctions` (captured at the end of the psm1) lists the runtime's own functions; the
  snapshot skips them, so only user functions travel (1.6 KB, job execution ~1000 ms -> ~45 ms).
- Spares are warmed with a real `Invoke-Bash*` call before they count as ready (`JobHostSpares.StartWarmAsync`).
- Guards: `ShellStateInventoryGuardTests` (every shell-state global is carried or listed as not inherited).

Known gaps / not done:

- A cold (missed) job start is ~2.5 s on a loaded machine; the numbers above are Debug-build and load bound, so re-run
  `scripts/bench-warm.ps1` A/B on a quiet machine with a release build before deciding the flip.
- Job output is live (§12) for a terminal-bound job; other jobs stay buffered until `wait`/completion. Jobs are still
  killed at command end in `-c` mode (Q1 detach not implemented).
- `PSBASH_JOBS_MAX` (default 32) is enforced in `BashJobProcess.Start`. No POSIX hard-kill containment (Q3), no native fast path.
- `JobHostSpares.Prewarm(n)` sizes the spare pool from the number of `Invoke-BashBackground` call sites in the command
  (bounded by `MaxSpares`), so a command with several `&` starts its spares before the first one is reached. Effect on the
  cold-miss bar is NOT yet measured: the only run after the change was on a saturated machine (echo p50 1023 ms vs 278 ms)
  and is not comparable. Re-run `scripts/bench-warm.ps1` on a quiet machine before deciding the default flip.
- A/B on the same Debug binary, back to back, machine at ~55% CPU (p50 ms, inproc -> process, 8 runs each):
  `{ :; } & wait` 935 -> 3505 (3.7x; bar 1.05x), `sleep 1 & wait` 1898 -> 2287 (fg `sleep 1` 1848, bar +50 ms),
  4 concurrent 7282 -> 4139 (batch wall, noisy). Process mode still misses the cold-miss bars even with the
  spare pool sized from the command, so the default stays `inproc` and Phase 6 (which this design gates on the
  bars) is not started. The remaining cost is a job host's start (process + runspace), not the prelude.
- Orphan tests that exist: command-end leaves no job process, and a launcher killed without cleanup leaves none
  (Windows; `BackgroundJobProcessTests`), 10 short jobs are all reaped, and a recorded identity does not match a
  different process on the same pid (`JobProcessTests`). Not written: a 100-job soak and the POSIX hard kill.
- Phase 6 is IMPLEMENTED in a simpler shape than §4.2: `HostServer` execution lease, `ExecBusyExitCode` (76)
  before STARTED, launcher spill to `-x<k>` slot endpoints (`IpcWorker.YieldToAnotherSlotAsync`), nested
  launchers detected by `ProcessAncestry.IsDescendantOf`. `_globalExecGate` and the nested-host machinery
  are removed. NOT done: the hub/ACQUIRE broker, executor scrubbing/leasing, and the POSIX hard-kill test.

## 12. Live job output (default, no extra process)

A job's stdout and stderr reach the shell's own streams while it runs. They are interleaved with the foreground
command's output in production order, memory is bounded, and nothing is printed twice. This works in both job
modes (job runspace, §12.5, and job host process) and adds no process.

The general layer it is built on (channels, fds, open/close, credit) is described in
[stdio-messaging.md](stdio-messaging.md). This section covers the job-specific rules.

### 12.1 Path

1. **Emitter.** `PsEmitter.CollectLiveJobs` (`PsEmitter.LiveJobs.cs`) marks the jobs of a unit whose output is the
   shell's own, and those jobs are emitted as `Invoke-BashBackground -Live`.
   - A job is marked when it is reached from the root through lists, `&&`/`||`, single-command pipelines, and
     `if`, loops, `case`, `{ }` or `( )` that have no output redirect.
   - A job is not marked inside:
     - a function body;
     - a multi-stage pipeline;
     - a command or process substitution;
     - `select`;
     - a compound command that has an output redirect;
     - anything after a command-less `exec >file`;
     - eval, source or trap text (`TranspileContext.Eval`).

   A live write would bypass the redirect or capture that encloses such a job, so these jobs keep the old
   collected-at-`wait` path. Correct output is chosen over live output.
2. **Module.** Under `-Live`, and only when `$global:__BashJobOutput` exists (set by the host's `SdkWorker`; plain
   module use keeps the old behaviour), `Invoke-BashBackground` opens a channel:
   - runspace jobs go through `PsBash.Cmdlets.BashJobOutput.BeginLive`, where DataAdded on the output collection
     and on the error stream writes to fd 1 and fd 2;
   - process jobs go through `BashJobProcess.Start(..., OutputMux)`, which routes the job host's stdout and stderr
     callbacks.

   The job record gets `Live = $true`. `Complete-BashBgJob` still calls EndInvoke for the status but prints
   nothing, so `wait` and `fg` do not replay output.
3. **Host.** For the length of each command, `SdkWorker.RunCommand` attaches the mux to that command's own
   delivery path: the stdout batcher and the stderr bytes path, under the same `outputLock` the foreground records
   take. Output a job queued between commands is flushed first. A job's text therefore never lands inside a
   foreground record, and IPC frames are only ever written by the connection's single `IpcOutputQueue`.

### 12.2 Cases

| Case | Behaviour |
|---|---|
| Job writes while a command of the session runs | Live, in production order. |
| Job writes between commands, stateful session (`--session`) | Queued, up to the per-channel window, then the job blocks. Flushed first by the next command. |
| Interactive prompt | `InteractiveShell` prints queued output before the prompt. While jobs are alive, `LineEditor.ReadKeyOrJobOutput` polls the mux every 20 ms between keystrokes and prints new output above the prompt (erase region, print, re-render prompt and line). It never writes from the job's thread, because the console allows only one writer. |
| `-c` after the script's last command | The job prints live during the existing 1 s exit grace. When the runspace is discarded, the job is stopped (Q1 unchanged). Output produced after that is queued until the stop, then dropped, like a job whose pipe reader went away. Detach is still Q1. |
| `cmd > f &`, `cmd &> log &` | The redirect is inside the job body. Nothing reaches the session. |
| `cmd \| tee f &` | Live: tee's stdout is the job's stdout. |
| Job stderr | Live on fd 2 (`STDERR:` frames). |
| `$!`, `wait`, `wait $!` status, `jobs`, `fg`, `kill` | Unchanged. |
| Job in a function, redirected group, `$( )`, eval | Not live: collected and printed at `wait` or exit, as before. |

### 12.3 Bounds and failure

- **Memory.** While detached, each channel holds at most `OutputMux.WindowChars` (64 Ki chars) of queued text;
  after that the job blocks. While attached, the job is held back by the IPC queue (4096 frames) exactly as the
  foreground is.
- **Teardown.** A job blocked on output gives up within 50 ms once it is stopped:
  - a runspace job when its `PowerShell` leaves Running or NotStarted;
  - a process job when its cancel token fires.

  `Stop()` and `Remove-BashBgState` therefore cannot deadlock against it.
- **Broken launcher.** Sink exceptions are swallowed in the mux. The foreground's own sink failure handling
  (`BeginStop`) is unchanged.
- **Tests.**
  - `OutputMuxTests` (Core.Tests): ordering, credit, abandon, close.
  - `LiveJobEmitterTests`: which jobs are live.
  - `BackgroundJobLiveOutputTests` (Host.Tests): ordering through the batcher, stderr routing, record integrity,
    no replay at `wait`, queue between commands, flood bound and teardown.
  - `BackgroundJobDifferentialTests` `*_LiveOutput_*`: against the bash oracle.

### 12.4 Launch: the job's start is the launcher's last act

In bash, `echo a & echo b` prints `b` first: after `fork` the parent runs on at once, and the child has more to do
before it writes. ps-bash keeps that order by the same margin. Once a job is started, the launcher's path back to the next command
must do no work that the job's path to its first write does not also do.

- **Invariant.** `Start-BashBgJob` (PsBash.psm1) is the only place a job starts (`BeginInvoke`, `BashJobOutput.BeginLive`,
  `BashJobProcess.Start`). It records the job in the table and sets `$!` first, then starts the job, then does only
  property and variable sets. Nothing follows a `Start-BashBgJob` call except `return`.
- **Why.** On Windows, PowerShell reports every script-to-.NET method call to AMSI under one process-wide lock
  (`AmsiUtils.s_amsiLockObject`); cmdlet calls, property reads and sets, and `[pscustomobject]` do not report (measured
  with the lock held 300 ms by another thread: `List.Add` and `[Math]::Abs` waited ~310 ms, the others 1-9 ms).
  A job's `BeginInvoke` returns only once the job's pipeline has compiled (it waits on the same lock too), so after it
  the job's path to `echo` is plain script with no report. The launcher used to build the record (`[pscustomobject]`,
  two `Add-Member`) and call the job table's `.Add` after the start. Under load an AMSI report is slow (Defender is
  busy), so the job printed first. The spec case "a & b" printed `word_a` first in 2 of the 4 spec slices of one
  loaded full gate (beagle-ab, g45, load average about 50 on 12 cores). On the same host under the same load, the
  case repeated 288 times per run failed 3 and 7 times in two runs before the fix (10 of 576) and 0 of 576 after it.
- **What it does not cover.** If the foreground's next command itself calls a .NET method, it pays an AMSI report that
  the job may not. Compiling the job ahead does not move its own lock wait: every new `PowerShell` invocation takes
  the lock (a precompiled function, the same script text and a cached script block each waited the full hold).
- **Guards.** `BackgroundJobLaunchGuardTests` (Host.Tests, an AST check of the psm1). The regression test is
  `BackgroundJobLiveOutputTests.LiveJobStart_ForegroundReachesItsNextCommand_WhileTheAmsiLockIsHeldFromTheJobsStart`.
  It holds the AMSI lock from the moment the job is started (`OutputMux.ProducerStarted`, raised by
  `OutputChannel.MarkStarted`) and requires the foreground's `word_b` to arrive while the lock is held. Before the fix
  the foreground stalled for the whole hold and the job printed first.

### 12.5 Start latency: jobs run in runspaces that are already open (`BashJobRunspaces`)

`for i in 3 2 1; do { sleep 0.0$i; echo $i; } & done; wait` prints `1 2 3` in bash: jobs start within a
fraction of a millisecond of their `&`, so the shortest sleep finishes first. The oils cases "wait for N parallel jobs
and check failure" and "wait -n" (`spec/oils/background.spec.sh`, `arch-bg-job-start-latency`) depend on this.

**Before (measured, Debug, loaded workstation, per-job timestamps; removed afterwards).** Each command opened a
private `RunspacePool` at its first `&`. The daemon discards the command runspace after each `-c`, so every command
paid this:

| Job | `&` → first statement | Where the time went |
|---|---|---|
| 1st job of a command | 437-687 ms | pool create + open (psm1 import), 415-665 ms |
| 2nd job launched while the 1st runs | 276-645 ms more | the pool opens another runspace inside `BeginInvoke` |
| Nth job, past the runspaces already open | 290-650 ms | same |
| any job, warm pool | 15-55 ms | a new pipeline thread per invocation (below) |

**Causes and fixes.**

1. *Runspace open on the launch path.* `PsBash.Cmdlets.BashJobRunspaces` holds the job runspaces for the whole process,
   not per command. The daemon host opens `Spares` of them (`PSBASH_JOB_SPARES`, default 4) on one background
   thread when a command that can start a job in this process is about to run. That is a call of
   `Invoke-BashBackground` without `-Isolated`, because an `-Isolated` job runs in a host process of its own (§11)
   (`Connection.UsesInProcessJobs` → `SdkRunspace.WarmJobRunspaces`). Once the spares are open, later calls do nothing. Elsewhere (a module import) the first job
   starts the warm-up. The host does not open them at start. Opening them beside other commands slows those
   commands: a fresh host's first command-not-found took 11.7 s while the spares opened at start, and 0.45-0.75 s
   with them opened lazily. A runspace whose job completed and left it clean
   goes back to the idle set, up to `MaxIdle` (8; about 4 MB each). `EndJob` removes the globals the job created and
   restores the ones it changed. A job that changed a function or alias, started jobs of its own, or was stopped
   leaves a runspace that is closed and replaced. Opens only top up the owned count (idle + running) to `Spares`, never
   after each take: an open compiles the psm1 under the AMSI lock (§12.4), and an open beside a burst of launches
   delayed each of their starts by about 100 ms. When no runspace is idle but the warm-up thread is opening one, the
   launcher waits for it. Two opens at once each took about twice as long: a fresh host's first job took 2.3-3.0 s
   beside the warm-up's open, against 1.1-1.8 s for one open alone. When nothing is opening, the launcher opens one
   itself (`ColdOpens`), late but still in launch order. A reused runspace keeps the last job's location, so
   `BashStateSnapshot.BuildJobOptions` now begins with the parent's `Set-Location`.
2. *A new thread per invocation.* `RunspaceFactory.CreateRunspace` defaults to `PSThreadOptions.UseNewThread`: on a
   loaded host `BeginInvoke` took 40-100 ms and the first statement ran 35-150 ms after it. With `ReuseThread` the
   same steps took 0.3-10 ms and 2-30 ms (a pool's runspaces already work this way).
3. *AMSI reports on the launch path.* Each script→.NET call on the launch path (about a dozen) waits on the lock
   that the previous job's start holds. `BashJobRunspaces.NewJob` builds the whole job (text, options, loop
   variables, arguments, runspace) in one call.
4. *`sleep` precision.* With starts in order, `sleep 0.03` vs `sleep 0.02` still tied: `Thread.Sleep` wakes on
   the 15.6 ms Windows tick (10 ms slept 13-35 ms, 20 ms slept 21-38 ms, 30 ms slept 35-49 ms). `sleep` now waits on a
   high-resolution waitable timer (`PreciseSleep`, Windows 10 1803+), which does not change the system timer
   resolution: 10.4-12.9, 20.2-21.0 and 30.2-38.0 ms on the same host.
5. *Launch order.* With ready runspaces, the jobs' pipelines compile at the same time. Each compile waits on the AMSI
   lock, which is not handed out in arrival order, so `echo word_a & echo word_b &` printed `word_b` first (1 of 3
   runs of `shell-grammar.spec.sh` "a & b &"). The old pool serialized this by accident: each `BeginInvoke` waited for
   its runspace to open. Now `NewJob` puts `Enter-BashJobTurn` before the body. It is a cmdlet, so it makes no AMSI
   report. It waits until every job launched earlier from the same runspace has begun its body or ended
   (`BashJobRunspaces.JobTurn`). A wait gives up after 2 s, for a job whose start was refused and so never begins.
   It also stops at once when the job is stopped. Tests: `JobTurnTests` (Cmdlets.Tests).

**After (same host and load, warm daemon, 94 jobs).** Bind 0.7-3.3 ms. `BeginInvoke` 0.4-2 ms, rarely up to 25.
`&` → first statement: median about 13 ms, 10.6-53 ms in bursts. The tail is the AMSI lock held by the other
agents' test processes on this machine. A cold open (more concurrent jobs than `Spares`) still costs 700-900 ms.
Single-digit ms was not reached under that load. The floor is the job pipeline's own compile, which waits on the
AMSI lock (§12.4) and cannot be moved ahead.

**The two oils cases (measured, same host).** "wait for N parallel jobs and check failure" (10 ms between the sleeps)
came out `1 2 3` in 10 of 20 and 14 of 20 runs of a loop in one command, so it stays XFAIL. "wait -n" (60 ms between the
sleeps) passed 20 of 20 and 19 of 20 in one command. As a spec case it passed in only 6 of 11 runs, and in 2 of 6
under CPU load. It is flaky, so it also stays XFAIL, and its XPASS turns the file red in about half the runs. Opening
the host's job runspaces before each case did not make it pass every time (tried in the harness, then reverted).
Both marks carry these numbers. A fresh host opens up to `Spares` runspaces once. The first opens
took 1.8-3.9 s while several spec hosts started together.

`background.spec.sh` took 44 s, as on main (45 s), with no case timeouts in 6 runs. `shell-grammar.spec.sh` "Invalid
token" passes, where main failed it in 3 of 3 runs.

Under 4 CPU-bound processes on 8 cores, both files ran clean in 2 of 3 runs, in 1 m 15 s-1 m 22 s. The third run had
one case timeout: "Async for loop", an `-Isolated` job that runs in a host process. Main, under the same load and
alternating with these runs, was clean in 0 of 3, in 1 m 39 s-1 m 43 s. Its failures were "Invalid token" 3 times
and one case timeout ("Start background pipeline").

**Guards.** `BackgroundJobStartLatencyTests` (Host.Tests, runs alone) starts the warm-up and launches a job at once.
It asserts that the job took the warm-up's runspace. It then launches `Spares` jobs back to back after the spares are
idle and asserts:
- no runspace opened on the launch path (`ColdOpens` unchanged);
- each first statement within 150 ms of its `&` (a runspace open took 300-900 ms);
- starts in launch order.

`ConnectionJobRunspaceWarmTests` pins which commands warm the runspaces. `BackgroundJobPoolLifetimeTests`
covers teardown. `BackgroundJobPoolTests` covers the execution policy of a job runspace.

## 13. A signal sent to a ps-bash shell

`$$` is the pid of the host process that runs the script. A host runs one command at a time (the executor lease),
so "the shell whose `$$` is P" means "the command host P is running now". A nested `sh -c "kill -USR1 $$"` runs in
another host, so its `kill` targets the enclosing script's host.

- **Invariant.** A signal for a ps-bash shell is a message to the command that host runs, never an OS signal or a
  process kill. A kill ended every script of the session, and the host with them. `kill` reaches another ps-bash shell
  only through `ShellSignals.SendToShell` (Transpiler, `ShellSignals.cs`). The host registers its route
  (`ShellSignalSender`, Core). The route finds the target's endpoint from the host sidecars in the owner-only runtime
  directory. It accepts a sidecar only if it was written after the pid's process started, the same rule
  `IpcTransportFactory.EnclosingHostOwnEndpoint` uses. It then sends `Mode.Signal(N)` (`MODE:Signal`, `SIGNAL:N`) on
  that endpoint. The target answers at once, without taking the lease (the lease holder is the target), with
  `ps-bash-host signal=<Outcome>` and exit 0. A pid that is no reachable host, or a host too old to know the request,
  is `NotAShell`. The kill cmdlet then refuses to `Process.Kill` it if it is a `ps-bash-host` (`IsShellHost`) and
  prints `signal not delivered`.
- **Receiving (`ShellSignals.Deliver`).** `SdkWorker.RunCommand` brackets each command with `BeginCommand` /
  `EndCommand`. A signal that arrives while no command runs is dropped (`NoCommand`). Before the command sets a trap
  for any real signal (not EXIT/ERR/DEBUG/RETURN; `trap` calls `NoteTrapSet`), no trap can exist, so the default
  applies at once. CHLD, CONT, URG, WINCH and the stop family are ignored. Every other signal ends the command with
  128+N: the worker's `SetShouldExit` plus `PowerShell.BeginStop`, the same channel as `kill $$`. After `NoteTrapSet`,
  signals are queued, and the trap table is read on the script's own thread. As POSIX keeps pending signals and bash
  runs their traps, each signal is pending once (a repeat coalesces), and the traps run lowest signal number first.
  `-9` always ends the command. A queued signal never outlives its command.
- **Running the trap.** In a script that can set a trap, the emitter appends `PsBuild.SignalTrapPoll` after each
  top-level statement. "Can set a trap" means the source mentions `trap` (statically inlined `eval` text included),
  or it sources a file (`source f`, `. f`), whose library may run `trap cleanup INT` (`PsEmitter._canSetTrap`). The
  poll reads a static property; only when the queue is non-empty does it call `Invoke-BashPendingSignals`. That
  cmdlet runs the trap, ignores the signal (empty action), or applies the default (`ShellSignalTraps`). `kill $$` uses the same code. As in bash, the trap runs after the current foreground
  command and leaves `$?` as it found it. A real signal's action counts its own `$LINENO` from 1, and DEBUG and ERR
  fire inside it.
- **Unix.** A real SIGUSR1/SIGUSR2 aimed at a host or a launcher is caught (`ShellSignalSender.CatchPosixSignals`) and
  never takes its default action. The host delivers it to its command, and the launcher forwards it to its host's
  endpoint. Windows has no POSIX signals, so `kill` is the only sender there, and both platforms behave the same.
- **Limits.** A trapped signal runs only at a poll. A subshell `( )` or `$( )` body has no poll, so a signal that
  arrives while one runs waits for the next top-level statement boundary. The same holds for a long-running single
  command. A trap that only a runtime `eval "$x"` sets, in a script that neither mentions `trap` nor sources a file,
  gets no polls. A signal queued after it is dropped when the command ends. A host that is
  wedged, or that takes more than 5 s to answer, is reported unreachable.
- **Guards.** `ShellSignalRoutingGuardTests` (Cmdlets.Tests) checks the source of the kill cmdlet: every pid it does
  not own goes through `SendToShell` before any `Process.Kill`, and `Process.Kill` stays behind `IsShellHost`. Unit
  tests: `ShellSignalsTests` (Core.Tests); where the poll goes: `SignalTrapPollEmitterTests` (Core.Tests). End to end against bash: `ShellSignalDifferentialTests`
  (Differential.Tests) and the spec cases `builtin-trap` "exit codes for traps are isolated" and `builtin-trap-bash`
  "Combine DEBUG/ERR trap and USR1 trap".
