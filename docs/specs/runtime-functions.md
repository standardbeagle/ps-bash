# Runtime Functions Specification

This document describes the PowerShell runtime module (`PsBash.psm1`) that provides
Unix command emulation for the ps-bash transpiler.

## Architecture

The psm1 module is loaded into a `pwsh` worker process managed by `PwshWorker`. Bash
commands transpiled by `PsEmitter` into PowerShell are evaluated via `Invoke-Expression`
inside this worker. The module provides `Invoke-Bash*` functions that emulate Unix
commands, and registers global aliases (e.g. `ls` -> `Invoke-BashLs`) so transpiled code
reads naturally.

Key layers:

1. **PsEmitter** (C#) -- transpiles bash AST nodes into PowerShell expressions.
2. **PwshWorker** (C#) -- spawns a `pwsh` process, imports the module, and evaluates
   transpiled expressions over stdin/stdout.
3. **PsBash.psm1** (PowerShell) -- the runtime library providing 76 `Invoke-Bash*`
   functions (75 commands + 1 internal helper), the BashObject model, escape handling,
   glob expansion, and tab completion.

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

| Command | Function | Key Flags | Arg Parsing | Pipeline | File |
|---|---|---|---|---|---|
| echo | Invoke-BashEcho | `-n`, `-e`, `-E` | ConvertFrom-BashArgs | No | No |
| printf | Invoke-BashPrintf | (format + args) | Positional | No | No |
| ls | Invoke-BashLs | `-l`, `-a`, `-h`, `-R`, `-S`, `-t`, `-r`, `-1` | ConvertFrom-BashArgs | No | Yes |
| cat | Invoke-BashCat | `-n`, `-b`, `-s`, `-E`, `-T` | ConvertFrom-BashArgs | Yes | Yes |
| grep | Invoke-BashGrep | `-i`, `-v`, `-n`, `-c`, `-r`, `-l`, `-E`, `-A`, `-B`, `-C` | Manual loop | Yes | Yes |
| rg | Invoke-BashRg | `-i`, `-w`, `-c`, `-l`, `-n`, `-N`, `-o`, `-v`, `-F`, `-g`, `-A`, `-B`, `-C`, `--hidden` | Manual loop | Yes | Yes |
| sort | Invoke-BashSort | `-r`, `-n`, `-u`, `-f`, `-k`, `-t`, `-h`, `-V`, `-M`, `-c` | Manual loop | Yes | Yes |
| head | Invoke-BashHead | `-n`, `-c` | Manual loop | Yes | Yes |
| tail | Invoke-BashTail | `-n`, `-c`, `-f` | Manual loop | Yes | Yes |
| wc | Invoke-BashWc | `-l`, `-w`, `-c` | ConvertFrom-BashArgs | Yes | Yes |
| find | Invoke-BashFind | `-name`, `-type`, `-size`, `-maxdepth`, `-mtime`, `-empty` | Manual loop | No | Yes |
| stat | Invoke-BashStat | `-c`, `-t`, `--printf` | Manual loop | No | Yes |
| cp | Invoke-BashCp | `-r`, `-v`, `-n`, `-f` | ConvertFrom-BashArgs | No | Yes |
| mv | Invoke-BashMv | `-v`, `-n`, `-f` | ConvertFrom-BashArgs | No | Yes |
| rm | Invoke-BashRm | `-r`, `-f`, `-v` | ConvertFrom-BashArgs | No | Yes |
| mkdir | Invoke-BashMkdir | `-p`, `-v` | ConvertFrom-BashArgs | No | Yes |
| rmdir | Invoke-BashRmdir | `-p`, `-v` | ConvertFrom-BashArgs | No | Yes |
| touch | Invoke-BashTouch | `-d`, `-a`, `-m`, `-c` | Manual loop | No | Yes |
| ln | Invoke-BashLn | `-s`, `-f`, `-v` | Manual loop | No | Yes |
| ps | Invoke-BashPs | `-e`/`-A`, `-f`, `-u`, `-p`, `--sort`, `-o` | Manual loop | No | No |
| sed | Invoke-BashSed | `-n`, `-i`, `-E`, `-e` | Manual loop | Yes | Yes |
| awk | Invoke-BashAwk | `-F`, `-v` | Manual loop | Yes | Yes |
| cut | Invoke-BashCut | `-d`, `-f`, `-c` | Manual loop | Yes | Yes |
| tr | Invoke-BashTr | `-d`, `-s` | Manual loop | Yes | No |
| uniq | Invoke-BashUniq | `-c`, `-d` | Manual loop | Yes | Yes |
| rev | Invoke-BashRev | (none) | Positional | Yes | Yes |
| nl | Invoke-BashNl | `-ba` | Manual loop | Yes | Yes |
| diff | Invoke-BashDiff | `-u` | Manual loop | No | Yes |
| comm | Invoke-BashComm | `-1`, `-2`, `-3` | ConvertFrom-BashArgs | No | Yes |
| column | Invoke-BashColumn | `-t`, `-s` | Manual loop | Yes | Yes |
| join | Invoke-BashJoin | `-t`, `-1`, `-2` | Manual loop | No | Yes |
| paste | Invoke-BashPaste | `-d`, `-s` | Manual loop | Yes | Yes |
| tee | Invoke-BashTee | `-a` | ConvertFrom-BashArgs | Yes | Yes |
| xargs | Invoke-BashXargs | `-I`, `-n` | Manual loop | Yes | No |
| jq | Invoke-BashJq | `-r`, `-c`, `-S`, `-s` | Manual loop | Yes | Yes |
| date | Invoke-BashDate | `-d`, `-u`, `-r`, `+FORMAT` | Manual loop | No | No |
| seq | Invoke-BashSeq | `-s`, `-w` | Manual loop | No | No |
| expr | Invoke-BashExpr | (expression tokens) | Positional | No | No |
| du | Invoke-BashDu | `-h`, `-s`, `-a`, `-c`, `-d` | Manual loop | No | Yes |
| tree | Invoke-BashTree | `-a`, `-d`, `-L`, `-I`, `--dirsfirst` | Manual loop | No | Yes |
| env | Invoke-BashEnv | (none) | Positional | No | No |
| basename | Invoke-BashBasename | `-s` | Manual loop | No | No |
| dirname | Invoke-BashDirname | (none) | Positional | No | No |
| pwd | Invoke-BashPwd | `-P` | ConvertFrom-BashArgs | No | No |
| hostname | Invoke-BashHostname | (none) | None | No | No |
| whoami | Invoke-BashWhoami | (none) | None | No | No |
| fold | Invoke-BashFold | `-w`, `-s`, `-b` | Manual loop | Yes | Yes |
| expand | Invoke-BashExpand | `-t` | Manual loop | Yes | Yes |
| unexpand | Invoke-BashUnexpand | `-t`, `-a` | Manual loop | Yes | Yes |
| strings | Invoke-BashStrings | `-n` | Manual loop | Yes | Yes |
| split | Invoke-BashSplit | `-l`, `-d`, `-a` | Manual loop | Yes | Yes |
| tac | Invoke-BashTac | `-s` | Manual loop | Yes | Yes |
| base64 | Invoke-BashBase64 | `-d`, `-w` | Manual loop | Yes | Yes |
| md5sum | Invoke-BashMd5sum | `-c`, `-b` | (delegates to Invoke-BashChecksum) | Yes | Yes |
| sha1sum | Invoke-BashSha1sum | `-c`, `-b` | (delegates to Invoke-BashChecksum) | Yes | Yes |
| sha256sum | Invoke-BashSha256sum | `-c`, `-b` | (delegates to Invoke-BashChecksum) | Yes | Yes |
| file | Invoke-BashFile | `-b`, `-i`, `-L` | Manual loop | No | Yes |
| gzip | Invoke-BashGzip | `-d`, `-c`, `-k`, `-f`, `-v`, `-l`, `-1`..`-9` | Manual loop | Yes | Yes |
| tar | Invoke-BashTar | `-c`, `-x`, `-t`, `-f`, `-z`, `-v`, `-C`, `--exclude` | Manual loop | No | Yes |
| yq | Invoke-BashYq | `-r`, `-o` | Manual loop | Yes | Yes |
| xan | Invoke-BashXan | `-d`, subcommands: `headers`, `count`, `select`, `search`, `table` | Manual loop | Yes | Yes |
| sleep | Invoke-BashSleep | (duration) | Positional | No | No |
| time | Invoke-BashTime | (command) | Positional | No | No |
| which | Invoke-BashWhich | `-a` | Manual loop | No | No |
| alias | Invoke-BashAlias | `-p`, `-u`, `-a` | Manual loop | No | No |
| unset | Invoke-BashUnset | `-v`, `-f` | Manual loop | No | No |
| pushd | Invoke-BashPushd | `+N` | Manual loop | No | No |
| popd | Invoke-BashPopd | `+N` | Manual loop | No | No |
| dirs | Invoke-BashDirs | `-c`, `-p`, `-v` | Manual loop | No | No |
| yes | Invoke-BashYes | `STRING` | Positional | Yes | No |
| tput | Invoke-BashTput | `CAPNAME` | Manual loop | No | No |
| shopt | Invoke-BashShopt | `-s`, `-u`, `-p`, `-q` | Manual loop | No | No |
| type | Invoke-BashType | `-t`, `-a`, `-p` | Manual loop | No | No |
| command | Invoke-BashCommand | `-v` | Manual loop | No | No |
| source | Invoke-BashSource | (none) | Positional | No | Yes |
| shift | Invoke-BashShift | `N` | Manual loop | No | No |
| realpath | Invoke-BashRealpath | (none) | Positional | No | No |

Additional aliases: `printenv` -> `Invoke-BashEnv`, `gunzip` -> `Invoke-BashGzip`,
`zcat` -> `Invoke-BashGzip`, `.` -> `Invoke-BashSource`.

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

## Escape Sequence Handling

`Expand-EscapeSequences` converts C-style escape sequences in string literals. It is
used by `echo -e`, `printf`, and `tr` operands.

### Replacement Chain

1. Replace `\\` with a sentinel: `\0ESCAPED_BACKSLASH\0`
2. Replace `\n` -> newline, `\t` -> tab, `\r` -> CR, `\a` -> bell, `\b` -> backspace,
   `\f` -> form feed, `\v` -> vertical tab
3. Replace sentinel back to literal `\`

The sentinel pattern uses NUL characters (`\0`) as delimiters to avoid collisions with
any valid input text. This two-pass approach ensures that `\\n` produces a literal
backslash followed by `n` rather than a newline.

### Usage in Commands

- **echo -e**: Expands escapes in the joined operand text.
- **printf**: Expands escapes in the format string (after `%%` sentinel replacement).
- **tr**: Expands escapes in both SET1 and SET2 operands before character class expansion.

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

### PwshWorker

Path: `ps-bash/module-{version}/ps-bash-worker.ps1`

Uses the same version-stamped directory as `ModuleExtractor`. The worker script is
extracted from an embedded resource. Timestamp-based invalidation compares the script
file's `LastWriteTimeUtc` against the assembly's; if the assembly is newer, the script
is re-extracted.

### Invoke-ProcessSub

Path: `ps-bash/proc-sub/{random-filename}`

Used for process substitution (`<(command)`). Creates a temp file with a random name,
writes the scriptblock's output to it, and returns the path. On error, the temp file
is cleaned up. On success, the caller is responsible for cleanup.

## Adding a New Command

To add a new `Invoke-Bash*` function:

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
