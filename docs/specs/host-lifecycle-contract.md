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
- Treat Windows named pipes as kernel objects, not filesystem paths.

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
structurally clean PowerShell session, and concurrent launchers run in parallel on
distinct runspaces (bounded by `PSBASH_POOL_MAX`). Warm spares are created on
dedicated (LongRunning) threads so warm-up never starves the host's async accept /
health-handshake loop. Pool size: `PSBASH_POOL_WARM` (default 2) warm spares,
`PSBASH_POOL_MAX` (default: CPU count clamped to [2, 8]) concurrency cap.

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

## Metadata Record

Each host owns a JSON metadata record beside the endpoint identity:

- `pid`: operating-system process id of the host.
- `executablePath`: full path to the host executable that created the record.
- `protocolVersion`: `HostProtocol.ProtocolVersion`.
- `buildIdentity`: the same build identity advertised by the health payload.
- `endpoint`: canonical endpoint string returned by `IpcTransportFactory`.
- `transportScheme`: `unix` or `pipe`.
- `startedAt`: UTC instant when this host began startup.
- `lastSeen`: UTC instant written after successful health checks and updated by
  the host while serving.
- `owner`: stable user identity for the endpoint owner. On Windows this must be
  the current user SID when available, with account name only as display data.
  On POSIX this must include uid when available, with user name as display data.

The record path is derived from the canonical endpoint:

- `unix`: `<endpoint>.host.json`, next to the socket path.
- `pipe`: `<temp>/ps-bash/<pipe-name>.host.json`, because a Windows named pipe
  endpoint is a kernel name under `\\.\pipe\`, not a removable filesystem
  object.

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

## Endpoint Cleanup vs Process Cleanup

Endpoint cleanup means removing bind artifacts so a replacement can claim the
canonical address:

- For `unix`, cleanup may unlink the socket path and remove the metadata record
  only after ownership validation says the artifacts are stale or obsolete.
- For `pipe`, there is no endpoint file to unlink. Cleanup is limited to the
  metadata and lock sidecars. The named pipe disappears when its server handles
  close; clients cannot delete it the way they delete a Unix socket path.

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

This gives single-host-per-**session** behavior: one daemon per `(user, session)`,
where the session token is an explicit `PSBASH_SESSION` or, when unset, the
launcher's parent process id (`ProcessAncestry.GetParentProcessId`). Repeated `-c`
invocations from one shell / agent share a parent → resolve the same endpoint →
reuse one warm daemon; independent shells / agents resolve distinct endpoints, so
load spreads instead of contending on a single per-user host (the contention that
serializes N callers behind one warm pool and starves it under multi-agent load).
The session token is **per-session, not per-invocation** — warm reuse within a
session is preserved; only independent sessions diverge. When no session token is
available the endpoint degrades to the historical per-user name (`host-{user}`).
The canonical endpoint remains stable for a given session; lifecycle metadata and
the lock make replacement decisions explicit, scoped to that endpoint.

### Implementation (single-flight spawn)

Steps 1-6 are implemented by `IpcWorker.EnsureHostReachableAsync` (the
`Lifetime.Daemon` path) plus `HostSpawnLock`
(`src/PsBash.Core/Runtime/Ipc/HostSpawnLock.cs`):

- The lock is an exclusively-opened file (`FileShare.None`) under
  `{TEMP}/ps-bash/spawn-{scheme}-{hash(endpoint)}.lock`. It is **endpoint-scoped**,
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

Without this, concurrent cold-start launchers each reached the spawn path and left
N-1 orphan runspaces — `UnixSocketTransport.ListenAsync` unlinks-before-bind (each
racer steals the socket path) and the Windows named pipe allows 16 server instances
(N hosts split the shared session state). Regression coverage:
`PsBash.Host.Tests/Server/HostStartupStressTests.ColdStartHerd_ConcurrentDaemonLaunchers_SpawnExactlyOneHost`
(a `Category=Stress` test; see `scripts/test.sh --stress`).

## Interaction With `IpcTransportFactory`

`IpcTransportFactory.ResolveEndpoint()` continues to be the source of truth for
the endpoint and transport scheme. The resolved name now carries a per-**session**
segment (`host-{user}-s{token}`, token = `PSBASH_SESSION` or parent pid); lifecycle
code derives metadata and lock paths from the resolved `(scheme, endpoint)` pair, so
each session's metadata/lock are naturally isolated. The session segment is the
**only** discriminator added — a per-*process* (per-invocation) suffix must NOT be
added to the canonical endpoint, because that would defeat warm-pool reuse (every
command would cold-start its own daemon). `Lifetime.PerInvocation` is the dedicated
path for process-local endpoints (`ResolvePerInvocationEndpoint`).

`IpcTransportFactory.RetireEndpoint()` remains endpoint cleanup only. For `unix`
it may unlink the socket path after lifecycle validation has decided cleanup is
allowed. For `pipe` it must remain a no-op for the endpoint, because Windows
named pipes are kernel namespace objects and have no path to delete.
