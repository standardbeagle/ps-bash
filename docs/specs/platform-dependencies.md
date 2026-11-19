# Platform dependencies

ps-bash ships release archives for win-x64, linux-x64 and osx-arm64 only (the `publish.yml` build matrix; `install.sh`
refuses any other RID). `Platform/` also answers for targets with no archive: linux-arm64, musl, osx-x64 (a build from
source). Every OS- or arch-dependent constant, struct
layout, probe and system tool path lives in ONE place: `src/PsBash.Transpiler/Platform/` (namespace
`PsBash.Core.Platform`; the Transpiler assembly is referenced by Cmdlets, Core, Host and Shell). A call site asks
`Platform/`; it never writes the fact down itself.

Why: each of these was a macOS or arm64 bug that no Windows or Linux x86_64 run could see — a signal number that
means another signal on macOS, a `struct stat` read with the x86_64 offsets on arm64, a `/proc` path with no macOS
branch, a variadic `ioctl` whose third argument Apple arm64 reads from the stack.

| Primitive | Owns | Guard |
|---|---|---|
| `OsSignals` | signal numbers, wait-status decoding, the runtime-owned signal | `Core.Tests/Platform/OsSignalsTests` (`OsSignalsGuardTests`) |
| `PosixStat` | `stat`/`lstat`/`fstat` and the `struct stat` layout per target | `Core.Tests/PlatformDependencyGuardTests`, `PosixStatTests` |
| `SystemTools` | the OS's own `stat`/`id`/`ps`/`sysctl`, `/bin/sh`, host-bash candidates | `PlatformDependencyGuardTests` |
| `ProcessInfo` | parent pid, zombie, process-group members, real uid, umask | `Core.Tests/PlatformProbeGuardTests` |
| `TerminalIoctl`, `TermiosLayout` | the one `ioctl` declaration (winsize), termios offsets | `PlatformProbeGuardTests` |
| `UnixSocketPath` | the AF_UNIX `sun_path` budget (103 macOS/FreeBSD, 107 Linux/Windows) | `UnixSocketPathTests` |
| `KernelIdentity` | `uname(2)` and the `struct utsname` layout (65-byte fields Linux, 256 Darwin) — what `uname` prints off Windows; Windows keeps the MSYS identity | `KernelIdentityTests`, `PlatformDependencyGuardTests.UnameCall_LivesOnlyInPlatform` |

Runtime facts .NET already knows come from .NET, never a literal: page size = `Environment.SystemPageSize`
(16K/64K arm64 kernels).

## Signals

Every number a script sees is **bash's number = Linux numbering**, on every OS: `kill -l`, `trap … USR1`,
`kill -10`, the `128+N` status of a child killed by a signal. Native numbers exist only at the OS boundary
(`kill(2)`, `killpg`, `PosixSignalRegistration`, a `waitpid` status) and are converted there:

- `OsSignals.SIGx` — bash numbers (`SIGUSR1 = 10`, `SIGCHLD = 17`, …).
- `OsSignals.ToNative(bash)` — the number to pass the OS; 0 when the signal does not exist there.
- `OsSignals.FromNative(native)` — back to bash's number (kept as-is when bash has none, e.g. macOS SIGINFO 29).
- `OsSignals.ExitStatusOf(waitStatus)` — `exit code` or `128 + bash number`.
- `OsSignals.WaitOutcome(rc, pid, status, errno)` — the one decoder of a `waitpid`: the status, null on EINTR (wait
  again), else `LostChildStatus` = 0. ECHILD means another waiter took the status (.NET reaps every child of a process
  that inherited SIGCHLD ignored); bash then records 0 (jobs.c `wait_for`: "can't find true status"). The job reaper
  reported 255. Guard: `Core.Tests/WaitOutcomeTests.WaitpidCallers_DecodeOnlyThroughOsSignals`.
- `OsSignals.AsPosixSignal(bash)` — for `PosixSignalRegistration`; refuses a runtime-owned signal.

The numbers differ on macOS/FreeBSD (BSD numbering):

