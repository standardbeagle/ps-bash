# Shell file descriptors (`exec N<file`) and `BASH_SOURCE`

Status: implemented. Tests: `Differential.Tests/ExecFdTests.cs`, `Differential.Tests/FdTableDifferentialTests.cs`, `BashSourceTests.cs`, `Core.Tests/Parser/ArrayElementDefaultWordTests.cs`, `Core.Tests/Parser/FdRedirectEmitGuardTests.cs`, `Cmdlets.Tests/BashFdTableTests.cs`.
Behavior below was checked against Ubuntu bash; scripts that locate their own directory with `${BASH_SOURCE[0]}` and
take locks or read files through numbered descriptors depend on it.

## BASH_SOURCE

`$global:BASH_SOURCE` is an array; `${BASH_SOURCE[0]}` is `$global:BASH_SOURCE[0]` (emitter, `EmitBracedVar`).

| Context | Value |
|---|---|
| `ps-bash script.sh` | `("script.sh")` as typed, the same text as `$0` |
| `ps-bash -c '…'` (and `-c … NAME`) | empty array — bash sets it only for a file |
| `source f` / `. f` while `f` runs | `("f", <caller's entries>)`: `[0]` is the sourced file as typed, `[1]` the caller; restored afterwards |

Set by the launcher (`Program.cs` `BuildPositionalPreamble`), pushed/popped by `Invoke-BashSource`, reset per invocation
(`Connection.PerInvocationReset`), saved/restored with subshells like the other shell globals (`BashShellState.GlobalNames`).

Not modelled: inside a function, bash gives the file the function was *defined* in; ps-bash gives the file currently
running (they differ only for a function defined in one sourced file and called from another). `BASH_LINENO` and
`FUNCNAME` are separate, unimplemented features.

### `${arr[i]:-WORD}` expands WORD
A default / alternative / assigned word after a subscript is now an expandable word, as for a plain variable. It stayed a
raw slice, so `${BASH_SOURCE[0]:-$0}` printed a literal `$0`. (`BashParser.Words.cs`, array branch of `ParseBracedVar`.)

## `exec`

| Form | Behavior |
|---|---|
| `exec cmd args…` | runs `cmd`, then leaves the shell with its status (a subshell: only itself). There is no process image to replace. |
| `exec N<file`, `N>file`, `N>>file` (N ≥ 3) | opens descriptor N (`Invoke-BashExecFd`); an open N is closed first |
| `exec N>&M`, `N<&M` | N becomes another name for open descriptor M |
| `exec N>&-`, `N<&-` | closes N |
| `exec >f`, `exec 2>&1`, `exec >&-`, `exec >&5` … (fd 1 / 2) | changes the shell's fd 1 / 2 in the descriptor table for the rest of the shell, wherever it runs: function, if, loop, group, `source`, `eval` (`Invoke-BashExecFd 1 '>' f`). See "The descriptor table". |
| `exec 0/1/2…; rest` where the rest of the list IS the rest of the shell (the script's top level, a subshell's top level), or any `exec <…` (fd 0) | the rest runs under the redirections, as `{ rest; } >&2` (emitter, `PsEmitter.ExecRewriteApplies`). Not for `exec >&N` (N ≥ 3: a copy of N now, not a name for N), nor for a file in a subshell (an open that fails must leave the rest to run). |
| `exec 0<…` with nothing after it (an interactive line) | **not supported**: `0: redirecting the shell's own stdin with exec is not supported`, status 1 |
| `exec N>&1`, `exec N>&2` | N becomes a name for the shell's stdout / stderr (`exec 3>&1` + `cmd >&3`, as dotnet-install.sh does) |
| `exec N<&0`, `exec N</dev/stdin` | N becomes a name for the shell's stdin. INVARIANT: it always succeeds (bash's dup(0) cannot fail while fd 0 is open; it failed `0: Bad file descriptor` here, with or without a console). Its readers read that stdin: `read -u N` is `read`, `cmd <&N` gets the shared stdin's records (`BashFdTable.ReadsShellStdin`; none to forward when no scope or launcher stdin is shared). Guard: `ExecFdTests.ExecDupOfStdin_Succeeds_AndReadsTheShellsStdin`. |
| `exec N<<< word`, `exec N<<EOF`, `{v}<<< word`, `{v}<<EOF` | opens N (or the lowest free from 10, stored in `v`) on the here-text (`PsEmitter.FdOpenStep` -> `Invoke-BashExecFd N '<<<' (text)`); also on one command (`read -u 3 l 3<<< x`) and after a compound (`done 3<<EOF`) |
| `exec N>&M-`, `N<&M-` | moves M to N: N becomes a name for M, then M is closed (`PsEmitter.MovedFd`). On one command (`cmd 4>&3-`) N is opened for the command but M is not closed for it (gap) |
| `exec N<>file`, `{v}<>file` | opens N for reading AND writing, the file created when missing, one position for both (`Redirect.ReadWrite` -> `Invoke-BashExecFd N '<>'` -> `BashFdTable`); a device name is refused |

