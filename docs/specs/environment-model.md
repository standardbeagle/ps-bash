# Environment model: shell variables, the export attribute, and a child's environment

Status: implemented for every child ps-bash starts itself; one gap remains (§7).

## 1. Rule

**A child process's environment is the shell's EXPORTED variables only, and one primitive builds it.**

`x=1; sh -c 'echo $x'` prints an empty line in bash. A variable is in a child's environment only when it was
inherited by the shell or exported (`export`, `declare -x`, `set -a`, `NAME=v cmd` for that one command).

## 2. Why this needs a table

Every scalar shell variable is a process environment variable of the host (`$env:NAME`, `BashVariableStore`), so
that the emitted PowerShell reads it without a lookup and a nested host inherits it. The process environment has no
attributes: a child handed that environment sees every variable. The export attribute is therefore kept beside it.

## 3. The table: `PsBash.Core.ShellExports` (Transpiler assembly)

A POSITIVE set of exported names. A name is exported when:

- it was in the process environment when the shell started (`Reset`: host startup, and each framed command after
  `SdkWorker` resets the environment to the launcher's), or it is `PWD` / `OLDPWD` / `HOME` (bash exports the first
  two itself; `HOME` is always inherited there). `OPTIND`, which the host seeds, is not;
- `export NAME`, `declare -x NAME`, `local -x NAME`, a run-time `export $n` marked it (`Export`);
- it was assigned while `set -a` was on (`AllExport`: a snapshot of the environment is taken when the option turns
  on, and every name whose value differs from the snapshot becomes exported when the table is next read).

The table tracks only inside a shell host (`Reset` turns it on). The module imported into a plain PowerShell has no
shell start: PowerShell code writes `$env:NAME` and means the environment, so there every set variable is exported
(`$env:COLUMNS = 40; Invoke-BashLs` keeps working, and the cmdlet and Pester suites run in that mode).

It stops being exported on `export -n` / `declare +x` (`Unexport`, the value stays) and on `unset` (`Forget`).

Because the set is positive, **an assignment site needs no hook**: `x=1`, `read x`, `for x`, `printf -v x`,
`getopts`, the emitter's own helper variables all write the environment and are unexported by default. A new way to
assign a variable cannot leak it.

Scopes:

| Bash | Mechanism |
|---|---|
| `NAME=v cmd` | `PsBuild.TempExportBegin` / `TempExportEnd` around the command (`BashVariableStore.TempExport`): exported for the command, the previous attribute restored after |
| `local NAME` | `VarFrameStack` records the hidden variable's attribute and restores it on return; the local keeps the outer attribute (bash: a local of an exported global is exported), `local -x` exports it |
| `( … )`, `$( … )` | `BashShellState.Save` / `Restore` carry `ShellExports.Save()` |
| `cmd &` in a job host (`PSBASH_BG_MODE=process`) | the job host inherits every variable; `BashStateSnapshot`'s prelude marks the unexported ones (`PsBuild.MarkUnexported`) |
| `env NAME=v prog`, `env -u`, `env -i` | `ShellExports.BeginTemp` / `EndTemp` around the run |

## 4. The primitive

`ShellExports.RemoveUnexported(childEnvironment)` removes from a child's environment block every entry that is the
shell's own unexported variable (same name and value as the process environment; an entry the caller set for the
child is kept). `BashVariableStore.ApplyTo(ProcessStartInfo)` calls it, and `ChildStdio.Start` calls that, so every
child a cmdlet starts gets it: nested `sh` / `bash`, `timeout`, `find -exec`, `awk system()`, `nohup` (Windows:
`ShellExports.WindowsEnvironmentBlock()` for `CreateProcessW`), a program whose stderr is merged (`Invoke-BashNative`).
The interactive shell's direct launch (`InteractiveShell.TryRunDirect`) and `CommandAssistProvider` call it too.

## 5. In-process views of the environment

A command that is an external program in bash runs in-process here and must see what that program would:

- `env`, `printenv` (status 1 for a name that is not exported), `compgen -e`, `declare -p` / `export -p` (`x` flag);
- `TZ` for `date` and `printf '%(…)T'` (`BashTimeZone`), `COLUMNS` / `TABSIZE` / `TIME_STYLE` (`ls`, `column`),
  `GREP_COLOR(S)`, `VERSION_CONTROL` / `SIMPLE_BACKUP_SUFFIX` (`cp`, `mv`, `ln`), `TAPE`, `_POSIX2_VERSION`, `DISPLAY`.

They read `BashVariableStore.GetExported` / `EnumerateExported`. Shell builtins and expansions (`IFS`, `OPTIND`,
`PS1`, `HISTFILE`, `LC_*` for the shell's own collation, …) read `Get` / `Enumerate`.

## 6. Guards

`Core.Tests/ChildEnvironmentGuardTests`:

- every product file that starts a process builds the child's environment through the primitive, or is in a named
  exemption list with its reason (launcher → host, the spawn mechanism, a forked job host);
- only the listed shell-view files call `BashVariableStore.Get` / `Enumerate`;
- the emitter writes the attribute only through `PsBuild`;
- direct tests of the table (a variable nobody exported is not in a child's environment, …).

`Differential.Tests/ExportedEnvironmentDifferentialTests`: 25 ways to set a variable × every way a child observes it,
against bash.

## 7. Remaining gap

**A program PowerShell itself starts inherits the whole process environment, unexported variables included.** That
is a plain `prog args` with no stderr merge, in any position (statement, `$( )`, pipeline stage, `&`, `exec prog`,
`env prog`, `command prog`, `xargs prog`, `find -exec prog`, `time prog`), and `.cmd` / `.bat`. PowerShell's
native-command processor copies the process environment and has no hook. Programs started with `2>&1` / `&>` / `|&`,
and every nested shell, are not affected.

Removing the unexported variables from the process environment around the launch is not a fix: a thread-mode
background job reads `$env:NAME` from the same process while the program starts, and would see the variable
missing. The fix is to start those programs through `ChildStdio` as the merged ones already are
(`Invoke-BashNative`); the same change closes the tree-kill gap in `stdio-messaging.md` §4.6. Measured on Windows,
200 runs of a small program in a loop: 3.4-3.8 s started by PowerShell, 4.0 s through `Invoke-BashNative`.

Smaller differences:

- Under `set -a`, re-assigning a previously unexported variable the value it already has does not export it.
- `declare -x NAME=v` inside a function sets a global (declare with flags is not function-local yet).
- `export -f`: functions are not passed to a nested shell.
