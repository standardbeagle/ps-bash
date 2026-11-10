# Plan: lowercase bash variables must not clobber Windows system variables

Status: **not started**. Written 2026-11-10 as a hand-off brief. Read `CLAUDE.md` and `CODE_MAP.md` first;
this file assumes their rules (tman only, oracle via `wsl.exe -d Ubuntu-24.04 -e bash`, PsBuild for emitted
text, PsEmitter.cs is Latin-1 + CRLF).

## The bug

Bash variables are stored as process environment variables (`BashVariableStore`). The Windows environment is
case-insensitive, so a bash variable whose name differs only in case from a system variable *is* that
system variable.

Observed on the dev build (Windows):

| Script | What happens | bash |
|---|---|---|
| `path=abc; git --version` | `git: command not found` (PATH is now `abc`) | git runs |
| `for path in a b; do :; done; ls` | same, PATH clobbered by the loop variable | fine |
| `tmp=zz; mktemp` / `temp=zz; mktemp` | file created under `./zz/…`; a directory named after the value appears in the cwd | under `/tmp` |

The `tmp=` case is also why spec case `>&word redirects stdout and stderr when word is not a number or -`
(`spec/oils/redirect.spec.sh`) fails with status 2 instead of 1, and it explains stray directories such as
`zz-36372.txt/` in the repo root.

Not yet reproduced, but the same mechanism and very common in real scripts: `home=`, `user=`, `os=$(uname)`,
`pwd=$(pwd)`, `shell=`, `hostname=$(hostname)`, `username=`, `lang=`, `term=`. **Confirm each on the dev
build before relying on it** (`tman dev -c '…'`).

Today this is documented only as a general limitation: `docs/specs/intentional-differences.md` line 37,
"Variable names differing only in case (`x=1; X=2`)". That row stays true for ordinary names; this plan
carves the reserved names out of it.

## Goal and non-goals

Goal: a bash variable named with a non-canonical casing of a **reserved** name is a separate variable, on
Windows only. `path=abc` leaves `PATH` alone; `echo $path` prints `abc`; `echo $PATH` is unchanged.

Non-goals:
- General case-sensitivity (`x` vs `X`). Lowercase names such as `http_proxy` / `no_proxy` must stay real
  environment names so child processes see them.
- Any behaviour change on Linux/macOS, where the environment is already case-sensitive.
- Faithful export of a mangled name to a child process. Windows cannot give a child both `PATH` and `path`.
  A mangled variable that is exported should be **left out** of the child's environment (decide and test
  this; do not leak the mangled name).

## Design

### 1. One pure mapping function

Add a small static class in **PsBash.Transpiler** (it is referenced by Cmdlets and Host), e.g.
`Transpiler/BashNameCase.cs`:

- `static string ToStore(string bashName)`: returns `bashName` unchanged unless the platform is Windows
  **and** `bashName` equals a reserved name ignoring case **and** is not exactly the reserved (upper-case)
  spelling. Then it returns the mangled name.
- `static string ToBash(string storeName)`: inverse; returns the input when it is not a mangled name.
- `static bool IsMangled(string storeName)`.
- A test seam to force the Windows behaviour on any OS (an `internal static bool? ForceWindows` or an
  overload taking `bool windows`), so the table tests run everywhere. No environment variable knob.

Mangled form must encode the casing, because the store is case-insensitive and `path`, `Path`, `pATH` are
three bash variables: `__psbash_cv_<hexmask>_<UPPERNAME>`, where bit *i* of the mask is set when character
*i* of the bash name is upper-case. Example: `path` → `__psbash_cv_0_PATH`, `Path` → `__psbash_cv_1_PATH`.
An all-upper-case name is never mangled.

Reserved set (fixed list, upper-case, compared ordinal-ignore-case). Start with:

- Windows: `PATH PATHEXT TMP TEMP COMSPEC SYSTEMROOT SYSTEMDRIVE WINDIR USERPROFILE APPDATA LOCALAPPDATA
  PROGRAMFILES PROGRAMDATA USERNAME USERDOMAIN COMPUTERNAME OS HOMEDRIVE HOMEPATH PSMODULEPATH`
- Shell: `HOME PWD OLDPWD IFS USER SHELL TERM LANG HOSTNAME UID RANDOM SECONDS LINENO OPTIND OPTARG REPLY
  PS1 PS2 PS4 BASH_VERSION`

Before finalising, check how each shell name is actually stored: several are mapped by
`PsBuild.TryMapSpecialVar` or held as PowerShell variables rather than environment entries, and a name that
never reaches the environment needs no entry here. Keep the list in one place with a comment per group.

