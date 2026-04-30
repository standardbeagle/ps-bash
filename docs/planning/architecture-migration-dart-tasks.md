---
title: "Dart task plan: in-process PowerShell host migration"
type: dart-task-plan
status: draft
date: 2026-04-29
origin:
  - docs/planning/architecture-migration-handoff.md
  - docs/plans/2026-04-28-001-refactor-in-process-powershell-host-plan.md
planning_skill: dartai:simple-planning
complexity_tier: architectural
---

# Dart Task Plan — Two-Binary IPC Host Migration

Source: yesterday's handoff (`architecture-migration-handoff.md`) and the
detailed plan (`2026-04-28-001-refactor-in-process-powershell-host-plan.md`).
This document re-shapes the 12 implementation units into bounded Dart tasks per
the `dartai:simple-planning` rubric — each task ≤ 5 files, ≤ 7 TDD steps,
< 200 lines of churn, with explicit dependencies. Two units (5 and 8) are split
to fit the rubric; Unit 12 is held as a parameterized verification task.

Adversarial validation tier: **architectural** (cross-cutting, multiple
subsystems, new patterns). Per the skill, deep adversarial planning is
recommended before execution begins; the existing plan doc already provides
the High-Level Technical Design and risk table that satisfy that gate.

Domain terms (consistent across tasks):

- **Launcher** — the AOT `ps-bash` binary (`PsBash.Shell`).
- **Host** — the JIT `ps-bash-host` binary (new `PsBash.Host`).
- **Worker** — `IWorker` abstraction (`PwshWorker`, `SdkWorker`, `IpcWorker`).
- **Transport** — IPC primitive (Unix socket / named pipe).
- **Protocol** — `<<<MODE:...>>>` + body + `<<<END>>>` + response sentinel.
- **Runspace** — long-lived `Microsoft.PowerShell.SDK` runspace inside the host.

New domain concepts introduced (must land in DOMAIN.md before code):

- `IWorker` — the seam abstraction for command execution.
- `HostLockFile` — the on-disk advertisement (`unix:` or `pipe:`) used for
  launcher → host rendezvous.

---

## Task Graph

```
T01 (IWorker seam)
 ├── T02 (Host skeleton + SdkWorker)
 ├── T03 (Transport + lock file)
 │    └── T04 (Wire protocol)
 │         ├── T05a (Host server + connections)
 │         │    └── T05b (Idle shutdown + parent-death watcher)
 │         └── T06 (IpcWorker client)
 │              └── T07 (Launcher dispatch + soft fallback)
 │                   ├── T08a (Mechanical move of REPL → host)
 │                   │    └── T08b (Interactive handoff wiring)
 │                   ├── T09 (Script mode: exit / source / set -e parity)
 │                   └── T10 (Process substitution: 3-path classifier)
 └── T11 (Build, publish, CI)
      └── T12 (Differential parity sweep + canary expansion)
```

---

## T01 — IWorker seam

```yaml
requested: |
  Carve a narrow worker boundary so launcher can swap implementations.
  (origin doc Unit 1)

domain_terms: [Worker, Launcher]
new_domain_concepts:
  - "IWorker: ExecuteAsync/QueryAsync/HasExited/OutputCallback contract,
    IAsyncDisposable. Pure refactor seam; first concrete impl is PwshWorker."

deliverable: |
  IWorker interface in PsBash.Core; PwshWorker implements it; Program.cs and
  InteractiveShell hold IWorker references via a factory delegate. No behavior
  change; full test suite green.

scope:
  files_to_create:
    - src/PsBash.Core/Runtime/IWorker.cs
    - src/PsBash.Core.Tests/Runtime/IWorkerContractTests.cs
  files_to_modify:
    - src/PsBash.Core/Runtime/PwshWorker.cs
    - src/PsBash.Shell/Program.cs
    - src/PsBash.Shell/InteractiveShell.cs

acceptance_criteria:
  - "IWorker compiles and PwshWorker : IWorker (RED→GREEN contract test)"
  - "git grep 'new PwshWorker' returns only the legacy-fallback site"
  - "./scripts/test.sh fully green with no expected diffs from prior baseline"

steps:
  1: "RED: contract test asserts IWorker.ExecuteAsync after DisposeAsync throws ObjectDisposedException"
  2: "GREEN: define IWorker; PwshWorker implements it; ObjectDisposedException added"
  3: "RED: contract test for OutputCallback save/restore across QueryAsync"
  4: "GREEN: lift the existing PwshWorker save/restore behind the interface"
  5: "Refactor: replace PwshWorker references in Program.cs with IWorker factory delegate"
  6: "Refactor: same swap in InteractiveShell.cs (StartWorkerAsync, EnsureWorkerAsync)"
  7: "Run ./scripts/test.sh — full suite green"

not_included:
  - "Adding new methods (ExecuteScriptAsync lands in T09)"
  - "Touching PsBash.Cmdlets (cmdlets keep their own runspace)"
  - "Visibility changes — InternalsVisibleTo deferred to T11"

dependencies: []
risk: low
estimated_lines: ~120
```

