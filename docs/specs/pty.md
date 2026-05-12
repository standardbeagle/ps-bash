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
| PTY-2  | Spawning the host child with the slave attached (`STARTUPINFOEX` on Windows; `fork+exec` + `dup2` + `ioctl(TIOCSCTTY)` on POSIX) |
| PTY-3  | tty mode switching (raw vs cooked) on the launcher-side master      |
| PTY-4  | Decoupling the IPC channel from the PTY data path                   |

No emitter changes; this work is launcher-only.