Why a fixed list rather than "any inherited variable": the transpiler runs before the script, so it cannot
see what the script will assign, and a rule that depends on the caller's environment makes the emitted text
(and every golden) machine-dependent.

### 2. Apply it where names enter the AST (static names)

Do **not** edit the ~93 `$env:` sites in `PsEmitter.cs` (plus 18 in `PsBuild.cs`). Rewrite the name once,
in the parser, so every later stage sees the store name. Constructor sites counted on 2026-11-10 in
`BashParser*.cs` / `BashLexer.cs`:

| Node | Sites |
|---|---|
| `new Assignment(` | 4 |
| `new WordPart.SimpleVarSub(` | 3 |
| `new WordPart.BracedVarSub(` | 14 |
| `new Command.ForIn(` | 1 |
| `new EnvPair(` | 1 |

PsEmitter also builds a few of these itself (`Assignment` ×2, `SimpleVarSub` ×1, `BracedVarSub` ×2); check
whether those take a name that already went through the parser.

Also find and cover: arithmetic identifiers (`Ast/Arithmetic.cs`, `$((path+1))`, `((path++))`), array
names and subscripts (`path[0]=x`, `${path[@]}`), `select` variables, and function-local declarations.
Prefer a single helper called from each constructor site over a post-parse AST walk, unless a walk turns
out to be simpler to prove complete; either way add a test that enumerates every variable-bearing node type.

Check locals separately: `local path=x` is emitted as a PowerShell variable (`$path`), not `$env:`.
PowerShell variables are also case-insensitive, and the emitter already special-cases names PowerShell owns
(`_psBuiltins`, `IsDynamicLocalName`). Confirm a mangled name is a legal PowerShell variable name in both
the `$env:` and the local form, and that `local path` followed by a read of `$PATH` still reads the real PATH.

### 3. Apply it at run time (dynamic names)

Names that are only known at run time arrive as strings: `declare` / `typeset` / `local` / `export` /
`readonly` / `unset` / `read` / `mapfile` / `getopts` / `printf -v` arguments, `${!ref}` indirection
(`Get-BashIndirectValue` in `PsBash.psm1`), namerefs, `eval`.

- `eval` and `source` re-enter the transpiler, so they are covered by step 2.
- Everything else should go through `BashVariableStore` (`Get` / `Set` / `Enumerate` / `ApplyTo`); 33 files
  use it. Apply `ToStore` inside the store's by-name entry points so callers do not each need to know.
- Then grep for by-name access that bypasses the store: `Environment.GetEnvironmentVariable` /
  `SetEnvironmentVariable` in `src/PsBash.Cmdlets` (37 files mention them, many for real system variables
  that must **not** be mapped), `Env:` paths in `PsBash.psm1` (15), and the emitter's dynamic sites
  (`Get-Item Env:$n` style). Classify each as "bash variable name" (map) or "real system variable" (leave).

### 4. Demangle wherever names are printed or listed

`env`, `printenv`, `set`, `declare -p`, `export -p`, `compgen -v`, `${!prefix*}` / `${!prefix@}`, the
`declare -p` text (`DeclarePrinter.cs`), `x: unbound variable` and `${x:?msg}` messages
(`PsBuild.ParamErrorThrowStatements`, built at transpile time from the name), `set -x` traces, and
tab-completion of variable names. Each must show the bash name. `Enumerate` in the store is the natural
place for the listing commands; the error-message sites take the bash name at emit time, so the emitter
needs the original spelling there (keep it available, do not demangle text after the fact).

### 5. Child processes

Find where the child environment is built (`BashRuntime.RunChildProcess`, `Core/Runtime/Processes/JobProcess*.cs`,
`NativeStdinBridge`, background-job state copy `BashStateSnapshot.cs`). Drop entries where
`BashNameCase.IsMangled` is true from what a native child inherits; keep them in the state copied to a
ps-bash background host (that is the same shell, so the variable must survive `cmd &`).

## Tests (each layer, per `.claude/rules/testing.md`)

- **Pure table** for `ToStore` / `ToBash` round-trip with the Windows seam forced on: `path`, `Path`,
  `PATH`, `tmp`, `Temp`, `os`, an ordinary name, a name that merely contains a reserved name (`mypath`,
  `path2`), an already-mangled-looking name.
- **Transpiler** (Core.Tests): with the seam forced, `path=abc` does not emit `$env:path`; `PATH=x` still
  emits `$env:PATH`; `for path in …`, `${path:-x}`, `$((path+1))`, `path[0]=1`, `path=x cmd` (env prefix)
  all use the mangled name. With the seam off, output is byte-identical to today (goldens must not move on
  Linux).
