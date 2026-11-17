# Host Lifecycle Metadata and Ownership Contract

This contract defines how launchers decide whether the canonical per-session
`ps-bash-host` (one daemon per `(user, session)`; see "Interaction With
`IpcTransportFactory`") can be reused, replaced, or left alone. It is intentionally
a design contract only: current behavior may be simpler until the lifecycle
implementation lands.

## Goals

- Keep the canonical endpoint from `IpcTransportFactory` as the single source of
  truth (now per-`(user, session)`, see below).
- Make host reuse decisions from explicit metadata plus a health handshake, not
  from endpoint presence alone.
- Distinguish endpoint cleanup from process cleanup.
- There is one transport: an AF_UNIX socket (Windows 10 1803+ included). The named-pipe transport was removed.

## Scope: this contract governs the shared `Daemon` host only

This contract covers the **shared, reusable** `ps-bash-host` — the one reached
through `IpcWorker.StartAsync` with `Lifetime.Daemon` on the canonical per-session
endpoint. As of the pooled-host change this is the **default** path for `-c`,
stdin-pipe, and script-file modes (the launcher in `src/PsBash.Shell/Program.cs`),
as well as the `ps-bash host restart` subcommand (`src/PsBash.Shell/HostCommands.cs`).

**Pooled isolation.** A `Daemon` host serves each connection from its own isolated
runspace, drawn from a warm `WorkerPool` (`src/PsBash.Host/Runtime/WorkerPool.cs`)
and **discarded on release**. So daemon reuse is fast (no per-command runspace
cold-start) without leaking session state between commands — each `-c` still gets a
structurally clean PowerShell session, and concurrent launchers each hold their own
distinct runspace (bounded by `PSBASH_POOL_MAX`). Warm spares are created on
dedicated (LongRunning) threads so warm-up never starves the host's async accept /
health-handshake loop. Pool size: `PSBASH_POOL_WARM` (default 2) warm spares,
`PSBASH_POOL_MAX` (default: CPU count clamped to [2, 8]) cap on in-use runspaces.
`PSBASH_POOL_WARM=0` is valid: the host reports ready immediately (there is no
warm-up to wait for) and each connection builds its runspace on demand.

**Isolated runspaces, one command per host.** Isolation is per connection (own
runspace, discarded on release); command *execution* is NOT parallel inside a host. The host
holds a one-permit **execution lease** (`HostServer.TryEnterExecution`) across every launcher-framed
command, because bash variables (`$env:NAME`) and the working directory
(`Environment.CurrentDirectory`) are process-global and shared by all runspaces. A framed request that
finds the lease taken is refused at once with `HostProtocol.ExecBusyExitCode` (before STARTED, nothing
ran, no stdin read) and the launcher re-sends it to another **executor slot**
(`executor-slots.md`); requests that cannot move (queries, interactive) wait for the lease. Concurrency
comes from several host processes, not from a queue. True in-process concurrency would need
invocation-scoped env/cwd, or separate processes (`PSBASH_PER_INVOCATION=1` gives
each launcher its own host).

Two host populations are explicitly **out of scope**:

- **`Lifetime.PerInvocation` hosts** (opt-in via `PSBASH_PER_INVOCATION=1`). Each is
  private to one launcher invocation on a process-local endpoint, is owned by that
  launcher's `IpcWorker`, and is killed on `DisposeAsync`. There is no discovery, no
  sidecar ownership classification, and no reuse — so none of the
  Reusable/Obsolete/Stale/Unsafe machinery below applies.
- **Interactive hosts.** The interactive REPL (`ps-bash -i`, and the bare REPL)
  does **not** go through `IpcWorker` at all. `Program.RunHostUnderPtyAsync`
  (and the legacy inherited-stdio fallback in `Program.cs`) spawns
  `ps-bash-host --interactive --launcher-pid=<pid>` **directly** via
  `PtySpawner` / `Process.Start`. An interactive host therefore:

  - is **never discovered** — it binds no canonical endpoint and the launcher
    never probes one before spawning;
  - **publishes no reuse sidecar** — there is no `HostMetadata` record another
    launcher could read to attach to it;
  - is **bound to exactly one launcher and one PTY** — it is fresh per session
    and exits when that launcher disconnects (PTY master EOF; or
    `ParentDeathWatcher`, armed via `--launcher-pid`, if the launcher dies
    abruptly).

  This is deliberate: an interactive host is PTY-bound, so sharing one across
  launchers would route one terminal's keystrokes into another's. A fresh host
  per interactive session is guaranteed *structurally* — the interactive path
  bypasses the discovery/reuse machinery entirely rather than relying on a
  runtime guard. The interactive host also does not honor the host's idle
  timeout the way a `Daemon` host does (see `IdleShutdown` remarks in
  `src/PsBash.Host/Server/IdleShutdown.cs`): its lifetime is its single session,
  not an idle-reaped daemon's. Regression-pinned by
  `PtySpawnTests.TwoInteractiveSpawns_GetDistinctHostPids` and
  `PtySpawnTests.InteractiveLaunchPath_NeverSelectsDaemonLifetime`. See also
  `docs/specs/pty.md` §10.5.

**Process exit is explicit.** Every host path ends in `Environment.Exit` once
`Main`'s work is done (`PsBash.Host/Program.cs`). Returning from `Main` is not
enough: PowerShell runs runspace pipelines on FOREGROUND threads, and a runspace
that a user command leaves open (a background job `&` opens the psm1's runspace
pool; nothing closes it) keeps the process alive after the idle timer fired and
the server drained. Before this, every daemon that had run a background job
lingered indefinitely. Regression-pinned by
`ProcessLifecycleTests.IdleDaemon_AfterBackgroundJob_ExitsInsteadOfLingering`.

## A daemon is never in a live launcher's process tree (Windows)

The launcher sets `WindowsHostSpawn.DetachTrampoline` to its own exe; a host is then started through
`ps-bash.exe --psbash-spawn-detached <host> <args>`, which spawns the host (same NUL-only handle list) and exits with
its pid. An agent harness stops a timed-out or cancelled command with `taskkill /T /F`, which finds descendants by
parent pid: a host that was the launcher's child died with it, and every other session command running on that shared
daemon failed with `host process exited with code 1` (no host-log exit line: the kill gives no chance to log). The
trampoline has exited before the command runs, so the walk does not reach the host. If the trampoline cannot run the
launcher starts the host directly. Pinned by `ProcessLifecycleTests.LauncherTreeKilledMidCommand_SharedHostItStartedSurvives`.
`ps-bash host gc` therefore never treats "parent gone" alone as orphaned (`HostCommands.ShouldGc`): it stops a host
only when no sidecar names it, its parent is gone, and it is past start-up.

## Session anchor lifetime: a daemon ends with its session

A `Daemon` host outlives the launcher that spawned it (that is the point: the next `-c` in the
session reuses it warm), but it must not outlive the **session**. When the endpoint was keyed by
the automatic session anchor (no `PSBASH_IPC_ENDPOINT`, no `PSBASH_SESSION`, top level — not
nested), the spawning launcher passes that anchor to the host as
`--session-anchor=<pid>:<startTicks>` (`ProcessIdentity.SessionAnchorArg`,
`IpcWorker.SpawnAndWaitAsync`). The host's `SessionAnchorWatcher` polls it every second; once the
pid is gone **or now belongs to a different process** (start time differs: PID reuse), it calls
`IdleShutdown.ShortenTimeout(5 s)`. In-flight commands finish; the host exits 5 s after the last
one ends. The host log records `session anchor <pid>:<ticks> exited; idle 5s with no connection`.

Why shortening the idle timer and not cancelling outright (as `ParentDeathWatcher` does for a
private host): the endpoint name embeds the anchor's pid **and** start time, so once the anchor
is gone no new launcher can ever resolve this endpoint — the only work left is what is already
connected, and cutting it off would fail a running command. A grace longer than the configured
idle timeout never extends it.

**Why this exists (GH #7).** The anchor walk (`ProcessAncestry.SelectSessionAnchor`) climbs
through recognized shells/agents only. An agent whose own process name is not recognized, and
that runs each command in a fresh shell, makes that per-call shell the anchor. Every call then
keys a new daemon, and before this watcher each one idled for the full 600 s — an agent session
left a stack of ~65 MB `ps-bash-host` processes. With the watcher each such daemon exits ~5 s
after its call. Recognized-agent sessions are unchanged: one warm daemon for the session, which
now exits when the agent does instead of 10 minutes later.

Not watched (idle timeout only, `PSBASH_HOST_IDLE_SECS`, default 600 s): an explicit
`PSBASH_SESSION` (the caller owns the grouping; no process represents it), an explicit
`PSBASH_IPC_ENDPOINT`, the per-user fallback, and slot daemons (already 60 s). A
`PerInvocation` host is private to its launcher and dies with it (`--launcher-pid`).

Regression-pinned by `SessionAnchorWatcherTests.Daemon_ExitsAfterItsSessionAnchorDies` (real
daemon, idle pinned at 600 s, stand-in anchor killed → host exits; verified red without the
watcher), `IdleShutdownTests.ShortenTimeout_*`, `ProcessIdentityTests`, and
`IpcTransportFactoryTests.ResolveEndpoint_*Anchor*`.

## Nested invocations run on another executor slot

A command executing inside a `Daemon` host may itself start a ps-bash: `bash -c '…'` in a script, awk
`"cmd" | getline`, `xargs bash -c`, a `.sh` that calls `ps-bash`. That child cannot be served by the host
running its parent: the parent holds that host's execution lease for its whole command, and the child waits for
the lease while the parent waits for the child. (A child of a different build would instead retire the host as
obsolete, and the parent saw `host connection reset after the command started executing`, exit 125.)

**Mechanism (executor slots, `executor-slots.md`).** There is no marker in the environment any more.
The child connects to its normal endpoint, is told `ExecBusyExitCode` before anything runs, and the launcher
recognises that it is a descendant of the busy host (`ProcessAncestry.IsDescendantOf`, parent-chain walk), so
it moves to slot k+1 at once: `IpcTransportFactory.ResolveSlotEndpoint(base, k)` = the endpoint plus `-x<k>`
(a unix path that would overflow `sun_path` becomes a hashed name), brought up by the normal Daemon path
(`EnsureHostReachableAsync`: HostSpawnLock single-flight, BuildIdentity replace, optimistic local reuse). A
slot daemon is kept warm for repeated nested calls and idles out after 60 s (`PSBASH_HOST_IDLE_SECS`, unless
the environment already asks for less) because nothing reaps it when its parent host goes away. When all
`MaxExecSlots` (6) slots are occupied by an ancestor chain the launcher falls back to a private
`PerInvocation` host. `PSBASH_PER_INVOCATION=1` is unchanged: such launchers always get a private host. After
every framed command the host moves its process cwd out of the caller's directory (a persistent host must not
pin it, or Windows cannot delete it). The nested launcher is also resolved to the `ps-bash` beside the host
process (`InvokeBashBashCommand.ResolvePsBashExecutable` tier 0), i.e. the SAME build as the host.
Regression-pinned by `NestedInvocationTests` (explicit and default endpoint, 1–2 levels, awk getline,
`NestedBashC_RepeatedCalls_ReuseOneWarmNestedHost` via `$$`), `HostServerTests.ExecutionLease_*`,
`IpcWorkerExecutorSlotTests`, `IpcTransportFactoryTests.ResolveSlotEndpoint_*` and
`SdkWorkerEnvironmentTests.LauncherFramedCommand_DoesNotSetTheRetiredNestingMarkers`.
## Runtime Directory

All per-user runtime artifacts — extracted module, IPC sockets, `.host.json`
sidecars, spawn locks and failure-tee logs — live under one runtime directory
resolved by `PsBashRuntimeDirectory`:

- **Windows**: `%TEMP%\ps-bash`. `%TEMP%` is already per-user and ACL-protected,
  so the fixed name is not attacker-reachable.
- **POSIX**: `$XDG_RUNTIME_DIR/ps-bash` when `XDG_RUNTIME_DIR` is a non-blank
  absolute path, else `$TMPDIR/ps-bash-<uid>`. `$TMPDIR` (usually `/tmp`) is
  world-writable, so a fixed `ps-bash` name would let another local user
  pre-create the extraction dir, replace the socket, or plant a sidecar.

On POSIX the directory is created 0700 and **validated on every use**: it must
be a directory, be owned by the current uid, and have no group/world write bit
(a symlink is judged by `lstat`, not its target). Any other state raises
`InsecureRuntimeDirectoryException` (an `IOException`) and the launcher refuses
to use the path. Sockets are bound only after this check, inside the 0700
directory, then narrowed to 0600. The sidecar kill gate additionally probes the
recorded PID's live uid (`/proc/<pid>/status` on Linux) so a planted
`owner=<victim>` record cannot authorize killing another user's process.

## Metadata Record

Each host owns a JSON metadata record beside the endpoint identity:

- `pid`: operating-system process id of the host.
- `executablePath`: full path to the host executable that created the record.
- `protocolVersion`: `HostProtocol.ProtocolVersion`.
- `buildIdentity`: the same build identity advertised by the health payload.
- `endpoint`: canonical endpoint string returned by `IpcTransportFactory`.
- `transportScheme`: `unix`.
- `startedAt`: UTC instant when this host began startup.
- `lastSeen`: UTC instant written after successful health checks and updated by
  the host while serving.
- `owner`: stable user identity for the endpoint owner. On Windows this must be
  the current user SID when available, with account name only as display data.
  On POSIX this must include uid when available, with user name as display data.

The record path is derived from the canonical endpoint:

- `<endpoint>.host.json`, next to the socket path.

A separate lifecycle lock may be stored beside the metadata record. The lock is
for writers racing to start or replace a host; it is not proof that a host is
alive.

## Validation States

A launcher classifies the current host state before taking action:

| State | Requirements | Launcher action |
| --- | --- | --- |
| Reusable | Metadata parses; owner matches the current user identity; endpoint and transport scheme match `IpcTransportFactory.ResolveEndpoint()`; protocol version is compatible; build identity is accepted by the caller policy; process id is alive and corresponds to the recorded executable when the platform can verify it; health handshake returns the expected `HostProtocol.HealthPayload`; `lastSeen` is fresh enough for diagnostics. | Reuse the running host. |
| Obsolete | Owner and endpoint match, but protocol version or build identity is incompatible, or the executable path no longer matches the requested host binary. | Start or wait for replacement under the lifecycle lock. Endpoint cleanup may be needed, but process cleanup is only allowed if ownership checks pass. |
| Stale | Metadata or endpoint remains but no compatible health handshake succeeds; pid is absent, dead, or cannot be verified; or `lastSeen` is beyond the stale threshold and health probing fails. | Remove only stale metadata and endpoint artifacts owned by the current user. Then start a replacement. |
| Unsafe to touch | Owner does not match; metadata endpoint/scheme does not match the canonical endpoint; executable path points outside the expected host binary policy; pid is alive but not the recorded executable; metadata is malformed in a way that prevents ownership validation; or filesystem permissions/ACLs are broader than owner-only. | Do not delete endpoint artifacts and do not kill processes. Fail with a host ownership error that names the unsafe reason. |

Validation must not treat endpoint presence as process ownership. A socket file,
metadata file, or pipe name can be stale, spoofed, or left by a crashing process.
The health handshake is required for reuse; ownership validation is required
before cleanup.

### Stale-artifact reaping is compare-then-delete

`StaleArtifactReaper` is the janitor that removes artifacts left by hosts killed
before they could clean up. It deletes **only** artifacts whose owner is provably
gone, and it re-reads the sidecar immediately before deleting: it reads the
sidecar, classifies the owner dead, then re-reads and requires the sidecar to
**still** name that same dead PID. If a new host rebound the endpoint in the race
window and rewrote the sidecar, the re-read sees a different PID and the reaper
leaves the sidecar and its companion socket untouched. Without this
compare-then-delete step a rebinding host's freshly bound socket could be unlinked
under it.

### A socket file is removed only when nothing listens on it

A replacement binds before it writes its sidecar, so a dead sidecar owner does not make the socket stale. Every
removal of a socket file first checks `UnixSocketProbe.NothingListens` (a refused connect). That covers the reaper,
a host's bind (`UnixSocketTransport.ListenAsync`, which removes a crashed host's leftover file), and a host's own
dispose, since after `RetireEndpoint` the path may belong to a replacement. On POSIX, deleting a live socket's file
succeeds silently and leaves that host running unreachable. A host whose endpoint is already served gets
`EndpointInUseException` and exits 0 with a host-log line. That includes losing a bind race; on Windows this used
to be an unhandled "address already in use", a FATAL crash. `RetireEndpoint` is the one deliberate removal of a
possibly live socket. Guard: `Core.Tests/Runtime/Ipc/UnixSocketGuardTests`; behavior: `TransportTests`.