---

## T02 — Host project skeleton + SdkWorker

```yaml
requested: "Stand up PsBash.Host with shared runspace + SdkWorker. (Unit 2)"
domain_terms: [Host, Worker, Runspace]
new_domain_concepts: [] # SdkWorker is a concrete IWorker, not a new concept

deliverable: |
  ps-bash-host binary skeleton that loads PsBash.psm1 once into a shared
  runspace and provides SdkWorker:IWorker for in-process command execution.
  Tests prove module loads exactly once and state survives across calls.

scope:
  files_to_create:
    - src/PsBash.Host/PsBash.Host.csproj
    - src/PsBash.Host/Program.cs
    - src/PsBash.Host/Runtime/SdkRunspace.cs
    - src/PsBash.Host/Runtime/SdkWorker.cs
    - src/PsBash.Host.Tests/Runtime/SdkWorkerTests.cs
  files_to_modify:
    - ps-bash.sln
  # Note: 6 files. Test csproj treated as inherited from sln modify, not a
  # net-new logical surface. If gate enforces hard ≤5, split csproj+sln add
  # into a precursor task.

acceptance_criteria:
  - "RED→GREEN: SdkWorker.ExecuteAsync 'Invoke-BashEcho hello' emits hello, exit 0"
  - "RED→GREEN: cd /tmp then pwd across two ExecuteAsync calls reports /tmp"
  - "Module init counter incremented exactly once across N calls"
  - "dotnet publish ps-bash-host -r win-x64 --self-contained < 100 MB"

steps:
  1: "RED: SdkWorkerTests asserts echo hello returns 'hello' on OutputCallback"
  2: "GREEN: Program.cs stub + SdkRunspace builds InitialSessionState importing PsBash module via ModuleExtractor"
  3: "GREEN: SdkWorker.ExecuteAsync uses PowerShell.Create() bound to shared runspace"
  4: "RED: cross-call state test (cd then pwd)"
  5: "GREEN: confirm runspace reuse — no new test code, just verify the ExecuteAsync path uses shared runspace"
  6: "RED: module-load-once counter test"
  7: "GREEN: Set-PSReadLineOption-style guard in SdkRunspace; publish-size CI check"

not_included:
  - "IPC server (T05a)"
  - "Interactive REPL move (T08a/T08b)"
  - "exit/source/set-e script semantics (T09)"

dependencies: []
risk: medium  # SDK-on-AOT validation here is the project's core feasibility bet
estimated_lines: ~180
```

---

## T03 — IPC transport + lock file

```yaml
requested: "Connection abstraction + Unix-socket-with-pipe-fallback + lock file. (Unit 3)"
domain_terms: [Transport, Launcher, Host]
new_domain_concepts:
  - "HostLockFile: $TEMP/ps-bash/host-{user}-{sessionId}.lock advertising
    'unix:/path' or 'pipe:name'. Atomic write-then-rename; deleted on shutdown."

deliverable: |
  IIpcTransport with UnixSocketTransport + NamedPipeTransport implementations
  and HostLockFile with atomic write/read/delete. Round-trips a 1 KB buffer in
  both transports; named-pipe ACL restricts to current user.

scope:
  files_to_create:
    - src/PsBash.Core/Runtime/Ipc/IIpcTransport.cs
    - src/PsBash.Core/Runtime/Ipc/UnixSocketTransport.cs
    - src/PsBash.Core/Runtime/Ipc/NamedPipeTransport.cs
    - src/PsBash.Core/Runtime/Ipc/HostLockFile.cs
    - src/PsBash.Core.Tests/Runtime/Ipc/TransportTests.cs

acceptance_criteria:
  - "RED→GREEN: 1 KB byte buffer round-trips on both transports"
  - "RED→GREEN: stale-lock-file detection deletes lock and surfaces SocketException"
  - "Unix socket file mode 0600 after Listen"
  - "Named-pipe ACL allows only Environment.UserName ([Trait Platform=Windows])"

steps:
  1: "RED: 1 KB round-trip test on Unix socket transport"
  2: "GREEN: implement UnixSocketTransport with AddressFamily.Unix"
  3: "RED: same round-trip on NamedPipeTransport"
  4: "GREEN: implement NamedPipeTransport with maxInstances=16 + user-only ACL"
  5: "RED: HostLockFile atomic write + read + stale-detect tests"
  6: "GREEN: implement HostLockFile (write-temp-then-rename, parse 'unix:'/'pipe:')"
  7: "Run ./scripts/test.sh --filter Ipc on all 3 OS via CI"

not_included:
  - "Wire protocol framing (T04)"
  - "Server accept loop (T05a)"
  - "Multi-host concurrency arbitration (deferred per origin doc Open Q)"

dependencies: []
risk: medium  # cross-OS socket nuances
estimated_lines: ~190
```