- **Differential** (oracle = WSL bash), new class e.g. `ReservedNameCaseTests`:
  `path=abc; echo "$path"; command -v ls >/dev/null && echo ok`, the `for path in` form, `tmp=zz; echo $tmp;
  f=$(mktemp); [ -e "$f" ] && echo made; rm -f "$f"; [ -d zz ] || echo no-dir`, `os=x; echo $os`,
  `declare -p path`, `export path=x; env | grep -c '^path='`, `unset path; echo "[${path-unset}]"`,
  `read path <<< v; echo $path`, `ref=path; echo ${!ref}`, `printf -v path %s v; echo $path`,
  `set -u; { echo "$path"; } 2>&1` (message names `path`), `local path` inside a function with `$PATH` read
  after it. Record with `PSBASH_ORACLE_RECORD=1`, then `git checkout -- src/PsBash.Differential.Tests/Cassettes`
  and add the new cassettes. Stderr is compared: use `{ …; } 2>/dev/null` where the message prefix differs.
  Do not assert child-visible `env` output for a mangled name beyond the documented behaviour.
- **Spec**: unmark `>&word redirects stdout and stderr when word is not a number or -` if it now passes, and
  re-run the files that use `path` / `tmp` / `temp` as variable names (`git grep -n -E '\b(path|tmp|temp|home|user|os)=' -- spec/oils`).

## Documentation

- `docs/specs/intentional-differences.md` line 37: narrow the row to ordinary names and add a row for the
  reserved names (separate variable; not exported to native children).
- `docs/specs/runtime-functions.md` or the variable-store section: the mangling rule and the reserved list.
- `CODE_MAP.md` "Where to find X": one line for `BashNameCase`.

## Order of work and gates

1. `BashNameCase` + table tests. Commit-ready on its own.
2. Parser seam + transpiler tests; confirm goldens unchanged with the seam off.
3. Store seam + bypass audit (step 3) + listing/message demangle (step 4).
4. Child-process filter (step 5).
5. Differential class, spec unmarks, docs.

Gate before committing: `tman test-proj src/PsBash.Core.Tests`, the new Differential class plus full
`tman test-proj src/PsBash.Differential.Tests` and `src/PsBash.Cmdlets.Tests`, `tman pester`, and the full
spec suite. The spec suite does not finish in one run: use the slice loop in the session scratchpad
(`gate.ps1`: `PSBASH_SPEC_BUDGET_MIN=8`, `PSBASH_SPEC_RESULTS=<tsv>`, `PSBASH_SPEC_RESUME=1`, repeat until
every one of the 222 files has a non-SKIP line; a single "Passed 222" is mostly SKIP). `build2` (Linux,
idle) can run Differential/Cmdlets/Host from a git bundle while the spec loop runs locally. Commit locally;
do not push unless asked.

## State of the tree when this was written

- Last commit `f729b873` (local, not pushed). Its full gate was still running.
- Uncommitted and in that gate: the `chmod` command (`InvokeBashChmodCommand.cs` and its registration
  points, cmdlet + differential tests) and a transpiler crash fix in `FusedLane.IsFusablePipeline`
  (`[ a = b -a c |= ddd ]` threw NullReferenceException; regression test
  `Transpile_TestWithPipeInside_DoesNotCrash` written but not yet run). Land or confirm these first so this
  work starts from a clean tree.
- Known noise, not caused by this work: spec case `wait -n returns the status of the next job to finish`
  fails under suite load and passes standalone; `serialize` case `set` depends on the caller's environment.
- Unconfirmed: 13 Linux-only failures seen on `build2` (`LsSortTimeDifferentialTests` ×6, `InvokeBashKill*` ×4,
  `Find*` ×3). Run the previous commit there to learn whether they predate this session.

## Risks to check early

- **Hot path**: `ToStore` runs for every by-name store access. Make the common case (name not reserved)
  a length/first-character reject with no allocation.
- **`PATH` written as `Path`**: Windows' own spelling is `Path`. A script that writes `Path=…` meaning the
  system variable would be diverted. bash on Linux treats `Path` as a different variable, so diverting is
  the bash-correct result, but note it in the docs row.
- **Snapshot/restore code** that copies the whole environment (`BashStateSnapshot`, subshell save/restore,
  `WrapEnvPairs` shadow names with prefix `__psbash_ea_`) must treat mangled entries as ordinary entries.
- **Completion and `compgen -v`** listing raw environment names would show `__psbash_cv_…`.
- **Installed-release Bash tool**: verify with `tman dev`, never the Bash tool (it is the installed build).
