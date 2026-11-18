# Paths: what a script sees, what the OS gets

Owner decision: **every path ps-bash produces for a script uses forward slashes, in drive form `C:/Users/x`.**
The drive letter stays (never MSYS `/c/Users/x`). Every path a script hands ps-bash keeps working in any
spelling it already accepted: `C:/x`, `C:\x`, `/c/x`, `/mnt/c/x`, `/tmp/x`, `//server/share/x`, relative.

There is exactly **one helper per direction**. Nothing else may convert separators.

| Direction | Helper | From PowerShell / emitted text |
|---|---|---|
| OS → script (outbound) | `PsBash.Core.BashPath.ToShell(path)` (`src/PsBash.Transpiler/BashPath.cs`) | `[PsBash.Cmdlets.BashRuntime]::ToShellPath(x)`; the emitter builds it with `PsBuild.RuntimeToShellPath(expr)` |
| script → OS (inbound) | `PsBash.Core.RuntimePath.Map` → `WindowsPath.Normalize` | cmdlets: `FileSystemHelpers.NormalizeOperandPath` / `ProviderPath`; emitted text: `PsBuild.RuntimeMapPath(expr)` (`BashRuntime.MapPath`) |

`PathMappingGuardTests` (Core.Tests) fails on any `.Replace('\\', '/')`, `.Replace('/', '\\')` (C#,
including inside emitted text) or `-replace '\\', '/'` (psm1/ps1) outside the two helpers. Its allow-list
names the only exceptions: tar member names, which are archive data, not filesystem paths.

## `BashPath.ToShell`

- Windows only. Off Windows a backslash is an ordinary file-name character and the path is returned unchanged.
- `\` → `/`; drive kept: `C:\Users\x` → `C:/Users/x`, `C:\` → `C:/`.
- UNC: `\\server\share\x` → `//server/share/x`. Long-path prefixes are dropped: `\\?\C:\x` → `C:/x`,
  `\\?\UNC\s\sh` → `//s/sh`.
- Relative stays relative: `..\x` → `../x`, `sub\dir` → `sub/dir`.
- Idempotent; `null` → `""`. The inbound mapper reads every result back (`WindowsPath.Normalize(ToShell(p)) == p`).
- Apply it only to a value that IS a path. It never parses or guesses: a message, a definition or a
  regex is not passed through it.

## The rule for new code

- A cmdlet that prints a path (or stores one a script reads) prints `BashPath.ToShell(path)`.
- A cmdlet that receives a path from a script resolves it with `NormalizeOperandPath` / `ProviderPath`
  and never prints the resolved native form back; diagnostics name the operand as typed (see
  runtime-functions.md, "Diagnostics name operands as typed").
- Emitted PowerShell never calls a Transpiler type directly (load order): it calls
  `[PsBash.Cmdlets.BashRuntime]::ToShellPath` / `::MapPath`, built by `PsBuild`.
- Native child processes receive arguments exactly as the script produced them: ps-bash does not
  rewrite argv. What changes is only what ps-bash itself produced (`$PWD`, `~`, `mktemp`, ...), which
  Windows programs accept with forward slashes.

## Inventory (outbound producers)

### Variables