Failure to open (`exec 3<missing`) writes `script: line N: missing: No such file or directory`, status 1, and the script
goes on (bash does the same for a non-interactive shell without `set -e`).

A numbered descriptor on one command (`cmd 9>&1`, `cmd 9>f`, `read -u 7 l 7<f`, `cmd 3<&0`, `( … ) 3>log`) is opened before
the command and put back after it (`PsEmitter.ScopedHighFdRedirects` / `WithScopedHighFds` -> `PsBuild.ScopedDescriptors`:
`Save-BashFd N` … `Restore-BashFd`), so an earlier `exec` of the same descriptor names its file again. `1<&2` / `2<&1` are
`1>&2` / `2>&1`. `cmd 1<> f` writes into `f` from its start without truncating it (`Invoke-BashRedirect -ReadWrite`,
`FileMode.OpenOrCreate`); `cmd 0<> f` reads `f`, creating it when missing, and a later `>&0` on the same command
writes into it. `2<>` is not modelled.

## The descriptor table

ONE table per shell, fds 0..N, as bash has one per process (`Cmdlets/BashFdTable.cs`). Invariants:

- `n>&m` COPIES what m names at that point (dup2); redirections apply left to right. `exec 3>&1; exec >f; echo x >&3`
  prints x on the terminal; `exec 4>&3; exec 3>g` leaves 4 on the old file.
- No entry for 0 / 1 / 2 = the shell's original stdin / stdout / stderr. A copy of one (`exec 3>&1`) is a Terminal entry that
  writes to the host's stream (`Transpiler/Stdio/ShellStdio.Terminal`), past whatever the writer runs under:
  `exec 3>&1; x=$(echo to3 >&3; echo cap)` prints to3 and sets x=cap.
- `exec` changes the table for the rest of the shell in ANY context. An exec-set fd 1 / 2 receives what reaches the shell's
  original stdout / stderr: the host offers every write to `ShellStdio.Divert` first (`SdkWorker.RunCommand`), which the
  table sets while its fd 1 or 2 has an entry.
- A redirection stage (`Invoke-BashRedirect`, a file or `/dev/null` target) and a command substitution
  (`ConvertTo-BashCapture`) ARE fd 1 / 2 of the command under them: they `Register` their target for the command's
  duration. So `{ exec 3>&1; } >f; echo x >&3` writes x to f (the copy keeps f open), and `x=$(exec >f; echo a)` writes a
  to f: a record that arrives after its fd moved goes where the table says.
- A subshell gets a COPY (`BashShellState.Save` / `Restore` carry `BashFdTable.Save` / `Restore`): `( exec 3>g )` leaves
  the parent's 3 alone; what the subshell opened is closed when it ends. An in-process background job gets a copy at the
  `&` (`BashFdTable.AttachCopy`, `BashJobRunspaces`) and lets it go when it ends.
- Keyed by runspace (the running shell): a job and the shell, or two runspaces, never see each other's entries or
  registrations. `Clear-BashFd` (each new invocation) empties every shell's table and the host's divert.
