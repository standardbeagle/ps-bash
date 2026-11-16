# Stdio messaging: output channels, fds and credit

Status: **stage 1 implemented** (2026-11): background jobs write through an in-host channel mux. **Stage 2 (wire
channels) is designed here, not built.** Companion to [background-jobs.md](background-jobs.md) §12 (the first
user) and [host-lifecycle-contract.md](host-lifecycle-contract.md) (the IPC framing it extends).

## 1. Why

Before this work, the only producer allowed to write to a session's stdout/stderr was the foreground command. The
IPC stream carries `STDOUT` frames (a bare base64 line) and `STDERR:` frames, written by one queue per connection
(`IpcOutputQueue`). Anything else that should reach the user had to borrow that path or be buffered:

- background jobs (`cmd &`), whose output was collected and printed at `wait`;
- nested shells (`bash -c` inside a command), which stream through the foreground today;
- later, the foreground command itself, so that it is one producer among several.

The goal is one messaging layer with these properties:

- every producer writes `(fd, bytes)` on its own channel;
- the launcher writes each channel's data to its real fds in arrival order;
- flow control is per channel, so one flooding producer blocks only itself;
- channels have explicit open and close.

## 2. Stage 1: the in-host mux (implemented)

Types live in `src/PsBash.Transpiler/Stdio/OutputMux.cs` (namespace `PsBash.Core.Stdio`). They sit in the
Transpiler assembly because the host, the cmdlets and the module all load it.

| Type / member | Role |
|---|---|
| `OutputMux` | One per session (`SdkWorker.JobOutput`, exposed to the module as `$global:__BashJobOutput`). |
| `OutputMux.Open(name)` → `OutputChannel` | **Open** message: a new producer, id unique within the mux. |
| `OutputChannel.Write(fd, text, abandoned)` | **Data** message: `fd` 1 = stdout, 2 = stderr (any fd number is carried). |
| `OutputChannel.Close()` | **Close** message: the producer ended. Text it already queued is still delivered. |
| `OutputMux.Attach(sink, newLine)` / `Detach()` | Connect or disconnect the session's real output (the running command's stdout/stderr path). |
| `OutputMux.TakePending()` | Drain the queue without a sink. The interactive prompt uses this. |
| `OutputMux.WindowChars` | Per-channel credit while detached (default 64 Ki chars, about a pipe buffer). |

### 2.1 Delivery and flow-control rule