| Value | Set at | Notes |
|---|---|---|
| `$PWD` | emitter `EmitCd`; `BashShellState.RestoreWorkingDirectory` (every subshell form: `( )`, `$( )`, pipe stages); `InvokeBashPushdCommand.SyncProcessWorkingDirectory` (pushd/popd, module-mode cd); launcher `Program.BuildInvocationCwdPreamble`; host startup (`Host/Program.cs`, interactive and stateful sessions); `Spec.Tests/InHostExecutor` | bash `$PWD` / `${PWD}` read `$env:PWD` (`PsBuild.TryMapSpecialVar`), not PowerShell's automatic `$PWD` (a native `PathInfo`). Native children inherit the same `PWD`. |
| `$OLDPWD` | `EmitCd`, `SyncProcessWorkingDirectory` | `cd -` reads it back through the inbound mapper. |
| `$HOME`, `~`, `~/x` | `SdkRunspaceSetup.ps1` (runspace creation), `SdkWorker.SyncHomeToEnvironment` (per request) | The PowerShell `$HOME` variable (ReadOnly, AllScope) holds the shell form; the FileSystem provider's Home stays native. A script's own `HOME=...` is kept as written. |
| `~+`, `~-` | emitter `TildeSub`, `BashParser.Simple` tilde-in-assignment | read `$env:PWD` / `$env:OLDPWD`. |
| `$0`, `BASH_SOURCE[0]` (script file) | launcher `Program.BuildPositionalPreamble` | `ps-bash sub\s.sh` → `sub/s.sh`; `-c CMD NAME` keeps NAME as given. |
| `BASH_SOURCE` (sourced file) | `InvokeBashSourceCommand` | the operand, through `ToShell`. |
| `$SHELL` default | `BashRuntime.DefaultShell` | |
| `<(cmd)`, `>(cmd)` (the temp file word) | psm1 `Invoke-ProcessSub`, `New-BashProcessSubPath` | through `BashRuntime.ToShellPath`: `wc -l <(cmd)` printed `C:\…\psub-…` |
| temp dir in a glob pattern | `PsEmitter.EmitGlobPatternExpr` (`PsBuild.TempDirExpr`) | |
| `PATH` | not changed | see "PATH" below. |

### Command output

| Command | Site |
|---|---|
| `pwd`, `dirs`, `pushd`/`popd` listing | `InvokeBashPwdCommand`, `InvokeBashDirsCommand.Present` |
| `cd -`, `cd` via `CDPATH` (echo the new directory) | `PsEmitter.EmitCd` |
| `mktemp` | `InvokeBashMktempCommand` (`BashText`; `Path` stays native for PowerShell callers) |
| `realpath`, `readlink` | `InvokeBashRealpathCommand`, `InvokeBashReadlinkCommand` |
| `which`, `type`, `command -v`/`-V` | `InvokeBashWhichCommand`, `InvokeBashTypeCommand.PathFiles` (PATH entry + `/` + file), `InvokeBashCommandCommand.Describe` |
| glob results | `BashGlob` keeps the pattern's own separators; `ConvertToBashWordsCommand` relativizes through `ToShell` |
| `find`, `ls`, `du`, `tree`, `file`, `stat` names | the per-cmdlet display helpers (`OperandDisplay`, `FileHeaders`) |
| file-name prefixes and headers (`grep`, `rg`, `head`/`tail` `==> f <==`, `wc`, `cat`, `sed`, `sort`, `split`, `gzip`, `tar -v`, `cp`/`mv`/`ln`/`rm`/`mkdir`/`rmdir`/`install`/`chmod`/`tee` `-v` and errors) | `BashPath.ToShell` at each display site (was a per-cmdlet `Replace`) |
| prompt `\w` / `\W` | `BashPromptText` (reads `$PWD`; `HOME` and the fallback cwd through `ToShell`) |

### Inbound mappers (one helper)

`RuntimePath.Map` (Windows: `/tmp` → `%TEMP%`, then `WindowsPath.Normalize`: `/c/x`, `/mnt/c/x`, `C:/x` → `C:\x`;
`PSBASH_UNIX_PATHS=1` also maps other `/x` forms). Callers: `FileSystemHelpers.NormalizeOperandPath` /
`ProviderPath` (every cmdlet operand), `BashRuntime.MapPath` (emitted `cd`, `[[ -f ]]`, redirects), the
interactive shell's cwd sync.

One comparison-only inverse exists: `RuntimePath.TryUnmapTmp` lets `[[ $PWD == /tmp* ]]` and `case` match a
temp-directory value against the `/tmp` spelling (`BashPatternMatch`). It never produces output.

### Canonical names (one walk)

