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

## Root cause 1 — uniq file mode (not reproduced; residual)

The reported `Uniq_FileMode_*` empty read was **not reproduced** in this
investigation: ~10 heavy full-project runs of `PsBash.Cmdlets.Tests` (with the
host at 2–3× CPU oversubscription, which reproduced other load-sensitive
failures — see below) never failed any uniq test. The test writes its file with
`File.WriteAllText` (flushed + closed) into a per-test GUID temp dir and reads it
back by **absolute path**, so there is no write/read race and no shared temp path
between the three cases. The acceptance criterion "deterministic reproducing test"
cannot be met for a mechanism that does not reproduce, so **no change is made**:
a speculative fixture "fix" would be an unverifiable retry, which the task
forbids. If this resurfaces it needs a captured raw failure (the reported "garbled
char at pos 0" suggests an encoding/BOM artifact, not an empty file).

## Observed residual (separate, in scope of the same load story)

Under the same heavy oversubscription, `BashRuntimeTests.RunChildProcess_*`
(`..._OutputAboveCaptureLimit_IsTruncatedButStillDrained`,
`..._LargeStderr_DrainsConcurrentlyWithoutDeadlock`) failed on their fixed 30 s
budget. These are `RunChildProcess` timeout-under-load cases, not the three named
surfaces; they are left for a follow-up rather than widened into this change.
