# PTY Architecture Specification

> **Naming note (PTY-12):** this file *is* the PTY architecture document.
> `docs/specs/pty-architecture.md` is **not** a separate file — PTY-1..11 all
> landed and each extended this spec in place (§7 PTY-3, §8 PTY-5, §9 PTY-7).
> Rather than fork a near-duplicate, PTY-12 consolidates the as-built system
> into the unified **§10 Architecture Overview** below. Sections 1–9 remain the
> per-component reference; §10 is the cross-cutting picture (data flow, state
> machines, IPC protocol, lifecycle, signals, min-OS matrix, fallbacks,
> decision record). Any reference to "pty-architecture.md" means this file.

The launcher allocates a pseudo-terminal before spawning the host in interactive
mode. This document specifies the cross-platform PTY contract owned by
`src/PsBash.Shell/Pty/IPty.cs` and its two adapters.

Source files:

- `src/PsBash.Shell/Pty/IPty.cs` — interface + `PtyAllocator` factory
- `src/PsBash.Shell/Pty/ConPtyAdapter.cs` — Windows (ConPTY)
- `src/PsBash.Shell/Pty/UnixPtyAdapter.cs` — POSIX (`posix_openpt`)
- `src/PsBash.Shell.Tests/Pty/PtyAllocationTests.cs` — acceptance tests

This task (PTY-1) owns **allocation, resize, dispose, and round-trip
verification only**. Spawning a child attached to the PTY belongs to PTY-2;
tty mode switching belongs to PTY-3; IPC decoupling belongs to PTY-4.

---

## 1. Surface

```csharp
internal interface IPty : IAsyncDisposable
{
    Stream Input  { get; }      // launcher writes -> child stdin
    Stream Output { get; }      // launcher reads  <- child stdout/stderr
    IntPtr SlaveHandle          { get; }   // Windows HPCON; Zero on POSIX
    int    SlaveFileDescriptor  { get; }   // POSIX slave fd; -1 on Windows
    void   Resize(short cols, short rows);
}

internal static class PtyAllocator
{
    public static ValueTask<IPty> AllocateAsync(short cols, short rows);
}
```

Adapter selection happens inside `PtyAllocator.AllocateAsync` via
`RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`. Linux, macOS, and
FreeBSD route to `UnixPtyAdapter`; unrecognized platforms throw
`PlatformNotSupportedException`.

The interface is `internal`; consumers live inside `PsBash.Shell`.
`InternalsVisibleTo("PsBash.Shell.Tests")` is already declared in the Shell
csproj.

---

## 2. Platform Support

| Platform | Adapter            | Native API                                      | Minimum |
|----------|--------------------|-------------------------------------------------|---------|
| Windows  | `ConPtyAdapter`    | `CreatePseudoConsole` / `ResizePseudoConsole` / `ClosePseudoConsole` (kernel32) | **10.0.17763 (1809)** |
| Linux    | `UnixPtyAdapter`   | `posix_openpt` + `grantpt` + `unlockpt` + `ptsname_r` + `open` (libc) | any glibc / musl shipping the unix98 PTY API |
| macOS    | `UnixPtyAdapter`   | same                                            | 10.13+ (any supported macOS) |
| FreeBSD  | `UnixPtyAdapter`   | same                                            | any |

### 2.1 Windows minimum build (17763 / 1809)

`CreatePseudoConsole` first shipped in Windows 10 1809 (build 17763). Earlier
insider builds exposed an API surface that drifted before stabilizing. The
adapter checks `Environment.OSVersion.Version.Build` at the top of
`AllocateAsync` and throws `PlatformNotSupportedException` on lower builds
rather than P/Invoking into a partially-implemented function.

This minimum is also recorded as an XML doc on `IPty` and on
`ConPtyAdapter.AllocateAsync`. Bump it only if Microsoft changes the
documented stable subset.

### 2.2 POSIX `posix_openpt` over `forkpty`

We chose the explicit `posix_openpt` → `grantpt` → `unlockpt` → `ptsname_r` →
`open` sequence rather than `forkpty` for two reasons:

1. The launcher does not fork before invoking the host. The child is spawned
   by PTY-2 using `posix_spawn` or `fork+exec`. `forkpty` would force the
   launcher into the fork+exec model.
2. Ownership is clearer: this code owns the master and the slave fd until
   the caller spawns a child; the spawn step dups the slave fd onto the
   child's stdio and closes it in the launcher.

The slave name is read with `ptsname_r` (thread-safe) into a 256-byte buffer;
real implementations use paths well under 64 bytes (`/dev/pts/<n>`).

---

## 3. Dispose Contract

**Closing the master before the slave can SIGHUP a child whose controlling
terminal is the slave.** Both adapters guarantee that on `DisposeAsync`:

- **POSIX:** the slave fd is closed first (via `close(2)`), then the master
  fd. The master `FileStream` wraps the master fd with `ownsHandle: false`,
  so the manual `close` after `_masterStream.Dispose()` is the authoritative
  release of the master.
- **Windows:** `ClosePseudoConsole` is invoked first. ConPTY then flushes its
  internal buffers, signals the attached child (if any), and closes its
  client-side pipe ends. Only after that do we dispose our outward-facing
  `AnonymousPipeServerStream` handles. Closing the pipe handles first can
  deadlock the console thread.

Double-`DisposeAsync` is safe and is covered by a regression test.

After dispose:
- `Input` and `Output` are closed and any further I/O raises
  `ObjectDisposedException`.
- `Resize` becomes a no-op (does not throw).
- `SlaveHandle` and `SlaveFileDescriptor` retain their last values but are
  no longer valid kernel objects; callers must not use them.

---

## 4. Round-Trip Verification

### 4.1 POSIX

`Posix_Allocate_Then_RoundTrip_Through_Master_Slave` proves the master and
slave are connected as a single PTY pair:

