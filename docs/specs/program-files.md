# Program files: how a resolved file is started

A command word that names a FILE (a PATH hit, `./x`, `/abs/x`) is started the way the kernel would start it. One
primitive decides: `PsBash.Core.ProgramFile` (`src/PsBash.Transpiler/ProgramFile.cs`).

## The rule

`ProgramFile.Plan(path)` returns null when the OS starts the file as it is, else a `ProgramLaunch`:

| Platform | File | Launch |
|---|---|---|
| Windows | PATHEXT extension (`.exe`, `.cmd`, ...) or unreadable | null: the OS starts it (or fails as it always did) |
| Windows | `#!` line naming `sh`/`bash`/`dash`/`ash`/`ksh` (directly or through `env`) | ps-bash runs it, as `bash FILE` does (one nested launcher) |
| Windows | `#!` line naming another interpreter (`python3`, `node --flag`) | that interpreter by name, its options, the file, the arguments |
| Windows | no `#!` line, or `#!/usr/bin/env` alone | not executable: `bash: NAME: Permission denied`, 126 |
| Unix | any execute bit | null: the kernel handles `#!` |
| Unix | no execute bit | not executable: 126 |

A missing interpreter is 127 (bash: `cannot execute: required file not found`; GNU nohup: `No such file or directory`).
On Unix the kernel's exec fails with ENOENT, which PowerShell reports only as "Program failed to run" with status 1. So
`Plan` answers that case itself: `ProgramFile.ShebangInterpreterMissing` reads the first line, and when its first
word is an absolute path that is not a file, the result is `ProgramLaunch.MissingInterpreter`. Its stand-in prints
bash's message with 127, and its `CommandLine` has no program, so nohup reports 127.

## Invariant

**Every site that holds a resolved program file asks `ProgramFile.Plan` before starting it.** It never hands the file to
`CreateProcess` or invokes PowerShell's `ApplicationInfo` for it directly. Without the answer, Windows fails to start a
`#!` script ("not a valid Win32 application"), and PowerShell gives a resolved file to the shell's "open with"
association. That opens a dialog nobody answers and reports 0.

The launch has two renderings:
- `StandInScript(shown)` is PowerShell that runs in place of the resolved command, taking the arguments as ``, passed on as literal strings (`PsBuild.ArgvSplat`, see emitter-strategy §3.1), and
  forwarding pipeline input.
- `CommandLine(shellExe, dirs)` is the argv for a site that starts a process itself.