| Signal | Linux / bash | macOS |
|---|---|---|
| BUS | 7 | 10 |
| USR1 | 10 | 30 |
| USR2 | 12 | 31 |
| CHLD | 17 | 20 |
| CONT | 18 | 19 |
| STOP | 19 | 17 |
| TSTP | 20 | 18 |
| URG | 23 | 16 |
| IO | 29 | 23 |
| SYS | 31 | 12 |
| STKFLT 16, PWR 30, real-time 34+ | exist | none |

XSI fixes 0, HUP 1, INT 2, QUIT 3, ABRT 6, KILL 9, ALRM 14 and TERM 15 on every system; only those may appear as
literals at a call site.

### The runtime-owned signal (macOS)

macOS has no real-time signals, so the .NET runtime (CoreCLR `pal/src/exception/signal.cpp`, NativeAOT
`UnixSignals.h`) uses **SIGUSR1** as its activation signal: it `pthread_kill`s its own threads with it on every GC
suspension. A `PosixSignalRegistration` for SIGUSR1 is chained after the runtime's handler and sees every one of
them. REGRESSION (v0.15.0 publish, macOS only): the host registered SIGUSR1 for script traps, so each GC was a USR1
delivery and scripts died with 138 and no output; the launcher forwarded its own GCs to the host the same way.

`OsSignals.RuntimeOwned(sig)` is true for SIGUSR1 on BSD numbering; `ScriptSignals()` (what the host and the launcher
catch for traps) leaves it out there, and `AsPosixSignal` throws for it. Consequence (intentional difference): on
macOS a SIGUSR1 sent by a non-ps-bash process to a ps-bash process does not reach a `trap … USR1`; `kill -USR1` from
a ps-bash shell does (it is delivered as a message, not an OS signal). SIGUSR2 is caught everywhere.

## struct stat — `PosixStat`

libc `stat`/`lstat`/`fstat` and the layout per target: Linux x86_64, Linux arm64 (`asm-generic`), Darwin 64-bit
inode (incl. the `$INODE64` symbols on x86_64); glibc < 2.33 has no plain symbols and goes through `__xstat`. The
kernel fills a byte buffer that the pure `TryDecode(span, layout)` reads, so every layout is unit-tested on any OS
(`PosixStatTests`, hand-built buffers).

## System tools — `SystemTools`

`Resolve(name)` finds the OS's own tool in `/usr/bin`, `/bin` (Alpine/busybox), `/usr/sbin`, `/sbin` — never PATH
first, because callers choose GNU or BSD flags by OS and Homebrew's GNU `stat` often leads a macOS PATH. Also
`PosixShell` (`/bin/sh`) and the host-bash candidates. psm1: `[PsBash.Cmdlets.BashRuntime]::SystemToolPath('stat')`.

## Process information — `ProcessInfo`

Linux `/proc`, macOS libproc (`proc_pidinfo`, `proc_listpids`), Windows `NtQueryInformationProcess`. Table and
struct offsets: `background-jobs.md` §6.1a.

## Terminal — `TerminalIoctl`, `TermiosLayout`

The only `ioctl` declaration. On osx-arm64 variadic arguments go on the stack, so the helper pads the register
arguments and the winsize pointer lands where `ioctl` reads it; Linux and macOS x86_64 use the plain 3-argument form.
termios `c_lflag`: offset 12 (4 bytes) on Linux, 24 (8 bytes) on macOS. See `pty.md` §7.2/§8.2.

## Rule and guards

A new platform fact goes into `Platform/` with a unit test that decodes or maps it on any OS, and a call site asks
it. The guards fail on, outside `Platform/`:

- a `SIGX = N` constant, a `(PosixSignal)` cast, or a `kill`/`killpg`/`raise`/`signal` call with a literal non-XSI
  number (`OsSignalsGuardTests`);
- a `struct stat` field or stat-family P/Invoke, or a `"/usr/bin/…"`, `"/bin/…"`, `"/usr/sbin/…"`,
  `"/usr/local/bin/…"` literal in product C# or psm1 (`PlatformDependencyGuardTests`; allowlist entries carry a reason);
- a `/proc` path or an `ioctl` declaration (`PlatformProbeGuardTests`).

Not yet in `Platform/`: `ps`'s `clkTck = 100`.