### A daemon host closes only the fds it inherited

A host spawned as a daemon (`PSBASH_HOST_DETACH=1`, POSIX) points fd 0-2 at `/dev/null`. It also closes the
launcher's pipes that leaked into fds 3 and up, since otherwise a caller reading the launcher's output never sees
EOF. It closes a pipe only if it came through the exec, meaning it has no `FD_CLOEXEC` (`InheritedFdDetach.IsInherited`).
The .NET runtime opens all of its own descriptors with `O_CLOEXEC`, its signal pipe included. Closing that pipe
aborts the host on the next signal: the first child that exits sends SIGCHLD, the runtime's write gets EBADF, and
it calls abort. The old rule, "every pipe at 3+", held only while the detach ran before any runtime pipe existed.
It broke once POSIX signal handlers were registered earlier in `Main`, and every native program run on a Linux daemon
host killed it. The rule does not depend on startup order. Test: `Host.Tests/Server/InheritedFdDetachTests`.

## Endpoint Cleanup vs Process Cleanup

Endpoint cleanup means removing bind artifacts so a replacement can claim the
canonical address:

- Cleanup may unlink the socket path and remove the metadata record
  only after ownership validation says the artifacts are stale or obsolete.

Process cleanup means terminating a running host process. It is a separate,
higher-risk action and is allowed only when all of these are true:

