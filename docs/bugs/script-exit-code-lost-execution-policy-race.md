# Bug: lost `.ps1` exit code and oracle cold-host timeouts (suite flakiness under load)

**Reported:** 2026-09-27
**Platform:** beagle-ab2, Windows 11; `tman run --alias test` on `main`
**Severity:** High — blocks the publish gate (`test`), 56 commits waiting behind it

## Symptom cluster

The suite was flaky: run 1 (load ~28) had 12 failures, run 2 had 2 different
failures, isolated reruns passed some times and failed others with no code
change. Three named surfaces:

1. `InvokeBashUniqCommandTests.Uniq_FileMode_*` — "Collections differ,
   Expected `["a","b","c"]`, Actual `[]`".
2. Differential oracle — `OracleTimeoutException: ps-bash.exe did not exit
   within 15s` on trivial scripts.
3. `ProgramEndToEndTests.ScriptFile_Ps1_ExitCodePropagates` — `Expected: 42,
   Actual: 0`.

## Root cause 3 — execution-policy race (lost `.ps1` exit code)

Script-file mode dot-sources a `.ps1`. The host's warm pool opens runspaces
**concurrently** (`WorkerPool.TopUpWarm` on dedicated threads). SMA resolves the
effective execution policy from a **process-global cache** populated lazily on
the first runspace open; that concurrent first init is racy. When a runspace
loses, it resolves to **Restricted**, so the dot-sourced `.ps1` fails to LOAD as
a **non-terminating** error, and the host reports `$LASTEXITCODE = 0` for a
script that never ran — a silent success.

**Evidence (pre-fix):** `ScriptFile_Ps1_ExitCodePropagates_AcrossConcurrentColdHosts`
(12 fresh cold hosts) reported a non-42 exit in **7 of 12** iterations, stderr:

```
File ...ps-bash-test-<guid>.ps1 cannot be loaded because running scripts is
disabled on this system.
```

### Fix

Pin the PROCESS-scope policy to `Bypass` before any runspace opens:

- `SdkRunspace.PinProcessExecutionPolicy()` sets
  `Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", "Bypass")`
  and is called from `SdkRunspace.Create()` and `PsBash.Host/Program.Main`.
- The launcher (`PsBash.Shell/Program.cs`) also exports it so a spawned host
  inherits it **at process start** — SMA snapshots the policy then, so setting
  it only inside the host's own `Main` can still lose the race.

`iss.ExecutionPolicy` is per-ISS and does **not** seed the process-global cache,
so it cannot prevent the race.

### Regression test

`ProgramEndToEndTests.ScriptFile_Ps1_ExitCodePropagates_AcrossConcurrentColdHosts`
— 12 fresh private hosts (`PSBASH_PER_INVOCATION=1`), each must report 42. Not a
retry: every iteration MUST succeed; the loop only widens exposure to the
concurrency the defect needs. The in-process companion is
`SdkWorkerTests.ExecuteAsync_DotSourcePs1WithExitCode_ConcurrentRunspaces_PropagateExitCode`.

## Root cause 2 — oracle cold-host timeouts (decision)

The differential suite spawned ps-bash with `PSBASH_PER_INVOCATION=1` — a **cold
host per spawn**. A cold ps-bash start (runspace + psm1 import) is ~3 s idle and
exceeds the suite's fixed 15 s per-spawn timeout under host load, so trivial
scripts (`x=hello; echo ${x}`) failed with `OracleTimeoutException`.

**Decision:** run **one warm host per fixture** (`BashOracleFixture`) — set
`PSBASH_PER_INVOCATION=0` plus a per-fixture IPC endpoint, so the first spawn
warms the daemon and every later spawn reuses it, removing the cold-start cost
entirely. We deliberately did NOT derive a larger timeout from a measured
startup: a measured-timeout fix is load-sensitive again, whereas a warm host
eliminates the cost. The fixture's idle window (`PSBASH_HOST_IDLE_SECS=20`) is
short so the daemon does not outlive the suite and lock `src/PsBash.Shell/bin`
DLLs against the next build (the original reason `PSBASH_PER_INVOCATION=1` was
forced).

