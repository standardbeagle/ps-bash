# Bug: pinned daemon host wedges → every `-c` invocation fails with "host communication failed" (no mid-command recovery)

**Reported:** 2026-06-16
**Installed build:** ps-bash 0.10.13 (`C:\Users\andyb\.local\bin\ps-bash.exe`); repo HEAD at 0.10.14
**Platform:** Windows 11 (26200), unix-domain-socket transport
**Severity:** High — wedges the Claude Code Bash tool for an entire session

## Symptom

Every Bash-tool command — including a bare `echo alive` — fails with:

```
ps-bash: host communication failed: Unable to read data from the transport connection:
An existing connection was forcibly closed by the remote host..
exit 125
```

The failure persists across retries within the same launcher lineage.

## Key diagnostic asymmetry: it is endpoint-specific, not global

A **fresh** launcher process succeeds instantly:

```powershell
& 'C:\Users\andyb\.local\bin\ps-bash.exe' -c 'echo alive'   # -> "alive", exit 0
```

while the Claude Code Bash tool keeps failing. Endpoints are per-session unix sockets
(`%TEMP%\ps-bash\host-andyb-s<pid>.sock`, resolved from the parent-pid ancestry chain — see
the `per-session-daemon-endpoint` memory). The Bash tool's launcher is **pinned** to one
**wedged** host; a brand-new process resolves/spawns a healthy one. So the wedge is per-host,
and a single poisoned daemon poisons every invocation routed to its endpoint.

## Host-side evidence (`~/.psbash/host.log`, 5148 lines / 745 KB)

Today's entries are *exclusively* connection errors (the host log records only errors — no
lifecycle/accept-success lines):

| Count (today) | Message |
|---|---|
| 437 | `connection error: Unable to write data to the transport connection: An existing connection was forcibly closed by the remote host.` |
| 5 | `accept error: An existing connection was forcibly closed by the remote host.` |

Last entries `2026-06-17T02:05–02:11Z` (= 22:05–22:11 local), matching the live failures.

**Two halves of one RST:** the launcher logs the *read* side forcibly-closed; the host logs the
*write* side forcibly-closed. The host is still **accepting** connections (only 5 accept errors)
but **every response write fails** — the launcher has already torn the connection down (RST)
before the host finishes writing its response. Consistent with: launcher hits its call
deadline, cancels, aborts the socket; host's pending write then fails. Each pinned invocation
produces one host-side write error → the 437 count is the accumulated retry storm.

Historical signatures in the same log (prior host-wedge episodes, **not** today):
- `2026-06-08` — `accept error: All pipe instances are busy.` (Windows named-pipe instance exhaustion)
- `2026-05-21` — `connection error: Cannot perform operation because object "PowerShell" has already been disposed.`

> Note: an earlier hypothesis of a `SemaphoreSlim` `ObjectDisposedException` could **not** be
> confirmed in the log — the only disposed-object errors are the `PowerShell`-disposed ones above.

## Root-cause analysis (recovery gap)

`src/PsBash.Shell/Program.cs:383-389` — the command runs at line 368 and any transport reset is
swallowed into exit 125 with **no kill-and-retry**:

```csharp
exitCode = await worker.ExecuteAsync(BuildInvocationCwdPreamble() + pwshCommand); // 368
...
catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
{
    Console.Error.WriteLine($"ps-bash: host communication failed: {ex.Message}");
    return 125;   // <- no respawn, no fresh-host retry
}
```

The pre-flight `IpcWorker.EnsureHostReachableAsync` (`src/PsBash.Core/Runtime/IpcWorker.cs:164`)
*does* probe and respawn an **unhealthy** host. But its health check is a lightweight 750 ms
handshake (`CheckHealthAsync`). A host that **passes the handshake yet resets the actual command
connection** is classified Healthy, reused, and the mid-command reset bubbles straight to exit
125. Nothing kills the poisoned-but-handshake-healthy daemon, so it keeps answering the
handshake and failing every real command for every launcher pinned to that endpoint.

## Contributing: stale-artifact leak