- A command's tail is modelled in the emitter (`PsEmitter.TryAppendModelledRedirectTail`, the fd model) with the command's
  numbered descriptors in order (`_fdPrelude`): when its final fd 1 / 2 is a table descriptor, a closed stdout, or a swap,
  the tail is `2>&1 | Invoke-BashFdRoute -Out D -Err D [-Name cmd]` (`PsBuild.FdRouteStage`), D = `out` / `err` (the
  command's original stdout / stderr), `null`, `close`, `pass` (a file: the `Invoke-BashRedirect` stage after it), or a
  table fd number. `cmd 2>&3`, `cmd 2>&1 1>&3`, `cmd 3>&1 1>&2 2>&3`, `{ …; } >&3`, `for … done >&3`, `if … fi 2>&3`.
  A descriptor the route writes to that is not open: `script: line N: 3: Bad file descriptor`, status 1, the command
  does not run. A closed stdout (`echo hi >&-`): `echo: write error: Bad file descriptor`, status 1.
- Guards: `Core.Tests/Parser/FdRedirectEmitGuardTests` (no redirect of the table's forms is emitted as a comment, on any
  command kind; an exec inside a compound reaches the table), `Differential.Tests/FdTableDifferentialTests` (dup form x
  command kind x exec context against bash), `Cmdlets.Tests/BashFdTableTests`.

Gaps (each a known difference, not a model):
- `( exec >&- ; echo x )`: a closed fd 1 set by exec drops the shell's later output silently, status 0 (bash: write error,
  status 1); only a closed stdout on the command itself (`echo x >&-`) fails.
- A background job's output does not pass through the table (`JobOutput` writes the host's streams): a job started after
  an `exec >log` that was not at the top level (`fx() { exec >log; }; fx; cmd &`) prints to the terminal. At the top level
  the rewrite covers it.
- `exec >/nonexist/f; rest` at the script's top level skips the rest (status 1); bash reports the error and runs it.
- `{ …; } >f 3>&1` opens 3 before the stage registers f, so 3 names the old stdout (bash: f). `3>&1 >f` (the common
  order) is right.
- A pipe stage, and a native's own `>$null` / `2>$null`, are not registered: an `exec 3>&1` inside `{ …; } | cat` copies
  the shell's stdout, not the pipe.
- `<&-` and `0<&N` on a compound are not modelled; `cmd 4>&3-` does not close 3 for the command (see the table above).

### Using a descriptor
- `read -u N [names]` reads one line from N (`InvokeBashReadCommand`); status 1 at end of file or on a closed N.
- `cmd >&N` sends the command's output to N in order with its other redirections (`Invoke-BashFdRoute -Out 'N'`, see
  "The descriptor table"); `cmd <&N` feeds the rest of N as stdin (`Read-BashFd N`).
  INVARIANT: `<&N` is the command's OWN stdin (`PsEmitter.HasOwnStdin`): it is never also fed the pipe or the scope's
  queue (`echo x | cat <&6` failed "The input object cannot be bound"; a feed would also drain what a later `read` gets).
  Only N ≥ 3 is routed here; `>&1`, `>&2`, `2>&1` keep their existing handling.
- `cmd >&$fd` / `cmd <&$fd` (a plain variable reference) is decided when the word expands (`PsEmitter.EmitRunTimeFdRedirect`):
  a number is that descriptor (`Write-BashFd` / `Read-BashFd`; 1 and 2 are the shell's own stdout/stderr, an unopened
  one is `N: Bad file descriptor`, status 1, nothing written); for output any other word is a file for both streams
  (`>&word` = `&>word`), for input `-` closes stdin.
- `cmd 7<file` (fd ≠ 0) no longer replaces the command's stdin (it used to be treated as `cmd <file`); the redirect is
  ignored for the command.

### Lifetime and scope — intentional differences
- The table is **cleared at the start of every invocation** (`Clear-BashFd` in `PerInvocationReset`), so an
  `exec`-opened descriptor never outlives its command and a daemon accumulates none.
- A background job in its own process (`PSBASH_BG_MODE=process`) starts with none open.
- `exec {var}>file` (any op) takes the lowest free descriptor from 10 up and stores its number in `var`
  (`Invoke-BashExecFd -Variable`); `exec {var}>&-` closes the descriptor `var` holds. On a command other than `exec`
  the `{var}` form is not provided (the redirect applies to the command's stdout).
- Not provided: `flock N`, `/dev/fd/N` paths, descriptors inherited by native children.