The `RunOneAsync` guard that defaults launcher spawns to `PSBASH_PER_INVOCATION=1`
is unchanged — the warm-host choice is explicit, so the guard still protects any
spawn that does not opt out.

### Regression test

`OracleTests.RunPsBashAsync_PsBashLauncher_UsesWarmSharedHost` — asserts the
fixture's spawn env keeps `PSBASH_PER_INVOCATION=0` and carries the shared
endpoint. Pre-fix it observed `"1"`; post-fix `"0"`.

### Residual (CLOSED — golden path routed to a canonical warm host)

The warm-host fix initially covered `BashOracleFixture.RunPsBashAsync` (the
`EqualAsync`/`RunBothAsync` differential path) but **not**
`AssertOracle.GoldenAsync`. `GoldenAsync` called `BashOracleFixture.RunOneAsync`
directly with `CanonicalEnv.ForPsBash(...)` and no `extraEnv`;
`CanonicalEnv.ForPsBash` sets `PSBASH_PER_INVOCATION=1` (CanonicalEnv.cs:101), so
every golden spawn paid a **cold host start** and a golden with a 15 s timeout
under load failed with the exact reported signature:

```
SeedDifferentialTests.Differential_CommandSubstitution_NestedQuoting [FAIL]
OracleTimeoutException : oracle timeout: ps-bash.exe did not exit within 15s
running script: echo "today is $(date +%Y)"
```

**Closed:** `GoldenAsync` now routes through a **separate canonical warm fixture**
(`AssertOracle.CanonicalFixture`) via `BashOracleFixture.RunPsBashAsync(..., canonicalizeEnv: true)`.

Two design points, each a bug the first attempt would have hit:

1. **Separate endpoint from the differential fixture.** A daemon freezes its
   environment when it starts, so if a golden shared the differential fixture's
   endpoint, a non-canonical `EqualAsync` spawn could warm that daemon first and
   freeze the runner's `$USER`/locale into what the golden observes. The
   canonical fixture has its own endpoint; a test asserts the two differ.
2. **One stable canonical HOME/TEMP per process, not per call.** On Windows the
   module extraction dir is `{TEMP}/ps-bash/module-{version}-{hash}`. A per-call
   canonical HOME was deleted by `GoldenAsync`'s `finally` while the warm daemon
   still referenced it, so the next golden spawn raced the daemon over a vanished
   module directory (`PsBash.Cmdlets.dll ... being used by another process`).
   HOME/TEMP is now a single lazily-created, process-lifetime `psb-g<13hex>` root
   that matches the daemon's lifetime and is left for OS temp cleanup.

**Regression test:** `OracleTests.GoldenAsync_PsBashSpawn_UsesCanonicalWarmHost`
— the spawn seam must show `canonicalizeEnv: true`, `PSBASH_PER_INVOCATION="0"`,
a warm-host endpoint, and an endpoint distinct from the differential fixture's.
Pre-fix it observed `PSBASH_PER_INVOCATION="1"` and no endpoint.

## Root cause 1 — uniq file mode (premise false; hardened + guarded)

The reported `Uniq_FileMode_*` empty read is **not a shared-temp-path race on
this tree**. The reported mechanism ("shared temp path between parallel tests",
"concurrent delete") cannot occur, and each of the three candidate mechanisms
was ruled out at the code level:

1. **Temp dir is per-instance and unique.** `InvokeBashUniqCommandTests.cs`
   computes `Path.Combine(Path.GetTempPath(), $"psb-uniq-{Guid.NewGuid():N}".Substring(0, 22))`.
   The `.Substring(0, 22)` binds to the **interpolated string** (member access
   precedence), not to the `Path.Combine(...)` call, so the GUID survives:
   the name is `psb-uniq-<13 hex>`. This matches the other 19 cmdlet test
   fixtures verbatim. (The failure would require the form
   `Path.Combine(...).Substring(0, 22)`, which would collapse the dir to
   `C:\Users\<user>\AppData` — not what the source says.)