1. **Attached.** A write is delivered to the sink at once, on the producer's thread, under the mux lock. The order
   across channels is therefore the arrival order. The sink applies whatever back-pressure is downstream (the
   batcher, then `IpcOutputQueue`'s bounded queue, then the socket), and that blocks the producer, as a full pipe
   blocks its writer.
2. **Detached.** A write is queued only while its channel holds fewer than `WindowChars` queued characters (the
   channel's credit). An empty channel always accepts one chunk, so a single oversized chunk cannot wedge it.
3. **Out of credit.** The producer blocks until either:
   - credit returns, through `Attach` (the queue is flushed in order) or `TakePending`; or
   - its `abandoned` check returns true. The check is polled every 50 ms. When it fires, `Write` returns false and
     drops that one chunk.

   The abandon check is what lets `PowerShell.Stop()` or a kill end a producer that is blocked on output. Without
   it, Stop() would wait on a thread that waits for credit that never comes.
4. **Memory bound.** Queued data is at most `WindowChars` × open channels, plus one chunk per channel.
5. **Sink failure.** If the sink throws (for example, the launcher went away), the exception is swallowed. The
   sink's owner handles the failure (the foreground command's own `BeginStop`), and the producer does not die.

### 2.2 Locking rule (no deadlock)

Lock order: producer → mux lock → the sink's `outputLock` → batcher → `IpcOutputQueue`.

- The foreground command takes `outputLock` but never the mux lock while holding it.
- `Attach` and `Detach` run outside `outputLock`.
- `IpcOutputQueue` back-pressure is released by its drain task, or by the command's cancellation when the client
  disconnects. So the bottom of the chain always makes progress or fails.

### 2.3 Compatibility

Stage 1 changes nothing on the wire. Job output is folded into the running command's STDOUT/STDERR frames, and a
frame is always a whole sink call, so a job's text never lands inside a foreground record. Old launchers work
unchanged.

## 3. Stage 2: channels on the wire (next step, not built)

### 3.1 Frames

Each frame is one line on the existing framed stream.

| Direction | Wire form | Meaning |
|---|---|---|
| host → launcher | `<base64>` | Unchanged: data on fd 1 of the foreground command (channel 0). |
| host → launcher | `STDERR:<base64>` | Unchanged: data on fd 2 of channel 0. |
| host → launcher | `CHOPEN:<id>:<name>` | A producer channel opened (`id` > 0). |
| host → launcher | `CH:<id>:<fd>:<base64>` | Data from channel `id` on fd `fd`. |
| host → launcher | `CHCLOSE:<id>[:<status>]` | The producer ended; optional exit status for `jobs` and `wait` notifications. |
| launcher → host | `CREDIT:<id>:<bytes>` | The launcher wrote `bytes` of channel `id` to its fds: credit returned. |

A frame with no channel id is channel 0, the foreground command. This is what keeps the protocol backward
compatible: a host only sends `CH*` frames after the launcher's hello has advertised `channels`. Against an older
launcher it stays on stage 1 and folds channel data into channel 0.

### 3.2 Launcher rule

The launcher writes frames to its real fds in arrival order and does not reorder across channels. Arrival order is
production order, because the host serializes onto one output queue.

`fd` ≥ 3 is reserved for `exec N>` passthrough. A launcher that does not support an fd sends it to fd 2 with a
one-time warning.

### 3.3 Flow control

- Each channel starts with `WindowBytes` of credit (default 64 KiB).
- The host decrements a channel's credit as it frames that channel's data, and blocks the producer at zero, using
  the abandon rule of §2.1.
- The launcher returns credit after each write to its fd. A launcher whose stdout is a full pipe therefore stops
  returning credit, and only the channels writing to it stall. Channel 0 keeps today's queue-based back-pressure
  until it moves onto credit too.

### 3.4 Migration order

1. Background jobs: stage 1 now, then stage 2 frames so that output keeps flowing after the foreground command
   returns, while the session is alive.
2. Nested shells: a nested `bash -c` opens a channel instead of borrowing the foreground's writers.
3. The foreground command becomes channel 0 with explicit credit. `STDOUT` and `STDERR:` stay as its aliases.

## 4. Child-process stdio merge invariant (implemented)

**Rule.** When a script merges a child process's stderr into its stdout (`2>&1`, `&>f`, `>f 2>&1`, `|&`, a merging
group/subshell/loop `{ ...; } 2>&1`, `exec 2>&1`), the child gets ONE pipe or file handle for fd 1 and fd 2, so the
order of its writes is kept by construction, as in bash (one open file description, `dup2`). Two pipes read by two
threads and merged afterwards have no order to keep: `printf 'echo one\necho two >&2' | sh 2>&1` printed `two` first.
A post-hoc merge of two separately read streams must never decide the order.

**The one primitive.** `Cmdlets/ChildStdio.cs`: `ChildStdio.Start(psi, ChildStdioMode)` → `SpawnedChild`
(`StandardError` is null when merged). Merged:

- Windows: `CreateProcessW` with `hStdOutput == hStdError` (one inheritable pipe end), a
  `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` naming only the child's handles, started suspended and resumed once the
  `Process` object holds it.
- Unix: `/bin/sh -c 'exec 2>&1; exec "$@"' sh PROG ARGS` (`dup2(1,2)`, then the program replaces the shell: same pid,
  same exit status, kill-tree unchanged).

`BashRuntime.RunChildProcess(..., mode)` (buffered) and `BashRuntime.StartChildProcessStreaming(..., mode)` (streaming,
`StreamingChildProcess` reads one stream when merged) are built on it.

