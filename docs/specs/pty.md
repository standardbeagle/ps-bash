# PTY Allocation Specification (PTY-1)

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
