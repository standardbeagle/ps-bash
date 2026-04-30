# ps-bash Architecture Migration — Planning Handoff

**Date**: 2026-04-28  
**Context**: This document captures the current state of ps-bash, a discussion of three
interconnected pain points, and the architectural options surfaced so another model can
drive the planning session.

---

## 1. Current Architecture

```
bash input
  → BashLexer → BashParser → PsEmitter
  → PwshWorker (subprocess, stdin/stdout text protocol)
  → PsBash.psm1 runtime (Invoke-Bash* functions)
```

### Key files

| File | Role |
|------|------|
| `src/PsBash.Core/Parser/BashLexer.cs` | Tokenizer |
| `src/PsBash.Core/Parser/BashParser.cs` | Recursive-descent AST builder |
| `src/PsBash.Core/Parser/PsEmitter.cs` | AST → PowerShell text |
| `src/PsBash.Core/Runtime/PwshWorker.cs` | Subprocess manager + text protocol |
| `src/PsBash.Shell/InteractiveShell.cs` | REPL loop, line editor, alias expansion |
| `src/PsBash.Shell/Program.cs` | Entry point, modes: -c / stdin / file / interactive |
| `src/PsBash.Module/PsBash.psm1` | 76 `Invoke-Bash*` runtime functions |

### Worker protocol

The worker is a `pwsh` process launched with `RedirectStandardInput = true`,
`RedirectStandardOutput = true`, `RedirectStandardError = false`, `CreateNoWindow = true`.

Commands are sent as newline-terminated PowerShell text terminated by `<<<END>>>`.
Responses are output lines terminated by `<<<EXIT:N>>>`. There is no out-of-band channel.

The module is base64-encoded and streamed over stdin at startup. The worker signals
readiness with `<<<READY>>>`. See `PwshWorker.BuildInitScript()` for the full loop.

### Build constraints — AOT

`PsBash.Shell` has `<PublishAot>true</PublishAot>`. This is load-bearing: it produces
the shipped win-x64/linux-x64/osx-arm64 binaries. **`Microsoft.PowerShell.SDK` does not
support AOT**. Any in-process hosting approach must address this constraint explicitly.

`PsBash.Core` targets `net8.0;net10.0` (NuGet package, no AOT).

---

## 2. Pain Points Under Discussion

### 2.1 Console I/O through the worker

**Symptom**: `cls`, `clear`, `reset` do nothing. `Clear-Host` in the worker writes to
the captured stdout pipe, not the real terminal.

**Fix applied today** (`InteractiveShell.cs:TryRunDirect`):
```csharp
if (cmdName is "cls" or "clear" or "reset")
{
    Console.Clear();
    exitCode = 0;
    return true;
}
```

**Root cause**: The worker's stdout is a protocol pipe. Any terminal control sequences
(ANSI, `Clear-Host`, cursor positioning) written by the worker go into the line-reader
channel, not the terminal. `$Host.UI.RawUI.WindowSize` returns 0 in non-interactive
pwsh with redirected stdout.

**Unaddressed**: The worker has no `COLUMNS`, `LINES`, or `TERM` environment variables.
Commands that adapt to terminal width (`ls` column layout, `column -t`, `tput cols`)
get garbage values. A partial fix (set these at startup, resend on resize) was
scoped but not implemented.

### 2.2 Process substitution `<()`

**Current implementation**: `Invoke-ProcessSub` in `PsBash.psm1` writes the
scriptblock's output to a temp file under `ps-bash/proc-sub/{random}` and returns the
path. The emitter wraps `<(cmd)` as `$(Invoke-ProcessSub { cmd })`.

**Problems**:
- Buffers all output before the consumer sees any — `diff <(slow-cmd) <(other-cmd)`
  serializes instead of streaming.
- Cleanup is manual; consumer crash → temp file leak.
- Some consumers need seekable input (e.g. `comm`), others need a stream. Temp files
  satisfy seekable; named pipes satisfy streaming. Neither works for both without
  detecting the consumer's needs.
- On Linux/macOS, bash uses `/dev/fd/N` (real file descriptors). ps-bash can't do this;
  a named pipe (`\\.\pipe\xxx` on Windows, `/tmp/fifoXXX` on Unix) is the best proxy.

### 2.3 Bash script execution (`ps-bash script.sh`)

