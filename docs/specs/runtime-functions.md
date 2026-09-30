# Runtime Functions Specification

This document describes the PowerShell runtime module (`PsBash.psm1`) that provides
Unix command emulation for the ps-bash transpiler.

This spec is split across three files:

- **runtime-functions.md** (this file) — runtime architecture, the BashObject model, arg-parsing patterns, escape handling, temp-file strategy, and how to add a new command.
- **[runtime-command-reference.md](./runtime-command-reference.md)** — the per-command flag / arg-parsing lookup table.
- **[runtime-migrated-cmdlets.md](./runtime-migrated-cmdlets.md)** — the REFACTOR-2 binary-cmdlet migration history.

## Architecture

The psm1 module is loaded into the `ps-bash-host` runspace managed by `SdkWorker`.
Bash commands transpiled by `PsEmitter` into PowerShell are evaluated via
`Invoke-Expression` inside this worker. The module provides `Invoke-Bash*`
functions that emulate Unix commands, and registers global aliases (e.g. `ls` ->
`Invoke-BashLs`) so transpiled code reads naturally.

Key layers:

1. **PsEmitter** (C#) -- transpiles bash AST nodes into PowerShell expressions.
2. **SdkWorker** (C#) -- owns the PowerShell runspace, imports the module, and
   evaluates transpiled expressions on behalf of the host server.
3. **PsBash.Cmdlets.dll** (C#) -- **where the commands now live**: ~100 binary
   `Invoke-Bash*` cmdlets, the compiled line-stream cores behind the fused lane,
   `Format-Styled` / `Show-Styled`, and the shared `BashRuntime` helpers.
4. **PsBash.psm1** (PowerShell) -- what did **not** migrate: the BashObject model,
   escape handling, glob expansion, tab-completion registration, the jq/YAML engines,
   `ls` formatting, the `browse` adapter registry, and the job-control commands.

### Where a command actually lives (REFACTOR-2 is essentially complete)

The psm1 is no longer the bulk of the runtime. Of its 88 top-level functions, only
**six** are leaf `Invoke-Bash*` commands; every other emulated command is a binary
cmdlet in `PsBash.Cmdlets.dll`.

| Still a psm1 function | Why it did not migrate |
|---|---|
| `Invoke-BashBackground`, `Invoke-BashWait`, `Invoke-BashJobs`, `Invoke-BashFg`, `Invoke-BashBg` | Job control owns runspace-pool state (`Get-BashBgRunspacePool`, `$script:` job table) that a stateless cmdlet cannot hold. |
| `Invoke-BashSed` | **A proxy, not the implementation.** `InvokeBashSedCommand` is the real sed; the psm1 function exists only to bundle repeated `-e A -e B` into one call, because the binder rejects a repeated array parameter *before* the cmdlet body runs. It has no `[CmdletBinding()]` on purpose — common parameters would prefix-match `-e` and defeat it. |

A handful of cmdlets still call **back into** psm1 helpers via parameter-bound
`InvokeScript` (`jq`/`yq` reuse the psm1 jq filter + YAML engines; `alias`/`trap` reach
`$script:`-scoped state; `browse` uses the psm1 adapter registry). Those are delegation
seams, not psm1 implementations of the command.

Consequence for the tables that follow: **every row in
[runtime-command-reference.md](./runtime-command-reference.md) is a binary cmdlet
unless the row says otherwise.** An "Arg Parsing" cell reading `Manual loop` /
`Positional` describes the *strategy* inside the cmdlet, not a psm1 function.

### Alias Architecture (Two-Tier)

`alias`/`unalias` have different implementations depending on the execution context:

- **Module mode** (`Import-Module PsBash`): `Invoke-BashAlias` stores alias definitions
  in `$script:BashUserAliases` and creates dynamic PowerShell functions via
  `Set-Item -Path "Function:\$name"`. Simple aliases like `alias ll='ls -la'` work
  because the generated function body (`ls -la $args`) calls the module's own aliases.
  Complex bash syntax (pipes, redirections) in alias values will not work.
- **Shell mode** (`ps-bash` interactive): alias management is handled entirely in C#
  by `InteractiveShell`. The shell maintains an alias dictionary, intercepts
  `alias`/`unalias` commands before transpilation, and expands the first word of each
  input line against the dictionary. Full bash syntax in alias values is supported
  because expansion happens before the transpiler sees the input.

## BashObject Model

All command output flows through a uniform object model so that pipeline composition
works correctly.

### Core Properties

Every output object carries a `BashText` property containing the string representation
that downstream commands consume. Objects have `PSTypeName = 'PsBash.TextOutput'`
(or a command-specific type like `PsBash.CatLine`, `PsBash.WcResult`).

### Key Functions

| Function | Purpose |
|---|---|
| `Emit-BashLine -Text $s` | **Primary output function.** Splits text on `\n` and emits one `BashObject` per line. Matches bash semantics where `\n` is a record boundary. Use for stdout-like text output (printf, echo -e, heredocs). |
| `New-BashObject -BashText $s` | Creates a single `PSCustomObject` with `BashText`. Does NOT split. Use for typed/structured objects (LsEntry, CatLine, PsEntry) that are inherently single-line. |
| `Set-BashDisplayProperty $obj` | Adds a `ToString()` ScriptMethod returning `$this.BashText` |
| `Get-BashText -InputObject $obj` | Extracts the string from any pipeline object: returns `.BashText` if present, otherwise stringifies via `"$obj"` |

### Shared C# Helpers (REFACTOR-2 Phase 2)

The arg-parsing and BashObject helpers every leaf `Invoke-Bash*` function
depends on are implemented as AOT-safe static methods on
`PsBash.Cmdlets.BashRuntime` (`src/PsBash.Cmdlets/BashRuntime.cs`). A migrated
binary cmdlet calls them directly with no C#→PowerShell callback. The psm1
functions `New-BashObject`, `Emit-BashLine`, `Set-BashDisplayProperty`,
`Get-BashText`, `ConvertFrom-BashArgs`, `New-FlagDefs`, and
`Expand-EscapeSequences` are now thin wrappers that delegate to
`BashRuntime`, so the script-callable surface is unchanged and the
differential suite proves the C# implementations against the live runtime.
`Write-BashError` and `Resolve-BashGlob` remain pure psm1 functions: both need
runspace/script scope (`$script:BashErrorMode`, the PowerShell path provider)
that a plain static helper cannot reach — only `BashRuntime.FormatBashError`
(the runspace-free message-formatting piece) is shared.

**Binary cmdlets do not call `Write-BashError`.** They report through
`FileSystemHelpers.WriteBashError`, which emits exactly ONE `ErrorRecord` on the
cmdlet's error stream (and sets `$LASTEXITCODE`). PowerShell redirection then
decides its fate — `2>/dev/null` discards it, `2>&1` merges it into stdout as its
one-line message — and the host (`SdkWorker`) streams the surviving records to
stderr **inline, in order with the surrounding stdout** (via `Streams.Error`
`DataAdded`), not after the run. Calling `Write-BashError` as well used to print
every diagnostic twice and made it immune to `2>/dev/null`, because Bash mode
writes through `$Host.UI.WriteErrorLine`. `Write-BashError` remains only for the
psm1 helpers (`Get-BashItem`, `Read-BashFile*`) and the job-control functions.

### Migrated Binary Cmdlets

Leaf `Invoke-Bash*` commands are progressively migrated from psm1 functions to
binary cmdlets in `PsBash.Cmdlets.dll`. The full per-command migration table
(phase, flag surface, colliding-flag handling, security probes) and the
awk / jq / find / echo / ls / sed migration decisions are in
**[Migrated Binary Cmdlets](./runtime-migrated-cmdlets.md)**.

### Output Strategy

```
Source produces text with \n  →  Emit-BashLine  →  one BashObject per line in pipeline
Source produces typed object  →  New-BashObject  →  one typed object in pipeline
Consumer receives items      →  pass through original objects (preserves type)
```

### Example

```powershell
# Text output — use Emit-BashLine (splits on \n)
Emit-BashLine -Text "line1`nline2`n"
# Emits TWO objects: BashText="line1\n" and BashText="line2\n"

# Typed output — use New-BashObject (single object)
$obj = New-BashObject -BashText "hello world`n"
# Emits ONE object with BashText="hello world\n"
```

## Pipeline Object Preservation

Consumer commands (grep, sed, tail, awk, sort, etc.) should **pass original objects
through** the pipeline, NOT create new BashObjects. This preserves typed properties
(e.g., `LsEntry.Name`, `CatLine.Content`) through pipe chains like `ls | grep .txt`.

Sources are responsible for emitting one object per line using `Emit-BashLine`. This
matches bash semantics where stdout is a byte stream and `\n` is the record separator.
The "pipe" (PowerShell pipeline) delivers individual line-objects to consumers.

**Defensive split for edge cases:** If a consumer receives a multi-line BashText item
(from an external source or legacy code), it should split only that item while passing
single-line items through unchanged:

```powershell
foreach ($item in $pipelineInput) {
    $text = Get-BashText -InputObject $item
    if ($text -match "`n" -and $text -ne "`n") {
        # Multi-line edge case: split into new BashObjects
        foreach ($subLine in ($text -replace "`n$",'' -split "`n")) {
            # process $subLine
        }
    } else {
        # Single-line: pass original $item (preserves LsEntry, CatLine, etc.)
    }
}
```

**DO NOT** unconditionally flatten all input into `$allLines` — this destroys typed objects.

## Command Reference

The full per-command table — `Invoke-Bash*` function, key flags, arg-parsing
strategy, and pipeline / file support — is in
**[Command Reference](./runtime-command-reference.md)**.

## Arg Parsing Pattern

All `Invoke-Bash*` functions follow one of two arg-parsing strategies.

### Strategy 1: ConvertFrom-BashArgs (simple boolean flags)

Used when all flags are simple on/off switches with no value arguments.

```powershell
function Invoke-BashFoo {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $defs = New-FlagDefs -Entries @(
        '-a', 'description of -a'
        '-b', 'description of -b'
    )
    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $flagA = $parsed.Flags['-a']   # $true / $false
    $operands = $parsed.Operands   # List[string]
}
```

`ConvertFrom-BashArgs` handles `--` (end of flags), bundled short flags (`-ab`), and
collects non-flag arguments into `Operands`.

### Strategy 2: Manual while loop (value-bearing flags)

Used when flags take a value argument (e.g. `-n 10`, `-F,`, `-A2`).

```powershell
function Invoke-BashBar {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $operands = [System.Collections.Generic.List[string]]::new()
    $someValue = $null

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-n') {
            $i++
            if ($i -lt $Arguments.Count) { $someValue = $Arguments[$i] }
            $i++; continue
        }
        $operands.Add($arg)
        $i++
    }
}
```

Both strategies support `--` to end flag parsing. Value flags often support joined form
(e.g. `-n10` as well as `-n 10`).

### Pipeline vs File Mode

Commands that accept both pipeline and file input follow this pattern:

```powershell
# Pipeline mode: no file operands, pipeline has data
if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
    # process $pipelineInput via Get-BashText
    return
}

