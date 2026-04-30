---
title: "refactor: Migrate to in-process PowerShell host via two-binary IPC"
type: refactor
status: active
date: 2026-04-28
origin: docs/planning/architecture-migration-handoff.md
---

# refactor: Migrate to in-process PowerShell host via two-binary IPC

## Overview

Replace the per-invocation `pwsh` subprocess worker with a long-lived in-process PowerShell SDK host. The shipped `ps-bash` binary stays AOT and tiny; a new `ps-bash-host` binary (JIT, self-contained) holds the runspace and serves the launcher over a Unix domain socket (named-pipe fallback). The current `PwshWorker` text protocol (`<<<END>>>`, `<<<EXIT:N>>>`) is reused over the socket so the runtime module and transpiler are unchanged.

This unblocks the three pain points enumerated in the handoff doc (terminal I/O, process substitution streaming, script-mode `exit`/`source` semantics) and removes ~700 ms of pwsh start + module-load cost from every `-c` invocation after the first (see origin: `docs/planning/architecture-migration-handoff.md` §3).

## Problem Frame

ps-bash currently spawns a fresh `pwsh` per invocation and talks to it through the worker process's redirected stdio. That pipe doubles as the protocol channel and the program's stdout, which causes three structural problems:

1. **Console I/O is broken.** `Clear-Host`, `$Host.UI.RawUI.WindowSize`, and any ANSI sent by the worker land in the protocol pipe instead of the user's terminal. The recent `cls`/`clear`/`reset` intercept in `src/PsBash.Shell/InteractiveShell.cs:TryRunDirect` is a stopgap; `COLUMNS`/`LINES`/`TERM` and dynamic resize are still wrong.
2. **Process substitution can't stream.** `Invoke-ProcessSub` in `src/PsBash.Module/PsBash.psm1` buffers the producer fully into a temp file before the consumer sees a byte, breaking `diff <(slow) <(other)` and similar patterns.
3. **Script-mode `exit` and `source` are fragile.** `exit N` inside a `.sh` kills the whole worker; the shell catches the dead-pipe `IOException` and respawns, which works but leaks state. `source ./lib.sh` requires opening a second file mid-execution and currently has no in-worker mechanism.

The decision to move to a two-binary IPC model is settled (see origin: `docs/planning/architecture-migration-handoff.md` §3). This plan covers **how** to land it without breaking the AOT launcher, the test matrix, or release packaging.

## Requirements Trace

- **R1.** AOT launcher (`PsBash.Shell` -> `ps-bash`) keeps `<PublishAot>true</PublishAot>` and stays small (~8 MB native, ~50 ms cold start).
- **R2.** A new `PsBash.Host` -> `ps-bash-host` self-contained JIT binary loads `Microsoft.PowerShell.SDK` in-process and owns a long-lived runspace with the embedded `PsBash` module pre-imported.
- **R3.** Launcher and host communicate over a per-user, per-session Unix domain socket on all three platforms; `NamedPipeServerStream` is the documented fallback when Unix sockets are unavailable.
- **R4.** Existing sentinel protocol (`<<<END>>>`, `<<<EXIT:N>>>`) reused verbatim over the byte stream, plus a single new `<<<MODE:...>>>` header.
- **R5.** All current modes work end-to-end through the host: `-c`, stdin pipe, `script.sh`, interactive REPL, `Invoke-BashEval`/`Invoke-BashSource` cmdlets (cmdlets remain in their own runspace; see Decisions).
- **R6.** Console I/O works: `clear`/`cls`/`reset`, `Console.WindowWidth`, `COLUMNS`/`LINES`/`TERM` env, terminal resize.
- **R7.** `<()` streams: producer and consumer run concurrently against an `AnonymousPipeServerStream`, no temp file buffering for the streaming path.
- **R8.** Script `exit N` returns to the host gracefully without killing the runspace; `source ./lib.sh` reuses the same runspace and persists state.
- **R9.** Soft fallback: if `ps-bash-host` is missing or fails to spawn, launcher falls back to today's `PwshWorker` subprocess path with a stderr warning. No hard regression for partial installs.
- **R10.** Test matrix (`PsBash.Shell.Tests`, `PsBash.Differential.Tests`, `PsBash.Canary.Tests`) runs against the new transport with the same assertions; canary tests run in M1–M6 on Windows/Linux/macOS per `.claude/rules/qa-rubric.md` Directives 4 and 5.
- **R11.** PSGallery module (`PsBash.psm1`) and `PsBash.Core` NuGet package contents are unchanged. Release pipeline (`scripts/pack-local.ps1`, `.github/workflows/publish.yml`) ships both binaries as a single archive per platform.

## Scope Boundaries

- This plan does **not** rewrite the transpiler (`PsBash.Core/Parser/PsEmitter.cs`) or the runtime module (`PsBash.Module/PsBash.psm1`). Both are reused as-is.
- This plan does **not** change the bash-language surface (no new builtins, no new flag handling). All existing parity tests stay green.
- This plan does **not** change `PsBash.Cmdlets` to share the host's runspace. Cmdlets stay independent (see Key Technical Decisions).
- This plan does **not** introduce a multi-user or networked daemon. Sockets are scoped to `Environment.UserName` + a session id, never bound to TCP.
- This plan does **not** address the deferred parity bugs tracked in `MEMORY.md` (broken pipe/SIGPIPE, pipefail/PIPESTATUS, eval caller scope, `$'...'` ANSI-C quoting, empty-var elision, adjacent-quote merging). They remain deferred.

### Deferred to Separate Tasks

- **`PROMPT_COMMAND` race against host idle-shutdown**: the existing `RunPromptCommandAsync` flow keeps working; tightening it for host-mode is a follow-up.
- **`set -x` xtrace routed through the host**: works today via `Set-PSDebug`; whether to surface trace lines on a side channel is a follow-up UX decision.
- **Multiple concurrent launchers against the same host**: phase 1 serializes commands per connection; concurrency policy (queue vs. reject vs. spawn-per-call) is a follow-up.

## Context & Research

### Relevant Code and Patterns

- `src/PsBash.Core/Runtime/PwshWorker.cs` — current subprocess worker. `BuildInitScript()` (~L176) is the worker loop to port; `ExecuteAsync` (~L310) and `QueryAsync` (~L354) define the protocol surface that `IWorker` must abstract.
- `src/PsBash.Shell/Program.cs` — entry point; modes dispatched at L55 (`script`), L105 (stdin), L120 (interactive), L160 (`-c`). Each mode currently constructs `PwshWorker` directly; this becomes the seam.
- `src/PsBash.Shell/InteractiveShell.cs` — REPL. Methods that touch the worker: `RunAsync` (L27), `StartWorkerAsync` (L1170), `EnsureWorkerAsync` (L1181), `SyncWorkerCwdAsync` (L456), `RunPromptCommandAsync` (L471), `SourceRcFileAsync` (L907). Console-mode helpers `EnsureVirtualTerminalEnabled` (L1235) and `EnsureConsoleInputRestored` (L1258) move to the host process.
- `src/PsBash.Cmdlets/PsBash.Cmdlets.csproj` — already references `System.Management.Automation` 7.4.* with `<PublishAot>false</PublishAot>` and `<TargetFramework>net8.0</TargetFramework>`. Same SDK shape the host needs; reuse this project's package version pin.
- `src/PsBash.Shell/JobObjectWatchdog.cs` (called from `Program.cs:11`) — Windows Job Object + parent-PID watcher. The launcher keeps using it; the host gets its own equivalent (see MEMORY: "Windows process death" + "Process spawn contract").
- `src/PsBash.Core/Runtime/ModuleExtractor.cs` (referenced from `Program.cs:65`) — module extraction pattern + `ps-bash/module-{version}/` cache; host loads the module from the same path.

### Institutional Learnings

- **Windows process death** (`MEMORY.md`): no SIGHUP on Windows; the Job Object on the launcher already handles "kill children when launcher dies." The host needs the same parent-PID poller pattern that `PwshWorker.BuildInitScript` already uses (`Get-Process -Id $pp` 200 ms poll) so it exits when the launcher dies during stdio handoff.
- **Process spawn contract** (`MEMORY.md`): every spawn needs timeout + `Kill(entireProcessTree)` in finally. The launcher's host-spawn path must follow this pattern from day one or it becomes a latent lockup.
- **Quote `--filter |` in xunit** (`MEMORY.md`): test scripts that run `IpcWorker` vs `SdkWorker` filters must quote.
- `docs/solutions/` is empty; no prior in-process-host solution to merge against.

### External References