`readlink -f/-e/-m`, plain `readlink` and `realpath` resolve names through `Cmdlets/CanonicalPath.cs`, a port of
gnulib `canonicalize_filename_mode`: one component at a time from the root, each symbolic link followed where it is
met (so `dl/..` is the parent of the link's target), the existence rule per mode (Existing / AllButLast / Missing),
gnulib's `/`, `/.`, `/..` directory checks, ENOTDIR for a name under a file, and its loop rule (after 20 links, the
first repeated link). The walk is pure over a probe, so it is unit-tested on a fake tree (`CanonicalPathTests`).
Never canonicalize with `Resolve-Path` / `GetUnresolvedProviderPathFromPSPath`: they fold `..` by text, follow no
link and glob `[`/`*`. A diagnostic names the operand as typed, GNU-quoted (`Cmdlets/GnuQuote.cs`: `''`, `'a b'`,
`"it's"`; a Windows drive colon does not force quoting).

## Device names

**A `/dev/*` device name is not a path and never reaches the filesystem.** Windows has no `/dev`: a name
handed to a file API becomes `C:\dev\stdout`, which is "No such file or directory" on most machines and,
on a machine that happens to have a `C:\dev` directory, a junk file with the command's output silently
missing. On Linux the stream names exist but name the *host* process's descriptors, not the command's.

One primitive: `PsBash.Core.DevicePath.Classify(path[, out fd])` (`src/PsBash.Transpiler/DevicePath.cs`)
returns a `DeviceKind`. No other product code holds a device-name literal (`DevicePathGuardTests`,
Core.Tests; its allow-list names the shell-script texts that are not device tests).

| Name | Kind | Read | Write |
|---|---|---|---|
| `/dev/null` (Windows: also `NUL`) | `Null` | empty | discarded |
| `/dev/stdin`, `/dev/fd/0` | `Stdin` | the command's stdin | "Permission denied" |
| `/dev/stdout`, `/dev/fd/1` | `Stdout` | — | the command's stdout, as `>&1` |
| `/dev/stderr`, `/dev/fd/2` | `Stderr` | — | the command's stderr, as `>&2` |
| `/dev/fd/N` (N ≥ 3) | `Fd` | the rest of descriptor N | descriptor N of `BashFdTable` |
| `/dev/full` | `Full` | NUL bytes | every write fails "No space left on device" |
| `/dev/zero` | `Zero` | endless NUL bytes | discarded |
| `/dev/random`, `/dev/urandom` | `Random` | endless random bytes | discarded |
| `/dev/tty` | `Tty` | the console (`CONIN$`) | the console (`CONOUT$`); "No such device or address" without one |

Rules:

- **Spellings.** The bash name, and on Windows what a path mapper turns it into before a site sees it:
  drive-rooted (`C:\dev\stdout`, `C:/dev/stdout`: `GetUnresolvedProviderPathFromPSPath`, `Path.GetFullPath`),
  UNC-rooted (`\\server\share\dev\stdout`), any letter case. A relative `dev/stdout` or `C:\work\dev\zero`
  is an ordinary file. A device name wins over a real file of that name directly under a drive's `dev`
  directory (after mapping the two cannot be told apart).
- **Inbound mapper.** `FileSystemHelpers.ProviderPath` returns a device name as written; it is never
  resolved against the current drive or PSDrive.
- **Choke points classify for themselves**, so a site that forgets cannot create a file:
  `RedirectFileWriter.OpenFile` / `OpenText` (every streaming write: redirect, `tee`, awk `print > f`,
  find `-fprint`, sed `w`, `sort -o`, uniq OUTPUT, `exec N> f`) and `BashFileSystem.OpenRead` (every read)
  open `DeviceStreams`, not a `FileStream`. `/dev/stdout` and `/dev/stderr` need the running cmdlet
  (the `owner` argument); without one the open throws `DeviceStreamException` (an `IOException`: the site
  reports a failed open, nothing is created). Guard: `DeviceChokePointTests` (Cmdlets.Tests) runs the
  cmdlets on a PSDrive rooted in a temp directory, with and without a `dev` folder holding files named
  like the devices, and asserts nothing in it is created, read or changed.