# File mode: operands are file paths, resolved via Resolve-BashGlob
foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
    # read file, process lines
}
```

`Resolve-BashGlob` expands `*` and `?` patterns and resolves relative paths against
PowerShell's `$PWD`.

## Shared argument parser (`PsBash.Cmdlets.Args`)

The ordered getopt-style parser that replaces per-cmdlet hand scans. Pure and AOT-safe (no
`PSCmdlet`/`SessionState`/reflection), so it is unit-tested in isolation
(`ArgParserTests`). **Migrated so far: `tee`, `cp`, `mv`, `rm`, `mkdir`, `rmdir`, `ln`, `touch`.** `BashRuntime.ConvertFromBashArgs` is
untouched — its contract (unknown flag becomes an operand) differs.

**API** (`src/PsBash.Cmdlets/Args/`):

- `OptSpec(Id, Short, Long, Kind)` with `OptKind { Flag, Value, OptionalValue }`; several specs
  may share an `Id` (`-r`/`-R` = "recursive"). `Short` is `'\0'` for none; `Long` has no `--`.
- `OptSpecSet` — built ONCE as `static readonly`: the specs, `validButUnsupported` names (as typed:
  `"-i"`, `"--interactive"`), `allowAbbrev` (getopt_long unique-prefix long options; an ambiguous
  prefix is an error listing candidates), `numericShorthandId` (`head -5`), `gnuInfoOptions`
  (`--help`/`--version` join abbreviation, so `--ver` is ambiguous with `--verbose`).
- `ArgParser.Parse(ReadOnlySpan<string> argv, OptSpecSet)` → `ParsedArgs`: `Tokens` in ORIGINAL
  order (`ArgTokKind` Operand/Option/DoubleDash; a bundle `-abc` is one token per letter), the
  first `Error` (`Unrecognized`, `ValidButUnsupported`, `MissingValue`, `Ambiguous`,
  `UnexpectedValue`), and `Has/Last/All/Operands()`. Scanning stops at the first error.
- Errors are classified DURING the scan, so nothing after `--` is ever an option or an error;
  a lone `-` is an operand; a value option ends its bundle (`-abn5`) and, like getopt, takes the
  next element even if it starts with `-`; options may follow operands (GNU permutation).
- `ArgError.Message(cmd)` holds the GNU wording; `FileSystemHelpers.TryWriteParseError` is the
  cmdlet-side sink (message + `ParsedArgs.ErrorExitCode`).
  **Exit status is per command**: `OptSpecSet(usageExitCode:)` (default **1** — GNU coreutils
  `EXIT_FAILURE` for tee/cp/mv/rm/mkdir/rmdir/ln/touch; set 2 for ls/grep/diff-style tools) covers
  unknown option, missing argument, ambiguous prefix and `--flag=x` on a flag. A valid-but-unsupported
  option (ps-bash's own refusal of a real GNU flag) is always **2** (`ArgError.UnsupportedExitCode`),
  so scripts can tell "ps-bash cannot do this" from "you typed it wrong". `TryHandleInfoOptions` acts on an abbreviated
  `--vers`/`--he`.

**Emitter opt-in.** `PsEmitter.OrderedArgCommands` (tee, cp, mv, rm, mkdir, rmdir, ln, touch, plus the command-running wrappers xargs, time, env — these keep their manual scans, which stop at the first operand, and only take the emitter quoting so the INNER command's flags are safe): for these, `EmitPassthrough`
single-quotes EVERY dash-leading literal word and `--` (via `PsBuild.SingleQuote`; quoted and mixed
words like `--x="a b"` collapse to one literal). No flag is then a PowerShell parameter token, so
each reaches `[ValueFromRemainingArguments] Arguments` verbatim and in order — no prefix collision
(`-i`/`-e`/`-p`), no binder-swallowed `--`, no decoy losing position. Dynamic words (`-$x`) keep the
normal path; the RC-7 unquoted-variable splat path shares the same arg renderer. Add a command to
the set only AFTER its cmdlet is migrated, and add it to
`CommonParameterCollisionGuardTests.EmitterForceQuoted` (all colliding letters).

**Decoy caveat (direct calls).** The cmdlets keep their single-letter decoy switches
(`Invoke-BashTee -a f`, `Invoke-BashCp -v a b` — Pester and interactive PowerShell bind them, the
transpiler never does). Re-inject them with `BashRuntime.PrependDecoys(Arguments, ...)` BEFORE
`Parse`; prepending is safe because a decoy can only bind ahead of any `--`. A direct call with a
colliding bare letter that has NO decoy (`Invoke-BashTee -i`) still fails in the binder — quote it.

**Migrating a command:**

1. Declare `static readonly string[]` valid-but-unsupported names (a `string[]` field keeps the
   collision guard's classifier scan working) and a `static readonly OptSpecSet`
   (`allowAbbrev: true, gnuInfoOptions: true` for GNU tools).
2. Add `internal static ParsedArgs ScanArgs(string[] args)` as the test seam.
3. In the cmdlet: `PrependDecoys` → keep the exact `--help`/`--version` early exits →
   `ScanArgs` → `TryWriteParseError` → `TryHandleInfoOptions` → read `Has/Last/Operands`.
   A value-bearing decoy (`touch`'s `D` for `-d`) is re-injected as the two elements `"-d", value`.
4. Before deleting the old scan, diff old vs new over an argv corpus (bundles, `--`, long forms,
   abbreviations, unknown and unsupported flags, lone `-`, dash operands after `--`) and check
   every divergence against GNU (`wsl bash`); pin the result as an expected-value table
   (`TeeArgScanTests`, `CpArgScanTests`, `MvArgScanTests`, `RmArgScanTests`, `MkdirArgScanTests`,
   `RmdirArgScanTests`, `LnArgScanTests`, `TouchArgScanTests`). Check the exit status too: set
   `usageExitCode:` only for tools whose GNU usage status is not 1. **Oracle-check the option
   TABLE itself** — batch 2 found ln and touch had no classifier at all (flags became link targets
   / file names) and that rmdir's "unsupported" `-Z`/`--context` do not exist in GNU 9.4.
5. Add the command to `OrderedArgCommands` and the guard map; add emitter tests.

## Escape Sequence Handling

ONE left-to-right scanner, `BashEscapes.Expand(text, EscapeDialect[, out stopped])`
(`src/PsBash.Cmdlets/BashEscapes.cs`), serves every builtin; the psm1
`Expand-EscapeSequences` / `BashRuntime.ExpandEscapeSequences` are the Echo-dialect wrapper.
The dialects differ exactly where bash's builtins do (oracle-checked, bash 5.2):

| Dialect (used by) | Octal | Also | `\c` |
|---|---|---|---|
| `Echo` (`echo -e`) | `\0NNN` only (0 + up to 3 digits); `\101` stays literal | `\xHH \uHHHH \UHHHHHHHH \e \E`; `\"` stays literal | stops ALL output incl. the newline |
| `PrintfB` (`printf %b` arg) | `\0NNN` and `\NNN` | as Echo | stops all output, including the rest of the format |
| `PrintfFormat` (printf format) | `\NNN` = 1-3 digits INCLUDING the first (`\0101` = `\010` + `1`) | `\xHH \u \U \e \" \' \?` | literal (not special) |
| `Tr` (tr SETs) | `\NNN` 1-3 digits | single-char escapes only; no `\x`/`\e` | n/a |

