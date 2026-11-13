# Shell file descriptors (`exec N<file`) and `BASH_SOURCE`

Status: implemented. Tests: `Differential.Tests/ExecFdTests.cs`, `BashSourceTests.cs`, `Core.Tests/Parser/ArrayElementDefaultWordTests.cs`.
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
| `exec 0/1/2…; rest` | the rest of the statement list runs under the redirections, as `{ rest; } >&2` (emitter, `PsEmitter.ExecRedirect.cs`). Gap: in a function body it ends with the function (bash keeps it for the caller). |
| `exec 0/1/2…` with nothing after it (an interactive line), or `exec 1>&-` | **not supported**: message `exec: N: redirecting the shell's own stdin/stdout/stderr with exec is not supported`, status 1 |
| `exec N>&1`, `exec N>&2` | N becomes a name for the shell's stdout / stderr (`exec 3>&1` + `cmd >&3`, as dotnet-install.sh does) |
| `exec N<>file` | not supported (`<>` is not tokenized) |

Failure to open (`exec 3<missing`) writes `exec: missing: No such file or directory`, status 1, and the script goes on
(bash does the same for a non-interactive shell without `set -e`).

A numbered descriptor on one command (`cmd 9>&1`, `cmd 9>f`, `read -u 7 l 7<f`, `( … ) 3>log`) is opened before the command
and closed after it, as `exec` would (`PsEmitter.ScopedHighFdRedirects` / `WithScopedHighFds`); an earlier `exec` of the same
descriptor is not restored. `cmd 3<&0` is ignored (the table has no stdin to name). `1<&2` / `2<&1` are `1>&2` / `2>&1`.

### Using a descriptor
- `read -u N [names]` reads one line from N (`InvokeBashReadCommand`); status 1 at end of file or on a closed N.
- `cmd >&N` pipes the command's output to N (`Write-BashFd N`); `cmd <&N` feeds the rest of N as stdin (`Read-BashFd N`).
  Only N ≥ 3 is routed here; `>&1`, `>&2`, `2>&1` keep their existing handling.
- `cmd >&$fd` / `cmd <&$fd` (a plain variable reference) is decided when the word expands (`PsEmitter.EmitRunTimeFdRedirect`):
  a number is that descriptor (`Write-BashFd` / `Read-BashFd`; 1 and 2 are the shell's own stdout/stderr, an unopened
  one is `N: Bad file descriptor`, status 1, nothing written); for output any other word is a file for both streams
  (`>&word` = `&>word`), for input `-` closes stdin.
- `cmd 7<file` (fd ≠ 0) no longer replaces the command's stdin (it used to be treated as `cmd <file`); the redirect is
  ignored for the command.

### Lifetime and scope — intentional differences
- The table (`BashFdTable`, Cmdlets) is process-global like env and cwd. It is **cleared at the start of every
  invocation** (`Clear-BashFd` in `PerInvocationReset`), so an `exec`-opened descriptor never outlives its command and a
  daemon accumulates none.
- Subshells do not isolate descriptors: `( exec 7<f )` leaves 7 open in the parent (bash closes it with the subshell).
  A background job in its own process (`PSBASH_BG_MODE=process`) starts with none open.
- `exec {var}>file` (any op) takes the lowest free descriptor from 10 up and stores its number in `var`
  (`Invoke-BashExecFd -Variable`); `exec {var}>&-` closes the descriptor `var` holds. On a command other than `exec`
  the `{var}` form is not provided (the redirect applies to the command's stdout).
- Not provided: `flock N`, `/dev/fd/N` paths, descriptors inherited by native children.