- The metadata owner matches the current user.
- The pid is alive and still resolves to the recorded executable path.
- The host is obsolete or wedged and cannot complete a graceful shutdown request.
- The cleanup code owns the replacement attempt under the lifecycle lock.

If any identity or executable check is inconclusive, the process is unsafe to
touch. The launcher may clean stale endpoint artifacts only when artifact
ownership is proven; it must not kill the process.

## Concurrency

Multiple clients may race to start, observe, or upgrade the host. They coordinate
through a short-lived lifecycle lock beside the metadata record:

1. All clients first probe the canonical endpoint and accept a reusable host.
2. If the host is absent, stale, obsolete, or starting, clients try to acquire
   the lifecycle lock with an atomic create/open operation.
3. The lock holder revalidates metadata and health after acquiring the lock. If
   another client already produced a reusable host, it releases the lock and
   reuses that host.
4. The lock holder may retire stale endpoint artifacts, spawn the replacement,
   write metadata, and wait for a healthy handshake.
5. Losers back off and continue probing until a reusable host appears or the
   startup timeout expires.
6. Lock files themselves can become stale. A client may break a stale lock only
   when the lock owner process is dead or the lock timestamp is beyond the
   startup timeout and no healthy host answers.

The **startup timeout** is `PSBASH_STARTUP_TIMEOUT` seconds, else a positive
`PSBASH_TIMEOUT` (legacy), else **60s** (`IpcWorker.ParseStartupTimeout`). It was 20s
until runspace init was observed to exceed that on a slow machine (OneDrive-synced
`PSModulePath`; GitHub #6). A host that exits during startup still fails immediately.

**The startup timeout is a checkpoint, not a verdict** (`Ipc/HostStartupDeadline`). Every
launcher wait on a starting host keeps its ONE clock there: the spawner waiting on its own host,
a launcher waiting on the spawn-lock holder's host (named by `PendingHostSpawn`), and the wait on
a host that answered `starting`. When the timeout passes, a host that is alive and has used CPU
since the last checkpoint is still starting and gets another full window, at most
`HostStartupDeadline.MaxExtensions` (3) times. A host that is idle at a checkpoint is stuck and
fails there. Rationale: a cold start on a loaded machine is correct but slow. With
`PSBASH_TIMEOUT=15` (inherited as the startup timeout) a differential run failed with "did not
accept connections within 15s", exit 125. `ContentionAwareWaitGuardTests` forbids a bare
`UtcNow + _startupTimeout` in Core. `PendingHostSpawn.Judge` deliberately stays time-only. It
anchors on the pending host's own start time, so every later launcher agrees when a host whose
launcher died has had long enough.

This gives single-host-per-**session** behavior: one daemon per `(user, session)`,
where the session token is an explicit `PSBASH_SESSION` or, when unset, the
launcher's **stable session anchor** — the nearest shell/agent ancestor of the
launcher, folded with that ancestor's process start time
(`ProcessAncestry.FindSessionAnchor`). The immediate parent is deliberately **not**
used as-is: a Bash-tool call may be launched through a fresh short-lived shim, so
using the parent verbatim spawns a new daemon per call and never reuses a warm
host. The anchor walk climbs past such transient launchers to the nearest
recognized shell (`bash`, `pwsh`, …) or agent (`node`, `opencode`, `claude`, …),
stopping at a shared service/build host (e.g. `dotnet`) that is not a per-session
owner, and falls back to the immediate parent when nothing is recognized. Repeated
`-c` invocations from one shell / agent share an anchor → resolve the same
endpoint → reuse one warm daemon; independent shells / agents resolve distinct
anchors, so load spreads instead of contending on a single per-user host.

The token is `<anchorPid>-<anchorStartTimeUtcTicks>`, not a bare PID: a PID is not
unique **over time**, and Windows reuses PIDs. Folding in the anchor's start time
makes a recycled PID a distinct session key, so an unrelated process can never
attach to a previous session's stale daemon (and inherit its stale environment —
see R06). The token is **per-session, not per-invocation** — warm reuse within a
session is preserved; only independent sessions diverge. When no session token is
available the endpoint degrades to the historical per-user name (`host-{user}`).
The canonical endpoint remains stable for a given session; lifecycle metadata and
the lock make replacement decisions explicit, scoped to that endpoint.

