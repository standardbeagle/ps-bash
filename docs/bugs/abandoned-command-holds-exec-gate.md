# Bug: an abandoned command keeps running and blocks every other session in the daemon

**Found:** 2026-07-26 (while diagnosing a test-suite flake family)
**Severity:** High — one killed launcher stalls the whole session for the command's full duration
**Fixed:** `Connection.WatchForClientDisconnectAsync` (per-connection client-disconnect watchdog)

## Symptom

A launcher is killed (timeout, Ctrl-C, agent give-up) while its command is running in the
shared daemon. The command keeps running to completion in the host, holding
`SdkWorker`'s **process-wide execution gate**, so every subsequent `-c` on that endpoint
queues behind work whose output nobody will ever read.

Measured on Windows, named-pipe transport:

| | next command latency |
|---|---|
| launcher killed 2 s into `sleep 30`, before fix | **30.9 s** |
| same, after fix | **0.2 s** |

## Why the existing guards missed it

The design already anticipates a dead client wedging the gate — `IpcOutputQueue`'s
`DefaultStallTimeoutMs` exists precisely for that, and its comment says so. But it can only
fire when the command **produces output**: the detector is the output queue filling up
because nobody is draining it. A command that writes nothing (`sleep`, a long `find` before
its first hit, a build step) never enqueues a frame, so nothing ever noticed the client was
gone.

Nothing else watched the connection: the `CancellationToken` reaching
`SdkWorker.ExecuteWithOutputAsync` is the *server lifetime* token, not a per-connection
"client went away" token. `SdkWorker.ExecuteAsync` already stops the PowerShell pipeline
when its token fires ("so a runaway command holds `_globalExecGate` forever" is called out
in that comment) — the cancellation machinery was in place, but nothing ever tripped it.

## Note on the gate itself (NOT a bug)

Serializing execution is deliberate and correct: bash variables are `$env:NAME` and the cwd
is `[System.Environment]::CurrentDirectory`, both process-global and shared by every runspace
in the host, so concurrent execution corrupts shared state. A **live** slow client blocking
others is the documented cost of that design (opt out per-launcher with
`PSBASH_PER_INVOCATION=1`). The bug was only that an **abandoned** command got the same
protection it no longer deserved.

## Fix

`Connection.HandleAsync` installs a per-connection watchdog before executing: a single
pending 1-byte read on the transport. In framed mode the entire request (including any
stdin/script body) is consumed by `ReadRequestAsync` and the client sends nothing further,
so that read can only ever complete with EOF (clean close) or throw (reset) — either of
which trips a CTS linked into the command's cancellation token.

Guards that keep it from firing wrongly:

- **Only a live duplex transport** — `!stream.CanSeek`. On a seekable stream (an in-memory
  test double) a read returning 0 means "end of buffer", not "peer gone"; watching one
  abandons every command. This was caught by two existing `Connection` tests that drive a
  `MemoryStream`.
- **Only framed mode.** An interactive session's stream carries further protocol traffic
  that the detector must not consume.
- **EOF only.** A read returning DATA is an unexpected protocol byte, not a disconnect, and
  does not trip the watchdog.
- **No half-close hazard.** The launcher never calls `Socket.Shutdown(SocketShutdown.Send)`
  — it writes, flushes, and keeps the socket open to read the response — so EOF genuinely
  means the peer is gone, on sockets as well as named pipes.
- The watchdog never throws; it is advisory. On transports where a pending read is not truly
  cancellable it is simply completed later by the stream's disposal.

## How it was found

A flake family in `PsBash.Escalation.Tests`: ~1 failure per full-suite run, a DIFFERENT test
each time, always the same signature — `ps-bash.exe did not exit within 30s` with **zero**
partial stdout and stderr. Per the "flake family = one harness bound" rule this was one
systemic cause, not N flaky tests — but the cause turned out to be a product bug rather than
a too-tight bound.

The chain of elimination:

1. Cold-start latency measured at 4.1 s, warm at 0.3 s; 8 concurrent cold starts on one
   endpoint all finished under 6.2 s — so contention was not it.
2. A saboteur killing hosts every 1.5 s under 4 concurrent workers produced honest exit-125
   diagnostics and a 12.2 s worst case — no hangs. Recovery was working.
3. Excluding `Regression_ProcessSpawnWithTimeout` made the suite 21/21 twice **and** cut its
   runtime from 76 s to 28 s. That test runs `ps-bash -c "Start-Sleep 60"` and kills the
   launcher tree after 2 s — parking a 60-second abandoned command on the shared daemon.
4. Direct repro confirmed it, independent of the test suite (table above).

After the fix the escalation suite is 22/22 three consecutive runs at 25–34 s. The flakes
disappeared because the root cause is gone — no bound was widened.

## Regression tests

- `PsBash.Host.Tests/Server/ConnectionClientDisconnectTests.cs` — verified red before the
  fix (timed out at 30 s waiting on a 60 s abandoned sleep), green after. Includes the
  no-false-positive case: a client that stays connected completes normally.
- `PsBash.Escalation.Tests` `Regression_ProcessSpawnDrainBoundedWhenGrandchildHoldsPipe` —
  the sibling harness hole found in the same investigation (see below).

## Sibling finding: unbounded post-exit drain in `ProcessSpawn`

`PsBash.Testing.ProcessSpawn` bounded the wait for process EXIT but not the stdout/stderr
drain that follows it. `ReadToEndAsync` completes on pipe EOF, not on child exit, so a
surviving grandchild holding the write end blocked forever — past a timeout that the child's
exit had already satisfied. That is the mechanism behind "running `PsBash.Host.Tests`
immediately after another suite can hang the test harness". Now bounded by
`ProcessSpawn.DrainGrace` (default 20 s, `PSBASH_TEST_DRAIN_GRACE_SEC`), reported as
`SpawnDrainTimeoutException` naming the likely cause.
