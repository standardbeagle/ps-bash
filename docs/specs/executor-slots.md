# Executor slots: one command per host, launcher-side spill

Status: IMPLEMENTED in the host (lease + BUSY) and the launcher (slot spill). Supersedes the global exec gate and the
nested-host machinery (`PSBASH_INSIDE_HOST`, `PSBASH_NEST_DEPTH`, `-nested<d>` endpoints). Deviates from the hub/ACQUIRE
broker sketched in `background-jobs.md` section 4.2: there is no broker process; the host itself arbitrates with a lease.

## 1. Problem

A bash command reads and writes variables as `$env:NAME` and the working directory as the process cwd, both
process-global. So one host process can run only one command at a time. The old design enforced that with a static gate
inside the host and queued everything behind it, which deadlocked when a command started a ps-bash of its own (the child
queued behind its parent). The workaround was a marker env var plus a per-depth "nested" host.

## 2. Design

**Host lease.** `HostServer` owns a one-permit lease. For a framed command request that carries a launcher environment
block, `Connection` tries the lease without waiting. If it is taken the host answers with a single EXIT frame,
`HostProtocol.ExecBusyExitCode` (76), BEFORE the Started sentinel, before any output, and before any stdin frame is
consumed. Nothing ran. Requests that cannot move to another host (queries and completion with no environment block,
interactive sessions) wait for the lease instead.

**Launcher spill.** `IpcWorker` treats exit 76 with no Started and no stdin frames delivered as "try another slot".
Slot 0 is the normal endpoint; slot k is `ResolveSlotEndpoint(base, k)` (suffix `-x<k>`, hashed to a short name if the unix
path would overflow). Slots are separate daemons started on demand and kept warm. Policy:

- **Nested** (the launcher is a descendant of the busy host, found by walking the parent chain, bounded): the busy host is
  waiting on us, so spill immediately to the next slot; if every slot (`MaxExecSlots` = 6) is busy, run on a private
  per-invocation host.
- **Contended** (an unrelated launcher): back off briefly and retry slot 0; after the spill delay (default 250 ms,
  `PSBASH_EXEC_SPILL_MS`) move to a healthy slot, or start one.
- **Stateful** hosts (named sessions with one pinned runspace) never receive spill from unrelated launchers; a nested
  launcher still spills, because it cannot wait on its own parent.
- The slot choice is sticky for the life of the launcher process.
- **Joining the enclosing session.** A launcher whose parent (or the first ancestor above at most 3 plain shells) is a
  registered `ps-bash-host` resolves that host's base endpoint (`ProcessAncestry.FindEnclosingHost`,
  `IpcTransportFactory.EnclosingHostBase`: sidecar with that PID, written after the process started; a `-x<k>` slot maps
  back to its base) and so reuses an idle session host or spills to the session's slots. Without it the default
  session anchor was the host itself, and every host started one more full daemon (one per host). Agent runtimes
  (node, claude, python) are not climbed; an explicit `PSBASH_IPC_ENDPOINT`/`PSBASH_SESSION` wins; an unregistered host
  (an interactive terminal's) keeps the anchored session.

The sequential warm path is unchanged: slot 0, lease free, no extra round trip.

## 3. Properties

- No deadlock by construction: a command never waits on a lease held by its own ancestor.
- No process-wide queue: a busy host costs a launcher one tiny round trip, not a wait.
- Idle slots exit after 60 s (the spawning launcher lowers `PSBASH_HOST_IDLE_SECS`, `IpcWorker.NestedIdleSecs`; slot 0
  keeps 600 s), so burst parallelism does not leave daemons behind. A slot host (endpoint `-x<k>`,
  `IpcTransportFactory.IsSlotEndpoint`) also warms 1 runspace, not 2 (`WorkerPool.SlotHostWarm`; `PSBASH_POOL_WARM`
  overrides): it runs one command at a time. `ps-bash host list` shows every host with its role and memory.
- The spec suite runner (`spec-suite.md`) uses N slots for N-way parallel cases.

## 4. Tests

Host: a second concurrent framed command is answered with exit 76 and no output while the first runs; the lease is
released when the first finishes (also on error and on cancellation). Launcher: slot endpoints derive and parse back;
a nested launcher spills at once; a contended launcher spills only after the delay; a stateful host does not spill
unrelated launchers. Behavioural: `NestedBashC_RepeatedCalls_ReuseOneWarmNestedHost` still holds (the nested slot is warm).

## 5. Measured trade-off

Process-mode background jobs still miss the cold-miss speed bars in `background-jobs.md` section 7.3 (`{ :; } & wait`
3505 ms vs 935 ms in-process); the user approved proceeding anyway, and the default stays in-process.

## 6. Concurrent-launcher test

`ConcurrentLaunchersTests` (Differential.Tests) starts 4 real launchers at once against one private session (own
`PSBASH_SESSION`, shared daemon, `PSBASH_HOST_IDLE_SECS=15` so its hosts exit on their own): each sets a variable, sleeps, and
prints it. A first burst creates the slot daemons; the second is checked for the right output per launcher (no state shared
across slots). The "they overlapped" timing check runs only with `PSBASH_PERF_TESTS=1`: on a memory-starved machine (3.8 GB
free, hosts of 0.5 to 1 GB each) the six-slot burst measured SLOWER than serial (22 to 37 s for 8 launchers of a 2 s command),
which is a property of the machine, not a slot bug, but it means slot spill has not been shown to give a speedup here.
