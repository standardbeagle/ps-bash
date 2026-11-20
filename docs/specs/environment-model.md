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
| `env NAME=v prog`, `env -u`, `env -i` | the command's own store, `ScalarOverlay.ForCommand`, attached to the runspace for the run (§11) |

## 4. The primitive

`ShellExports.RemoveUnexported(childEnvironment)` removes from a child's environment block every entry that is the
shell's own unexported variable (same name and value as the process environment; an entry the caller set for the
child is kept). `BashVariableStore.ApplyTo(ProcessStartInfo)` calls it, and `ChildStdio.Start` calls that, so every
child a cmdlet starts gets it: nested `sh` / `bash`, `timeout`, `awk system()`, `nohup` (Windows:
`ShellExports.WindowsEnvironmentBlock()` for `CreateProcessW`), a program whose stderr is merged (`Invoke-BashNative`).
`find -exec prog` and `env prog` start the program by name: through `ChildStdio` (filtered) only when
`InvokeBashNativeCommand.MergedProgramPath` answers, i.e. stderr is merged or the command runs in an in-process
background job; otherwise PowerShell starts it and §7 applies. Under `env` a program is always started through `ChildStdio`, from
the command's own store (§11).
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

`Core.Tests/ShellVariableStorageGuardTests`: one storage per variable (§8).

## 7. Remaining gap

**A program PowerShell itself starts inherits the whole process environment, unexported variables included.** That
is a plain `prog args` with no stderr merge, in any position (statement, `$( )`, pipeline stage, `&`, `exec prog`,
`env prog`, `command prog`, `xargs prog`, `find -exec prog`, `time prog`), and `.cmd` / `.bat`. PowerShell's
native-command processor copies the process environment and has no hook. Programs started with `2>&1` / `&>` / `|&`,
and every nested shell, are not affected. In an in-process background job none of this applies except `.cmd` /
`.bat`: the job runspace's lookup hook starts every other program through `Invoke-BashNative` and `ChildStdio`, from
the job's own store (`background-jobs.md` §3). A `.cmd` / `.bat` there gets the process environment, which is the
shell's current values, not the job's launch-time ones.

Removing the unexported variables from the process environment around the launch is not a fix: a thread-mode
background job reads `$env:NAME` from the same process while the program starts, and would see the variable
missing. The fix is to start those programs through `ChildStdio` as the merged ones already are
(`Invoke-BashNative`); the same change closes the tree-kill gap in `stdio-messaging.md` §4.6. Measured on Windows,
200 runs of a small program in a loop: 3.4-3.8 s started by PowerShell, 4.0 s through `Invoke-BashNative`.

Smaller differences:

- Under `set -a`, re-assigning a previously unexported variable the value it already has does not export it.
- `declare -x NAME=v` inside a function sets a global (declare with flags is not function-local yet).
- `export -f`: functions are not passed to a nested shell.

## 8. One storage per variable

**A bash scalar lives only in the shell variable store; an array lives only as a PowerShell variable. A name
resolves through ONE chain.**

| Kind | Storage | Scoping |
|---|---|---|
| scalar (`x=1`, `read x`, `for x`, `printf -v x`, `getopts`, `(( x = 1 ))`, `let`, `declare x`) | `BashVariableStore` (`$env:x`) | `local x`: the caller's value parked in `$__lsd_x` / `VarFrameStack`, restored on return (dynamic, as bash) |
| indexed / sparse / associative array | a PowerShell variable (`$a`: `object[]`, `BashSparseArray`, `BashAssoc`) | PowerShell scopes; `local -a` is the function's lexical variable |

The class this closes: a scalar held in BOTH (a PowerShell variable beside `$env:x`). PowerShell resolves a variable
through its own dynamic scope chain, which is not bash's, and readers disagreed on the order. The bug that found it:
`for q in one; do f; done` with `f() { local q; q=$(echo 9); echo $((q + 1)); }` printed 1. The loop bound `$q` as a
PowerShell variable too, and the arithmetic cmdlet read a PowerShell variable first, so the callee saw the caller's
loop variable instead of its own local. The same shape: `read q` in a function left a global `$q` that every later
`(( q > 0 ))` read (a recursion on `local q=$1` never ended), `read x; x=7; $((x))` read the stale copy, a callee's
`q=5` never reached a loop body that read `$q`, and `input=4; $((input + 1))` read PowerShell's `$input`.

Rules:

- **Run time: `PsBash.Cmdlets.BashVariableLookup`** is the one access by name. `Scalar` / `ArrayHolder` / `IsSet` read
  (a PowerShell variable answers only when it holds an array); `SetScalar` assigns (element 0 of an array the name
  holds, else the store); `SetArray` / `ReplaceArray` / `RemoveArray` write an array in the scope that owns it.
  Arithmetic, `read`, `printf -v`, `getopts`, `mapfile`, `[[ -v ]]`, `declare -p`, `unset`, `${!ref}` (psm1) use it.