All dialects: `\\ \a \b \f \n \r \t \v`; an unknown escape keeps its backslash. `\0` yields a
real NUL char, which survives pipes, `tee` and `>` (`printf 'x\0' > f` is 2 bytes). Values above
`\177` become the corresponding Unicode char (not a raw byte) — known gap. `$'…'` is expanded by the
emitter's own `ExpandAnsiCEscapes` (Transpiler cannot reference Cmdlets); it truncates the word at
the first NUL, as bash's C strings do.

`printf %b` reads the RAW argument text (not the int/double coercion used by `%d`), so `\0101`
keeps its leading zero.

## Temp File Strategy

All temp files are written under a `ps-bash/` subdirectory of the system temp path
(`[System.IO.Path]::GetTempPath()`).

### ModuleExtractor

Path: `ps-bash/module-{version}/`

Extracts the embedded module files (`PsBash.psd1`, `PsBash.psm1`,
`PsBash.Format.ps1xml`) from the assembly's manifest resources into a version-stamped
directory. A `.extracted` marker file signals that extraction completed successfully.

Cache invalidation: if the assembly file's `LastWriteTimeUtc` is newer than the
marker's timestamp, the marker is deleted and files are re-extracted on next access.

### Host Worker

Path: `ps-bash/module-{version}/`