### Known gap: the process environment is NOT per-invocation

Each `-c` gets an isolated **runspace** (discarded on release), and
`Connection.PerInvocationReset` clears the PowerShell globals a transpiled
command may set. Neither covers `$env:`, which in .NET is **process-wide** state
owned by the host, not the runspace. So an `export` outbound-escapes its
invocation:

```bash
ps-bash -c 'export LEAKTEST=hello'
ps-bash -c 'echo "${LEAKTEST-unset}"'   # ps-bash: hello   ·   bash: unset
```

Real bash cannot do this — each `bash -c` is a separate process.

**Fixed in R06.** The launcher now captures its full environment block and sends
it with every framed request (`HostProtocol` `ENV:<base64 name>,<base64 value>`
lines; a pre-R06 frame has none and decodes to `null`). The host resets its
process environment to **exactly** that block before running the command —
entries are set to the launcher's value and every variable *not* in the block is
removed — so a var changed or unset in the launcher is observed, and an
`export` from a previous invocation does not survive. A `null` block (the
in-process interactive path, or a legacy launcher) leaves the environment
untouched. `ENV:` lines are recognised only in the frame's leading header
position (before the first body line), so a Command/Stdin body that contains an
`ENV:`-prefixed line — heredoc data, say — is delivered to the command verbatim.
On Windows the reset's keep-set comparison is case-insensitive to match the OS,
so a launcher entry whose casing differs from the daemon's existing instance is
retained rather than set-then-deleted. End-to-end coverage:
`ProgramEndToEndTests.Command_{Changed,Removed}EnvVar…`
and `…ExportInOneInvocation_NotVisibleToNext`; protocol/worker coverage in
`HostProtocolTests.RoundTrip_*WithEnvironment_*` and
`SdkWorkerEnvironmentTests`.