---

## T04 — Wire protocol (mode header + sentinel framing)

```yaml
requested: "Implement HostProtocol framing — MODE header, body, END, EXIT:N. (Unit 4)"
domain_terms: [Protocol, Transport]
new_domain_concepts: []

deliverable: |
  HostProtocol with WriteRequest/ReadRequest/WriteResponse/ReadResponse
  reusing PwshWorker's existing sentinel format byte-for-byte plus a single
  new MODE header line. Mode discriminated union: Command|Stdin|Script|Interactive.

scope:
  files_to_create:
    - src/PsBash.Core/Runtime/Ipc/HostProtocol.cs
    - src/PsBash.Core/Runtime/Ipc/Mode.cs
    - src/PsBash.Core.Tests/Runtime/Ipc/HostProtocolTests.cs
    - src/PsBash.Core.Tests/fixtures/protocol-request-c.bin

acceptance_criteria:
  - "Round-trip Mode.Command produces byte-exact match to fixture"
  - "Output line containing '<<<EXIT:N>>>' as substring (not bare line) is delivered, not interpreted as terminator"
  - "Mode.Script with argv containing newlines and quotes round-trips via base64"
  - "Truncated request surfaces IOException from ReadRequest"

steps:
  1: "RED: byte-exact fixture test for Command request"
  2: "GREEN: WriteRequest/ReadRequest for Command and Stdin modes"
  3: "RED: response decode with multiple output lines + EXIT:0"
  4: "GREEN: WriteResponse/ReadResponse loop ported from PwshWorker.ExecuteAsync"
  5: "RED: argv-with-newlines-and-quotes round-trip for Mode.Script"
  6: "GREEN: base64 envelope inside Script mode header"
  7: "RED→GREEN: Interactive mode emits MODE+END only, no body"

not_included:
  - "Server-side dispatch (T05a)"
  - "Cancellation message — phase 1 closes the connection instead"

dependencies: [T03]
risk: low
estimated_lines: ~150
```

---

## T05a — Host server: accept loop + per-connection dispatcher

```yaml
requested: "ps-bash-host accepts connections, runs one request per stream, stays alive across bad commands. (Unit 5, part 1)"
domain_terms: [Host, Transport, Protocol, Runspace, Worker]
new_domain_concepts: []

deliverable: |
  HostServer.RunAsync(IIpcTransport) accept loop spawning a Connection per
  stream; each Connection reads one request, dispatches to SdkWorker, streams
  response. Exceptions caught at the connection boundary keep the host alive.

scope:
  files_to_modify:
    - src/PsBash.Host/Program.cs
  files_to_create:
    - src/PsBash.Host/Server/HostServer.cs
    - src/PsBash.Host/Server/Connection.cs
    - src/PsBash.Host.Tests/Server/HostServerTests.cs

acceptance_criteria:
  - "RED→GREEN: Server accepts a connection, runs Invoke-BashEcho, returns EXIT:0, closes stream"
  - "Two sequential connections share runspace state (cd/pwd)"
  - "Garbled MODE header → server replies EXIT:2 with stderr line, stays alive"
  - "16 simultaneous connections all complete; output does not interleave"

steps:
  1: "RED: HostServerTests one-connection happy-path test"
  2: "GREEN: HostServer accept loop wiring IIpcTransport + Connection"
  3: "GREEN: Connection.HandleAsync reads HostProtocol request → SdkWorker.ExecuteAsync → writes response"
  4: "RED: 'two connections share runspace' test"
  5: "RED: 'garbled mode header → host stays alive' test"
  6: "GREEN: per-connection try/catch logs to ~/.psbash/host.log, never propagates"
  7: "Run --stop-host poison-pill manual smoke + test"

not_included:
  - "Idle shutdown (T05b)"
  - "Parent-PID death watcher (T05b)"
  - "Process substitution streaming (T10)"

dependencies: [T02, T03, T04]
risk: high  # request-handler exception discipline is load-bearing for stability
estimated_lines: ~190
```

---

## T05b — Idle shutdown + parent-death watcher

