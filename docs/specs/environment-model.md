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

### 8.1 Function-local declarations and attributes

**A local and everything `declare` attaches to it belong to the function's frame (`VarFrameStack`). The return puts
back what the local hid.**

- **Kinds.** `local -A m` / `local -a q`, and `declare` / `typeset` with those flags in a function, make a local of
  that kind, empty or with a value, through `PsBuild.DeclareLocalArray`. Converting one kind to the other is refused
  with `BUILTIN: q: cannot convert ...`, status 1, using the builtin word as invoked (`ShAssignment.Builtin`). A plain
  `declare x` with no value only marks the name declared; it is unset until assigned, and `declare -p` shows
  `declare -- x`. `declare -a e` makes the empty array. A function's `local x` with no value exists while unset:
  `declare -p x` shows `declare -- x` in it and in the functions it calls (`VarFrameStack.HasLocal`). Under nounset,
  `$x` for an array with no element 0 (`local -a x`, `a=(); a[1]=z`) is unbound.
- **Declarations in text built at run time** (`declare "$2"`, `builtin declare v=$1`, `$dc v=1`, `eval "$c"`, a
  sourced file) are emitted without their function, so the run-time call decides: each scalar a `declare` / `typeset`
  without `-g` names becomes the running function's local (`PsBuild.LocalWhenInFunction` over `PsBuild.RunTimeLocal`,
  the frame a run-time `local` takes: `$__lsd_NAME` + `VarFrameStack.DeclareLocal`), and stays global at the top level.
  An inner function's declaration is local to it (dynamic scope): such text looks for its park slot in its own
  PowerShell scope only (`PsBuild.NotParked`), since dynamic scope would show it the caller's. The quoted field a
  run-time call hands over (`declare 'v=1'`) is the written declaration `declare v='1'`
  (`PsEmitter.TryEmitQuotedStaticDeclaration`); with `-a`/`-A` a quoted `NAME=(…)` is the compound assignment
  (bash expands inside it), unless it would close the list early. `-g`, `readonly` and the print forms keep the
  declaration global. REGRESSION: `f(){ declare "$2"; }; f a e=b` left e set after f.
  Behavior: `Differential.Tests/RunTimeDeclareScopeDifferentialTests`.
- **Arrays and namerefs in such text** (`local -a "$1"`, `declare -A "$2"`, `eval 'declare -a a=(1)'`) are emitted in
  both forms, the run-time depth choosing (`PsBuild.WhenInFunction`, `PsEmitter.EmitRunTimeArrayLocals`): in a
  function the array is a PowerShell variable of the scope the text runs in, listed as its local array
  (`PsBuild.LocalArrays`) with its attributes put aside (`VarFrameStack.LocalAttributes`); at the top level the
  shell's global. A nameref (`declare -n "$3"`, `local -n r=x`) takes the run-time local's frame, which puts the
  caller's reference back. A static eval's text in a function is the function's own code: it parses as the body does
  (`BashParser.Parse(text, inFunctionBody: true)`, its `declare` local) and shares the body's attribute and nameref
  sets. REGRESSION: `f(){ local -a "$1"; }; f 'a=(1 2)'` left a global a. Behavior:
  `Differential.Tests/RunTimeArrayAttributeDifferentialTests`.
- **Attributes set by such text hold for the writes after it** (`eval 'declare -i n'; n=2*3` stores 6; `-u`/`-l`
  convert, `+=` too). The emitter cannot see them, so in a unit that runs run-time declaration text
  (`PsEmitter.RunsRunTimeDeclarations`: a command named at run time, `eval` of text known only then, `source`, a
  declaration builtin with an expanded name) a plain scalar write to a name with no static attribute goes through
  `PsBuild.AssignAttributed` → `BashShellState.AssignScalar` (the one attribute table: nameref target, `-u`/`-l`;
  an `-i` target is evaluated as arithmetic and stored by `AssignEvaluated`). Every other unit keeps the plain
  `$env:` write. Not covered: `for`/`read` into such a variable, `+i` removal, a run-time nameref's reads.