**Why a naive save/restore bracket would be wrong.** Snapshotting the environment
on connect and restoring it on release would race: the pool runs up to
`PSBASH_POOL_MAX` connections *concurrently in one process*, and they all share
the single process environment block. Two overlapping commands would restore
each other's snapshot and corrupt both. The R06 implementation sidesteps the race
by reusing the **host's execution lease** (`HostServer`): a host already serialises command
execution because bash variables and cwd are process-global, so the environment reset runs
inside that lease and no other runspace is executing during the swap — the
"serialized env epoch" the earlier analysis called for.

Two further divergences share this root cause (bash variables modeled as
environment variables):

- **Non-exported variables reach a program PowerShell starts.** Every assignment lands
  in the process environment; the export attribute is kept beside it
  (`ShellExports`, `environment-model.md`), so `env`, `printenv`, nested shells and
  every child ps-bash starts itself see exported variables only. A plain `prog args`
  started by PowerShell still inherits all of them.
- **Windows environment names are case-insensitive**, so `$x` and `$X` are the
  same variable where bash treats them as distinct.

### Step 1 of the fix is in place: `BashVariableStore`

Every cmdlet read/write of a bash VARIABLE now goes through
`PsBash.Cmdlets.BashVariableStore`, and every child-process spawn calls
`BashVariableStore.ApplyTo(startInfo)`. Today the store *is* the process
environment and `ApplyTo` is a no-op, so behavior is byte-for-byte unchanged —
the point is that the ~100-site audit is already done. Making the store
per-invocation becomes a change to those two method bodies plus a `sessionMode`
switch, instead of a tree-wide hunt.