1. Allocate a PTY at 80×24.
2. Open the raw slave fd in write mode (via `SafeFileHandle` +
   `FileStream`).
3. Write `"ping\n"` to the slave.
4. Read up to 256 bytes from the master `Output` stream with a 2 s
   cancellation token.
5. Assert the observed bytes contain `"ping"`. (Canonical-mode echo means
   the master surfaces the input shortly after the kernel routes it
   through the line discipline; we don't depend on termios state.)

### 4.2 Windows

A ConPTY cannot perform a master/slave round-trip without a child process
attached via `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` — that
spawn step belongs to PTY-2. For PTY-1, `Windows_Allocate_Returns_Valid_HPCON_And_Pipes`
verifies the next-best invariants:

- `SlaveHandle` (HPCON) is non-zero after allocation.
- `SlaveFileDescriptor` is `-1`.
- `Input.CanWrite` and `Output.CanRead` are true.
- `Resize` succeeds against the live HPCON.
- Dispose completes without throwing.

The full ConPTY end-to-end round-trip lands in PTY-2's test suite once the
spawn path exists.

---

## 5. Test Invocation

### Linux side (this WSL2 host)

```bash
./scripts/test.sh --filter "FullyQualifiedName~PtyAllocationTests"
```

Posix_* tests and platform-neutral tests run; Windows_* skip with reason.

### Windows side (CI)

`actions/setup-dotnet@v4` provisions the SDK on `windows-latest` and the
existing `dotnet test` invocations in `.github/workflows/build.yml` and
`.github/workflows/publish.yml` pick up the new tests automatically.
Posix_* tests skip there; Windows_* execute.

### Windows side (local, manual)

Requires a Windows-side .NET SDK (not just the runtime). If installed:

```powershell
powershell.exe -NoProfile -Command "& 'C:\Program Files\dotnet\dotnet.exe' test '\\wsl$\Ubuntu\<path>\src\PsBash.Shell.Tests\PsBash.Shell.Tests.csproj' --filter 'FullyQualifiedName~PtyAllocationTests'"
```

This developer host currently has only the .NET runtime on the Windows side,
so the local Windows leg relies on CI for execution. Manual invocation steps
above remain valid once the SDK is provisioned.

---

## 6. Out of Scope (Owned by Later Tasks)

| Task   | Owns                                                                |
|--------|---------------------------------------------------------------------|
| PTY-2  | Spawning the host child with the slave attached (`STARTUPINFOEX` on Windows; `fork+exec` + `dup2` + `ioctl(TIOCSCTTY)` on POSIX) — **done** |
| PTY-3  | tty mode switching (raw vs cooked) on the launcher-side stdio — **done; see §7** |
| PTY-4  | Decoupling the IPC channel from the PTY data path                   |

No emitter changes; this work is launcher-only.

---

## 7. PTY-3 — Launcher TTY Mode Switching

The launcher (ps-bash) is the user's terminal. When PSBASH_PTY=1 the
launcher spawns the host under a real pseudo-terminal (PTY-2) and then
pumps bytes between its own stdio and the PTY master. For TUI apps
(vim, less, fzf) to see keystrokes as they arrive, the launcher's own
stdin must be in **raw mode** — otherwise the kernel line-buffers each
byte until Enter and the host never sees individual keys.

Source files:

- `src/PsBash.Shell/Pty/TerminalMode.cs` — save / set raw / restore scope
- `src/PsBash.Shell/Program.cs::RunHostUnderPtyAsync` — wiring + fallback
- `src/PsBash.Shell.Tests/Pty/TerminalModeTests.cs` — acceptance tests

### 7.1 Surface

```csharp
internal static partial class TerminalMode
{
    // Enter raw mode against the launcher's stdin (and stdout on Windows).
    // Returns an IDisposable scope; Dispose restores the saved state.
    // If stdin is not a tty, returns an inactive scope (no-op dispose).
    public static Scope EnterRawIfTty();

    // POSIX-only entry used by tests; targets a specific fd.
    [UnsupportedOSPlatform("windows")]
    public static Scope EnterRawForFd(int fd);

    // Windows: pure functions used by tests to assert mode-bit math.
    public static uint ComputeRawInputMode(uint current);
    public static uint ComputeRawOutputMode(uint current);
}
```

### 7.2 POSIX termios path

`EnterRawForFd(fd)`:

1. `tcgetattr(fd, &saved)` — snapshot current termios. Non-zero return
   ⇒ fd is not a tty (e.g. xunit testhost, `< /dev/null`); return
   `Scope.Inactive`.
2. Copy `saved` → `working`. `cfmakeraw(&working)` — flips off ICANON,
   ECHO, ISIG, IEXTEN, plus the input-flag mask that strips CRs / 8th
   bit / XON-XOFF. `tcsetattr(fd, TCSANOW, &working)`.
3. Return `Scope.Active(new PosixRestorer(fd, saved))` whose
   `Restore()` calls `tcsetattr(fd, TCSANOW, &saved)`.

The opaque `struct termios` is treated as a fixed-size byte buffer
(256 bytes — comfortably larger than glibc's 60, musl's 60, macOS's
44). The libc functions only ever read/write the prefix they
understand.

### 7.3 Windows console-mode path

`EnterRawWindows()`:

1. `GetStdHandle(STD_INPUT_HANDLE)` / `STD_OUTPUT_HANDLE`,
   `GetConsoleMode(hIn, out savedIn)`. False ⇒ stdin is not a console
   (redirected pipe); return `Scope.Inactive`.
2. `ComputeRawInputMode(savedIn)`:
   - clears `ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT`
   - sets `ENABLE_VIRTUAL_TERMINAL_INPUT`
   - preserves window / mouse / unrelated bits
3. `ComputeRawOutputMode(savedOut)`:
   - sets `ENABLE_VIRTUAL_TERMINAL_PROCESSING`
   - sets `DISABLE_NEWLINE_AUTO_RETURN` (LF stays LF; no auto-CR
     injection that flickers TUI cursor moves)
4. `SetConsoleMode(hIn, rawIn)` / `SetConsoleMode(hOut, rawOut)`.
5. `Restore()` calls `SetConsoleMode` with the captured saved values.

### 7.4 Crash-safety: ProcessExit guard

A stuck terminal is the canonical "ps-bash ate my shell" failure.
`TerminalMode.Scope` registers itself with an `AppDomain.ProcessExit`
+ `UnhandledException` guard that disposes any live scope on shutdown.
A normal `Dispose()` unregisters first, so the guard only fires for
abnormal exits. `Interlocked.Exchange` on the restorer field
serializes the dispose path so the guard and a normal call cannot
race.

### 7.5 Pre-Win10-1809 fallback

`ConPtyAdapter.AllocateAsync` already throws
`PlatformNotSupportedException` when `Environment.OSVersion.Version.Build
< 17763`. `Program.RunHostUnderPtyAsync` does **not** catch it itself —
the outer call site (the interactive branch in `Program.cs`) catches
`PlatformNotSupportedException`, writes a warning to stderr, and
falls through to the legacy inherited-stdio path:

```text
ps-bash: PSBASH_PTY=1 requested but the platform does not support a
pseudo-terminal (...). Falling back to inherited-stdio mode (TUI apps
will be line-buffered).
```

This means a misconfigured Windows-7 / Server-2012 user still gets a
working shell, just without TUI passthrough.

### 7.6 Pump latency

The pumps use `Stream.CopyToAsync` (default 4 KiB buffer) — direct
byte copy with no translation, no encoding step, no record assembly.
This is load-bearing for TUI apps: any per-byte allocation or
translation introduces flicker on cursor moves.

### 7.7 What PTY-3 does *not* do

- **SIGWINCH forwarding**: launcher does not propagate window-resize
  events to the host PTY. **Done in PTY-5** (§8) — `SignalForwarder`
  installs a `SIGWINCH` handler that re-pushes the launcher tty's
  winsize onto the PTY master.
- **Ctrl-C signal injection**: PTY-3 relied on the host's own
  `Console.CancelKeyPress` handler. **PTY-5 (§8) adds explicit
  launcher-side forwarding** for the raw passthrough case where the
  launcher — not the host — is the process attached to the user's real
  tty.
- **Mode state machine in the host's `InteractiveShell`**: the line
  editor lives in the host, not the launcher. The host already toggles
  console-mode in its own startup path; PTY-3's launcher-side raw mode
  is orthogonal to that.

### 7.8 Test invocation

```bash
./scripts/test.sh --filter "FullyQualifiedName~TerminalModeTests"
```

POSIX tests run; Windows tests skip with reason on Linux. The
Windows leg runs on the CI matrix via `actions/setup-dotnet@v4` on
`windows-latest`.

---

## 8. PTY-5 — Launcher↔Host Signal Forwarding

Owned by `src/PsBash.Shell/Pty/SignalForwarder.cs`; wired into
`RunHostUnderPtyAsync` in `src/PsBash.Shell/Program.cs` alongside the
PTY-3 raw-mode scope. Tests: `src/PsBash.Shell.Tests/Pty/SignalForwarderTests.cs`.

### 8.1 Why the launcher must forward

In raw passthrough mode the **launcher** is the process attached to the
user's real tty. The kernel delivers terminal signals — `SIGINT`
(Ctrl-C), `SIGTSTP` (Ctrl-Z), `SIGWINCH` (resize) — to whoever owns the
controlling terminal, i.e. the launcher. The host runs under a *separate*
PTY (PTY-2) and never sees the user's keystrokes as signals. Without
explicit forwarding, Ctrl-C is dead inside ps-bash and a window resize is
silent (vim/htop never repaint).

### 8.2 POSIX

`SignalForwarder.Install` installs three handlers for the lifetime of the
raw-mode host child:

| Signal | Managed API | Action |
|--------|-------------|--------|
| `SIGINT` | `PosixSignalRegistration` (covers `SIGINT`); `ctx.Cancel = true` | `killpg(hostPgid, SIGINT)` — re-deliver to the host's foreground process group. Launcher is *not* terminated. |
| `SIGTSTP` | raw `signal()` P/Invoke (`PosixSignal` does not enumerate `SIGTSTP`) | `killpg(hostPgid, SIGTSTP)`. Launcher is *not* stopped — a stopped launcher freezes byte pumping forever. |
| `SIGWINCH` | raw `signal()` P/Invoke | `ioctl(TIOCGWINSZ)` on the launcher tty → `IPty.Resize` (`ioctl(TIOCSWINSZ)`) on the PTY master. The kernel then sends `SIGWINCH` to the host through the slave. |

`hostPgid` = the host pid. PTY-2 spawns the host with
`POSIX_SPAWN_SETSID`, so it is a session/process-group leader and its
pgid equals its pid; `killpg(hostPid, …)` therefore targets the whole
foreground job.

**Orphaned-process-group caveat.** Because the host is its own session
leader, its process group is *orphaned* by POSIX definition (no member
has a parent in a different group of the same session). POSIX §2.4.3
specifies that a stop signal (`SIGTSTP`/`SIGTTIN`/`SIGTTOU`) whose action
is the **default** is *discarded* for an orphaned group. So Ctrl-Z only
actually suspends the host if the host installs its own `SIGTSTP`
handler (which makes the action non-default, so it is delivered rather
than discarded). The launcher's job is strictly to **deliver** the
signal; what the host does with it is the host's concern. The
`Posix_RaisingSigtstp_*` test reflects this — it asserts *delivery* (via
a `trap`-handling child), not the stopped (`T`) state.

### 8.3 Windows — Ctrl-C vs Ctrl-Break matrix

ConPTY changes the model: it **auto-translates** a console Ctrl-C into a
`CTRL_C_EVENT` delivered to the attached host process, which surfaces
there as `Console.CancelKeyPress`. The launcher does **not** re-inject
anything. The launcher's only job is to install a `SetConsoleCtrlHandler`
that **suppresses its own default terminate-on-Ctrl-C** so the launcher
process survives to keep pumping bytes. For resize, Windows has no
`SIGWINCH`; a 150 ms background poll watches `Console.WindowWidth/Height`
and calls `IPty.Resize` (ConPTY `ResizePseudoConsole`).

| Console event | Crosses ConPTY boundary to host? | Launcher handler returns | Effect |
|---------------|----------------------------------|--------------------------|--------|
| `CTRL_C_EVENT` | Yes — ConPTY delivers it to the host as `CTRL_C_EVENT` → host `Console.CancelKeyPress`. | `TRUE` (handled — suppress launcher's own terminate). | Host cancels the running pipeline; launcher survives and keeps pumping. |
| `CTRL_BREAK_EVENT` | Yes — and on some console hosts Ctrl-Break is the *only* control event that crosses cleanly, so it is treated as a first-class cancel path. | `TRUE` (handled — suppress launcher terminate). | Same as Ctrl-C: host sees the event, launcher survives. |
| `CTRL_CLOSE_EVENT` | N/A — the console window is closing. | `FALSE` (unhandled — let the system tear the launcher down). | Launcher and host both exit; no suppression. |
| `CTRL_LOGOFF_EVENT` / `CTRL_SHUTDOWN_EVENT` | N/A — session ending. | `FALSE` (unhandled). | Normal system-driven teardown. |

**Recommendation:** prefer `CTRL_C_EVENT` for interactive cancel; rely on
`CTRL_BREAK_EVENT` as the fallback on console hosts where Ctrl-C does not
propagate. The launcher suppresses its default terminate for *both* so
neither kills the launcher mid-pump.

### 8.4 `signal-delivered` IPC token

`HostProtocol.SignalDeliveredPrefix` (`<<<SIGNAL-DELIVERED:NAME>>>`) is
the canonical wire token for a launcher that runs a framed interactive
channel and wants to tell a prompt renderer "the host was just
interrupted, reset prompt state". In the PTY raw byte-pump path
(`RunHostUnderPtyAsync`) the launcher and host share no framed IPC
channel, so the launcher instead tracks delivered signals directly via
`SignalForwarder.SigintDeliveredCount` / `SigtstpDeliveredCount` /
`WinchForwardedCount`. The constant is defined now so a future framed
interactive launcher extends the existing protocol coherently rather
than inventing a parallel channel.

### 8.5 Lifecycle & AOT

`SignalForwarder` is a save-set-restore `IDisposable`, installed inside
the same `using` region as the PTY-3 raw-mode scope and disposed in the
outer `finally` so a crashed pump still removes the handlers and the
user's parent shell regains normal Ctrl-C behaviour. A non-tty launcher
stdin yields an inactive scope (`Install` returns `Inactive`) that
installs nothing. The launcher publishes `PublishAot=true`: `SIGINT` uses
the managed `PosixSignalRegistration`, the raw `signal()` /
`SetConsoleCtrlHandler` P/Invokes use statically-known delegate types
(no runtime codegen), so the whole path is AOT-safe.

### 8.6 Test invocation

```bash
./scripts/test.sh --filter "FullyQualifiedName~SignalForwarderTests"
```

POSIX tests run on Linux/WSL2 — they spawn a real session-leader child,
install a forwarder targeting that pgid, raise the signal in-process, and
assert the child group received it (the `SIGINT` handler sets
`ctx.Cancel=true` so the test host is not killed). The Windows Ctrl-C /
`ResizePseudoConsole` runtime verification is CI-gated; the Windows-tagged
test asserts the headless install/dispose contract.

## 9. PTY-7 — Crash Recovery & Terminal-Mode Restoration

If the launcher goes down while it is in raw passthrough mode — host
crash, `kill -9` of the host, SIGHUP, Ctrl-C, an unhandled exception —
the user's terminal must not be left with no echo, raw input, and a
TUI-corrupted screen. The launcher must restore the terminal on *every*
exit path.

### 9.1 What PTY-3/5/6 already covered

- **termios / console-mode save+raw+restore** — `TerminalMode` (PTY-3).
- **`AppDomain.ProcessExit` + `UnhandledException` restore** — PTY-3's
  `ProcessExitGuard` already hooked both.
- **Idempotent restore** — each `TerminalMode.Scope` uses an
  `Interlocked.Exchange` guard, so a normal `using` dispose and a crash
  hook can both run safely; whichever runs first wins.
- **SIGINT / SIGTSTP / SIGWINCH forwarding** — `SignalForwarder` (PTY-5),
  itself an idempotent save-set-restore scope.
- **`host-exiting` → `RestoreTerminal` action** — `HandoffStateMachine`
  (PTY-6) maps the IPC sentinel to a terminal-mode action (the framed
  interactive channel that *delivers* the sentinel is still deferred).

### 9.2 What PTY-7 added

- **SIGHUP restore (POSIX)** — `ProcessExitGuard` now registers a
  `PosixSignalRegistration.Create(PosixSignal.SIGHUP, …)` handler. On
  SIGHUP it restores the terminal, then leaves `ctx.Cancel` false so the
  default disposition terminates the launcher. AOT-safe (PTY-5 already
  relies on `PosixSignalRegistration` for SIGINT).
- **`Console.CancelKeyPress` restore** — Ctrl-C / Ctrl-Break delivered to
  the launcher itself now routes through the guard. The handler does
  **not** set `Cancel=true` — the launcher still terminates, it just
  hands back a sane tty first.
- **Host-crash / socket-EOF detection** — `RunHostUnderPtyAsync` calls
  `TerminalMode.EmergencyRestoreAll()` when the host exits with a
  non-zero exit code (crash, or a signal death surfaced as 128+N). A
  clean exit (`exitCode == 0`) takes the normal `using` dispose path.
- **Emergency-restore escape sequence** — on every *abnormal* path the
  restore also writes `TerminalMode.EmergencyResetSequence` (`ESC c`,
  the VT100 RIS full reset — the byte-level `tput reset`) to stdout
  *after* the termios restore. A `kill -9`'d TUI never ran its own
  teardown, so the alternate screen buffer / hidden cursor / scroll
  region survive a termios restore; RIS clears them so the parent shell
  redraws clean. It is **emergency-path only** — a clean transition back
  to cooked mode must not emit it, or every command return would flicker.

### 9.3 Restore-ordering contract

**Terminal-mode restore MUST run before the launcher process exits, on
every path:**

| Path | Hook | Emits reset escape? |
|------|------|---------------------|
| Clean host exit (`exitCode == 0`) | `using modeScope` dispose | No |
| Host crash / non-zero exit | `EmergencyRestoreAll()` then `using` dispose (no-op) | Yes |
| `AppDomain.ProcessExit` | `ProcessExitGuard` | Yes |
| `AppDomain.UnhandledException` | `ProcessExitGuard` | Yes |
| `Console.CancelKeyPress` (Ctrl-C/Break) | `ProcessExitGuard` | Yes |
| SIGHUP (POSIX) | `ProcessExitGuard` via `PosixSignalRegistration` | Yes |
| SIGINT / SIGTSTP | `SignalForwarder` forwards to host; launcher keeps running | n/a |

Within an emergency restore the order is **termios/console-mode restore
first, reset escape sequence second** — the escape bytes must go out on a
stdout that is already back in a sane mode.

`Environment.FailFast` is the one path that bypasses `ProcessExit` and
all managed hooks. **The launcher must not call `FailFast` while a
raw-mode scope is live** — if a future change needs a fail-fast path it
must call `TerminalMode.EmergencyRestoreAll()` immediately before
`FailFast`. (This is the `process_spawn_contract` rule the PTY-7 task
asked to record; there is no `MEMORY.md` / `docs/solutions/` convention
in this repo yet, so it is documented here in the PTY spec instead.)

### 9.4 Manual vs automated test split

- **Manual** (not CI-automatable): `kill -9` the host process while
  `vim` is running under an interactive `ps-bash`, then confirm the
  launcher's tty has echo + line discipline back and the screen is
  redrawn clean.
- **Automated proxy**: `TerminalModeTests` asserts (a) the emergency
  reset sequence is exactly `ESC c`, (b) `EmergencyRestoreAll` with no
  active scope is a safe no-op, (c) on a real PTY slave fd
  `EmergencyRestoreAll` restores ICANON/ECHO and a following `Dispose`
  is an idempotent no-op, and (d) a source-text regression that
  `RunHostUnderPtyAsync` calls `EmergencyRestoreAll` gated on a non-zero
  exit code.

### 9.5 Test invocation

```bash
./scripts/test.sh --filter "FullyQualifiedName~TerminalModeTests"
```

---

## 10. Architecture Overview (PTY-12)

This section is the cross-cutting, as-built picture of the PTY subsystem after
PTY-1..11 landed. It does not re-derive the per-component detail in §§1–9; it
ties them together: the launcher↔host↔PTY data flow, the mode state machine,
the IPC event protocol, the daemon-vs-interactive lifecycle, the signal
forwarding table, the minimum-OS matrix, and the fallbacks for when a PTY is
unavailable or unwanted.

### 10.1 Why a launcher/host split exists at all

ps-bash ships as **two** processes: a small AOT-compiled launcher (`ps-bash`)
and a PowerShell-SDK host (`ps-bash-host`).

- **The launcher is Native-AOT.** It must start fast and have no managed-JIT
  warmup. The VT100 line editor, FlagSpecs, and IPC client are all written to
  be AOT-safe (no `DynamicMethod`, no reflection-emit) — see
  [`design-decisions.md`](./design-decisions.md) §"AOT-compatible" and
  §"FlagSpecs in C#".
- **The host hosts the PowerShell SDK.** `Microsoft.PowerShell.SDK` pulls in
  assemblies that cannot be AOT-published on .NET 10 (the PTY-0 spike confirmed
  this — recorded in `.dartai/loop-state.json` for task `4V0QRPY3bk4N`; the
  probe lives on the unmerged `pty-aot-probe` branch). So the SDK runs in a
  separate JIT process and the launcher talks to it over IPC.

The split is therefore **not optional** — it is forced by the AOT-vs-SDK
constraint. PTY-12's decision record (§10.8) captures *why we kept it* even
after REFACTOR-7, so future maintainers do not relitigate it.

### 10.2 Data flow: launcher ↔ host ↔ PTY

Two wiring topologies exist. Which one is used depends on mode (see §10.5).

**(a) PTY raw-passthrough topology** — interactive, `PSBASH_PTY=1`, launcher
stdin is a real tty:

```
        user's real terminal
                │  (raw mode — TerminalMode.EnterRawIfTty, §7)
                ▼
   ┌─────────────────────────┐        allocates          ┌──────────────┐
   │   ps-bash (launcher)    │ ───────────────────────▶   │  PTY pair    │
   │   AOT, owns the tty     │   PtyAllocator (§1,§2)     │ master/slave │
   │                         │                            └──────┬───────┘
   │  byte pump (CopyToAsync)│◀── pty.Output ── master           │ slave
   │  byte pump (CopyToAsync)│ ── pty.Input ──▶ master           │ attached as
   │                         │                                   │ stdin/out/err
   │  SignalForwarder (§8) ──┼── SIGINT/TSTP/WINCH ──▶ host pgid  ▼
   └─────────────────────────┘                          ┌──────────────┐
                                                         │ ps-bash-host │
                                                         │ PowerShell   │
                                                         │ SDK, JIT     │
                                                         └──────────────┘
```

The launcher is the process attached to the user's tty. It allocates the PTY,
spawns the host with the **slave** as stdio (`PtySpawner`, §6/PTY-2), puts its
own stdin in raw mode, and runs two `Stream.CopyToAsync` byte pumps between its
stdio and the PTY **master**. No translation, no record assembly — see §7.6.

**(b) Framed-IPC topology** — non-interactive (`-c`, stdin pipe, script file),
*and* the non-tty interactive fallback:

```
   ┌─────────────────────────┐                       ┌──────────────┐
   │   ps-bash (launcher)    │ ── framed request ──▶ │ ps-bash-host │
   │   AOT                   │   (HostProtocol, §10.4)│ PowerShell   │
   │   IpcWorker             │ ◀─ framed response ──  │ SDK          │
   └─────────────────────────┘   Unix socket / pipe  └──────────────┘
```

No PTY is allocated. The launcher and host exchange length-framed messages over
a Unix domain socket (POSIX) or named pipe (Windows) — `IpcWorker` +
`HostProtocol` in `src/PsBash.Core/Runtime/`. This is the legacy path and the
universal fallback.

### 10.3 Mode state machine

Two distinct state machines operate at different layers; do not conflate them.

**(a) Launcher-side raw/cooked terminal mode** — `TerminalMode` (§7), driven by
the `HandoffStateMachine` (`src/PsBash.Shell/Pty/HandoffStateMachine.cs`,
PTY-6). The machine consumes IPC sentinels from the host and emits a terminal
action:

```
        ┌───────────────┐   <<<PROMPT-READY>>>   ┌──────────────┐
        │  HostRunning  │ ─────────────────────▶ │ PromptReady  │
        │ (host owns tty│   action: LeaveRawMode │ (launcher    │
        │  — raw mode)  │ ◀───────────────────── │  owns prompt)│
        └───────┬───────┘     <<<BUSY>>>         └──────┬───────┘
                │             action: EnterRawMode      │
                │                                       │
                │  <<<HOST-EXITING>>>   ┌─────────────┐ │ <<<HOST-EXITING>>>
                └─────────────────────▶ │ HostExiting │◀┘
                  action: RestoreTerminal│ (terminal)  │
                                         └─────────────┘
```

`HostExiting` is terminal: once reached, further sentinels are ignored. Note
that the **framed interactive channel** that would *deliver* these sentinels in
the PTY raw-pump path is still deferred (see §8.4) — in raw-passthrough today the
launcher tracks signal delivery directly via `SignalForwarder` counters. The
state machine is implemented and tested so a future framed interactive launcher
plugs in coherently.

**(b) PTY raw vs cooked termios/console-mode** — `TerminalMode.EnterRawIfTty()`
(§7.2 POSIX termios, §7.3 Windows console mode). This is a save→set-raw→restore
scope, not a multi-state machine: `Active` (raw, restorable) or `Inactive`
(stdin was not a tty — no-op). Crash-safety and restore-ordering across every
exit path is the §9 contract (PTY-7).

### 10.4 IPC event protocol

The launcher↔host wire protocol is defined in
`src/PsBash.Core/Runtime/Ipc/HostProtocol.cs`. It is line-oriented UTF-8 (no
BOM). A request frame carries headers then a body then `<<<END>>>`:

| Wire token | Direction | Meaning |
|------------|-----------|---------|
| `MODE:Command` / `Stdin` / `Script` / `Interactive` | launcher → host | request kind (`ModeHeaderPrefix`) |
| `SESSION:Framed` / `SESSION:Interactive` | launcher → host | PTY-4 session-mode header; **absent ⇒ `Framed`** (pre-PTY-4 wire-compat) |
| `PATH:` / `ARGV:` / `BODY:` / `DEADLINE:` | launcher → host | request headers |
| `<<<END>>>` | both | end-of-frame sentinel |
| `<<<EXIT:n>>>` | host → launcher | command exit code (`ExitPrefix`) |
| `STDERR:` | host → launcher | stderr line prefix |
| `<<<PROMPT-READY>>>` | host → launcher | PTY-4: host is at a prompt, launcher may take the tty. **Interactive mode only.** |
| `<<<BUSY>>>` | host → launcher | PTY-6: host started running a command, launcher should re-enter raw mode. **Interactive mode only.** |
| `<<<HOST-EXITING>>>` | host → launcher | PTY-6: host is about to exit; launcher restores the terminal. Emitted **before** the host closes its stream. **Interactive mode only.** |
| `<<<SIGNAL-DELIVERED:NAME>>>` | host → launcher | PTY-5: host acknowledges a forwarded signal (`SignalDeliveredPrefix` + `Suffix`). Canonical token for a future framed interactive channel; unused in today's raw-pump path. |

**Session-mode gating.** `PromptReady` / `Busy` / `HostExiting` /
`SignalDelivered` are emitted **only** under `SESSION:Interactive`. Under
`SESSION:Framed` (the default, and what every non-interactive launcher uses)
the host MUST NOT emit them — this keeps pre-PTY-4 launchers wire-compatible.
See `SessionMode` in `src/PsBash.Core/Runtime/Ipc/Mode.cs`.

### 10.5 Daemon-vs-interactive lifecycle

REFACTOR-7 (commit `701502e`) split host lifetime into two models, selected via
the `Lifetime` enum passed to `IpcWorker.StartAsync`
(`src/PsBash.Core/Runtime/IpcWorker.cs`):

| `Lifetime` | Used by | Host lifetime | Endpoint | Worker kills host on dispose? |
|------------|---------|---------------|----------|-------------------------------|
| `PerInvocation` (default) | non-interactive: `-c`, stdin pipe, script file | one private host per launcher invocation | process-local (`ResolvePerInvocationEndpoint`) | **Yes** — owns the `Process` handle, kills the tree |
| `Daemon` | **`ps-bash host restart` only** (`HostCommands.cs`) — the explicit daemon-management subcommand | long-lived shared host, reused across launchers | canonical per-user (`ResolveEndpoint`) | **No** — left running for the next launcher |

The rationale: a `PerInvocation` host never outlives its single client, so the
pipe-inheritance hazard is contained within the launcher's lifetime and every
`-c`/script run gets a clean PowerShell session by construction.

> **PTY-10 correction.** Earlier REFACTOR-7-era prose described `Daemon` as the
> lifetime "for the interactive REPL". That mapping was never wired: **no
> interactive launcher path selects `Lifetime.Daemon`.** The interactive REPL —
> both the PTY raw-passthrough path (`Program.RunHostUnderPtyAsync`, §10.2a) and
> the legacy inherited-stdio fallback (`Program.cs`) — spawns its host
> **directly** via `PtySpawner` / `Process.Start` with
> `--interactive --launcher-pid`, and never touches `IpcWorker` at all. The only
> caller of `Lifetime.Daemon` is `ps-bash host restart`. This matters because an
> interactive host is PTY-bound: if two launchers shared one interactive host,
> one terminal's keystrokes would land in the other. A fresh host per
> interactive session is therefore guaranteed *by construction* — the
> interactive path bypasses shared-socket discovery entirely; it is not a
> runtime check that could regress silently. Regression-pinned by
> `PtySpawnTests.TwoInteractiveSpawns_GetDistinctHostPids` and
> `PtySpawnTests.InteractiveLaunchPath_NeverSelectsDaemonLifetime`.

The host-reuse decision for the `Daemon` path (when is a running host
*reusable* vs *stale* vs *unsafe to touch*) is a separate design contract —
see [`host-lifecycle-contract.md`](./host-lifecycle-contract.md).

The PTY raw-passthrough path (§10.2a) is orthogonal to `Lifetime`: it spawns
the host directly via `PtySpawner` with the PTY slave as stdio, not through
`IpcWorker`. The `Lifetime` enum governs the **framed-IPC** topology, and within
that topology only the `host restart` subcommand uses `Daemon`.

### 10.6 Signal forwarding table

In raw-passthrough mode the launcher owns the user's tty and must forward
terminal signals to the host (which runs under a separate PTY and never sees the
keystrokes as signals). Full detail in §8; summary:

| Signal / event | POSIX action | Windows action |
|----------------|--------------|----------------|
| `SIGINT` (Ctrl-C) | `killpg(hostPgid, SIGINT)` via `PosixSignalRegistration`; launcher not terminated | ConPTY auto-delivers `CTRL_C_EVENT` to host; launcher installs `SetConsoleCtrlHandler` returning `TRUE` to suppress its own terminate |
| `SIGTSTP` (Ctrl-Z) | `killpg(hostPgid, SIGTSTP)` via raw `signal()`; launcher not stopped (a stopped launcher freezes the pumps) | n/a |
| Ctrl-Break | n/a | `CTRL_BREAK_EVENT` crosses ConPTY to host; launcher returns `TRUE` (first-class cancel fallback where Ctrl-C does not propagate) |
| `SIGWINCH` (resize) | `ioctl(TIOCGWINSZ)` on launcher tty → `IPty.Resize` (`TIOCSWINSZ`) on the master | no `SIGWINCH`; 150 ms poll of `Console.WindowWidth/Height` → `IPty.Resize` (ConPTY `ResizePseudoConsole`) |
| `SIGHUP` (POSIX) | `ProcessExitGuard` restores the terminal, then default disposition terminates the launcher (PTY-7, §9.2) | n/a |
| `CTRL_CLOSE` / `LOGOFF` / `SHUTDOWN` | n/a | handler returns `FALSE` — let the system tear both processes down |

Owned by `src/PsBash.Shell/Pty/SignalForwarder.cs`; a non-tty launcher stdin
yields an inactive forwarder that installs nothing.

### 10.7 Minimum-OS matrix and fallbacks

**Min-OS matrix** (full detail in §2):

| Platform | Adapter | Native API | Minimum |
|----------|---------|------------|---------|
| Windows | `ConPtyAdapter` | `CreatePseudoConsole` (kernel32) | **Windows 10 1809 / build 17763** |
| Linux | `UnixPtyAdapter` | `posix_openpt` + unix98 PTY API (libc) | any glibc/musl with the unix98 PTY API |
| macOS | `UnixPtyAdapter` | same | 10.13+ |
| FreeBSD | `UnixPtyAdapter` | same | any |

There are **three** distinct fallback paths. They are independent — each has its
own trigger and its own behavior:

**(a) Pre-Win10-1809 fallback** (ConPTY unavailable). `ConPtyAdapter.AllocateAsync`
checks `Environment.OSVersion.Version.Build` and throws
`PlatformNotSupportedException` on build < 17763, rather than P/Invoking into a
partially-implemented `CreatePseudoConsole`. `PtyAllocator` also throws it on
unrecognized platforms. `Program.cs` catches it around the
`RunHostUnderPtyAsync` call, writes a stderr warning, and falls through to the
legacy inherited-stdio path:

```text
ps-bash: PSBASH_PTY=1 requested but the platform does not support a
pseudo-terminal (...). Falling back to inherited-stdio mode (TUI apps
will be line-buffered).
```

This is **as-built** — see §7.5. The user still gets a working shell, just
without TUI passthrough; interactive TUI cmdlets degrade to line-buffered rather
than crashing.

**(b) Non-tty fallback** (launcher's own stdin is redirected — PTY-12). When the
launcher itself is started with redirected stdin — CI log capture, a GUI process
spawning `ps-bash` with a pipe, `ps-bash < /dev/null` — there are no keystrokes
to pump and no terminal signals to forward, so allocating a PTY is pointless and
can wedge a non-interactive parent expecting plain pipe semantics. The decision
is made **before** any allocation, by the pure function
`PtyLaunchPolicy.ShouldUsePty(ptyOptIn, launcherStdinRedirected)` in
`src/PsBash.Shell/Pty/PtyLaunchPolicy.cs`:

```
ShouldUsePty = ptyOptIn && !launcherStdinRedirected
```

`Program.cs` calls it with `Console.IsInputRedirected`. When it returns false,
the launcher skips `RunHostUnderPtyAsync` entirely and falls through to the
legacy inherited-stdio path — i.e. it behaves exactly like the current
pipe-based interactive harness. Unlike fallback (a), this path never attempts
allocation and never throws; it is a pre-flight decision. Tested by
`PtyLaunchPolicyTests` (`src/PsBash.Shell.Tests/Pty/PtyLaunchPolicyTests.cs`).

**(c) Opt-in gate** (`PSBASH_PTY` unset). The PTY path is still opt-in: with
`PSBASH_PTY` unset, `ptyOptIn` is false and `ShouldUsePty` returns false on
every platform. The legacy inherited-stdio interactive path remains the default.

Summary of which path runs for an interactive launch:

| `PSBASH_PTY` | launcher stdin | platform | path taken |
|--------------|----------------|----------|------------|
| unset | any | any | legacy inherited-stdio (opt-in gate) |
| `1` | redirected | any | legacy inherited-stdio (non-tty fallback, no allocation) |
| `1` | real tty | Win < 1809 / unknown | legacy inherited-stdio (caught `PlatformNotSupportedException` + warning) |
| `1` | real tty | Win ≥ 1809 / POSIX | PTY raw-passthrough (`RunHostUnderPtyAsync`) |

### 10.8 Decision record — why we kept the launcher/host split

**Context.** REFACTOR-7 reworked host lifetime (`PerInvocation` vs `Daemon`,
§10.5). During that work the question "could we collapse the launcher and host
into one process?" was on the table. The answer was no, and this record
captures why so it is not relitigated.

**Decision.** Keep the two-process launcher/host split. Keep `PerInvocation`
lifetime for non-interactive modes; keep `Daemon` lifetime available for the
`ps-bash host restart` subcommand. (See the PTY-10 correction in §10.5: the
interactive REPL does **not** use `Daemon` — it spawns its host directly and
gets a fresh host per session.)

**Forces.**

- *AOT vs PowerShell SDK (the hard constraint).* The launcher must be
  Native-AOT for startup latency and a dependency-free single binary. The
  PowerShell SDK cannot be AOT-published on .NET 10 — the PTY-0 spike confirmed
  this (loop task `4V0QRPY3bk4N`; probe on the unmerged `pty-aot-probe`
  branch). A single-process design would force the whole shell to be
  JIT/SDK-hosted, losing AOT startup — or force dropping the PowerShell SDK,
  losing the entire runtime. Neither is acceptable. The split is **structurally
  required**, not a stylistic choice. See
  [`design-decisions.md`](./design-decisions.md) §"AOT-compatible".
- *State isolation.* `PerInvocation` hosts give every `-c`/script run a clean
  PowerShell session by construction — no leaked variables, modules, or
  `$PWD` between invocations. Collapsing into one process would require
  re-implementing that isolation in-process.
- *Crash containment.* A host crash (SDK bug, runaway script) takes down the
  host process, not the launcher. The launcher restores the terminal (§9) and
  exits cleanly. One process means a runtime crash corrupts the user's tty
  directly.
- *Daemon reuse trade-off.* `Daemon` lifetime pays for cross-launcher
  state-isolation guardrails and the dup2-detach hang fix, in exchange for not
  paying SDK startup on every connect. It is used by `ps-bash host restart` to
  leave a long-lived host on the canonical endpoint. For non-interactive modes
  — many short invocations that must each be clean — daemon reuse does not pay
  off, hence `PerInvocation` there. The interactive REPL does not use either
  `IpcWorker` lifetime: it spawns its host directly under a PTY and gets a
  fresh, single-session host (PTY-10; see §10.5).

**Consequences accepted.** Two binaries to ship and version together; an IPC
protocol to maintain (`HostProtocol`, §10.4); a host-lifecycle ownership
contract (`host-lifecycle-contract.md`); and the PTY plumbing in this document
to bridge the launcher's tty to the host's stdio. These costs are the price of
keeping both AOT startup and the full PowerShell runtime.

### 10.9 Related specifications

- [`host-lifecycle-contract.md`](./host-lifecycle-contract.md) — how a launcher
  decides whether a running `Daemon` host is reusable, stale, obsolete, or
  unsafe to touch. The `Lifetime.Daemon` half of §10.5.
- [`browse.md`](./browse.md) — the `browse` object workbench. `browse` opens an
  interactive line-mode workbench when stdin is a terminal and emits row objects
  when redirected; PTY-11 revived `Invoke-BrowseInteractive` as a single-key
  workbench. The tty-vs-redirected branch in `browse` mirrors the non-tty
  fallback logic in §10.7(b).
- [`design-decisions.md`](./design-decisions.md) — the AOT-compatible launcher
  rationale and the FlagSpecs-in-C# decision underpinning §10.1 and §10.8.
- [`architecture-overview.md`](./architecture-overview.md) — the launcher
  startup sequence and the AOT-shell↔PowerShell-runtime interface table.

> The PTY-0 AOT spike (`aot-on-net10.md`) referenced by the loop history was
> never committed to `docs/spikes/` — its findings live in
> `.dartai/loop-state.json` (task `4V0QRPY3bk4N`) and on the unmerged
> `pty-aot-probe` branch. §10.1 and §10.8 above carry the load-bearing
> conclusion (SDK blocks AOT on .NET 10 ⇒ keep the split).