| Site | Uses |
|---|---|
| Host command-lookup hook (`GetVerbFallbackGuard`, a word PowerShell resolves) | `StandInScript` as the lookup's script block |
| `command NAME` (`InvokeBashCommandCommand`: `Get-Command` returns the raw `ApplicationInfo`, so the hook's stand-in never applied) | `StandInScript`; never `RunMerged` for such a file |
| `Invoke-BashNative`, and `MergedProgramPath` for `env` / `xargs` / `find -exec` / `time` / `flock` | `StandInScript`, or no merged `CreateProcess` (run by name; the hook answers) |
| `nohup` (`DetachedProcess.Spawn` takes a resolved path) | `CommandLine`: the ps-bash launcher for a shell, else the interpreter found on PATH |
| Interactive direct launch (`InteractiveShell.TryRunDirect`) | declines, so the normal path (and the hook) runs it |

Guard: `Core.Tests/ProgramFileGuardTests` checks that every product file naming `ApplicationInfo`,
`ExecutableLookup.Find/FindAll/IsExecutableFile` or `DetachedProcess.Spawn` calls `ProgramFile.Plan`, or is exempt with a
reason because it never starts the file (`type`, `hash`, `test -x`, completion). Behavior against bash:
`Differential.Tests/ScriptFileExecutionDifferentialTests`.

## Opening a script file (`ps-bash FILE`, `bash FILE`)

The launcher checks FILE through one primitive, `PsBash.Shell.ScriptFile`, on the run path and the `-n` path alike
(bash `open_shell_script`):

| FILE | Status | Message |
|---|---|---|
| missing | 127 | `ps-bash: FILE: No such file or directory` |
| a directory | 126 | `FILE: FILE: Is a directory` (bash names it by `$0`, already the script) |
| unreadable, or any other read failure | 126 | `ps-bash: FILE: Permission denied` / the error |
| a NUL before the first newline in the first 80 chars | 126 | `cannot execute binary file` |

Guard: `Shell.Tests/ScriptFileTests.Launcher_ChecksTheScriptFileOnlyThroughScriptFile`.

## Batch files

**A Windows `.cmd` / `.bat` (`npm`, `npx`, `yarn`, `pnpm`, `mvn`) is always started by ps-bash, through
`%SystemRoot%\System32\cmd.exe` with the command line `PsBash.Core.Runtime.Processes.BatchFile.Arguments` builds.**
A batch file runs in `cmd.exe`, which parses its command line again. Outside double quotes, `&`, `|`, `<` and `>` start
another command and `^` escapes, and `%NAME%` expands everywhere. Neither PowerShell's launcher nor C runtime quoting
(`CreateProcess`, `ProcessStartInfo.ArgumentList`) accounts for that (CVE-2024-24576, "BatBadBut"). Measured with
PowerShell 7.6 starting `tool.cmd`:

| bash word | what the batch file got |
|---|---|
| `'a&b'` | `a`, then `cmd` ran `b` |
| `'a\|b'` | output piped into `b` |
| `'a^b'` | `ab` |
| `'%PATH%'` | the expanded PATH |
| `''` | nothing (dropped) |

The escaping is the one Rust's standard library adopted for that CVE:

- An argument is double-quoted unless every character is an ASCII letter or digit or one of `#$*+-./:?@\_`. An empty
  argument, and one ending in `\`, is always quoted.
- Inside the quotes, a `"` is doubled, and so are the backslashes before it. A `%` gets `%cd:~,%` after it: that
  zero-length substring of `%cd%` expands to nothing and keeps a `%NAME%` pair from reading as a variable.
- An argument holding a line break or NUL is refused, because `cmd.exe` would end the command there. `Invoke-BashNative`
  reports it as `NAME: an argument with a line break cannot be passed to a batch file`, 126.
- `/d` skips the AutoRun registry commands, `/v:OFF` keeps `!NAME!` literal, and `/e:ON` is what makes `%cd:~,%` work.
  `cmd.exe` is the system directory's copy, never `%ComSpec%`, which a script can set.

So a script's `%1` is the word, quoted when it needed quotes, and a program the script hands `%*` (npm.cmd's
`node npm-cli.js %*`) gets each word exactly. A word with a `"` reaches such a program with C runtime meaning
(`""` inside quotes is one `"`).

Sites:

- The command-lookup hooks put `PsBuild.NativeProgramStandIn` in place of a batch file: the host's
  (`GetVerbFallbackGuard`), a background job's (`JobProgramLookup`), and an embedding's (`HostTerminalLookup`, once
  `ShellSecretsSession.Install` or `HostTerminalLookup.Install` ran).
- `Invoke-BashNative` starts a batch file itself whether or not its stderr is merged, and
  `InvokeBashNativeCommand.ProgramStartInfo` builds the `cmd.exe` line. That covers `RunMerged` and
  `MergedProgramPath` (`env`, `xargs`, `find -exec`, `time`), `command`, and the host terminal.
- `nohup`: `DetachedProcess` starts `cmd.exe` with the same line.

Because ps-bash starts it, a batch file now gets its environment from `BashVariableStore.ApplyTo`: a background job's
scalars and a session's granted secrets (`environment-model.md` §7, §13). A runspace with none of these hooks (a bare
SDK runspace, Pester's) that runs `& tool.cmd` directly still goes through PowerShell's launcher.

## Cost

A `#!` sh script costs one nested ps-bash launcher, a new process plus a host start, exactly like `bash FILE`. The spec
case `divergence.spec.sh` "Changing PATH will invalidate PATH cache" (six script runs under a 10 s budget) is XFAIL
`gap-nested-launcher-cost` for that reason; PATH lookup is not the cost. Running a script in-host would need a fresh
shell scope inside the host (own variables, only exported ones inherited, own `$0`/positionals, traps, options). No such
scope exists yet.