### Step 3 feasibility — measured, not assumed

The remaining step is making `$env:` itself per-invocation. The emitter renders
bash variables as literal `$env:NAME` in ~67 places, so a C#-side overlay alone
would be invisible to the emitted script — the *drive* has to change. Probed
directly against pwsh:

| Question | Answer |
|---|---|
| Is `$env:` resolved through a session-state PSDrive? | **Yes** — `Remove-PSDrive Env` makes `$env:X` read empty |
| Does removing `Env:` break external command resolution? | **No** — PowerShell uses the real process PATH internally, not the drive |
| Do child processes still inherit the real environment? | **Yes** — unaffected by the drive |
| Can a replacement `Env:` drive be session-scoped? | **Yes** — writes to it do not reach the process environment |

So the mechanism is sound and the two risks that would have killed it are
cleared. What is NOT viable is the free version: backing the drive with the
built-in **Variable** provider maps `Env:` onto the same table as PowerShell
variables, so `$env:x` and `$x` become the same cell. ps-bash emits loop
variables as bare `$i` and bash variables as `$env:i`, so that would collide them
outright — the failure mode already on record as `psm1-toplevel-loopvar-leak`.

Step 3 therefore needs a **custom provider** over a per-runspace dictionary,
plus routing `BashVariableStore` through `SessionState` (cmdlets have it;
the static helpers do not) and seeding the drive per invocation. Semantics it
must preserve, all verified available on a replacement drive: unset reads as
`$null` while set-to-empty reads as `""` (the `${X:-w}` vs `${X-w}` split),
`Remove-Item` returns a name to null (`unset`), and enumeration (`env` /
`printenv`).

Two things keep it from rotting, both mutation-tested:
`BashVariableStoreGuardTests.NoCmdlet_ReadsOrWritesABashVariable_ThroughEnvironmentDirectly`
and `...EverySpawnSite_AppliesTheMaterializationSeam`. Host CONFIGURATION knobs
(`PSBASH_*`, `NO_COLOR`, `USERNAME`, `PATH`) are deliberately exempt and must keep
reading the real process environment — `BashRuntime.IsHostConfigTruthy` is named
for that contract, which is how the guard tells the two apart when the variable
name arrives as a parameter.

### Implementation (single-flight spawn)

Steps 1-6 are implemented by `IpcWorker.EnsureHostReachableAsync` (the
`Lifetime.Daemon` path) plus `HostSpawnLock`
(`src/PsBash.Core/Runtime/Ipc/HostSpawnLock.cs`):

- The lock is an exclusively-opened file (`FileShare.None`) under
  `<runtime-dir>/spawn-{scheme}-{hash(endpoint)}.lock`. It is **endpoint-scoped**,
  so an isolated test endpoint (`PSBASH_IPC_ENDPOINT`) never serializes against the
  real per-user daemon, and the `Lifetime.PerInvocation` path — whose endpoints are
  process-local and uncontended — never acquires it.
- Step 6 (stale-lock breaking) is handled by the OS: a file handle is released on
  `Dispose` **or on process death**, so a crashed lock holder cannot deadlock the
  herd — the next launcher's `TryAcquire` simply succeeds. A file handle (unlike
  `System.Threading.Mutex`) is not thread-affine, so it survives the `await` in the
  spawn path.
- The lock holder runs `SpawnOrReplaceHostAsync` (classify → graceful-retire
  obsolete → kill wedged owned host → retire artifacts → spawn); losers poll health
  and reuse the winner's host the moment it answers.