```yaml
requested: "Host self-terminates on idle and on launcher death. (Unit 5, part 2)"
domain_terms: [Host, Launcher]
new_domain_concepts: []

deliverable: |
  IdleShutdown timer (default 600s, env override PSBASH_HOST_IDLE_SECS) that
  exits the host when in-flight count drops to zero; ParentDeathWatcher uses
  peer credentials (peercred / GetNamedPipeClientProcessId) to detect launcher
  death mid-command and abort PowerShell.Stop().

scope:
  files_to_create:
    - src/PsBash.Host/Server/IdleShutdown.cs
    - src/PsBash.Host/Server/ParentDeathWatcher.cs
    - src/PsBash.Host.Tests/Server/IdleShutdownTests.cs
    - src/PsBash.Host.Tests/Server/ParentDeathWatcherTests.cs
  files_to_modify:
    - src/PsBash.Host/Server/Connection.cs

acceptance_criteria:
  - "RED→GREEN: idle timer (1s test override) fires when no connections; host exits 0; lock file deleted"
  - "Idle timer resets on new connection accept"
  - "RED→GREEN: launcher PID killed mid-command → ParentDeathWatcher fires → PowerShell.Stop() called"
  - "Watcher poll interval 200ms matches existing PwshWorker pattern (MEMORY: Windows process death)"

steps:
  1: "RED: idle-shutdown timer fires after 1s with zero connections"
  2: "GREEN: IdleShutdown timer + lock-file cleanup on exit"
  3: "RED: timer resets when new connection arrives"
  4: "GREEN: connection-count signal wired to timer"
  5: "RED: parent-death test — fake launcher dies, watcher signals abort"
  6: "GREEN: ParentDeathWatcher reads peercred/Win pipe client PID, polls every 200ms, calls PowerShell.Stop()"
  7: "Verify lock file is deleted on every exit path (idle, --stop-host, parent-death, crash)"

not_included:
  - "Multi-launcher concurrency policy (deferred — origin Open Q)"
  - "Side channel for set -x trace (deferred)"

dependencies: [T05a]
risk: high  # MEMORY: 'Process spawn contract' demands timeout + Kill(entireTree)
estimated_lines: ~170
```

---

## T06 — IpcWorker (launcher-side client)

```yaml
requested: "AOT-safe thin client that connects to host and proxies IWorker calls. (Unit 6)"
domain_terms: [Worker, Launcher, Host, Transport]
new_domain_concepts: []

deliverable: |
  IpcWorker:IWorker that reads HostLockFile, dials the advertised transport,
  spawns ps-bash-host on cache miss with a 5s startup poll, and proxies
  ExecuteAsync/QueryAsync over the wire. AOT-publish-clean (no unreferenced-code
  warnings).

scope:
  files_to_create:
    - src/PsBash.Core/Runtime/IpcWorker.cs
    - src/PsBash.Core/Runtime/HostUnavailableException.cs
    - src/PsBash.Core.Tests/Runtime/IpcWorkerTests.cs

acceptance_criteria:
  - "RED→GREEN: against test host runs echo hello, exit 0, OutputCallback fires"
  - "Stale lock file → IpcWorker deletes it and spawns new host"
  - "Host binary missing → HostUnavailableException (not generic exception)"
  - "AOT publish of PsBash.Shell with /warnaserror succeeds"
  - "PSBASH_TIMEOUT env override honored (reuse PwshWorker pattern)"

steps:
  1: "RED: round-trip echo against in-test HostServer fixture"
  2: "GREEN: StartAsync reads lock file, dials transport, holds connection"
  3: "GREEN: ExecuteAsync writes Mode.Command + body, reads response"
  4: "RED: stale-lock-file detection test"
  5: "GREEN: spawn host with Process.Start (no stdio inherit), 50ms × 100 startup poll"
  6: "RED: missing-host-binary throws HostUnavailableException"
  7: "CI step: dotnet publish -p:PublishAot=true /warnaserror green"

not_included:
  - "Soft-fallback decision logic (T07)"
  - "ExecuteScriptAsync (T09 extends IWorker)"
  - "Cancellation protocol (phase 1 closes connection)"

dependencies: [T01, T03, T04]
risk: medium
estimated_lines: ~180
```

---

## T07 — Launcher dispatch + soft fallback