**Knowing that a merge is in force.** `Cmdlets/StderrMerge.JoinsStdout(cmdlet)`: the command runtime's
`ErrorMergeTo == Output` (its own `2>&1`) or an error pipe that IS its output pipe (an enclosing block's `2>&1`), and
the stage that pipe feeds does not split stderr off again: `2>f` and `2>&1 >/dev/null` are emitted as a PowerShell
`2>&1` into `Invoke-BashRedirect -ErrorPath f` / `-Path $null -PassErrors`, which
`InvokeBashRedirectCommand.KeepsStderrWithStdout` answers. The SMA internals are read by reflection; unreadable means
"not merged" (the old behavior) and `ChildStdioTests.StderrMerge_CanReadThePipelinesMergeState` fails.
`StderrMerge.Redirected(cmdlet)` is the first half alone: the cmdlet's stderr goes down the pipeline at all (merged, or
split off to a file or `/dev/null`). The emitter treats a merge whose one target is `/dev/null` (`>/dev/null 2>&1`,
`&>/dev/null`) as no merge: output nobody reads has no order to keep.

**Programs run directly.** PowerShell runs a native command with two pipes. The emitter
(`PsEmitter.NativeMergeRunner`) prefixes a literal program name with `Invoke-BashNative -- ` when bash merges its
stderr: its own redirects (`RedirectsMergeStderr`, a dup-tracking walk), a pipe stage before `|&`, or an enclosing
compound command with a merging redirect (`_stderrMergeDepth`). `Invoke-BashNative` resolves NAME; a program under
`JoinsStdout` runs through `ChildStdio` (piped input fed, else the inherited stdin), anything else (function, cmdlet,
alias, a split-again stage, a Windows `.cmd`/`.bat`) runs unchanged as a steppable pipeline of `& NAME ARGS`.

**Sites** (every way ps-bash starts a child whose output reaches the script):

| Site | Merged path |
|---|---|
| nested `bash`/`sh` (`-c`, script, program on stdin) | `InvokeBashBashCommand` → `StartChildProcessStreaming(mode)` |
| program run directly, incl. in `$( )`, groups, subshells, loops, `|&` stages | `Invoke-BashNative` |
| `env`, `command`, `xargs`, `find -exec/-ok`, `time`, `flock` | `InvokeBashNativeCommand.MergedProgramPath` + `RunMerged` (under `Redirected`: one pipe when `JoinsStdout`, else stderr through the cmdlet's error stream so `2>f` reaches the file; PowerShell's nested `& $c` wrote it straight to the host's stderr) |
| `timeout` | runs the command line with `2>&1` appended, so its program goes through `Invoke-BashNative`; under `2>f` the nested pipeline's errors are merged back (`MergeMyResults`) and re-written on the cmdlet's error stream |
| awk `system()`, `print \| "cmd"` | `AwkShell` passes `ChildStdioMode` from awk's own merge state |
| `nohup` | `DetachedProcess`: fd 1 and fd 2 are already one file handle |

Not merge sites (the child's output is data, not passed through): awk `cmd | getline`, sed `e`, the psgit / psav
wrappers, `stat`/`id`/`ps` probes. Interactive `less` / `traceroute` own the console.

**Known gap.** A function called with a merge (`f 2>&1`, `{ f; } 2>&1` where `f` is a function) whose body runs a
program: the body is emitted once, before any call site, so its programs are not routed through `Invoke-BashNative`
and keep PowerShell's two pipes.

**Guards.** `Core.Tests/ChildStdioGuardTests` (no raw `Process.Start` outside `ChildStdio` and named exemptions; every
streaming spawn states its mode; every run-by-name cmdlet consults the merge), `Cmdlets.Tests/ChildStdioTests` (the
primitive keeps write order), `Differential.Tests/StderrMergeOrderDifferentialTests` (child × merge form against bash).