- **A lock holder can die mid-spawn** (Claude Code cancels a status-line command still
  waiting on a cold host, which takes 2-8 s). The lock goes with it while its host is
  still starting and not yet bound, so it is invisible. The spawner therefore records
  the host as `<endpoint>.spawning` (`PendingHostSpawn`, `pid:startTicks`) the moment it
  starts, and clears the record once the host is up or killed. The next holder sees no
  host answering, finds the record, and waits for that process. The wait is measured
  from the host's own start time and is bounded by the startup timeout, after which
  the host is killed so it cannot bind late and orphan its replacement. The holder
  also waits for a host answering **Starting** instead of killing it as wedged.
  Without this, each cancelled refresh left another ~150 MB host idling for 10 min.
  Regression coverage: `HostStartupStressTests.LauncherKilledMidSpawn_NextLauncher_WaitsForThatHost_SpawnsNone`.

Without this, concurrent cold-start launchers each reached the spawn path and left
N-1 orphan runspaces — `UnixSocketTransport.ListenAsync` unlinks-before-bind (each
racer steals the socket path, so the last binder wins and the earlier hosts are orphaned). Regression coverage:
`PsBash.Host.Tests/Server/HostStartupStressTests.ColdStartHerd_ConcurrentDaemonLaunchers_SpawnExactlyOneHost`
(a `Category=Stress` test; see `scripts/test.sh --stress`).

## Interaction With `IpcTransportFactory`

`IpcTransportFactory.ResolveEndpoint()` continues to be the source of truth for
the endpoint and transport scheme. The resolved name now carries a per-**session**
segment (`host-{user}-s{token}`, token = `PSBASH_SESSION` or
`{anchorPid}-{anchorStartTicks}`); lifecycle code derives metadata and lock paths
from the resolved `(scheme, endpoint)` pair, so each session's metadata/lock are
naturally isolated. The session segment is the **only** discriminator added — a
per-*process* (per-invocation) suffix must NOT be added to the canonical endpoint,
because that would defeat warm-pool reuse (every command would cold-start its own
daemon). `Lifetime.PerInvocation` is the dedicated path for process-local
endpoints (`ResolvePerInvocationEndpoint`).

Transport: always `unix`. A canonical path over the `sun_path` budget gets the user part hashed,
then fails with an `IOException` that names `TMPDIR` and `PSBASH_IPC_ENDPOINT`; there is no
fallback transport. A process that cannot create the runtime directory (for example a read-only
restricted-token sandbox) fails the same way, with the cause named. The named-pipe transport and
its restricted-token ACL handling were removed in 0.14.

`IpcTransportFactory.RetireEndpoint()` remains endpoint cleanup only. For `unix`
it may unlink the socket path after lifecycle validation has decided cleanup is
allowed.

## A script that does not parse is never silent

The host parses the WHOLE transpiled script before running any of it, so a PowerShell-level
parse failure (an emitter defect, or raw PowerShell with a stray `}` reaching the host through the
launcher's PowerShell fallback) means nothing ran — not even the statements before the bad one.
`SdkWorker.RunCommand` reports it on the stderr sink as `ps-bash: parse error: <PowerShell message>`
and returns **2**, bash's syntax-error status (the launcher's own bash `ParseException` path, in
`-c`, stdin and script-file mode, already exits 2). A runtime error stays exit 1. This holds for
the shared `Daemon` host and `PSBASH_PER_INVOCATION=1` alike — it is host behaviour, independent
of lifetime. Coverage: `PsBash.Host.Tests/Runtime/SdkWorkerParseErrorTests` (worker level) and
`PsBash.Shell.Tests/ProgramEndToEndTests.UnparseableScript_*` (launcher, -c / -s × daemon /
per-invocation).

## No input may take the host down

A host runs one command at a time under its execution lease (env vars and cwd are
process-global), so one hostile command used to cost every session: a stack overflow killed the
process, and a command that could not be stopped held the lease forever. The rules:

- **Recursion is bounded.** Every recursive scanner/parser/evaluator cycle enters `NestingGuard`
  (lexer `ScanBalancedParens`, `BashParser.ParseCompoundOrSimple`, `ParseBracedVar`, the arithmetic
  parser, the `test` evaluator). Past 1000 levels, or near the end of the thread's stack, it throws a
  normal parse error. The emitter's two recursion entries (`Emit(Command)`, `EmitBracedVar`) probe the
  stack too. Transpiles run inline; one that runs out of stack (not the depth cap) is retried on a
  large-stack worker (`NestingGuard.WithStackFallback`) — inline, because a thread hop costs
  milliseconds on an STA caller. A new recursive descent MUST enter the guard.
- **Transpile-time expansion is bounded.** Brace expansion beyond 1,000,000 words is a parse error.
- **Regex matches time out.** `HostRegexTimeout` sets the process default (15 s) before any `Regex`
  exists; a managed match cannot be stopped any other way.
- **A cancelled command that will not stop poisons the host.** `StuckCommandWatchdog` (daemon only):
  20 s after a command's cancellation fired, the host logs the reason and exits; launchers respawn.
- **`kill $$` ends the command, not the host.** `$$` is the host's pid; the kill cmdlet applies bash's
  outcome (the script dies of the signal, status 128+N) to the current command. A signal from another shell
  (`sh -c "kill -USR1 $$"`, or a real SIGUSR1/2 on Unix) arrives as a `Mode.Signal` request, is answered
  without the lease, and reaches only the running command (`ShellSignals.Deliver`, background-jobs.md §13).
  Nothing in that path can end the host: a malformed request fails its connection, and a failing stop is
  swallowed.