```yaml
requested: "Program.cs routes through WorkerFactory: IpcWorker when possible, PwshWorker when not. (Unit 7)"
domain_terms: [Launcher, Worker, Host]
new_domain_concepts: []

deliverable: |
  WorkerFactory.CreateAsync that returns IpcWorker on success and PwshWorker
  on PSBASH_DISABLE_HOST=1, missing host binary, or HostUnavailableException.
  All three Program.cs modes (-c, stdin, script) route through the factory.
  Soft-fallback warning rate-limited to once per launcher invocation.

scope:
  files_to_create:
    - src/PsBash.Shell/WorkerFactory.cs
    - src/PsBash.Shell.Tests/WorkerFactoryTests.cs
    - src/PsBash.Shell.Tests/ProgramFallbackTests.cs
  files_to_modify:
    - src/PsBash.Shell/Program.cs
    - src/PsBash.Shell/InteractiveShell.cs

acceptance_criteria:
  - "RED→GREEN: PSBASH_HOST=/path/to/test-host yields IpcWorker"
  - "RED→GREEN: PSBASH_DISABLE_HOST=1 yields PwshWorker, output identical"
  - "Missing host binary → fallback, single stderr warning, exit 0"
  - "All three modes (-c, stdin, script) call WorkerFactory.CreateAsync"
  - "DisposeAsync timeout-bounded (no exit blocked on host disposal)"

steps:
  1: "RED: WorkerFactoryTests for env-var override path"
  2: "GREEN: WorkerFactory.CreateAsync env-var + locator (mirror PwshLocator shape)"
  3: "RED: missing-host-binary fallback test (PSBASH_HOST=/nonexistent)"
  4: "GREEN: HostUnavailableException → log warning + return PwshWorker"
  5: "Refactor Program.cs three branches to call WorkerFactory.CreateAsync"
  6: "RED: bounded-disposal test"
  7: "Run full ./scripts/test.sh"

not_included:
  - "Interactive handoff (T08a/T08b)"
  - "Script-mode parity (T09)"
  - "CI matrix updates (T11)"

dependencies: [T01, T06]
risk: low
estimated_lines: ~150
```

---

## T08a — Mechanical move: REPL/history/completer to PsBash.Host

```yaml
requested: "Move InteractiveShell + dependents into PsBash.Host with no behavior change. (Unit 8, mechanical commit per origin note)"
domain_terms: [Launcher, Host]
new_domain_concepts: []

deliverable: |
  InteractiveShell.cs, LineEditor.cs, SqliteHistoryStore.cs, TabCompleter.cs
  moved to src/PsBash.Host/Shell/. csproj references shifted accordingly.
  Pure rename — git history preserved via 'git mv'. All existing tests still
  pass against the moved files.

scope:
  files_to_modify:
    - src/PsBash.Shell/PsBash.Shell.csproj
    - src/PsBash.Host/PsBash.Host.csproj
  files_to_move:
    - src/PsBash.Shell/InteractiveShell.cs → src/PsBash.Host/Shell/InteractiveShell.cs
    - src/PsBash.Shell/LineEditor.cs → src/PsBash.Host/Shell/LineEditor.cs
    - src/PsBash.Shell/SqliteHistoryStore.cs → src/PsBash.Host/Shell/SqliteHistoryStore.cs
    - src/PsBash.Shell/TabCompleter.cs → src/PsBash.Host/Shell/TabCompleter.cs
  # 4 moves + 2 csproj edits. Counts as ≤5 logical files (csproj edits paired).

acceptance_criteria:
  - "git log --follow on each moved file shows full pre-move history"
  - "PsBash.Shell.csproj no longer compiles those files"
  - "PsBash.Host.csproj compiles them; Sqlite package reference moved"
  - "Existing PromptRenderingTests + tab-completion tests pass against new location"
  - "Diff is purely additive/move — zero logic changes (verified by grep against semantic patterns)"

steps:
  1: "git mv all four files into src/PsBash.Host/Shell/"
  2: "Update PsBash.Shell.csproj: remove file globs, drop Sqlite package ref"
  3: "Update PsBash.Host.csproj: include new shell directory, add Sqlite package ref"
  4: "Fix namespace declarations on moved files (PsBash.Shell → PsBash.Host.Shell)"
  5: "Update using statements in any tests that referenced the old namespace"
  6: "Build solution; run full test suite"
  7: "Verify git log --follow on each file"

not_included:
  - "Interactive handoff wiring (T08b)"
  - "Behavior changes — strictly mechanical move per origin Execution note"

dependencies: [T02, T07]
risk: medium  # large mechanical change; easy to miss a using/namespace
estimated_lines: ~80  # mostly csproj + namespace edits
```

---

## T08b — Interactive handoff: launcher delegates tty to host