**Current implementation** (`Program.cs:55-101`): read file → `BashTranspiler.Transpile`
→ prepend positional preamble → `worker.ExecuteAsync(wholeScript)`.

**Problems**:
- `exit N` inside the script sends PowerShell `exit N` to the worker, which kills the
  worker process. The shell catches the `IOException` from the dead pipe and respawns.
  Exit code is recovered from `_process.ExitCode`. This works but is fragile.
- `source ./lib.sh` requires the worker to open another file mid-execution — currently
  there is no mechanism for this; the sourced file must be pre-transpiled separately.
- `set -e` (`$global:__BashErrexit = $true`) works across the whole transpiled block
  since it's sent as one string, but interacts poorly with the worker's exit-code
  propagation path.
- Scripts that call `exit` while inside a function or subshell emit PowerShell `exit`
  which escapes the entire worker, not just the script.

---

## 3. Confirmed Direction: Two-Binary IPC Model

**Decision**: AOT is incompatible with `Microsoft.PowerShell.SDK` (reflection-heavy,
dynamic type loading, JIT-compiled script compilation — structural, not fixable with
annotations). The confirmed architecture is **two binaries communicating over a named pipe**.

### `ps-bash` (AOT launcher — unchanged)

- Stays AOT, stays small (~8MB native binary), cold-starts in ~50ms
- Argument parsing, path resolution, alias expansion, transpilation (all in `PsBash.Core`
  which already targets net8.0+net10.0 without AOT)
- For every mode: connects to the host over a named pipe, sends the transpiled command,
  streams output back, exits with the returned code
- If no host is running: spawns `ps-bash-host` and waits for the ready signal

### `ps-bash-host` (JIT, SDK-hosted — new binary)

- `Microsoft.PowerShell.SDK` in-process, non-AOT, self-contained JIT (~60-80MB)
- Maintains a **persistent named-pipe server** and a **long-lived runspace** with the
  PsBash module pre-loaded
- Accepts connections from the AOT launcher (one connection per command invocation)
- For interactive mode: owns the terminal directly — line editor, REPL loop, console I/O
  all run here in-process, no indirection
- Lifecycle: started on first `ps-bash` invocation, stays alive across calls (eliminates
  the ~700ms pwsh spawn + module load on every `-c` invocation after the first)

### IPC transport

Unix domain socket preferred, named pipe fallback:

```csharp
// Linux/macOS
var sockPath = $"/tmp/psbash-{Environment.UserName}-{sessionId}.sock";
// Windows (Unix sockets available since Win10 1803 / .NET 5)
var sockPath = Path.Combine(Path.GetTempPath(), $"psbash-{Environment.UserName}-{sessionId}.sock");
// Fallback if Unix socket unavailable (legacy Windows, restrictive environments)
var pipeName = $"psbash-{Environment.UserName}-{sessionId}";
```

`Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)` is available
in .NET 5+ on all three platforms. Named pipe (`NamedPipeServerStream`) is the fallback
for environments where Unix sockets are unavailable (e.g. very old Windows builds,
containers with restricted socket access).

The launcher probes Unix socket first; if `Connect` fails with `SocketException`
`AddressFamilyNotSupported`, retries with named pipe. Host advertises which transport
it's listening on via a small lock file: `psbash-{sessionId}.lock` containing
`unix:/path` or `pipe:name`.

### IPC protocol

The existing sentinel protocol (`<<<END>>>`, `<<<EXIT:N>>>`) reused verbatim over the
socket/pipe byte stream. Pipe name: `psbash-{username}-{sessionid}` (user-scoped,
one host per login session).

The protocol already handles multiplexed output + exit code. The only new message needed
is a **mode header** sent before the command body:

```
<<<MODE:c>>>          # -c one-shot
<<<MODE:interactive>>>  # hand off terminal
<<<MODE:script:/path/to/script.sh arg1 arg2>>>
<<<END>>>
<transpiled powershell>
<<<END>>>
```

For interactive mode, the launcher immediately delegates the terminal to the host process
and waits; the host runs the REPL loop directly.

### What this fixes