- **Every accepted connection is answered.** A failure escaping `Connection.HandleAsync` is caught
  while the stream is still open (`HostServer.TryAnswerFailureAsync`: stderr frame + EXIT 125, logged
  with type and top frame); a request that never arrives or is malformed gets EXIT 2. Only a client
  that is already gone gets no frame. (A late catch here was "Response stream closed before EXIT
  sentinel" under load.)
- **Accept never caps below connections.** The
  accept loop backs off exponentially and logs once per failure streak.- **The launcher never dumps a stack trace.** Every execute path (`-c`, `.sh`, `.ps1`) goes through
  `HostFailureGuard`: timeout → 124, host failure → one `ps-bash:` line + 125.
- **Every failure is attributable.** `~/.psbash/host.log` (`HostLog`; `PSBASH_HOST_LOG` overrides;
  rotated at 4 MB) prefixes each line with `pid=`, `ep=`, `v=`; each host logs `start` and
  `exit code=N reason=…` (idle timeout, launcher exited, client shutdown request, console signal,
  unhandled exception, or `none recorded` for an Environment.Exit outside the host / a signal). A
  start line with no exit line means an uncatchable death (stack overflow, FailFast, kill). A reset
  after STARTED tells the launcher user whether the host process exited (with its code) or a live
  host dropped the connection (`IpcWorker.DescribeStartedReset`).
- **Not covered:** PowerShell code a command runs in-process (`Invoke-Expression`, .NET calls) can
  always end the host; only a process per command could isolate it.

## Measured warm-path latency (installed 0.13.0-dev, Windows 11, warm daemon)

Mean of 20 sequential launcher invocations from a shell, output discarded (2026-10-23):

| Command | ms |
|---|---|
| `cmd.exe /c exit` (process-spawn floor) | 19 |
| `ps-bash -c ':'` | 301 |
| `ps-bash -c 'echo a \| cat'` | 328 |
| `pwsh -NoProfile -c 1` (cold PowerShell, for scale) | 389 |

So a warm `ps-bash -c` costs about 280 ms over the spawn floor, and a second command adds only about 27 ms: the cost is per
launcher invocation (launcher start, IPC connect, transpile, per-request setup), not per command. This is the number a
latency optimization (the spec suite's in-host runner, a persistent client) must beat; it has not been profiled by phase yet.

### Where the time goes (partial)

`PSBASH_TIMING=1 ps-bash -c CMD` prints `[ps-bash-timing] transpiled | host connected | executed: N ms` (ms since process start) to stderr.
Measured so far:

- `ps-bash --version` (launcher start only, no parse of a command, no IPC) costs about 106 ms in the 20-run mean above,
  so process start plus runtime init is roughly a third of a warm `-c`.
- Through `tman dev` (a per-invocation host, so the connect phase includes a cold runspace and is not the warm number):
  start to transpiled 90 to 115 ms, executed minus connected 75 to 90 ms.
- The warm-daemon split between connect and execute has NOT been measured: the installed release predates the flag and a
  dev build cannot be run against a shared daemon without `tman dev` forcing a per-invocation host. Measure it after the next release.

### Nested launcher (`bash -c` inside a command), by phase

Debug JIT dev build, Windows 11, heavily loaded machine (other agents' builds), 2026-11-15. Per-phase marks in the launcher
and host; medians of 9 warm calls per topology. A = explicit `PSBASH_IPC_ENDPOINT` (middle shell on slot 0); B = default
nested path (joins the enclosing host's session; middle shell on slot 1, inner launchers depth 2).

| Phase (warm) | ms |
|---|---|
| `Invoke-BashBash` spawn to launcher process start | 2 to 10 |
| process start to `Main` (runtime start, Debug JIT) | 100 to 360 |
| `Main` to transpiled (Debug JIT parse + emit) | 120 to 450 |
| endpoint resolve + warm-host check (`StartAsync`) | 50 to 170 |
| one BUSY refusal round trip (connect, env-block request, exit 76) | 50 to 160 |
| host: request read to worker acquired | 1 to 30; 200 to 700 when the slot's one warm runspace is still being replaced |
| host: env reset + `echo x` + exit frame | 70 to 250 |

Removed (`executor-slots.md` "Inside the enclosing session"): in B every inner launcher met slot 0 BUSY about 8 times and
backed off for the 250 ms spill delay before spilling (slot hosts are spawned detached, so the ancestry walk never saw it
was nested), then met its own enclosing slot BUSY once more: first connect to host request went from about 360 ms to
about 120 ms, 8 refusals to 0. The launcher now also resolves and reaches the host while it transpiles. A whole warm nested
call still costs about 0.8 to 1.2 s on this machine (A and B alike): what is left is launcher process start, JIT and
transpile, plus per-command host setup. `timeout 0.5 $SH -c` cannot fit that without running `bash -c` in-process.