```yaml
requested: "On 'interactive', launcher exec-replaces with host so host owns the real tty. (Unit 8, behavior commit)"
domain_terms: [Launcher, Host, Worker, Runspace]
new_domain_concepts: []

deliverable: |
  Launcher's interactive branch sends MODE:interactive, receives host PID,
  then re-spawns ps-bash-host --interactive --launcher-pid=N with no stdio
  redirection so the spawned host inherits the tty. Console.Clear,
  Console.WindowWidth, EnsureVirtualTerminalEnabled all work in-host. Legacy
  in-launcher REPL retained for soft-fallback path.

scope:
  files_to_modify:
    - src/PsBash.Shell/Program.cs
    - src/PsBash.Host/Program.cs
    - src/PsBash.Host/Shell/InteractiveShell.cs
  files_to_create:
    - src/PsBash.Host.Tests/Shell/InteractiveModeTests.cs

acceptance_criteria:
  - "RED→GREEN (PTY transcript on Linux): 'clear' clears the screen"
  - "RED→GREEN: tput cols matches actual tty width post-resize (Linux SIGWINCH)"
  - "Ctrl+C at prompt → fresh prompt; Ctrl+C mid-command → runspace stops, prompt returns"
  - "Host missing → falls back to legacy in-launcher REPL with PwshWorker; documented limitation: Console.WindowWidth still wrong"
  - "Manual: ps-bash interactive on Windows Terminal — clear works, width correct"

steps:
  1: "RED: PTY transcript test asserts 'clear' produces ANSI clear sequence"
  2: "GREEN: launcher interactive branch — Process.Start host with no stdio redirect, await exit"
  3: "GREEN: host --interactive arg parsing → InteractiveShell.RunAsync against shared SdkWorker"
  4: "RED: tput cols matches Console.WindowWidth post-resize"
  5: "GREEN: confirm EnsureVirtualTerminalEnabled runs in host process — likely already correct after T08a"
  6: "RED: Ctrl+C semantics test (mid-command runspace stop)"
  7: "Verify legacy in-launcher REPL fallback still works when ps-bash-host missing"

not_included:
  - "Sharing runspace across separate launcher invocations (each gets fresh state — matches bash)"
  - "PROMPT_COMMAND race against idle shutdown (origin Deferred)"

dependencies: [T08a]
risk: high  # tty handoff has many cross-OS corner cases
estimated_lines: ~140
```

---

## T09 — Script mode: exit / source / set -e parity on shared runspace

```yaml
requested: "Fix the three script-mode bugs (handoff §2.3) by leaning on the in-process runspace. (Unit 9)"
domain_terms: [Worker, Host, Runspace]
new_domain_concepts: []

deliverable: |
  IWorker.ExecuteScriptAsync added; SdkWorker catches ExitException at
  PowerShell.Invoke boundary (returns N without aborting runspace);
  source ./lib.sh reuses the runspace; set -e propagates correctly.
  PwshWorker fallback keeps existing positional-preamble flow.
  Per-invocation scoping for $global:BashPositional and $global:__BashErrexit.

scope:
  files_to_modify:
    - src/PsBash.Core/Runtime/IWorker.cs
    - src/PsBash.Core/Runtime/PwshWorker.cs
    - src/PsBash.Core/Runtime/IpcWorker.cs
    - src/PsBash.Host/Runtime/SdkWorker.cs
    - src/PsBash.Host/Server/Connection.cs
  files_to_create:
    - src/PsBash.Differential.Tests/ScriptModeFixturesTests.cs
  # 6 files. Splittable if gate is strict: T09a (interface + PwshWorker + IpcWorker
  # additions) → T09b (SdkWorker semantics + Connection routing + fixtures).

acceptance_criteria:
  - "Differential vs bash: exit 7 inside function inside script → exit 7"
  - "source ./lib.sh; foo where lib.sh defines foo() — prints from-lib, exit 0"
  - "set -e; false; echo unreachable → exit 1, no 'unreachable' (matches bash)"
  - "Two consecutive ps-bash script.sh against same host — fresh BashPositional each invocation"
  - "Differential vs bash: source nonexistent.sh → exit 1, matching error message"

steps:
  1: "RED: differential test 'exit 7 in function in script'"
  2: "GREEN: extend IWorker with ExecuteScriptAsync; PwshWorker delegates to existing preamble+ExecuteAsync"
  3: "GREEN: SdkWorker.ExecuteScriptAsync catches ExitException via Invoke boundary"
  4: "RED: per-invocation scoping test (two scripts, fresh BashPositional each)"
  5: "GREEN: scope $global:BashPositional and __BashErrexit per invocation in SdkWorker"
  6: "RED: source ./lib.sh + set -e propagation differential tests"
  7: "Verify git diff PsBash.psm1 is empty (no runtime module change)"

not_included:
  - "Refactoring Invoke-BashSource (already works in PsBash.Cmdlets)"
  - "Cancellation through running script (deferred to phase 2)"

dependencies: [T02, T06, T07]
risk: medium
estimated_lines: ~190
```

---

## T10 — Process substitution: 3-path classifier