| Problem | Fix |
|---------|-----|
| `clear`/`cls`/`reset` | Host owns the real console; `[Console]::Clear()` works natively |
| `$Host.UI.RawUI.WindowSize` | In-process, reflects actual terminal |
| COLUMNS/LINES/TERM | Read directly from `Console.WindowWidth` etc., always accurate |
| `-c` tight loop latency | Host stays warm; second+ calls skip module load (~700ms saved) |
| `exit N` in scripts | Caught at `PowerShell.Invoke()` boundary as `ExitNeedException` |
| `source ./lib.sh` | Second `ps.AddScript()` on same runspace, state persists |
| `<()` streaming | `AnonymousPipeServerStream` + background runspace, genuinely concurrent |

### `<()` with in-process SDK

```csharp
var (serverStream, clientStream) = AnonymousPipeServerStream.CreatePair();
var bgPs = PowerShell.Create();
bgPs.Runspace = CreateChildRunspace(_sharedRunspace);
bgPs.AddScript(transpiledSubCmd);
Task.Run(() => { bgPs.Invoke(); serverStream.Dispose(); });
// Return clientStream read-end path to the consuming command
// On Windows: \\.\pipe\{clientStream.GetClientHandleAsString()}
// On Linux: /proc/self/fd/{handle}
```

### Build / publish changes

- `PsBash.Shell.csproj`: remove `<PublishAot>true</PublishAot>`, keep self-contained
- New `PsBash.Host.csproj`: non-AOT, self-contained, references `Microsoft.PowerShell.SDK`
- CI matrix: build both binaries per platform, zip together, upload to release
- `PsBash.Core.csproj`: unchanged (no AOT, no SDK dependency)

### Open questions for planning

1. **Host lifecycle**: explicit `ps-bash --stop-host` command, or idle timeout (e.g.
   10 min), or tied to login session via Job Object?
2. **SDK version pin**: bundle a specific PowerShell SDK version, or use whatever pwsh
   is installed? Bundling = predictable; installed = user's modules/profiles work.
3. **Fallback**: if host binary is missing (e.g. stripped install), fall back to current
   subprocess model or hard error?
4. **PsBash.Cmdlets**: already uses in-process PS via cmdlets — should it share the
   host's runspace or stay independent?
5. **Windows Terminal vs ConPTY**: for interactive mode the host needs to handle
   `Console.CancelKeyPress` and VT processing; current `EnsureVirtualTerminalEnabled`
   logic moves to the host.

---

## 4. Migration Strategy

The existing `PwshWorker` interface (`ExecuteAsync`, `QueryAsync`, `StartAsync`,
`DisposeAsync`) is a clean boundary. The recommended migration path:

1. **Extract `IWorker` interface** from `PwshWorker` — same methods, same signatures.
   `InteractiveShell` and `Program.cs` already use `PwshWorker` through a narrow surface.

2. **Implement `SdkWorker : IWorker`** — in-process SDK execution, same protocol
   semantics, no subprocess.

3. **Implement `NamedPipeWorker : IWorker`** — thin client that connects to the host
   daemon over the named pipe and proxies calls. This is what the AOT launcher uses.

4. **Create `PsBash.Host` binary** — runs `SdkWorker` behind a named-pipe server,
   handles host lifecycle, owns the terminal for interactive mode.

5. **Swap at the `Program.cs` entry point** — launcher detects whether it's running as
   the host or the thin launcher, instantiates the right `IWorker` implementation.

This keeps `PsBash.Core` (transpiler) and `PsBash.Module` (runtime) completely
unchanged. The test suite (`PsBash.Shell.Tests`, `PsBash.Differential.Tests`) can test
against either worker implementation via the interface.

---

## 5. Recent Changes (This Session)

- `InteractiveShell.cs`: Added `cls`/`clear`/`reset` intercept in `TryRunDirect` so
  `Console.Clear()` is called directly in the shell process rather than going to the
  worker. This is a minimal fix; the broader console I/O problem is unresolved.

---

## 6. Useful Entry Points for Planning

- Worker protocol loop: `PwshWorker.BuildInitScript()` (~line 176) and
  `PwshWorker.ExecuteAsync()` (~line 310)
- Script execution: `Program.cs` lines 55–101
- Process substitution: `PsBash.psm1` `Invoke-ProcessSub` function
- Interactive REPL loop: `InteractiveShell.RunAsync()` lines 71–175
- `TryRunDirect` (the direct-execution fast path): `InteractiveShell.cs` ~line 195
- Emitter passthrough principle: `docs/specs/emitter-strategy.md`