- **An element named at run time: `PsBash.Cmdlets.BashArrayElement`.** A cmdlet that gets `NAME[subscript]` from an
  operand or a value (`printf -v 'm[$k]'`, `unset 'm[$k]'`, `x='a[i+1]'; $(( x = 1 ))`) resolves and writes it there,
  on top of `BashVariableLookup`. A `$(...)` in the subscript runs. An associative key keeps a single-quoted key
  literal, drops outer double quotes and expands `$k` / `${k}`; an empty key is a bad subscript. An indexed subscript
  is arithmetic, a negative one counts from the end, and a write past the end makes the array sparse. A scalar becomes
  element 0, and a new array is global. The bug class: three cmdlets each had a copy of these rules, and `printf -v`'s
  never expanded `$k`, so `printf -v 'm[$k]'` added a key named `$k` (oils `array-assoc`). Guard:
  `Core.Tests/ArrayElementGuardTests` (no other cmdlet holds the key-expansion regex). Behavior:
  `Differential.Tests/ArraySubscriptWordTests.RunTimeElementOperand_ResolvesLikeBash`.
- **PowerShell-owned names** (`PsBash.Core.PowerShellOwnedVariables`: `$input`, `$Host`, `$PID`, `$args`, …) are
  never a bash value and the emitter never names a PowerShell variable after one.
- **Emitter: a loop binds the shell variable.** `for` iterates a private `$__psbash_lvN` and sets the shell variable
  each pass (`PsBuild.SetShellScalar`: `[PsBash.Cmdlets.BashVariableStore]::Set`, a direct .NET call like the
  `[Environment]::SetEnvironmentVariable` it replaced, which a background job would have bypassed);
  `for ((;;))` runs every clause through `Invoke-BashArith` against the store; `while read` sets the store per line
  and, at end of input, to "" as bash's failing `read` does (a `break` leaves the values). `_storeLoopVars` only keeps
  the no-word-split choice and the job capture. Only a function's lexical locals (`_lexicalLocals`: local arrays,
  `IFS`) emit as `$name`.
- **Background job**: an in-process job's scalar storage is its own store, `ScalarOverlay`, copied at the `&` and
  keyed by the job's runspace (`background-jobs.md` §3). `BashVariableStore` resolves it first, and the job
  runspace's `$env:` drive is served from it, so a job never reads a value the shell wrote after its launch
  (`x=a; { sleep 1; echo $x; } & x=b` prints a) and nothing it writes reaches the shell. A loop variable is also
  handed over as `$name` by `Invoke-BashBackground -Vars`, for the body's PowerShell-variable reads.

Guards (`Core.Tests/ShellVariableStorageGuardTests`): no cmdlet reaches a PowerShell variable by a computed name
outside `BashVariableLookup` (internal `__Bash*` variables named through a constant are listed with a reason); no
psm1 function does (`Get-Variable -Name $n`); every binding construct, at top level and in a function, emits no
PowerShell variable for the bash scalar. Behavior: `Differential.Tests/VariableScopeChainTests` (loop kind × callee
access × shadowing × the loop variable after the call, against bash).

## 9. Shell-owned variables

**Every variable bash defines is in ONE table: `PsBash.Core.ShellVariables` (Transpiler assembly).** Two kinds:

- **Seeded** (`ShellVariables.Seeded`): bound when a shell starts, then an ordinary variable. `IFS` (`$' \t\n'`),
  `OPTIND`, `OPTERR`, `PS4`, `SHLVL` (inherited + 1, exported), `PPID` (the launcher's parent, handed over as
  `PSBASH_PPID` and removed), `BASH`, `BASH_VERSION`, `SHELL`, `HOSTNAME`, `HOSTTYPE`, `MACHTYPE`, `OSTYPE`,
  `COMP_WORDBREAKS`. `SdkWorker` calls `SeedShellStart()` after `ShellExports.Reset()` on every invocation (a stateful
  session: once), with bash's export attribute (`AlwaysUnexported` feeds `ShellExports.ShellOwned`).
- **Computed** (`ShellVariables.Computed`): worked out on each read. Scalars map in `PsBuild.TryMapSpecialVar`
  (`RANDOM SRANDOM SECONDS BASHPID BASH_SUBSHELL EPOCHREALTIME ...`), arrays in `PsBuild.ShellArrayExpr`
  (`GROUPS DIRSTACK BASH_ALIASES FUNCNAME BASH_LINENO BASH_SOURCE BASH_VERSINFO`; runtime `Cmdlets/ShellArrays.cs`);
  `${!ref}` reads both through the same two (psm1 `Get-BashIndirectValue`). `BASH_SUBSHELL` is the depth
  `BashShellState.Save` counts. A computed scalar can be a number, and 0 is false in PowerShell: a colon
  operator (`${SECONDS:+y}`) tests its text (`PsEmitter.IsNumericShellValue`, also `$? $# $$ $!`).