```yaml
requested: "Replace blanket temp-file buffering with emitter-classified string-capture / pipeline-object / real-pipe paths. (Unit 10)"
domain_terms: [Protocol, Runspace, Worker]
new_domain_concepts:
  - "ProcessSubBridge: in-host AnonymousPipeServerStream pair returning
    \\.\\pipe\\<handle> on Windows or /proc/self/fd/<n> on POSIX, used only
    for the rare external-consumer pipe path."

deliverable: |
  PsEmitter classifies each <(producer) site at emit time and rewrites to one
  of: Invoke-ProcessSubString (capture-to-string for source/eval),
  Invoke-ProcessSubPipeline (objects into mapped runtime functions), or
  Invoke-ProcessSub (real anonymous pipe via ProcessSubBridge for external
  consumers). Seekable cases pinned to existing temp-file path.

scope:
  files_to_modify:
    - src/PsBash.Core/Parser/PsEmitter.cs
    - src/PsBash.Module/PsBash.psm1
  files_to_create:
    - src/PsBash.Host/Runtime/ProcessSubBridge.cs
    - src/PsBash.Differential.Tests/ProcessSubFixturesTests.cs

acceptance_criteria:
  - "Differential: source <(echo 'export FOO=bar'); echo $FOO → bar"
  - "Differential: diff <(seq 1 10) <(seq 1 10) → empty, exit 0 (pipeline-object path)"
  - "Streaming proof: slow-producer feeds external consumer; first read before producer finishes"
  - "Edge: paste <(seq 1 5) <(seq 6 10) doesn't deadlock"
  - "comm <(sort big1) <(sort big2) routes to seekable temp-file path (documented + asserted)"
  - "Both IpcWorker and PwshWorker paths give byte-identical output (PwshWorker keeps temp-file fallback)"

steps:
  1: "RED: source <(...) string-capture differential test"
  2: "GREEN: Invoke-ProcessSubString runtime function + emitter classifier rule for source/eval shapes"
  3: "RED: diff <() <() pipeline-object differential test"
  4: "GREEN: Invoke-ProcessSubPipeline + emitter rule for IsKnownCommand consumers"
  5: "RED: streaming proof against external tar (Trait Network=true, gated)"
  6: "GREEN: ProcessSubBridge.cs anonymous pipe pair + emitter fallback to Invoke-ProcessSub for external consumers"
  7: "Add classification rate trace under PSBASH_TRACE; verify pipe path is rare on real fixtures"

not_included:
  - "Removing legacy temp-file path entirely (kept as PwshWorker fallback)"
  - "Auto-detection of seekable consumers beyond the documented list"

dependencies: [T02, T05a]
risk: high  # emitter classification correctness affects every existing <() test
estimated_lines: ~200  # at the cap; consider split if it exceeds
```

---

## T11 — Build, publish, CI updates

```yaml
requested: "Ship both binaries together; publish.yml mirrors host build; AOT publish stays warning-free. (Unit 11)"
domain_terms: [Launcher, Host]
new_domain_concepts: []

deliverable: |
  Release zip layout per platform contains ps-bash + ps-bash-host + runtimes/.
  publish.yml has a parallel build-host-binaries matrix job; build.yml validates
  host on PRs; canary.yml runs every test in both ipc and subprocess transports;
  scripts/pack-local.ps1 + scripts/test.sh expose PSBASH_HOST / PSBASH_DISABLE_HOST.

scope:
  files_to_modify:
    - .github/workflows/publish.yml
    - .github/workflows/build.yml
    - .github/workflows/canary.yml
    - scripts/pack-local.ps1
    - scripts/test.sh

acceptance_criteria:
  - "CI green on Win/Linux/macOS for both transports"
  - "dotnet publish PsBash.Shell -p:PublishAot=true /warnaserror — zero warnings"
  - "Release zip extracts and runs on a host without pwsh installed (host self-contained)"
  - "Old release zip without ps-bash-host → fallback warning + identical behavior"
  - "scripts/test.sh exposes PSBASH_HOST and PSBASH_DISABLE_HOST as documented"

steps:
  1: "RED: CI step adds dotnet publish AOT /warnaserror — initially expected green"
  2: "GREEN: extend publish.yml matrix with build-host-binaries job"
  3: "GREEN: combine build outputs into single per-platform zip in publish step"
  4: "GREEN: canary.yml [Theory] InlineData ipc/subprocess wiring"
  5: "GREEN: pack-local.ps1 bundles host alongside launcher"
  6: "GREEN: test.sh sets PSBASH_HOST for IpcWorker tests, PSBASH_DISABLE_HOST=1 for legacy"
  7: "Manual: tag v0.9.0-rc1 → gh run watch → verify both binaries in release"

not_included:
  - "Module manifest version bump (separate release commit)"
  - "Differential parity sweep (T12)"

dependencies: [T02, T07]
risk: medium  # CI matrix changes are fragile
estimated_lines: ~150
```

---

## T12 — Differential parity sweep + canary expansion