2. **The write is flushed and closed before the read.** `File.WriteAllText`
   returns only after the handle is closed; the read uses the **absolute** path.
3. **No concurrent delete inside the class.** xUnit runs methods of one test
   class serially (one collection = one class); the shared `SharedPwshFixture`
   is per-class. `Dispose()` (per-test instance) deletes only that instance's
   unique directory.

**Evidence (this tree):** the three file-mode tests were run 15× against the
full `PsBash.Cmdlets.Tests` assembly with 8 background CPU-burner jobs pinning
the box — 15/15 passed (45/45 test cases). A prior ~10-run heavy oversubscription
sweep (see `## Observed residual` below) also never failed a uniq test.

### Change made (no speculative "fix", no retry)

- **Hardened the fixture delete.** `Dispose()` now routes through the shared
  `FileSystemHelpers.DeleteDirectoryForce` instead of a raw
  `Directory.Delete(recursive: true)` — the os-interface rule requires the
  force-delete helper for any destructive delete (raw delete throws on a
  Windows read-only descendant). This removes the raw-delete surface the report
  named without altering behavior on the current inputs.
- **Added a deterministic isolation guard,**
  `InvokeBashUniqCommandTests.TmpDir_PerInstance_IsUniqueAndGuidNamed`: two
  constructed instances must get distinct directories of the form
  `psb-uniq-<13 hex>` under `Path.GetTempPath()`. It passes on this tree and
  fails if a future edit truncates the COMBINED path (the exact hazard the
  report's "garbled char / shared path" symptom pointed at), so the isolation
  contract is now pinned rather than assumed.

If the failure resurfaces it needs a **captured raw failure** (the reported
"garbled char at pos 0" reads as an encoding/BOM rendering artifact, not proof
of an empty file); the isolation invariant above will be the first thing ruled
out.

## Observed residual (separate, in scope of the same load story)

Under the same heavy oversubscription, `BashRuntimeTests.RunChildProcess_*`
(`..._OutputAboveCaptureLimit_IsTruncatedButStillDrained`,
`..._LargeStderr_DrainsConcurrentlyWithoutDeadlock`) failed on their fixed 30 s
budget. These are `RunChildProcess` timeout-under-load cases, not the three named
surfaces; they are left for a follow-up rather than widened into this change.

## Escalation suite: same cold-host cost (added-criteria review)

The escalation suite's `ProcessRunHelper` spawned `PSBASH_PER_INVOCATION=1` — a
cold host per launch — so under full-suite load its 30 s spawn budget was blown
the same way (review-added cases: `FaultInjectionTests.MissingCommand_Exits127`
→ `SpawnTimeoutException ... within 30s`;
`ScaleTests.Scale_LargePipe_WcCount` and
`KnownBadRegressionTests.Regression_LastExitcodeNotPollutedBetweenCommands` →
`Assert.Equal` value differences on a launcher that never reached a clean exit).

**Fix (same decision as root cause 2):** the suite now spawns on ONE shared warm
daemon (`ProcessRunHelper._sharedDaemon`, a `PSBASH_HOST_IDLE_SECS=20`
`IsolatedDaemon`) by default. A test that needs isolation passes its own env,
which wins: the deliberate kill-tree timeout test and the stdin-as-script test
pass `PerInvocationEnv`; the concurrent-daemon corruption test passes its own
`IsolatedDaemon.Env`. Cost: the suite dropped from 44 s to 33 s cold→warm and no
longer leaves a leaked dev-build host per launch (those leaks were a second
flakiness source — they lock `src/PsBash.Shell/bin` DLLs and turn the next build
into MSB3027).