Not provided, with reasons: `ShellVariablesGuardTests.NotProvided` (`BASH_ARGC BASH_ARGV BASH_ARGV0 BASH_CMDS
BASH_COMMAND BASH_EXECUTION_STRING BASH_LOADABLES_PATH HISTCMD _`). Guard: `Core.Tests/ShellVariablesGuardTests`
(every bash name in exactly one place; every computed name mapped). Behavior, one name at a time:
`Differential.Tests/ShellLanguageGapDifferentialTests`.

## 10. Prompt counters (`\j`, `\#`, `\!`)

**Invariant: a prompt counter is the shell's count, read from the one place that keeps it.** `BashPromptText.Decode`
asks for `\j`, `\!`, `\#` by letter and never makes one up (they were fixed at 0, 1, 1).

- `\j`: the job table's size, psm1 `Get-BashJobCount` (`$script:BashBgJobs`). A job leaves the table when a builtin
  reports it (`jobs`, `wait`, `fg`); jobspecs resolve in one place (background-jobs.md §8).
- `\#`: bash's `current_command_number`, the top-level input lines read so far: `Cmdlets/ShellReader.cs`, per runspace.
  The root script reports each input line before it runs (`PsBuild.ReaderCommand`, placed by
  `PsEmitter.ReaderCommandFor` from the parser's line spans `BashParser.InputLinesOf`): `a; b` is one line, a multi-line
  `if` is one, an `eval`'s or sourced file's text is none. Under `bash -c` (`PSBASH_DASH_C`) it is 0, as in bash.
- `\!`: the history number. Under `set -o history` each reported line is also a history entry, joined as bash's
  `cmdhist` stores it (`Parser/CmdHist.cs`, also used by the piped `bash -i` replay); `\!` is the entry count (1 when
  history is off or empty), one past it at an interactive prompt (the next command is not in the list yet).
- An interactive shell (`InvokeBashShoptCommand.InteractiveShell`: the REPL, piped `bash -i`, `-i -c`) reports
  nothing: its front end reads the lines and records history (`history -s`), and the text it runs is not the input.
- Cost: the report is one script-to-.NET call per input line, so only a script that can observe it gets one
  (`PsEmitter.ReaderModeFor`: the text has `@P` or turns history on). Every other script's emission is unchanged.

Guards: `Core.Tests/Parser/PromptCounterEmitterTests` (one report per input line; none for other scripts),
`Cmdlets.Tests/BashPromptTextTests` (counters come from the shell). Behaviour: oils `prompt.spec.sh`.

## 11. A command's own environment (`env [-i] [-u NAME] [NAME=VALUE] cmd`)

**Invariant: building a child's environment never writes the process environment.** The foreground's scalars live in
the process environment, which every runspace of the host process shares (a concurrent command, an in-process job,
the host's own `TEMP` lookups, a parallel test in the cmdlet suite). `env -i cmd` used to clear it around the
command and put it back after: meanwhile every other runspace saw `TEMP`, `TMP` and `PATH` unset (a parallel test
resolved its temp directory to `C:\WINDOWS`).

`env` gives its command a store of its own, `ScalarOverlay.ForCommand(ignore, unset, assignments)`: the shell's
EXPORTED variables (none under `-i`), minus `-u`, plus `NAME=VALUE`, every one exported. It is attached to the
wrapper's runspace for the command only (`ScalarOverlay.AttachScoped`, which gives a job its own store back after),
so the command sees what bash's `env` hands a program: `BashVariableStore` / `ShellExports` read it (an in-process
`printenv`, `date` with `TZ`), and a program is started through `ChildStdio` from it (`InnerCommand.Run`,
`InvokeBashNativeCommand.MergedProgramPath`: `ScalarOverlay.Current` is set). Nothing the command writes reaches the
shell.

Limits, the same as an in-process job's (§7, `background-jobs.md` §3): a `.cmd` / `.bat` and PowerShell code reading
`$env:NAME` in the wrapper's runspace see the process environment; a program name is looked up on the host's `PATH`,
not the command's (`env -i ls` finds `ls` on Windows, where there is no `/bin` to fall back on; `env PATH=/x prog`
still finds `prog` on the host's `PATH`). A later stage of the same pipeline runs on the same runspace and sees the
command's store while the command writes to it.

Guards: `Core.Tests/BashVariableStoreGuardTests.OnlyShellAssignments_WriteTheVariableStore` (a named list of the
files that assign a bash variable, each with its assignment; a wrapper is not one) and
`NoCmdlet_SetsTheProcessEnvironment_ExceptTheStore`; `Cmdlets.Tests/InvokeBashEnvCommandTests.Env_IgnoreEnvironment_*`
(the process environment while `env -i`'s command runs, from the same and from another runspace);
`Core.Tests/ScalarOverlayTests.ACommandsEnvironment_*`.