```yaml
requested: "Prove behavioral parity across the failure-surface matrix; lock in known-bad regression tests. (Unit 12)"
domain_terms: [Worker, Launcher, Host]
new_domain_concepts: []

deliverable: |
  Every PsBash.Differential.Tests fixture parameterized on transport (ipc /
  subprocess). PsBash.Canary.Tests has one file per failure-surface mode
  (M1–M6, qa-rubric Directive 4) under 60s per mode per platform.
  PsBash.Escalation.Tests/HostLifecycleTests.cs codifies known-bad MEMORY
  entries as permanent regression tests.

scope:
  files_to_modify:
    - src/PsBash.Differential.Tests/  # base fixture parameterization (≤2 files in practice)
  files_to_create:
    - src/PsBash.Canary.Tests/Modes/M1_HappyPath.cs
    - src/PsBash.Canary.Tests/Modes/M2_LargeInput.cs
    - src/PsBash.Canary.Tests/Modes/M3_BrokenPipe.cs
    - src/PsBash.Escalation.Tests/HostLifecycleTests.cs
  # M4–M6 added in a follow-up T12b if this exceeds size cap.

acceptance_criteria:
  - "Every existing differential test runs in both ipc and subprocess modes — green"
  - "Canary suite < 60s per mode per platform (qa-rubric Directive 8)"
  - "Branch coverage ≥80% on IpcWorker, HostServer, HostProtocol, SdkWorker"
  - "Diff coverage ≥90% on this PR"
  - "Zero flakes over 100 re-runs of canary on CI matrix"
  - "MEMORY 'Windows process death' → HostDiesWhenLauncherKilled regression test"
  - "MEMORY 'Process spawn contract' → HostSpawnTimeout regression test"

steps:
  1: "RED: parameterize Differential test base class on (string transport)"
  2: "GREEN: existing fixtures gain ipc coverage automatically"
  3: "RED: M1 happy-path canary in both modes"
  4: "GREEN: implement M1 + M2 + M3 canaries, ≤60s/mode/platform"
  5: "RED: HostLifecycleTests for stale lock, host crash mid-command, idle race, parent-death during interactive"
  6: "GREEN: implement HostLifecycleTests against real spawned host fixtures"
  7: "Run flake-detector: 100× canary on CI; expect zero failures"

not_included:
  - "M4-M6 modes (deferred to T12b if needed for size)"
  - "Performance regression suite (separate effort)"

dependencies: [T01, T02, T05a, T05b, T06, T07, T08b, T09, T10, T11]
risk: medium  # mostly mechanical, but flake hunting can extend timeline
estimated_lines: ~200  # cap; split if exceeded
```

---

## Adversarial Validation Notes

Per `dartai:simple-planning` validation-by-tier (architectural):

- **Coherence:** task graph has a single root (T01) and converges at T12; no
  task depends on a non-ancestor.
- **Scope:** every task carries an explicit not_included list; the
  whole-migration scope boundaries from the source plan (no transpiler
  changes, no runtime-module rewrites, no PsBash.Cmdlets share) propagate to
  each task.
- **Sizing:** T05 and T08 split per the rubric. T09 and T10 are at the cap;
  flagged for split if implementation reveals more touchpoints than estimated.
  T12 may need T12b for M4–M6.
- **Risk concentration:** T05a, T08b, T10 carry the highest residual risk
  (request-handler discipline, tty handoff, emitter classification) — these
  are the deep-validation candidates for `dartai:adversarial-planning-loop`
  before execution.
- **Memory hooks honored:**
  - "Windows process death" → T05b (parent-PID poller) + T12 regression test.
  - "Process spawn contract" → T06 (5s startup poll, bounded) + T12 regression test.
  - "Quote `--filter |`" → T11 (test.sh) and any developer-doc mention.
  - "Fix reproduces bug" → applied transitively to every host-touching task —
    fix and test infrastructure must land in the same task per qa-rubric D11.

## Open Items Held for Decision Before Execution

1. `IWorker` visibility — `internal` + `[InternalsVisibleTo]` (preferred) or
   `public`? Decided in T01 step 7. Default: `internal`.
2. `ExecuteScriptAsync` location on the interface vs a separate `IScriptWorker`
   — T09 chooses; default: extend `IWorker` once (narrow surface).
3. T12 split decision — leave as one task and split only if M4–M6 cause >200
   lines; document in T12 close-out.

## Suggested Execution Order

1. T01 — unblocks everything else, lowest risk.
2. T02 + T03 in parallel (no shared files).
3. T04 → T05a → T05b serially (server stack is sequential).
4. T06 → T07 (client stack).
5. T08a (mechanical) → T08b (handoff).
6. T09 + T10 in parallel after T07/T05a (different surfaces).
7. T11 once T07 is in.
8. T12 last — depends on everything.

Total estimated work: ~13 Dart tasks, ~2 KLOC total churn, full migration.