Uses the same version-stamped directory as `ModuleExtractor`. The host process
loads the extracted module into its runspace before executing transpiled scripts.

### Invoke-ProcessSub

Path: `ps-bash/proc-sub/{random-filename}`

Used for process substitution (`<(command)`). Creates a temp file with a random name,
writes the scriptblock's output to it, and returns the path. On error, the temp file
is cleaned up. On success, the caller is responsible for cleanup.

## Adding a New Command

> **Default: write a binary cmdlet**, not a psm1 function — `src/PsBash.Cmdlets/InvokeBash{Name}Command.cs`,
> using the `BashRuntime` helpers below. Reach for a psm1 function only when the command
> needs runspace/script scope a static cmdlet cannot hold (the job-control case above).
> Either way you still add the emitter switch case (`PsEmitter.TryEmitMappedCommand`),
> the `Set-Alias` in the psm1, the `BashFlagSpecs.json` entry, and the reference-table row.
> Check the colliding-flag rules in
> [runtime-migrated-cmdlets.md](./runtime-migrated-cmdlets.md) **before** picking flag
> letters — a bare `-e`/`-i`/`-o`/`-p`/`-w` hard-crashes the binder and `-c`/`-d`/`-v` is
> silently swallowed.

The psm1-function recipe (for the rare case that is the right answer):