- `Microsoft.PowerShell.SDK` runspace hosting: `RunspaceFactory.CreateRunspace(InitialSessionState)`, `PowerShell.Create()`, `ps.Runspace = sharedRunspace`, `AnonymousPipeServerStream.CreatePair`. These are the in-process equivalents of the current text-pipe protocol; no external research delegated since the API set is the one already used by `PsBash.Cmdlets`.
- `System.IO.Pipes.NamedPipeServerStream` and `System.Net.Sockets.UnixDomainSocketEndPoint` (.NET 5+) are the transport primitives.

## Key Technical Decisions

- **Two binaries, not one self-elevating process.** `ps-bash` stays AOT; `ps-bash-host` is non-AOT JIT. Splitting the binary is the only way to keep cold-start fast for `-c` while permitting `Microsoft.PowerShell.SDK` (which is structurally non-AOT). *Rationale: confirmed in origin doc §2.5; reaffirmed by `PsBash.Cmdlets.csproj` already isolating `<PublishAot>false</PublishAot>` for the same reason.*
- **`IWorker` is the only seam touched in the launcher.** Extract an interface from `PwshWorker` (same method shapes: `ExecuteAsync`, `QueryAsync`, `StartAsync`, `DisposeAsync`, `OutputCallback`, `HasExited`). Both `PwshWorker` (legacy fallback) and a new `IpcWorker` (host client) implement it. *Rationale: keeps `Program.cs` and `InteractiveShell.cs` diff small; lets the test suite parameterize on the implementation.*
- **Reuse the existing sentinel protocol verbatim.** The body of every command is the same transpiled PowerShell that goes through `PwshWorker.ExecuteAsync` today. Add exactly one new framing element: a `<<<MODE:c|stdin|script:<path-with-args>|interactive>>>` line before the body. *Rationale: minimizes churn to a working protocol; lets us run both transports off the same parser code on day one.*
- **Unix domain socket primary, named pipe fallback.** `Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)` works on .NET 8+ across Win10 1803+/Linux/macOS. `NamedPipeServerStream` is the documented fallback for restricted Windows builds and locked-down containers. A small lock file at `Path.GetTempPath()/ps-bash/host-{user}-{session}.lock` advertises which transport the running host listens on (`unix:/path` or `pipe:name`). *Rationale: one transport pattern that works everywhere, with a known escape hatch; lock file lets the launcher pick without probing.*
- **Host lifecycle: idle timeout, default 600 s, env-overridable.** No login-session binding (too intrusive on Linux/macOS), no hard cap. `ps-bash --stop-host` is provided. Host exits on parent-death (it polls *each* connected launcher's PID) and on idle. *Rationale: predictable; matches the spirit of pwsh subprocess (which dies with the launcher) without paying spawn cost on every call. Aligned with `MEMORY.md` "Process spawn contract."*
- **PowerShell SDK version pinned by host project.** `PsBash.Host.csproj` references `Microsoft.PowerShell.SDK 7.4.*` (matching `PsBash.Cmdlets`). Host self-contains its own SDK; user's installed pwsh isn't consulted. User profiles are still loaded from `$HOME/.../profile.ps1` paths — the runspace's `InitialSessionState` includes them when `--noprofile` is not set. *Rationale: predictable behavior across machines; users keep their custom modules via `$env:PSModulePath`.*
- **Soft fallback when host is missing.** Launcher tries to connect → spawn → connect; on any spawn failure (binary missing, permission denied, signature mismatch on macOS), it logs a stderr warning and falls back to today's `PwshWorker`. *Rationale: partial installs (zip strip, antivirus quarantine) shouldn't make ps-bash unusable.*
- **`PsBash.Cmdlets` stays independent of the host.** Cmdlets run inside the user's own pwsh; the host runs in its own process; the two never share a runspace. *Rationale: lifecycle coupling is a strict regression — a crashing cmdlet would kill the host, and a host idle-shutdown would orphan a user's pwsh session.*
- **One connection per command in phase 1.** Each launcher invocation opens a fresh socket, sends mode + body, streams output until `<<<EXIT:N>>>`, closes. No multiplexing, no command-queueing. *Rationale: matches today's invocation semantics one-for-one; concurrency is deferred (see Scope Boundaries).*
- **Host owns the terminal in interactive mode.** When `<<<MODE:interactive>>>` is sent, the launcher hands off stdin/stdout/stderr to the host (via a small handle-passing dance documented in the relevant unit) and waits on the host's exit code. The REPL loop, `LineEditor`, `EnsureVirtualTerminalEnabled`/`EnsureConsoleInputRestored`, and history all move into the host. *Rationale: only way to make `Console.Clear()` and `Console.WindowWidth` correct without re-implementing them.*

## Open Questions

### Resolved During Planning

- *Host lifecycle*: idle timeout 600 s default, env override `PSBASH_HOST_IDLE_SECS`, explicit `ps-bash --stop-host` (origin §3 Q1).
- *SDK version pin*: bundle `Microsoft.PowerShell.SDK 7.4.*` matching `PsBash.Cmdlets` (origin §3 Q2).
- *Fallback when host missing*: soft-fallback to existing `PwshWorker` with stderr warning (origin §3 Q3).
- *`PsBash.Cmdlets` shares runspace*: no, stays independent (origin §3 Q4).

### Deferred to Implementation

- **Exact handle-passing mechanism for interactive mode handoff**: on Windows, `DuplicateHandle` + `GetStdHandle` is the obvious path; on Linux/macOS, an `execve`-style `Process.Start` of the host with stdin/stdout already attached to the launcher's tty is simpler. Final pick depends on what survives `Console.IsInputRedirected` checks; pick during Unit 8.
- **Whether `<()` returns `\\.\pipe\xxx` strings on Windows or `/proc/self/fd/N`-style paths**: depends on which the consumer command (`diff`, `comm`, etc.) actually accepts; verify in Unit 10 against the existing process-sub differential tests.
- **Final host-binary placement on disk**: alongside `ps-bash` in the same archive directory is the obvious choice; release-pipeline unit (Unit 11) confirms once both binaries publish cleanly.
- **Connection authentication**: phase 1 relies on Unix-socket file mode (`0600`) and named-pipe ACL (current user only). If a future use case demands cross-user or cross-session, revisit.

## High-Level Technical Design

> *This illustrates the intended approach and is directional guidance for review, not implementation specification. The implementing agent should treat it as context, not code to reproduce.*

```
                  ┌────────────────────────────────────────────────────┐
                  │  ps-bash (AOT, ~8 MB)                              │
                  │  PsBash.Shell + PsBash.Core (transpiler)           │
                  │                                                    │
                  │  Program.cs                                        │
                  │    └─► IWorker  ──────┬────────────────────────┐   │
                  │            ▲          │                        │   │
                  │            │          ▼                        ▼   │
                  │            │   IpcWorker (new)         PwshWorker  │
                  │            │   (host present)         (fallback)   │
                  └────────────┼──────────┼─────────────────┼──────────┘
                               │          │                 │
                               │          │ unix-socket     │ stdio (legacy)
                               │          │ /tmp/ps-bash/.. │
                               │          ▼                 ▼
                  ┌────────────┼────────────────┐    ┌──────────────────┐
                  │  ps-bash-host (JIT, ~80 MB) │    │  pwsh subprocess │
                  │  PsBash.Host                │    │  (when no host)  │
                  │                             │    └──────────────────┘
                  │  Acceptor ─► Connection ─►  │
                  │                  │          │
                  │                  ▼          │
                  │            SdkWorker        │
                  │            (in-proc)        │
                  │                  │          │
                  │                  ▼          │
                  │      Microsoft.PowerShell   │
                  │      .SDK Runspace          │
                  │      ├ PsBash module loaded │
                  │      └ shared across calls  │
                  └─────────────────────────────┘
```

Wire-level command framing (one connection per launcher invocation):

```
client ─► <<<MODE:c>>>\n
client ─► <transpiled-powershell>\n
client ─► <<<END>>>\n
host   ─► output line 1\n
host   ─► output line 2\n
host   ─► <<<EXIT:0>>>\n
(connection closed by host or client)
```

Mode header variants: `<<<MODE:c>>>`, `<<<MODE:stdin>>>`, `<<<MODE:script:<base64-encoded {path, argv}>>>>`, `<<<MODE:interactive>>>`. Interactive mode does not stream output through the socket — the launcher hands off the tty and waits on the host's exit; the socket is only used for the initial handshake.

## Implementation Units

- [ ] **Unit 1: Extract `IWorker` interface; PwshWorker implements it**

**Goal:** Carve a narrow seam at the worker boundary so the launcher can swap implementations.

**Requirements:** R1, R10

**Dependencies:** none

**Files:**
- Create: `src/PsBash.Core/Runtime/IWorker.cs`
- Modify: `src/PsBash.Core/Runtime/PwshWorker.cs`
- Modify: `src/PsBash.Shell/Program.cs`
- Modify: `src/PsBash.Shell/InteractiveShell.cs`
- Test: `src/PsBash.Core.Tests/Runtime/IWorkerContractTests.cs`

**Approach:**
- Define `IWorker : IAsyncDisposable` with `Task<int> ExecuteAsync(string, CancellationToken)`, `Task<string> QueryAsync(string, CancellationToken)`, `bool HasExited`, `Action<string>? OutputCallback { get; set; }`.
- `PwshWorker.StartAsync` signature stays; add `static async Task<IWorker> Create(...)` that returns the same instance typed as the interface.
- Replace direct `PwshWorker` references in `Program.cs` and `InteractiveShell.cs` with `IWorker`. The `EnsureWorkerAsync` / `StartWorkerAsync` helpers return `IWorker` and accept a factory.
- No behavioral change. This unit is a pure refactor.

**Patterns to follow:**
- `src/PsBash.Cmdlets/Commands/InvokeBashEvalCommand.cs` for cmdlet-level abstraction reuse (look at how it calls into transpilation without depending on the worker).

**Test scenarios:**
- *Happy path:* `PwshWorker` cast as `IWorker` runs `echo hello` and returns exit 0. (`./scripts/test.sh --filter IWorkerContract`)
- *Edge case:* `IWorker.OutputCallback` mutation after a previous `QueryAsync` does not leak between calls (mirrors current `QueryAsync` save/restore).
- *Error path:* `ExecuteAsync` after `DisposeAsync` throws `ObjectDisposedException` consistently across implementations (set up as a contract test others can subclass).

**Verification:**
- Full test suite (`./scripts/test.sh`) green with no expected diffs from the prior run.
- `git grep -n "new PwshWorker"` returns only the legacy-fallback construction site.

---

- [ ] **Unit 2: Create `PsBash.Host` project skeleton with shared runspace bootstrap**

**Goal:** Stand up the new binary, load the embedded `PsBash` module into a `Microsoft.PowerShell.SDK` runspace, prove the in-process command path works at the unit-test level.

**Requirements:** R2, R5

**Dependencies:** none (Unit 1 not strictly required, but recommended to land first)

**Files:**
- Create: `src/PsBash.Host/PsBash.Host.csproj`
- Create: `src/PsBash.Host/Program.cs`
- Create: `src/PsBash.Host/Runtime/SdkRunspace.cs`
- Create: `src/PsBash.Host/Runtime/SdkWorker.cs`
- Modify: `ps-bash.sln` (add new project)
- Test: `src/PsBash.Host.Tests/PsBash.Host.Tests.csproj`
- Test: `src/PsBash.Host.Tests/Runtime/SdkWorkerTests.cs`

**Approach:**
- `PsBash.Host.csproj`: `<TargetFramework>net8.0</TargetFramework>`, `<PublishAot>false</PublishAot>`, `<SelfContained>true</SelfContained>` for release builds, `<AssemblyName>ps-bash-host</AssemblyName>`. Reference `PsBash.Core` for transpiler + module-extraction reuse. Reference `Microsoft.PowerShell.SDK 7.4.*` (match `PsBash.Cmdlets`).
- `SdkRunspace`: builds an `InitialSessionState`, imports the embedded `PsBash.psm1` (extracted via `ModuleExtractor` from `PsBash.Core`), opens a single shared `Runspace`. Idle init time is the first-call cost; after that all calls reuse it.
- `SdkWorker : IWorker`: `ExecuteAsync` creates a `PowerShell.Create()` bound to the shared runspace, calls `AddScript(transpiledBody).Invoke()`, captures output and exit code via `$LASTEXITCODE`, surfaces output to `OutputCallback`. Mirrors `PwshWorker.ExecuteAsync` semantics line-for-line.
- `Program.cs` for this unit: a stub that loads the module and exits cleanly. The IPC server lands in Unit 5.

**Patterns to follow:**
- `src/PsBash.Cmdlets/Commands/InvokeBashEvalCommand.cs` — same SDK API surface (PowerShell.Create + AddScript + Invoke), same ExecutionPolicy quirks.
- `src/PsBash.Core/Runtime/PwshWorker.cs:BuildInitScript` — port the global-variable initialization (`$global:__BashErrexit = $false`, etc.) into `SdkRunspace.cs`.

**Test scenarios:**
- *Happy path:* `SdkWorker.ExecuteAsync` runs `Invoke-BashEcho hello` and emits `"hello"` via `OutputCallback`, returns 0.
- *Happy path:* `SdkWorker.QueryAsync('$LASTEXITCODE')` after a failing command returns the right code.
- *Happy path:* Two consecutive `ExecuteAsync` calls share state — `cd /tmp` then `pwd` reports `/tmp`.
- *Edge case:* Module is loaded exactly once across many `ExecuteAsync` calls (assert via a counter incremented in module init).
- *Error path:* Bad transpiled PowerShell raises an error, `OutputCallback` receives the message on a side channel (stderr equivalent), exit code is non-zero.
- *Integration:* `set -e` errexit semantics from `PwshWorker` carry over — `$global:__BashErrexit` propagates across calls.

**Verification:**
- `dotnet build src/PsBash.Host` succeeds on Win/Linux/macOS.
- `./scripts/test.sh src/PsBash.Host.Tests` green.
- `dotnet publish src/PsBash.Host -c Release -r win-x64 --self-contained` produces `ps-bash-host.exe` <100 MB.

---

- [ ] **Unit 3: IPC transport primitives — Unix socket + named-pipe fallback + lock file**

**Goal:** Provide a connection abstraction the host listens on and the launcher dials, with the fallback decision baked into a single discoverable lock file.

**Requirements:** R3, R4

**Dependencies:** none

**Files:**
- Create: `src/PsBash.Core/Runtime/Ipc/IIpcTransport.cs`
- Create: `src/PsBash.Core/Runtime/Ipc/UnixSocketTransport.cs`
- Create: `src/PsBash.Core/Runtime/Ipc/NamedPipeTransport.cs`
- Create: `src/PsBash.Core/Runtime/Ipc/HostLockFile.cs`
- Test: `src/PsBash.Core.Tests/Runtime/Ipc/UnixSocketTransportTests.cs`
- Test: `src/PsBash.Core.Tests/Runtime/Ipc/NamedPipeTransportTests.cs`
- Test: `src/PsBash.Core.Tests/Runtime/Ipc/HostLockFileTests.cs`

**Approach:**
- `IIpcTransport` exposes `Task<Stream> ConnectAsync(CancellationToken)` for the client side and `IAsyncDisposable Listen(Func<Stream, Task> handler)` for the server side.
- Lock file path: `Path.Combine(Path.GetTempPath(), "ps-bash", $"host-{Environment.UserName}-{sessionId}.lock")` per `.claude/rules/temp-files.md`. Contents: a single line, either `unix:/tmp/.../psbash-...sock` or `pipe:psbash-andyb-{guid}`. Written atomically (write-temp-then-rename); deleted on host shutdown.
- `UnixSocketTransport`: `Socket(AddressFamily.Unix, ...)` everywhere; on Windows older than 1803, `Socket.Bind` throws `SocketException(AddressFamilyNotSupported)` — the host catches, falls through to `NamedPipeTransport`.
- `NamedPipeTransport`: `NamedPipeServerStream(name, PipeDirection.InOut, maxInstances: 16, ...)` with a Windows ACL allowing only the current user. Linux/macOS use this only as a unit-test fallback.
- Session id: `Guid.NewGuid()` once per host process, written to lock file.

**Patterns to follow:**
- `src/PsBash.Core/Runtime/PwshWorker.cs:ExtractWorkerScriptAsync` (~L402) for the temp-file write-with-share + retry-on-IOException pattern. Reuse the same `ps-bash/` subdirectory convention from `.claude/rules/temp-files.md`.

**Test scenarios:**
- *Happy path:* Server listens on Unix socket; client connects; round-trip a 1 KB byte buffer.
- *Happy path:* Same as above with named pipe transport.
- *Edge case:* Lock file points to a stale path (host crashed); client fails to connect, surfaces `SocketException`, deletes lock file. Next attempt spawns a new host.
- *Edge case:* Two clients connect simultaneously; both succeed; their output does not interleave on the wire (each handler gets its own `Stream`).
- *Error path:* Lock file directory not writable (e.g. `/tmp` mounted ro). `HostLockFile.Write` throws a typed exception the launcher can convert into the soft-fallback path.
- *Edge case (Windows, named pipe):* ACL denies a different-user process. Mark `[Trait("Platform", "Windows")]` per `.claude/rules/qa-rubric.md` Directive 5.

**Verification:**
- `./scripts/test.sh src/PsBash.Core.Tests --filter Ipc` green on Win/Linux/macOS.
- Unix socket file is `0600` after listen.
- Named-pipe ACL allows only `Environment.UserName`.

---

- [ ] **Unit 4: Wire protocol — mode header + sentinel framing reused**

**Goal:** Implement the framing that wraps the existing transpiled-PowerShell body, keeping `<<<END>>>`/`<<<EXIT:N>>>` exactly as today.

**Requirements:** R4

**Dependencies:** Unit 3

**Files:**
- Create: `src/PsBash.Core/Runtime/Ipc/HostProtocol.cs`
- Create: `src/PsBash.Core/Runtime/Ipc/Mode.cs`
- Test: `src/PsBash.Core.Tests/Runtime/Ipc/HostProtocolTests.cs`

**Approach:**
- `Mode`: discriminated union — `Command(string body)`, `Stdin(string body)`, `Script(string path, string[] argv, string body)`, `Interactive`.
- `HostProtocol.WriteRequest(Stream, Mode)`: emits `<<<MODE:c>>>\n<body>\n<<<END>>>\n`, or `<<<MODE:script:<base64-of-json{path,argv}>>>>\n<body>\n<<<END>>>\n`, or for interactive, just `<<<MODE:interactive>>>\n<<<END>>>\n` and stops writing (handoff happens out-of-band).
- `HostProtocol.ReadRequest(Stream)`: parses the same back; mode header is a single line with strict literal match per origin doc.
- `HostProtocol.WriteResponse` / `ReadResponse`: streams output lines, terminated by `<<<EXIT:N>>>`, identical to current `PwshWorker._outputChannel` consumer.

**Patterns to follow:**
- `src/PsBash.Core/Runtime/PwshWorker.cs:ExecuteAsync` (~L310) for the response reader loop — port literally.
- `src/PsBash.Core/Runtime/PwshWorker.cs:BuildInitScript` (~L176) for the `<<<EXIT:N>>>` emission contract on the server side.

**Test scenarios:**
- *Happy path:* Round-trip a `Mode.Command("Invoke-BashEcho hi")` request through a `MemoryStream`; assert byte-exact framing matches the spec in High-Level Technical Design.
- *Happy path:* Response stream with multiple output lines and `<<<EXIT:0>>>` decodes into `(["line1","line2"], 0)`.
- *Edge case:* Body containing `<<<END>>>` as a substring within a string literal does not terminate framing prematurely. (Bodies are length-prefixed? No — bodies are PowerShell text and may legitimately contain those characters inside string literals. Solution: the mode-header line is fixed; the body is read until the next bare `<<<END>>>` on its own line. Validate test reproduces the existing `PwshWorker` behavior, which has the same exposure today.)
- *Edge case:* Output line containing `<<<EXIT:N>>>` as substring (not at start) is delivered as output, not interpreted as terminator. (Mirrors current `line.StartsWith("<<<EXIT:") && line.EndsWith(">>>")` check at `PwshWorker.cs:329`.)
- *Edge case:* `<<<MODE:script:...>>>` with argv containing newlines / quotes — base64 encoding sidesteps escaping concerns. Test argv `["a", "b\nc", "d'e"]` round-trips byte-exact.
- *Error path:* Truncated request (client closes mid-body) surfaces `IOException` to the host's request handler.
- *Security:* Body containing `; Remove-Item -Path C:\` is not interpreted by the framing layer — it's the runspace's job. (Per `.claude/rules/qa-rubric.md` Directive 12.)

**Verification:**
- `./scripts/test.sh --filter HostProtocol` green.
- Byte-for-byte fixture file `src/PsBash.Core.Tests/fixtures/protocol-request-c.bin` matches the spec.

---

- [ ] **Unit 5: Host server — accept loop, connection dispatcher, idle timeout, parent-death watcher**

**Goal:** Make `ps-bash-host` a real long-lived process that listens, dispatches, and cleans up correctly.

**Requirements:** R2, R3, R6 (partially), R8 (partially)

**Dependencies:** Units 2, 3, 4

**Files:**
- Modify: `src/PsBash.Host/Program.cs`
- Create: `src/PsBash.Host/Server/HostServer.cs`
- Create: `src/PsBash.Host/Server/Connection.cs`
- Create: `src/PsBash.Host/Server/IdleShutdown.cs`
- Create: `src/PsBash.Host/Server/ParentDeathWatcher.cs`
- Test: `src/PsBash.Host.Tests/Server/HostServerTests.cs`
- Test: `src/PsBash.Host.Tests/Server/IdleShutdownTests.cs`

**Approach:**
- `HostServer.RunAsync(IIpcTransport)` runs the accept loop, spawns a `Connection` per accepted stream. Each `Connection` reads exactly one request (mode + body), calls `SdkWorker.ExecuteAsync` (or `QueryAsync` for the cwd/PROMPT_COMMAND probes), streams the response, closes the stream.
- `IdleShutdown`: when the in-flight connection count drops to zero, start a timer (default 600 s, env override `PSBASH_HOST_IDLE_SECS`); on tick, exit. Reset the timer when a new connection accepts.
- `ParentDeathWatcher`: each `Connection` registers the launcher's PID (sent in mode header? — simpler: `peercred` on Unix sockets, `GetNamedPipeClientProcessId` on Windows pipes). On disconnect, if the PID dies during execution, abort the in-progress `PowerShell.Stop()`.
- `Program.cs`: parses `--stop-host` (sends a poison-pill request and exits), `--debug` (logs to stderr), default mode (writes lock file, listens). Logs go to a rolling file at `~/.psbash/host.log` for diagnosis.
- Critical: catch every connection-handler exception and log; one bad request must not bring down the host. (Per `.claude/rules/qa-rubric.md` Directive 7.)

**Patterns to follow:**
- `src/PsBash.Core/Runtime/PwshWorker.cs:BuildInitScript` parent-PID poller (~L196) — reuse the 200 ms `Get-Process -Id $pp` pattern.
- `src/PsBash.Shell/JobObjectWatchdog.cs` for the Windows-specific kill-tree on host crash.

**Test scenarios:**
- *Happy path:* Server accepts a connection, executes `Invoke-BashEcho hi`, returns `<<<EXIT:0>>>`, closes the stream.
- *Happy path:* Two sequential connections share the runspace — `cd /tmp` on connection A, `pwd` on connection B reports `/tmp`.
- *Edge case:* 16 simultaneous connections (matching named-pipe `maxInstances`) all complete; 17th queues briefly. Output does not interleave.
- *Edge case:* Idle timeout fires after 1 s (test override) when no connections; host process exits cleanly; lock file deleted.
- *Edge case:* Idle timer resets when new connection accepts.
- *Error path:* Connection sends garbled mode header → host returns `<<<EXIT:2>>>` with a stderr-like line, closes connection, stays alive.
- *Error path:* `SdkWorker.ExecuteAsync` throws → caught at connection boundary, host stays alive.
- *Error path:* `--stop-host` from launcher → host completes in-flight commands, refuses new connections, exits 0.
- *Integration:* Launcher process dies mid-command; host detects via parent-PID poll; aborts `PowerShell.Stop()`; cleans up runspace state. (Per `MEMORY.md` "Windows process death".)
- *Negative:* Bad ACL on named pipe (different-user connection attempt) → connection refused at OS layer; host log records the attempt. `[Trait("Platform","Windows")]`.
- *Negative:* Bad transpiled PowerShell with `; Remove-Item -Path C:\` literal in a string → executes inside the runspace's quoting boundary, does not escape. Per `.claude/rules/qa-rubric.md` Directive 12.

**Verification:**
- `./scripts/test.sh src/PsBash.Host.Tests --filter HostServer` green on Win/Linux/macOS.
- Log file at `~/.psbash/host.log` contains start/stop/idle-shutdown lines after a manual run.

---

- [ ] **Unit 6: `IpcWorker : IWorker` — launcher-side client**

**Goal:** Implement the thin client that the AOT launcher uses; pluggable into the seam from Unit 1.

**Requirements:** R1, R3, R4, R10

**Dependencies:** Units 1, 3, 4

**Files:**
- Create: `src/PsBash.Core/Runtime/IpcWorker.cs`
- Test: `src/PsBash.Core.Tests/Runtime/IpcWorkerTests.cs`

**Approach:**
- `IpcWorker.StartAsync(string hostBinaryPath, ...)`:
  1. Read lock file (Unit 3); if present, dial the advertised transport.
  2. If absent or stale (peer doesn't accept), spawn `ps-bash-host` with `Process.Start` (no console, parent's stdio not inherited), wait up to 5 s for the lock file to appear (poll every 50 ms — bounded, no `Sleep(N)` per `.claude/rules/qa-rubric.md` Directive 6 — this is a startup poll, not a test-side sleep).
  3. Once dialed, the connection stays open until `DisposeAsync`. `ExecuteAsync` writes a `<<<MODE:c>>>` request and reads the response; same for `QueryAsync` (just a different `mode` payload? — phase 1 reuses `<<<MODE:c>>>` with the body being `$LASTEXITCODE`).
- `OutputCallback` semantics identical to `PwshWorker` — every line not matching the `<<<EXIT:N>>>` sentinel is delivered to the callback.
- Crucially: `IpcWorker` is in `PsBash.Core`, which targets `net8.0;net10.0`. AOT compatibility verified by attempting an AOT publish in CI (Unit 11).

**Patterns to follow:**
- `src/PsBash.Core/Runtime/PwshWorker.cs:ExecuteAsync` for the read-loop and timeout handling — the 120 s default + `PSBASH_TIMEOUT` env override is reused verbatim.

**Test scenarios:**
- *Happy path:* `IpcWorker` against a running test host runs `echo hello`, returns 0, output callback fires with `"hello"`.
- *Happy path:* `IpcWorker.QueryAsync("$LASTEXITCODE")` returns `"0"`.
- *Edge case:* Lock file exists but host is dead; `IpcWorker.StartAsync` detects, deletes stale lock, spawns new host.
- *Edge case:* Host spawn race — two launchers spawn at the same instant; only one host wins, the other connects to the survivor. (Use a file-lock guard around lock-file write.)
- *Error path:* Host binary missing → `IpcWorker.StartAsync` throws a typed `HostUnavailableException` that the launcher catches and falls back to `PwshWorker`.
- *Error path:* `PSBASH_TIMEOUT=1` env set, command takes 5 s → returns 124, sends `<<<MODE:cancel>>>`? — phase 1 just closes the connection; the host's parent-PID watcher kicks in as the secondary stop. Document this in code.
- *Integration:* AOT publish of a sample app referencing `IpcWorker` succeeds (no `[RequiresUnreferencedCode]` warnings introduced).

**Verification:**
- `./scripts/test.sh src/PsBash.Core.Tests --filter IpcWorker` green.
- `dotnet publish src/PsBash.Shell -c Release -r linux-x64 -p:PublishAot=true` succeeds with no new warnings.

---

- [ ] **Unit 7: Launcher dispatch — connect → spawn → fallback**

**Goal:** Make `Program.cs` route every mode through `IWorker`, picking `IpcWorker` when possible and `PwshWorker` when not.

**Requirements:** R1, R5, R9

**Dependencies:** Units 1, 6

**Files:**
- Modify: `src/PsBash.Shell/Program.cs`
- Modify: `src/PsBash.Shell/InteractiveShell.cs` (worker factory only — interactive handoff comes in Unit 8)
- Create: `src/PsBash.Shell/WorkerFactory.cs`
- Test: `src/PsBash.Shell.Tests/WorkerFactoryTests.cs`
- Test: `src/PsBash.Shell.Tests/ProgramFallbackTests.cs`

**Approach:**
- `WorkerFactory.CreateAsync(...)`:
  1. If `PSBASH_DISABLE_HOST=1` env is set → return `PwshWorker` immediately. (Test-mode + emergency override.)
  2. Locate `ps-bash-host` next to `ps-bash` on disk. If missing → log `[ps-bash] host binary not found, falling back to subprocess pwsh`, return `PwshWorker`.
  3. Try `IpcWorker.StartAsync(...)`. On `HostUnavailableException` → log warning, fall back.
  4. Otherwise return `IpcWorker`.
- `Program.cs`: replace each `await PwshWorker.StartAsync(...)` with `await WorkerFactory.CreateAsync(...)`.
- Soft-fallback warning is rate-limited per process — once per launcher invocation max.

**Patterns to follow:**
- `src/PsBash.Shell/Program.cs:42` `PwshLocator.Locate()` — same shape (probe, log, return) for `ps-bash-host`.

**Test scenarios:**
- *Happy path:* `PSBASH_HOST=/path/to/test-host ps-bash -c 'echo hi'` uses `IpcWorker`.
- *Happy path:* `PSBASH_DISABLE_HOST=1 ps-bash -c 'echo hi'` uses `PwshWorker`; output identical.
- *Happy path:* `PSBASH_HOST=/nonexistent ps-bash -c 'echo hi'` falls back to `PwshWorker`, prints one warning, exit 0.
- *Edge case:* Host binary exists but is executable that immediately exits 1 — fallback after 5 s spawn timeout, warning includes the host's stderr.
- *Integration:* All three of `-c`, stdin, `script.sh` paths in `Program.cs` use the factory.
- *Negative:* `WorkerFactory.CreateAsync` does not block the launcher's exit on disposal failures (timeout-bounded `DisposeAsync`).

**Verification:**
- `./scripts/test.sh src/PsBash.Shell.Tests` green.
- `PSBASH_TRACE=trace.log ps-bash -c 'echo hi'` shows the factory decision in the trace.

---

- [ ] **Unit 8: Interactive mode — host owns terminal**

**Goal:** When invoked interactively, the launcher hands off the tty to the host, which runs the REPL in-process. Fixes `clear`/`cls`/`reset` and `Console.WindowWidth`.

**Requirements:** R5, R6

**Dependencies:** Units 1–7

**Files:**
- Modify: `src/PsBash.Shell/Program.cs` (interactive branch at L120)
- Modify: `src/PsBash.Host/Program.cs` (add interactive sub-command)
- Move: `src/PsBash.Shell/InteractiveShell.cs` → `src/PsBash.Host/Shell/InteractiveShell.cs`
- Move: `src/PsBash.Shell/LineEditor.cs` → `src/PsBash.Host/Shell/LineEditor.cs`
- Move: `src/PsBash.Shell/SqliteHistoryStore.cs` → `src/PsBash.Host/Shell/SqliteHistoryStore.cs`
- Move: `src/PsBash.Shell/TabCompleter.cs` → `src/PsBash.Host/Shell/TabCompleter.cs`
- Modify: `src/PsBash.Shell/PsBash.Shell.csproj` (drop moved files)
- Modify: `src/PsBash.Host/PsBash.Host.csproj` (add moved files + Sqlite ref)
- Test: `src/PsBash.Host.Tests/Shell/InteractiveModeTests.cs` (PTY-based per `.claude/rules/qa-rubric.md` Directive 6)

**Approach:**
- Launcher behavior on `interactive`: send `<<<MODE:interactive>>>` over the socket, host responds with its PID, launcher then `exec`-style replaces itself by calling `Process.Start(hostBinary, "--interactive --launcher-pid=<pid>")` with `RedirectStandardInput=false`, `RedirectStandardOutput=false`, `RedirectStandardError=false` so the spawned host inherits the real tty. Launcher waits and forwards the host's exit code.
- Inside the host's `--interactive` mode: existing `InteractiveShell.RunAsync` runs, but now in the host's address space. `Console.Clear()`, `Console.WindowWidth`, `EnsureVirtualTerminalEnabled` all work because the host owns the tty.
- The shared runspace is reused — interactive mode talks to the same `SdkWorker` instance, no IPC.
- `PROMPT_COMMAND`, history, alias state (currently in `InteractiveShell.Aliases` static field) survive across calls naturally because the process stays alive.
- Keep the legacy `PwshWorker`-backed interactive REPL for the soft-fallback path: `Program.cs` interactive branch picks between "exec into host" and "in-launcher REPL" based on whether the host is available.

**Patterns to follow:**
- `src/PsBash.Shell/InteractiveShell.cs` as-is — moves wholesale, no behavior change. Only the worker construction shifts from `PwshWorker.StartAsync` to direct `SdkWorker` access.

**Execution note:** Move files first as a single mechanical commit (no behavior change), then change the worker plumbing in a second commit. Keeps the diff reviewable.

**Test scenarios:**
- *Happy path:* `ps-bash` (no args) prompts; user types `clear` → screen clears (assert via PTY transcript).
- *Happy path:* `Console.WindowWidth` reported by `tput cols` matches actual tty width post-resize (`[Trait("Platform","Linux")]` for SIGWINCH; manual on Windows).
- *Happy path:* `cd /tmp` then `pwd` → `/tmp` across the same session, then again on a new `ps-bash` invocation that connects to the *same* host (state survives because runspace persists across REPL turns within one process; new launcher invocations open new connections, so they get fresh `cd` state — confirm this matches user expectation; it should match bash since separate `bash` invocations don't share cwd).
- *Edge case:* `Ctrl+C` at the prompt returns to a fresh prompt; `Ctrl+C` mid-command stops the runspace and returns control. (Existing `Console.CancelKeyPress` handler.)
- *Edge case:* Host binary missing → launcher falls back to legacy in-launcher REPL with `PwshWorker`; `clear` still works via the existing `TryRunDirect` intercept; `Console.WindowWidth` is still wrong (documented limitation in fallback mode).
- *Error path:* Host crashes during interactive session → launcher prints `[ps-bash] host died; exiting`, returns 1.
- *Negative:* PROFILE script throws → message surfaces, prompt still appears. (Existing behavior at `InteractiveShell.cs:387`.)

**Verification:**
- `./scripts/test.sh src/PsBash.Host.Tests --filter Interactive` green on Linux/macOS PTY.
- Manual: `ps-bash` interactive on Windows Terminal → `clear` works; `tput cols` reports correct width; resize updates.
- `ps-bash --interactive` from inside Claude Code's Bash tool does not crash on `Console.IsInputRedirected = true` paths.

---

- [ ] **Unit 9: Script mode — `exit N`, `source`, `set -e` parity on the shared runspace**

**Goal:** Fix the three structural bugs in `Program.cs` script-mode (handoff doc §2.3) by leaning on the in-process runspace.

**Requirements:** R5, R8

**Dependencies:** Units 2, 6, 7

**Files:**
- Modify: `src/PsBash.Host/Runtime/SdkWorker.cs` — add `ExecuteScriptAsync(string path, string[] argv, string transpiledBody)`
- Modify: `src/PsBash.Host/Server/Connection.cs` — handle `<<<MODE:script:...>>>`
- Modify: `src/PsBash.Core/Runtime/IpcWorker.cs` — add `Task<int> ExecuteScriptAsync(...)` to `IWorker`? — keep the surface narrow: extend `IWorker` with one method only.
- Modify: `src/PsBash.Core/Runtime/IWorker.cs` — add the new method
- Modify: `src/PsBash.Core/Runtime/PwshWorker.cs` — implement the new method by emitting the existing positional-preamble + `Invoke-Expression` flow (legacy fallback semantics unchanged)
- Modify: `src/PsBash.Shell/Program.cs` — script branch (L55) calls `worker.ExecuteScriptAsync` instead of constructing the preamble + body manually
- Test: `src/PsBash.Differential.Tests/ScriptModeFixturesTests.cs` (new fixture group: `exit-in-function.sh`, `source-lib.sh`, `set-e-in-script.sh`)

**Approach:**
- In `SdkWorker.ExecuteScriptAsync`: catch the SDK's `ExitException` (raised by `exit N` in the script body) at the `PowerShell.Invoke()` call boundary, return `N` without aborting the runspace.
- `source ./lib.sh`: the host's `Invoke-BashSource` runtime function transpiles the file and `Invoke-Expression`s it into the same runspace — already works in `PsBash.Cmdlets`; reuse via the in-process call path.
- `set -e`: `$global:__BashErrexit = $true` setting propagates within the runspace just as it does today inside one transpiled block; now it also propagates across `source` boundaries, which is correct bash semantics.
- For the legacy `PwshWorker` path, the new `ExecuteScriptAsync` falls back to the existing `BuildPositionalPreamble + ExecuteAsync(preamble + body)` shape. No regression.

**Patterns to follow:**
- `src/PsBash.Module/PsBash.psm1` `Invoke-BashSource` for the recursive transpile+execute pattern. The host calls into this function; no new code to write on the runtime side.

**Test scenarios:**
- *Happy path:* `bash -c 'echo $?' ; ps-bash echo-then-exit-7.sh` → exit 7 (matches bash). (Differential per Directive 1.)
- *Happy path:* `source ./lib.sh; foo` where `lib.sh` defines `foo() { echo from-lib; }` → prints `from-lib`, exit 0.
- *Edge case:* `exit 3` inside a function inside a script → entire script exits 3, host runspace stays alive for next invocation.
- *Edge case:* `set -e; false; echo unreachable` → exit 1, "unreachable" not printed. Differential: same exit code as bash.
- *Edge case:* `set -e` inside a `source`d file leaks back to the caller (per bash semantics) — confirmed by existing differential tests; verify they pass under `SdkWorker`.
- *Error path:* `source nonexistent.sh` → exit 1, error message matches bash. Differential.
- *Edge case:* Two consecutive `ps-bash script.sh` invocations against the same host don't share state (each gets a fresh `BashPositional`, fresh `__BashErrexit`). This requires the host to **scope** these variables per-invocation; document in `SdkWorker.ExecuteScriptAsync` and assert.
- *Negative:* `ps-bash script.sh` where script does `exit 0; rm -rf /` — second statement does not execute. Per Directive 12.
- *Failure-axis:* large script (10 MB transpiled body) round-trips. Per Directive 3 axis 2.

**Verification:**
- `./scripts/test.sh src/PsBash.Differential.Tests --filter ScriptMode` green.
- `git diff` on `src/PsBash.Module/PsBash.psm1` is empty for this unit.

---

- [ ] **Unit 10: Process substitution — string-capture and pipeline-object paths primary; pipe path for external-to-external only**

**Goal:** Replace `Invoke-ProcessSub`'s blanket temp-file buffering with three paths chosen by emitter context. Pipes are the rare path, not the headline.

**Requirements:** R7

**Dependencies:** Units 2, 5

**Files:**
- Modify: `src/PsBash.Core/Parser/PsEmitter.cs` — at `<()` emit sites, classify the consumer and emit one of three forms
- Modify: `src/PsBash.Module/PsBash.psm1` — `Invoke-ProcessSub` keeps the temp-file fallback; add `Invoke-ProcessSubString` (capture-to-string) and `Invoke-ProcessSubPipeline` (yield producer output as pipeline objects)
- Create: `src/PsBash.Host/Runtime/ProcessSubBridge.cs` — only the pipe path; opens an `AnonymousPipeServerStream` pair when the emitter requested it
- Test: `src/PsBash.Differential.Tests/ProcessSubFixturesTests.cs`

**Approach:**

Classify each `<(producer)` site at emit time by the consumer it feeds:

1. **String-capture path (common).** Consumer reads the file whole and small — `source <(...)`, `eval "$(...)"` (semantically equivalent shape), or any `<()` whose output is bounded and feeds an in-runtime command that wants a body string. Emitter rewrites to `Invoke-ProcessSubString { producer }` which runs the scriptblock, captures stdout into a string, returns it. Consumer takes the string directly — no path, no pipe, no temp file. `source <(fnm init --bash)` is the canonical example.

2. **Pipeline-object path (common).** Consumer is one of the 66 mapped runtime functions and its semantic input is "lines from this stream." Emitter can rewrite `cmd <(producer) other-arg` into `producer | Invoke-BashCmd other-arg` — equivalent for line-oriented consumers. `diff <(grep foo a) <(grep foo b)` uses two producers; emitter routes via `Invoke-BashDiff` taking two scriptblock parameters and reading them into two PowerShell collections, no path involved.

3. **Real anonymous pipe (rare).** Consumer is an *external* process not in our mapping (`tar -xf <(curl ...)`, `ffmpeg -i <(...)`, `kubectl apply -f <(helm template ...)`), or two external consumers (`/usr/bin/diff <(...) <(...)` after the user explicitly disabled the runtime mapping). Emitter falls back to `Invoke-ProcessSub { producer }` which:
   - Inside the host: opens `AnonymousPipeServerStream.CreatePair()`, runs producer in a child runspace writing to the server end, returns `\\.\pipe\<handle>` (Windows) or `/proc/self/fd/<n>` (Linux/macOS) for the client end.
   - Outside the host (legacy `PwshWorker` fallback): keeps today's temp-file behavior unchanged.

4. **Seekable-required temp file (rare).** Specific consumers that `lseek` (`comm` reading two inputs in lockstep when both are large) — emitter pins these to the temp-file path. Document the list in `PsEmitter.cs` next to the classification logic.

Classification is entirely an emitter concern; runtime functions just expose the three entry points and don't know which was picked.

**Patterns to follow:**
- `src/PsBash.Core/Parser/PsEmitter.cs` — existing mapping table for the 66 commands tells the emitter which consumers are in-runtime. Reuse `IsKnownCommand`.
- `src/PsBash.Module/PsBash.psm1:Invoke-ProcessSub` — keep the function name and signature; new variants live alongside.

**Test scenarios:**
- *Happy path (string capture):* `source <(fnm init --bash)` works; `fnm` runs once; `Invoke-BashSource` receives the body as a string. No temp file written. Differential vs bash.
- *Happy path (string capture):* `source <(echo 'export FOO=bar')` then `echo $FOO` → `bar`.
- *Happy path (pipeline object):* `diff <(seq 1 10) <(seq 1 10)` → empty, exit 0. Both producers feed `Invoke-BashDiff` directly. No path, no pipe. Differential vs bash.
- *Happy path (pipeline object):* `diff <(echo a) <(echo b)` → one-line diff, exit 1.
- *Happy path (pipeline object):* `cat <(echo one) <(echo two)` → two lines.
- *Happy path (pipe path):* `tar -tzf <(curl -s https://example.com/x.tgz)` lists archive contents while curl is still streaming. (External tar; pipe needed.) Skipped when no network — gate behind `[Trait("Network","true")]`.
- *Pipe-path streaming proof:* with a deliberately slow producer (`Invoke-BashSeq 1 100; Start-Sleep 0.01`) feeding an external consumer, first consumer read happens before producer finishes. Bounded by less than producer total runtime.
- *Seekable path:* `comm <(sort big1) <(sort big2)` falls into the seekable temp-file path; documented and asserted.
- *Edge case:* Consumer doesn't read all input → producer scriptblock cancellation token signals; producer exits without leaking the runspace. (Pipe and pipeline-object paths.)
- *Edge case:* Two `<()` substitutions on one pipeline don't deadlock — `paste <(seq 1 5) <(seq 6 10)` works whether classified as pipeline-object (both into `Invoke-BashPaste`) or pipe (if mapping is disabled).
- *Error path:* Producer scriptblock throws — string-capture path returns empty body and non-zero exit code visible in `$LASTEXITCODE`; pipeline-object path raises in the consumer; pipe path serializes error to consumer's stderr.
- *Per Directive 3 axis 5 (broken pipe):* pipe-path consumer closes early → producer doesn't hang.
- *Per Directive 3 axis 6 (slow reader):* pipe-path producer doesn't buffer unbounded.
- *Coverage:* every test that uses `<()` runs against both `IpcWorker` and `PwshWorker` (legacy keeps temp-file behavior); behavior parity asserted byte-for-byte.

**Verification:**
- `./scripts/test.sh src/PsBash.Differential.Tests --filter ProcessSub` green.
- Manual: `source <(fnm init --bash)` (or any `eval "$(...)"`-shaped script in the wild) works in interactive mode.
- Trace counter: classification rates logged under `PSBASH_TRACE` show string + pipeline-object paths dominate in real-world fixture scripts; pipe path rare.

---

- [ ] **Unit 11: Build, publish, and CI updates**

**Goal:** Ship both binaries together; existing release flow works unchanged; AOT publish stays clean.

**Requirements:** R1, R2, R11

**Dependencies:** Units 2, 7

**Files:**
- Modify: `src/PsBash.Shell/PsBash.Shell.csproj` — confirms `<PublishAot>true</PublishAot>` still works after `IpcWorker` lands in `PsBash.Core`
- Modify: `src/PsBash.Host/PsBash.Host.csproj` — `<SelfContained>true</SelfContained>`, `<PublishTrimmed>false</PublishTrimmed>`, `<RuntimeIdentifiers>win-x64;linux-x64;osx-arm64</RuntimeIdentifiers>`
- Modify: `.github/workflows/publish.yml` — new matrix job `build-host-binaries` mirroring `build-binaries`; combine outputs into single per-platform zip
- Modify: `.github/workflows/build.yml` — add host build to PR validation
- Modify: `.github/workflows/canary.yml` — canary suite (Directive 8) runs against both `IpcWorker` and `PwshWorker` paths
- Modify: `scripts/pack-local.ps1` — bundles host binary alongside launcher for local testing
- Modify: `scripts/test.sh` — sets `PSBASH_HOST` env to the local host build for `IpcWorker` tests, `PSBASH_DISABLE_HOST=1` for `PwshWorker` regression tests
- Test: `src/PsBash.Canary.Tests/HostFallbackCanary.cs` — runs the canary suite end-to-end in both modes

**Approach:**
- Release zip layout per platform:
  ```
  ps-bash-{version}-{rid}.zip
    ├── ps-bash{.exe}        # AOT launcher
    ├── ps-bash-host{.exe}   # JIT host
    └── runtimes/            # SDK self-contained runtime (host's deps)
  ```
- Canary suite (Directive 8) gets a parameterized fixture `[Theory] [InlineData("ipc")] [InlineData("subprocess")]` so every canary test runs against both transports. Hard cap: under 60 s per mode per platform per Directive 8.
- AOT publish guard: a CI step does `dotnet publish src/PsBash.Shell -c Release -r linux-x64 -p:PublishAot=true /warnaserror` to fail the build on any new AOT warning introduced by `IpcWorker`.

**Patterns to follow:**
- Existing `.github/workflows/publish.yml` matrix shape — replicate, don't reinvent.
- `scripts/pack-local.ps1` for the version-sync logic.

**Test scenarios:**
- *Happy path:* CI green on Win/Linux/macOS for both transports.
- *Happy path:* `dotnet publish src/PsBash.Shell -c Release -r linux-x64 -p:PublishAot=true` produces a binary <10 MB with no warnings.
- *Happy path:* `dotnet publish src/PsBash.Host -c Release -r linux-x64 --self-contained` produces a directory <100 MB.
- *Edge case:* Release zip extraction on a Windows machine with no installed pwsh → `ps-bash -c 'echo hi'` works (host is self-contained).
- *Edge case:* Old release zip without `ps-bash-host` → fallback warning, behavior identical to v0.8.x.
- *Negative:* `dotnet test` for `PsBash.Shell.Tests` doesn't accidentally invoke `dotnet test` directly per `CLAUDE.md`. Use `./scripts/test.sh`.

**Verification:**
- `gh run list --workflow=publish.yml --limit 1` green for the next tag after this lands.
- `Find-Module PsBash | Select-Object Version` shows the new version on PSGallery (module publish unchanged).
- AOT-publish step has zero warnings.

---

- [ ] **Unit 12: Differential parity sweep + canary expansion**

**Goal:** Prove behavioral parity between the two transports across the failure-surface matrix, lock in known-bad regression tests.

**Requirements:** R10

**Dependencies:** Units 1–11

**Files:**
- Modify: `src/PsBash.Differential.Tests/` — every existing differential test runs in both `ipc` and `subprocess` modes via a shared fixture
- Create: `src/PsBash.Canary.Tests/Modes/` — one test file per mode (M1–M6) per `.claude/rules/qa-rubric.md` Directive 4
- Create: `src/PsBash.Escalation.Tests/HostLifecycleTests.cs` — known-bad regression tests for: stale lock file, host crash mid-command, host idle-shutdown race, parent-death during interactive
- Test: all the above

**Approach:**
- Parameterize the differential test base class on transport. Tests that already exist gain coverage for free.
- Canary tests must run in <60 s per mode per platform (Directive 8). Pick the smallest representative test per failure-surface axis.
- Per `.claude/rules/qa-rubric.md` Directive 13, every known-bad memory becomes a permanent regression test:
  - "Windows process death" → `HostDiesWhenLauncherKilled` test (host's parent-PID poll fires).
  - "Process spawn contract" → `HostSpawnTimeout` test (5 s cap on host startup, falls back).
  - "Quote `--filter |`" → already an `xunit` config concern; document in test runner script.
- Per Directive 1, every new behavioral test is differential against bash where bash has equivalent behavior.

**Patterns to follow:**
- `src/PsBash.Differential.Tests/` existing fixtures — extend the base class, don't fork.
- `src/PsBash.Canary.Tests/` existing structure — mirror per-mode.

**Test scenarios:**
- *Coverage*: every existing differential test runs in `ipc` mode and stays green.
- *Coverage*: every existing differential test runs in `subprocess` (legacy) mode and stays green.
- *Per Directive 3 axis 1 (empty input):* `echo "" | ps-bash` → exit 0 in both modes.
- *Per axis 2 (large input):* 10 MB through a pipe, both modes, exit 0, byte-exact output.
- *Per axis 3 (unicode):* BOM, emoji, combining marks; both modes; byte-exact.
- *Per axis 5 (broken pipe):* `seq 1 1000000 | head -1` — both modes, exit 0, no orphaned producer.
- *Per axis 7 (signal):* `Ctrl+C` mid-pipe in interactive mode under host — runspace stops, prompt returns.
- *Per axis 8 (exit code):* `false || true && echo $?` → `0` in both modes; differential vs bash.
- *Per axis 13 (locked file):* Windows binary swap during `ps-bash install ./tool.exe` — runtime function unchanged, both modes.
- *Per Directive 12 (security):* var with `;`, `$(...)`, scriptblock chars, IFS=$'\n' with glob, heredoc with `"`/`$`/backtick — none execute injection in either mode.
- *Known-bad:* "Fix reproduces bug" — a fix-in-progress against the host that visibly reproduces its own bug must land with test infra (per `MEMORY.md`). Codify by requiring every host-touching PR to include a regression test before merge — enforced by `.claude/rules/qa-rubric.md` Directive 11 acceptance bars.

**Verification:**
- `./scripts/test.sh` green on Win/Linux/macOS.
- Branch coverage ≥80% on `IpcWorker.cs`, `HostServer.cs`, `HostProtocol.cs`, `SdkWorker.cs` (per Directive 2).
- Diff coverage ≥90% on this PR (per Directive 2).
- Zero flakes over 100 re-runs of the canary suite on CI matrix (per Directive 2).

## System-Wide Impact

- **Interaction graph:** `Program.cs` and `InteractiveShell.cs` route every worker call through `IWorker`. The only direct `PwshWorker` references after this lands are inside `WorkerFactory` (fallback path) and `PsBash.Cmdlets` (which uses its own host-pwsh runspace, untouched).
- **Error propagation:** Each `Connection` boundary in the host catches all exceptions; the host stays alive across one bad command. Launcher-side, `IWorker.ExecuteAsync` returns the same `(stdout, exit)` shape; `OperationCanceledException` from `Ctrl+C` is preserved verbatim. Soft-fallback warnings go to stderr exactly once per launcher invocation.
- **State lifecycle risks:** Shared runspace = shared global state across calls within one host process. `$global:BashPositional`, `$global:__BashErrexit`, `$env:*` mutations persist. Unit 9 explicitly scopes these per-invocation in `ExecuteScriptAsync`; Unit 5 documents the policy in `Connection`. Lock file write/delete must be atomic to avoid two hosts racing.
- **API surface parity:** `IWorker` is internal to `PsBash.Core`. `PsBash.Core` NuGet consumers (the package is published; see `PsBash.Core.csproj`) get a new public type — review whether `IWorker` should be `internal` instead. Default: `internal` plus `[InternalsVisibleTo]` for the test projects.
- **Integration coverage:** The test matrix in Unit 12 is the cross-layer assurance. Unit tests alone don't prove the protocol round-trips real bash semantics.
- **Unchanged invariants:** `BashLexer`, `BashParser`, `PsEmitter`, `PsBash.psm1` runtime functions, `PsBash.psd1` manifest, alias model, history schema, tab-completer behavior. PsBash.Core's NuGet contents do not gain SDK or pipe dependencies (those live in `PsBash.Host`).

## Risks & Dependencies

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `Microsoft.PowerShell.SDK` brings runtime conflicts (e.g. `System.Management.Automation` version skew with user-installed pwsh modules) | Med | High | Self-contain the host; don't share AppDomain with user pwsh. Pin SDK version. Test against modules pinned in `~/.psbashrc` fixtures. |
| Unix socket path on macOS exceeds 104 char `sun_path` limit when `Path.GetTempPath()` returns a long sandboxed path | Med | Med | Probe path length; fall back to named pipe (Linux/macOS named pipes are FIFO files; alternative is `${HOME}/.psbash/sock`). Implement in Unit 3. |
| Host crash mid-interactive leaves the user's terminal in a broken VT mode | Low | Med | Launcher catches host exit; runs `EnsureConsoleInputRestored` before exiting (port the existing helper). |
| AOT launcher gains a warning from `IpcWorker` referencing pipe APIs that aren't trim-safe | Med | High | CI gate (Unit 11) fails on new AOT warnings. Use `[DynamicallyAccessedMembers]` + `[RequiresUnreferencedCode]` annotations only when unavoidable. |
| Per-launcher process leaks one connection if launcher crashes after sending request but before reading response | Med | Low | Host detects via parent-PID poll; closes connection; cancels in-flight `PowerShell.Stop()`. Per `MEMORY.md` "Process spawn contract." |
| Stale lock file after host SIGKILL leaves new launchers spinning | Low | Med | Connect probe with 100 ms timeout before trusting the lock file; on failure, delete and respawn. |
| Two ps-bash processes from different ttys share one host and step on `$env:*` mutations | High | Med | Phase 1 documents this as expected (matches "all tabs share one bash" mental model? — bash actually doesn't share, so this is **divergent**). Mitigation: per-connection env snapshot/restore in `Connection`. Spike in Unit 5; if costly, scope env per-call by default. |
| Antivirus quarantines `ps-bash-host.exe` on first run → silent fallback | Med | Low | Soft-fallback warning is loud (stderr, not silent). |
| Idle-shutdown timer races with an in-flight long command | Low | Med | Timer is reset by accept *and* extended by in-flight count > 0. |
| Memory leak in long-lived runspace (modules accumulate, GC roots persist) | Med | Med | 512 MB cap from current worker is preserved as a host-wide limit; on breach, host exits. (Existing `PSBASH_MAX_MEMORY` env carries over.) Per Directive 9, log workingset on shutdown. |

## Phased Delivery

### Phase 1: Seam + host stub (Units 1–4)
- `IWorker` extracted, `PwshWorker` continues to be the only impl. No behavior change.
- `PsBash.Host` builds and runs as a no-op standalone.
- Transport + protocol code lands with full unit-test coverage.
- Mergeable as a single PR; zero user-visible change.

### Phase 2: Real host wired in (Units 5–7)
- Host server runs commands; launcher connects when host is available; soft-fallback otherwise.
- `-c`, stdin, and script modes work end-to-end through the host.
- Behavior matches `PwshWorker` byte-for-byte on the differential suite.
- Mergeable as a second PR; opt-in via env (`PSBASH_HOST=...`); off by default in this phase.

### Phase 3: User-visible wins (Units 8, 9, 10)
- Interactive mode hands off to host. `clear`, `Console.WindowWidth`, terminal resize all correct.
- Script mode: `exit N`, `source`, `set -e` parity. Differential tests on previously-skipped fixtures pass.
- `<()` streams. Streaming differential tests pass.
- Mergeable as a third PR; host becomes the default if present, fallback warns.

### Phase 4: Build, ship, and lock in regression coverage (Units 11, 12)
- Both binaries ship in one zip per platform.
- Canary expanded; failure-surface matrix populated; all known-bad regression tests in place.
- Release tagged; PSGallery and NuGet publishes unchanged.

## Documentation Plan

- `docs/specs/host-architecture.md` (new): wire protocol, lifecycle, fallback, lock-file format.
- `docs/specs/runtime-functions.md` §7 update: `Invoke-ProcessSub` host-aware fast path.
- `CLAUDE.md`: add `PSBASH_DISABLE_HOST=1` and `PSBASH_HOST_IDLE_SECS` env vars to the release-process notes; mention `ps-bash --stop-host` as a debugging command.
- `README.md`: one-paragraph note that v0.9 ships two binaries, no install change for users.
- `docs/solutions/in-process-host-migration.md`: capture learnings post-merge per CE practice (this directory is currently empty; this becomes the seed entry).

## Operational / Rollout Notes

- **Default flag rollout:** Phase 2 ships the host opt-in. Phase 3 flips the default to "use host if available." Phase 4 leaves the legacy path in place indefinitely as soft-fallback.
- **Telemetry:** `PSBASH_TRACE` env var (already present in `Program.cs:18`) gains a `transport=ipc|subprocess` field per invocation.
- **Diagnostics:** `ps-bash --diagnose` (new flag) prints host status, lock file contents, runspace memory, and last-N log lines.
- **Rollback:** `PSBASH_DISABLE_HOST=1` env makes a single launcher session bypass the host without code changes. CI canary runs both modes pre-release.
- **Monitoring:** GitHub Actions canary workflow runs every 6h on the matrix; alerts on regression.

## Sources & References

- **Origin document:** [docs/planning/architecture-migration-handoff.md](../planning/architecture-migration-handoff.md)
- Related code: `src/PsBash.Core/Runtime/PwshWorker.cs`, `src/PsBash.Shell/Program.cs`, `src/PsBash.Shell/InteractiveShell.cs`, `src/PsBash.Cmdlets/PsBash.Cmdlets.csproj`, `src/PsBash.Module/PsBash.psm1` (`Invoke-ProcessSub`).
- Specs: `docs/specs/parser-grammar.md`, `docs/specs/emitter-strategy.md`, `docs/specs/runtime-functions.md`.
- Rules: `.claude/rules/qa-rubric.md` (Directives 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13), `.claude/rules/temp-files.md`.
- Memory: `MEMORY.md` — Windows process death, Process spawn contract, Fix reproduces bug, Deferred hard parity bugs.
