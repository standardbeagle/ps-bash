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
| `$PWD` | emitter `EmitCd`; `PsBuild.PopLocationRestoringProcessCwd` (subshell restore); `InvokeBashPushdCommand.SyncProcessWorkingDirectory` (pushd/popd, module-mode cd); launcher `Program.BuildInvocationCwdPreamble`; host startup (`Host/Program.cs`, interactive and stateful sessions); `Spec.Tests/InHostExecutor` | bash `$PWD` / `${PWD}` read `$env:PWD` (`PsBuild.TryMapSpecialVar`), not PowerShell's automatic `$PWD` (a native `PathInfo`). Native children inherit the same `PWD`. |
| `$OLDPWD` | `EmitCd`, `SyncProcessWorkingDirectory` | `cd -` reads it back through the inbound mapper. |
| `$HOME`, `~`, `~/x` | `SdkRunspaceSetup.ps1` (runspace creation), `SdkWorker.SyncHomeToEnvironment` (per request) | The PowerShell `$HOME` variable (ReadOnly, AllScope) holds the shell form; the FileSystem provider's Home stays native. A script's own `HOME=...` is kept as written. |
| `~+`, `~-` | emitter `TildeSub`, `BashParser.Simple` tilde-in-assignment | read `$env:PWD` / `$env:OLDPWD`. |
| `$0`, `BASH_SOURCE[0]` (script file) | launcher `Program.BuildPositionalPreamble` | `ps-bash sub\s.sh` → `sub/s.sh`; `-c CMD NAME` keeps NAME as given. |
| `BASH_SOURCE` (sourced file) | `InvokeBashSourceCommand` | the operand, through `ToShell`. |
| `$SHELL` default | `BashRuntime.DefaultShell` | |
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