- **A callee's array write** reaches the nearest caller's local of that name, else the global (bash's dynamic
  scope). The function's prologue lists its local arrays (`PsBuild.LocalArrays` → `VarFrameStack.LocalArrays`), and
  every array write target goes through `PsBuild.ArraySlot` → `VarFrameStack.ArraySlot`. That covers element writes,
  `+=`, `mapfile`, `read -a`, `unset 'a[i]'` and `printf -v 'a[i]'`. As an argument the slot is wrapped by
  `PsBuild.TargetArg`.
- **Attributes** (`-i`, `-l`, `-u`, `-r`, `-x`, `-n`) of a local are its frame's. `VarFrameStack.TakeAttributes`
  saves the caller's attribute letters, its read-only state and its nameref target when a local of that name is
  made. `Exit` restores them: the attributes come back, a read-only mark the function added is removed, and the outer
  nameref returns. `-l` with `-u` cancels. `-g` keeps the attribute global. A plain `local g` over an outer nameref
  shadows it: writes go to the local, not the target.
- **Namerefs.** A literal target (`local -n r=t`) is recorded statically and at run time
  (`BashShellState.SetNameref`). A run-time target (`local -n c=$1`) is recorded by `VarFrameStack.LocalNameref` in
  whichever per-caller variant runs. Code that resolves names when it runs (the arithmetic cmdlet's reads and writes)
  follows `BashShellState.ResolveNameref`: a chain of up to 16 hops, stopping at a cycle; `c[1]` resolves the base.
  `local -n s=s` warns `circular name reference` with status 0, and an invalid target is refused with status 1.
  The name itself is the frame's, as for a plain local, so after `local +n r` the plain r (holding the target name)
  ends with the function.
- **A nameref with no target** (`declare -n r`, `local -n r`) is held in the run-time table with an empty target.
  `declare -p r` prints `declare -n r`, `declare -n` and `declare -p` list it, `set` does not, and `$r` reads as
  unset. The FIRST WRITE names the target, whatever writes it: `r=$v`, `r+=x`, `read r`, `printf -v r`,
  `getopts o r`, `local -n r; r=$1`. A value that names no variable (`5`, `a b`, empty) is "not a valid identifier":
  an assignment drops the rest of its line, read/printf -v fail with status 1, and the target stays unset. The rule
  lives in one place, `BashShellState.WriteTarget`, which every run-time writer goes through
  (`BashVariableLookup.AssignThroughNameref`, `BashArrayElement.Assign`, the arithmetic cmdlet). A literal `r=x`
  still names it at transpile time. After any other first write the emitter cannot know the target, and neither can
  it for `declare -n r=$v` / `local -n r=$1` (`PsBuild.DeclareNameref` → `BashVariableLookup.DeclareNameref`, with
  bash's "invalid variable name for name reference"; a function whose callers pass literal names still gets static
  variants, `PsEmitter.Namerefs.cs`, and this is their fallback). Such a name joins `_runtimeNamerefs`, its later
  writes go through `PsBuild.AssignThroughNameref`, and EVERY expansion reads the target through the run time, never
  r's own storage:
  - scalar forms (`$r`, `${r:-x}`, `${#r}`, `${r#p}`, `${r/a/b}`, `${r^^}`, `${r@Q}`, a slice) apply their operator
    to the one scalar read, `PsBuild.ReadThroughNameref`; `${r:=x}`/`${r=x}` assign through the reference
    (`AssignDefaultThroughNameref`) and `${r?}` names r, both by recognizing that read (`ReadThroughNamerefName`);
  - array forms (`${r[@]}`, `${r[1]}`, `${#r[@]}`, `${!r[@]}`, `"${r[@]}"` in a `for` list or `x=(...)`, slices,
    per-element operators, `r+=(z)`, `r[0]=A`) read and write `$r`, a PowerShell variable whose value follows the
    reference (`BashVariableLookup.ViewNameref`, made in the scope of the `declare -n`/`local -n`, or by the first
    write or `for` pass): the target's array, null for a scalar target, so the forms fall back to the scalar read
    (an array write builds on it too: `PsEmitter.ScalarValueOf`). The view is made DOT-SOURCED (`PsBuild.ViewNameref`):
    a PowerShell block that assigns `$r` (`r+=(z)` in a subshell or function) is otherwise compiled with a local slot,
    which holds a copy of the view's value and takes assignments itself;
  - `${!r}` is the name it stands for, at the end of its chain (`BashVariableLookup.NamerefName`; none: "invalid
    indirect expansion"); `${r@a}` is the target's attributes; `[[ -v r ]]` tests the target (`Invoke-BashTest`).
  Guard: `Core.Tests/Parser/NamerefRuntimeReadGuardTests` (no form of such a name reads `$env:r` or the store).
  `declare -p` lists r as a nameref, never as its target's array. A local one hides an outer reference of that name
  until the function returns. `declare +n r` leaves a declared, unset scalar (`declare -- r`). A plain `unset r` removes the reference
  itself, because there is no target to unset. Under nounset, `$r` / `${r}` / `${#r}` of such a nameref with no target,
  or with an unset one, is r's "unbound variable" (`PsEmitter.RuntimeNamerefRead`); the default forms are not. An array
  assignment `r=(...)` / `r+=(...)` while r has no target is not a first write naming it: bash warns "r: removing
  nameref attribute" and the array is r's own (`PsBuild.ArrayAssignToNameref` → `BashVariableLookup.DropTargetlessNameref`).
  A write through r to an element whose subscript is no expression (`a[1/0]`) is the subscript expansion error
  (`BashArrayElement.WriteThroughReference`; `bash -c` ends).
- **`for r in WORDS` over a nameref** (with a target or without) makes each word r's target in turn
  (`BashShellState.BindLoopNameref`); a word that names no variable ends the loop with status 1. The body and what
  follows read and write r through the run time, as above.
- **`unset -n NAME`** removes NAME's reference with its attributes, never the variable it names. On a name that is
  no nameref it changes nothing (bash 5.2: no variable, no function), except that a readonly NAME is still refused.
  The emitter and `Invoke-BashUnset` read the options through one parser, `UnsetOptions` (bash's `internal_getopt
  "fnv"`): only leading words, letters cluster (`-vn`), `--` and the first name end them. `-f` overrides `-n`
  (`unset -nf r` keeps the reference); `-f` with `-v` in any order unsets nothing ("cannot simultaneously unset a
  function and a variable", status 1). unset is on `OrderedArgCommands`, so `--` and `-v` reach the cmdlet in place.
  A plain `unset r` of a nameref whose target is known only at run time unsets that target (the cmdlet resolves it).
- **The per-shell tables** (readonly names, declare attributes, namerefs) are process-wide statics in
  `BashShellState`, not runspace state, so no PowerShell scope ends them. They are listed in one place,
  `BashShellState.ShellTables`. A subshell's `Save` copies each one and `Restore` puts it back, so
  `( declare -n r=y ); declare -p r` finds nothing. A new invocation empties each one (`ClearReadonly`). A new table
  goes into that list (guard `BashShellStateTests.EveryPerShellTable_IsSavedByTheSubshellSnapshot`). The emitter's
  compile-time copies (readonly names, namerefs, `-i`, `-l`/`-u`, `-A`) follow the same rule at every subshell form
  (`( )`, `$( )`, `<( )`, `cmd &`, a pipe stage): `PsEmitter.EnterSubshellTables` copies them and
  `LeaveSubshellTables` puts them back, so `( declare -i x ); x=1+1` stores `1+1`.
- **A subshell's arrays** are PowerShell variables, so they end with its block only when they are made in its scope.
  A subshell body's own `arr=(...)` is (`$arr` there). Every other write (`mapfile`, `read -a`, a write through a
  nameref: `BashVariableLookup.SetArray`/`ReplaceArray`; a function defined inside the subshell, which
  `PsEmitter._subshellFunctionDepth` routes through `VarFrameStack.ArraySlot` → `ArrayWriteSlot`) writes the
  nearest variable it sees, in place, and the subshell's journal (`BashVariableLookup.ArrayWriteJournal`, opened by
  `BashShellState.Save`) records that variable's value before its first write, or the global the write made.
  `Restore` undoes them newest first. So the subshell sees the write and its caller does not
  (`( mapfile -t arr <<< x )`, `( f() { arr=(x y); }; f; echo "${arr[*]}" )`). Every pipe stage, `$( )` and
  `while read` in a pipeline is such a subshell, so the lookup is one `PSVariable.Get`, never a scope walk or a
  script, and the emitter binds the slot once per write statement (`$__psbSlot`, `PsBuild.BindArraySlot`), not
  once for the target and again for the append base. Guard: `Core.Tests/ArrayWriteHotPathGuardTests`.
  REGRESSION (65cf7a7c): each such write ran a scope-walking script that threw at every intermediate scope.
- **Attributes read at run time.** `declare -i/-l/-u` record their letter in the one run-time table
  (`BashShellState.MarkAttribute`) only when the script reads attributes at run time (`declare -p`, `${v@a}`,
  `${v@A}`; `PsEmitter._declarePrintMode`). Elsewhere the emitter's compile-time tables are enough, and the common
  declaration emits no call. `-r` and `-x` are always recorded (`MarkReadonly`, `ShellExports`).

Known gaps:

- A callee's `n=n+1` to a caller's `declare -i` local is not evaluated as arithmetic. The integer attribute is known
  at compile time (`_intVars`) per function, not looked up when the callee runs.
- An array named after a PowerShell-owned variable (`input`, `args`, …) in a function goes global from a callee.
- A kind conversion with a value in the same function (`local -a a=(1); declare -A a=([k]=v)`) is not refused. bash
  refuses it with status 1 and keeps the indexed array; only the no-value form is guarded.
- Arrays live as PowerShell variables, which are case-insensitive on every OS, so `ta` and `tA` are one array
  (`intentional-differences.md`).

Behavior: `ShellLanguageGapDifferentialTests.FunctionLocal*`, `FunctionDeclare_*`, `FunctionNameref_*`,
`Declare_NoValueIsDeclaredButUnset`, `UnsetLocals_ExistAndUnsetArraysAreUnbound`, `UnsetNamerefTests`, `NamerefNoTargetTests`,
`NamerefRuntimeTargetTests`, `NamerefRuntimeReadTests`, `Subshell_ArrayWritesToAnInheritedArrayStayInIt`; options: `Core.Tests/Parser/UnsetOptionsTests`.

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

## 12. A reserved name in lower or mixed case (`path`, `tmp`, `Temp`, `os`)

**Invariant: a bash name maps to ONE store name, in one place, and every name that is printed is the bash name.**
On Windows the environment (and the export table, `NameComparer`) is case-insensitive, so `path=abc` was `PATH=abc`
(the next `git` was "command not found") and `tmp=x` sent temp files to `./x/`. `PsBash.Core.BashNameCase`
(Transpiler assembly) gives a non-upper-case spelling of a RESERVED name (a fixed list: Windows system variables and
the shell/tool variables that live in the environment) its own store name `__psbash_cv_<hexmask>_<UPPER>`, bit i of
the mask set when letter i is upper-case (`path` -> `__psbash_cv_0_PATH`, `Path` -> `__psbash_cv_1_PATH`).
`ToStore` is idempotent and `ToBash` reverses it. On only on Windows (`Enabled`; the `Override` seam for tests); a
fixed list so the emitted text does not depend on the machine. Ordinary names (`x` / `X`) stay one variable
(`intentional-differences.md`).

Where the mapping happens:

| Name source | Mapped by |
|---|---|
| Parsed as a variable (`$path`, `path=v`, `NAME=v cmd`, `for`/`select`, arithmetic names) | The AST record constructors (`WordPart.SimpleVarSub`, `BracedVarSub`, `Assignment`, `EnvPair`, `Command.ForIn`, `Command.Select`, `ArithmeticExpr.*`): the emitter only sees store names |
| Builtin operand text in the emitter (`declare`/`typeset` names, `readonly NAME=v`, `export NAME=v`, a nameref target) | `PsEmitter.StoreNameWord` / `BashNameCase.ToStore` at those sites; `export "$n=v"` maps at run time |
| A name handed to the runtime (`read`, `printf -v`, `getopts`, `unset`, `mapfile`, `${!ref}`, `[[ -v ]]`, arithmetic text, `env NAME=v`) | The by-name primitives themselves: `BashVariableStore`, `BashVariableLookup`, `ShellExports`, `ScalarOverlay.ForCommand`, `BashShellState` (readonly, attribute and nameref tables). Each takes a bash name or a store name; callers never map |

The emitter's own tables keyed by variable name (readonly names, namerefs, a function's locals, `-i`/`-l`/`-u`
attributes, env-prefix shadows) hold names from both sources: a record's store name (`__psbash_cv_0_TMP`) and word
text (`unset tmp`, `local -p path`, the target in `declare -n path=path`). They compare by store name
(`BashNameCase.StoreNameComparer`, the `PsEmitter.VarNameKeys` comparer every such table is built with), so neither
side has to remember to map. Text with an operator glued on (`path+=` in a run-time `export "path+=:b"`) maps only
after the operator is stripped.

Printing: listings (`declare -p`, `export -p`, `set`, `env`, `printenv`, `compgen -v`/`-e`, `${!prefix*}`, jq's
`$ENV`) show `ToBash(name)`; diagnostics are demangled at the writers (`BashParamErrorException`,
`FileSystemHelpers.WriteStderr`, `PsBuild.ShellDiagnosticLine` for a literal message, psm1 `Write-BashShellDiagnostic`)
with `ToBashText`, and so is every `set -x` line (psm1 `Write-BashXtrace`: `+ path=abc`). `ToBashText` rewrites any
whole-word store name in the text, so a store name the user typed into a message (`echo __psbash_cv_0_PATH >&2` through
a cmdlet's stderr) also prints as `path`: accepted, the prefix is ps-bash's own.

**Windows' own spellings (reference: Git Bash, MSYS2).** Windows creates some variables in a non-upper spelling
(`windir`, `Path`, `SystemRoot`, `ComSpec`, `ProgramFiles`, `SystemDrive`, `ProgramData`, `PSModulePath`). Git Bash
imports most of them UPPER-cased and leaves the native spelling unset (`$windir` is empty, `$WINDIR` is
`C:\Windows`; `env` lists `WINDIR=`, `PATH=`), which is what diverting the native spelling gives here. Two follow:

- Exempt spellings: Git Bash keeps `ProgramData` and `PSModulePath` under their native spelling (`$ProgramData` is set,
  `$PROGRAMDATA` is not). Those exact spellings are not diverted (`BashNameCase.IsExemptSpelling`) and read the system
  value; any other spelling of them (`programdata`) still is.
- Listing a native entry: an environment key that is a divertible spelling (`windir`, `Path`) IS the upper-case
  variable (the environment ignores case), so `BashVariableStore.Enumerate` / `EnumerateExported` return it
  UPPER-cased (`BashNameCase.FromEnvironment`; raw `Env:` listings use `ToListed`). `env`, `printenv`, `set`,
  `declare -p`/`-x`, `export -p`, `compgen -v`/`-e` and `${!prefix*}` show `WINDIR=...`, agreeing with `printenv windir`
  (unset). It also keeps a subshell's restore on the real variable: `( WINDIR=x )` restored through the raw key
  `windir`, which maps to the diverted name, and the change leaked out. A child's environment block
  (`ShellExports.Enumerate`) keeps Windows' own spelling.

A child: an EXPORTED mangled variable reaches a child under its store name (Windows cannot hand a child both `PATH`
and `path`); a nested ps-bash reads it back as `path`, another program does not see `path`. Not exported, it never
reaches a child (§1).

Guards: `Core.Tests/BashNameCaseTests` (`EveryAstRecordWithAVariableName_IsCovered`: a new AST record with a variable
name must map it; `EmitterVariableNameTables_CompareByStoreName`: every name-keyed emitter table is built with
`VarNameKeys`; the primitive and message tests); `Differential.Tests/ReservedNameCaseTests` (bash oracle; the
native-spelling cases are Windows-only facts checked against Git Bash).