1. **Define the function** following the naming convention `Invoke-Bash{Name}`.

2. **Choose an arg parsing strategy**:
   - Simple boolean flags: use `ConvertFrom-BashArgs` with `New-FlagDefs`.
   - Value-bearing flags: use the manual while loop pattern.

3. **Collect pipeline input** at the top of the function:
   ```powershell
   $Arguments = [string[]]$args
   $pipelineInput = @($input)
   ```

4. **Preserve pipeline objects**: when processing pipeline input, pass original
   objects through (preserving typed properties like LsEntry.Name). Use the
   defensive split pattern for multi-line edge cases (see Pipeline Object Preservation).

5. **Support file mode** if applicable: use `Resolve-BashGlob` on operands, read files
   with BOM-aware UTF-8 decoding, normalize `\r\n` to `\n`.

6. **Emit output** using the right function:
   - `Emit-BashLine -Text $s` for text output (splits on `\n`, one object per line)
   - `New-BashObject` for typed objects (LsEntry, CatLine — single-line, preserves type)

7. **Register the alias** at the bottom of the module:
   ```powershell
   Set-Alias -Name 'foo' -Value 'Invoke-BashFoo' -Force -Scope Global -Option AllScope
   ```

8. **Add help and completion metadata** to `$script:BashHelpSpecs` and
   `$script:BashFlagSpecs`.

## `install` Command

`Invoke-BashInstall` copies files and sets attributes, with special handling for
in-use binaries on Windows.

### Supported flags

| Flag | Description |
|------|-------------|
| `-d` | Create directories |
| `-D` | Create leading path components |
| `-m` | Set mode (tracked, not enforced on Windows) |
| `-v` | Verbose output |
| `-s` | Strip (no-op on Windows) |
| `-t` | Target directory |
| `-S` | Swap suffix (default: `.old`) |

### Windows binary swap

When the destination file already exists and is locked:

1. Move existing file to `{dest}{suffix}`
2. Copy new file to destination
3. Schedule deferred deletion of the old file (via `MoveFileEx` with `MOVEFILE_DELAY_UNTIL_REBOOT`)

This reproduces the Unix `install` behavior where a running binary can be replaced
because the inode remains open; on Windows the equivalent is rename-then-copy.