- **Stream names are dups.** `exec 3>/dev/stdout`, `{v}>/dev/stderr`, `exec 3>/dev/fd/7` are
  `exec 3>&1`, `{v}>&2`, `exec 3>&7` (`BashFdTable.Open`): a later `>&3` writes the stdout of the command
  that writes, so an enclosing pipe or redirect applies.
- **stdin is the cmdlet's pipeline input**, read by the cmdlet, not opened: a cmdlet treats the operand
  through `FileSystemHelpers.IsStdinOperand` (`-`, `/dev/stdin`, `/dev/fd/0`); the parser drops a literal
  `cmd < /dev/stdin` (`BashParser.AddRedirect`: the command keeps its pipe) and the emitter feeds a command
  with such an operand (`StdinReaders.ShouldFeed`). `BashFileSystem.OpenRead` refuses the name.
- **Off Windows** the real `/dev/null|zero|full|urandom|tty` are opened (`DevicePath.NativeDevice`), as
  bash opens them; the stream kinds are virtual on every OS.
- **Tests / stat.** `[ -e /dev/stdout ]`, `[ -c /dev/null ]`, `-r`, `-w` answer from the kind
  (`InvokeBashTestCommand`), with no filesystem lookup; `/dev/fd/N` exists while N is open.

Differences that remain (none of them touches the filesystem under a device name through a choke point;
oracle: `DeviceNameDifferentialTests`):

- `/dev/stdin` / `/dev/fd/0` as a file OPERAND is read only by `cat` and awk's `getline < "/dev/stdin"`.
  `head`, `tail`, `sort`, `uniq`, `grep`, `sed`, `cut`, `wc`, `tac`, `nl`, `paste`, `rev`, `base64`,
  `md5sum`, `cp`, `source` and awk's file operand report that it cannot be opened (use `-` or a pipe).
  `cmd < "$f"` with `f=/dev/stdin` (not a literal) fails the same way; the literal form keeps the pipe.
- `cp`, `touch`, `stat` and `source` do not accept a device operand (`cp f /dev/null`, `source /dev/null`);
  they stat the name as a path and report "No such file or directory". `dd` is not a ps-bash command.
- A file test whose operand is not a literal (`d=/dev/null; [ -e "$d" ]`) is lowered to `Test-Path` and is
  false; `test -e "$d"` and the literal `[ -e /dev/null ]` are answered from the device.
- A descriptor duplicated from stderr (`exec 3>/dev/stderr`, as `exec 3>&2`) writes the shell's stderr
  directly: an enclosing `2>&1 |` or `2> f` on a compound command does not capture `>&3`.
- An endless device read by a whole-input command (`sort /dev/zero`) never ends, as in bash; `od` asks
  for `-N`.
- `ls -l /dev/null` shows fixed mode bits; `/dev/tcp`, `/dev/udp` and `/dev/pts/*` are not devices here.

## PATH

`PATH` keeps the Windows form (`;` separators, native entries) and is not rewritten for display:

- Native programs inherit `PATH` and need `;`. A `:`-separated copy would collide with the drive colon
  (`C:/bin:C:/x` is ambiguous), so a two-way mapping is lossy.
- A script that writes `PATH=dir:$PATH` already works: the emitter normalizes the written value to `;`
  (`PsBuild.NormalizePathVariable`). Lookups (`type`, `command -v`, `which`) print each hit as
  `ToShell(entry)/file`.
- What stays different: a script that splits `$PATH` on `:` (`IFS=:; for d in $PATH`) sees Windows
  entries. Recommended follow-up, if needed: an opt-in that presents `PATH` as `C:/a;C:/b` (forward
  slashes, still `;`), which is lossless; not `:`.

## Differences that remain

- `/` is `C:/`, `/tmp` is `%TEMP%`: an absolute path a script prints starts with a drive letter, not `/`.
  Spec cases that compare an absolute path with a Linux literal carry `## XFAIL ps-bash platform-windows-paths`.
- A path a native program prints (`git rev-parse --show-toplevel`, `cmd /c cd`) is that program's output and
  is not rewritten.
