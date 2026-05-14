# Host Lifecycle Metadata and Ownership Contract

This contract defines how launchers decide whether the canonical per-user
`ps-bash-host` can be reused, replaced, or left alone. It is intentionally a
design contract only: current behavior may be simpler until the lifecycle
implementation lands.

## Goals

- Keep the existing canonical per-user endpoint from `IpcTransportFactory`.
- Make host reuse decisions from explicit metadata plus a health handshake, not
  from endpoint presence alone.
- Distinguish endpoint cleanup from process cleanup.
- Treat Windows named pipes as kernel objects, not filesystem paths.

## Scope: this contract governs the shared `Daemon` host only

This contract covers the **shared, reusable** `ps-bash-host` — the one reached
through `IpcWorker.StartAsync` with `Lifetime.Daemon` on the canonical per-user
endpoint. As of PTY-10 the only caller of that path is the `ps-bash host
restart` subcommand (`src/PsBash.Shell/HostCommands.cs`).

Two host populations are explicitly **out of scope**:

- **`Lifetime.PerInvocation` hosts** (the default for `-c`, stdin pipe, and
  script-file modes). Each is private to one launcher invocation on a
  process-local endpoint, is owned by that launcher's `IpcWorker`, and is killed
  on `DisposeAsync`. There is no discovery, no sidecar ownership classification,
  and no reuse — so none of the Reusable/Obsolete/Stale/Unsafe machinery below
  applies.
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

This gives tmux-style single-host-per-user behavior without per-session endpoint
names. The canonical endpoint remains stable; lifecycle metadata and the lock
make replacement decisions explicit.

## Interaction With `IpcTransportFactory`

`IpcTransportFactory.ResolveEndpoint()` continues to be the source of truth for
the per-user endpoint and transport scheme. Future lifecycle code should derive
metadata and lock paths from the resolved `(scheme, endpoint)` pair and should
not add per-process suffixes to the endpoint itself.

`IpcTransportFactory.RetireEndpoint()` remains endpoint cleanup only. For `unix`
it may unlink the socket path after lifecycle validation has decided cleanup is
allowed. For `pipe` it must remain a no-op for the endpoint, because Windows
named pipes are kernel namespace objects and have no path to delete.