`%TEMP%\ps-bash\` holds ~116 zero-byte `host-andyb-s*.sock` files and ~250 `spawn-*.lock` files
dating back to 2026-06-13 — one per session, never cleaned up. Not the direct cause, but
unbounded growth and a sign session endpoints/locks are not reaped on exit.

## Proposed fixes

1. **Mid-command recovery (primary).** On an `IOException`/`SocketException` out of
   `worker.ExecuteAsync`, treat the host as poisoned: kill the owned host (ownership-gated, as in
   `SpawnOrReplaceHostAsync`), clear endpoint+sidecar, respawn, and retry the command **once**
   before returning 125. A reset mid-command is exactly the "wedged owned host" case the spawn
   path already handles — it just isn't reached from the execute catch.
2. **Stronger health probe.** Make `CheckHealthAsync` actually round-trip a trivial command (or
   have the host self-check its SDK runspace) so a host that can handshake but not execute is
   classified Unhealthy and replaced by the existing pre-flight path.
3. **Artifact reaping.** Reap stale `host-*.sock` / `spawn-*.lock` whose owning process is gone
   (sidecar PID check) on launcher startup or host shutdown.

## Immediate mitigation

Kill the wedged host(s) so the next launcher spawns fresh:

```powershell
Get-Process ps-bash-host -ErrorAction SilentlyContinue | Stop-Process -Force
```

Or isolate this session's endpoint: set `PSBASH_IPC_ENDPOINT=pipe:psbash-<guid>` (or
`PSBASH_SESSION=<unique>`) for the Bash tool's environment.

## Resolution (2026-06-17 redesign)

The launcher↔host connection flow was hardened along three axes, each validated by
the chaos burn-in harness (`scripts/burn-in.ps1`):

1. **Mid-command reset recovery (safe pre-output retry)** — `IpcWorker.SendRequestAsync`
   now wraps the connect→write→read exchange in a retry loop. On a transport RESET
   (`IsTransportReset`: `IOException`/`SocketException`) it retires the broken host and,
   **only if no output frame was delivered**, respawns (reuse-if-healthy else fresh, via
   `EnsureHostReachableAsync`/`RetireAndRespawnAsync`) and retries once. If output already
   streamed, it does not retry (no double-execute of a side-effecting command) but still
   retires so the next call self-heals.
2. **Host-liveness watchdog** — while connected, a background loop polls the host PID
   (owned handle for PerInvocation, sidecar for Daemon). If the host process dies, it trips
   `hostDeadCts`, converting the death into the recoverable reset path. This fixes the
   Windows AF_UNIX case where a killed peer does **not** surface a socket reset on the
   pending read — previously a hang to the idle timeout (exit 124), or forever under the
   unbounded default. A genuinely slow-but-alive host is never tripped (no false retry).
3. **Stale-artifact reaper** — `StaleArtifactReaper.Reap()` runs once at host startup and
   deletes only artifacts whose owner is provably gone (dead-PID sidecar + companion socket;
   exclusively-openable spawn lock). Stops the unbounded `%TEMP%/ps-bash` leak.

### Burn-in evidence (6 min, 6 workers, saboteur killing hosts + corrupting artifacts every ~7s)

| Build | ok rate | transport125 | idle-timeout(124) | hangs | artifact leak (sock/lock/sidecar) |
|---|---|---|---|---|---|
| installed 0.10.13 (baseline) | 53% | 31% | 11% | 7 | 15 / 18 / 138 |
| + recovery + reaper | 67% | 9% | 16% | 9 | 7 / 1 / 5 |
| + host-liveness watchdog | _(see FINAL-REPORT in artifacts/burn-in/watchdog-*)_ | | | | |

The harness raises a WEDGE ALARM (with host.log tail + process dump) if a sequential recovery
probe fails several consecutive cycles with no fresh sabotage in between — the metric that
separates expected chaos-noise from a genuine stuck wedge. None fired on the hardened build.

## Reproduction

1. Run a slow/complex `-c` command (the trigger here was `find … -exec cat {} \; ; echo ; curl …`)
   such that the launcher hits its call deadline and aborts mid-response.
2. The pinned daemon enters the write-fails state.
3. Every subsequent `-c` (even `echo alive`) on the same session endpoint returns exit 125 with
   "host communication failed", while a fresh launcher process succeeds.
