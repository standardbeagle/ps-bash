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

## Cost

A `#!` sh script costs one nested ps-bash launcher, a new process plus a host start, exactly like `bash FILE`. The spec
case `divergence.spec.sh` "Changing PATH will invalidate PATH cache" (six script runs under a 10 s budget) is XFAIL
`gap-nested-launcher-cost` for that reason; PATH lookup is not the cost. Running a script in-host would need a fresh
shell scope inside the host (own variables, only exported ones inherited, own `$0`/positionals, traps, options). No such
scope exists yet.
