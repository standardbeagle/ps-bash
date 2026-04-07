# Error Handling Specification

This document describes how ps-bash simulates bash error handling, where it
matches real bash behavior, and where PowerShell semantics intentionally diverge.

Source files:
- `src/PsBash.Module/PsBash.psm1` -- runtime error functions and file I/O helpers
- `src/PsBash.Core/Runtime/PwshWorker.cs` -- exit code protocol between worker and host
- `src/PsBash.Core/Parser/PsEmitter.cs` -- `set -e`/`-u`/`-x` translation

---

## 1. How Common Commands Handle Errors

All commands report errors via `Write-BashError`, which writes to stderr in
the format `command: path: reason` -- matching bash conventions. Each error
sets `$global:LASTEXITCODE` to the appropriate exit code.

### 1.1 File Operations

| Command | Error Format | Exit Code | Example |
|---------|-------------|-----------|---------|
| `cat` | `cat: /path: message` | 1 | `cat: /tmp/nofile: Could not find file` |
| `ls` | `ls: cannot access '/path': message` | 1 | `ls: cannot access '/bad': Could not find a part of the path` |
| `cp` | `cp: missing file operand` | 1 | Missing args, directory without `-r`, source not found |
| `mv` | `mv: missing file operand` | 1 | Missing args, source not found |
| `rm` | `rm: cannot remove 'path': reason` | 1 | Not found, is a directory (without `-r`), protected path |
| `mkdir` | `mkdir: cannot create directory 'dir': reason` | 1 | Already exists (without `-p`), parent missing |
| `rmdir` | `rmdir: failed to remove 'dir': reason` | 1 | Not a directory, not empty |
| `touch` | `touch: cannot touch 'file': reason` | 1 | Parent not found, invalid date |
| `ln` | `ln: failed to create link 'name': File exists` | 1 | Target exists, missing operand |

File operations use `Get-BashItem` for path resolution and `Read-BashFileLines` /
`Read-BashFileBytes` for reading. These helpers centralize error formatting (see
Section 3).

### 1.2 Text Processing

| Command | Error Format | Exit Code | Notes |
|---------|-------------|-----------|-------|
| `grep` | `grep: path: No such file or directory` | 2 | Exit 1 = no match, 2 = error |
| `sed` | `sed: bad substitution` | 2 | Syntax errors in expressions |
| `awk` | `awk: usage: awk [options] program [file ...]` | 2 | Missing program argument |
| `sort` | (via file helpers) | 1 | File not found |
| `head` | (via file helpers) | 1 | File not found |
| `tail` | (via file helpers) | 1 | File not found |
| `wc` | `wc: path: No such file or directory` | 1 | File not found |
| `cut` | (via file helpers) | 1 | File not found |

`grep` follows the bash convention: exit 0 = match found, exit 1 = no match,
exit 2 = error. Most other text commands use exit 1 for all errors.

`sed` validates expression syntax at parse time and reports specific errors:
`unterminated address regex`, `bad substitution`, `unsupported command`, `missing
command`, and `y: source and dest must be the same length`.

### 1.3 Navigation

`cd` is not an `Invoke-Bash*` function. The emitter translates it to native
PowerShell `Set-Location`, which raises a standard PowerShell error if the path
does not exist.

### 1.4 Utility Commands

| Command | Error Format | Exit Code | Notes |
|---------|-------------|-----------|-------|
| `printf` | `printf: usage: printf format [arguments]` | 2 | Missing format string |
| `diff` | `diff: missing operand` | 1 | Missing file arguments |
| `comm` | `comm: missing operand` | 1 | Missing file arguments |
| `join` | `join: missing operand` | 1 | Missing file arguments |
| `jq` | `jq: parse error: message` | 5 | JSON parse error; exit 2 for missing file |
| `expr` | `expr: missing operand` | 2 | Also: `division by zero`, `non-integer argument` |
| `date` | `date: invalid date 'string'` | 1 | Invalid date or missing reference file |
| `rg` | `rg: path: No such file or directory` | 2 | Exit 1 = no match (like grep) |
| `gzip` | `gzip: missing file operand` | 1 | Also: file not found |
| `tar` | `tar: you must specify -f archive` | 1 | Also: missing `-c`/`-x`/`-t`, file not found |
| `xargs` | `xargs: no command specified` | 1 | No command to execute |
| `split` | `split: missing operand` | 1 | Missing input |
| `sleep` | `sleep: invalid time interval 'arg'` | 1 | Non-numeric or negative duration |
| `time` | `time: missing command` | 1 | No command to time |
| `which` | `which: no name in PATH` | 1 | Command not found |
| `stat` | `stat: missing operand` | 1 | No file specified |
| `tee` | `tee: path: No such file or directory` | 1 | Output file error |
| `xan` | `xan: missing subcommand (...)` | 1 | Also: unknown subcommand, parse error |
| `yq` | `yq: parse error: message` | 1 | Also: file not found |
| `file` | `file: cannot open 'path' (No such file or directory)` | 1 | File not found |
| `base64` | `base64: path: message` | 1 | File not found |

---

## 2. Global Error Infrastructure

### 2.1 Error Mode

```powershell
$script:BashErrorMode = 'Bash'   # default

function Set-BashErrorMode {
    param([ValidateSet('Bash','PowerShell')][string]$Mode)
    $script:BashErrorMode = $Mode
}
```

Two modes control how errors are emitted:

| Mode | Behavior |
|------|----------|
| `Bash` (default) | `[Console]::Error.WriteLine($Message)` -- plain text to stderr |
| `PowerShell` | `Write-Error -Message $Message -ErrorAction Continue` -- PS error records with stack traces |

`Bash` mode produces output indistinguishable from real bash errors. `PowerShell`
mode is useful for debugging since error records include source location.

### 2.2 Write-BashError

```powershell
function Write-BashError {
    param(
        [Parameter(Mandatory)][string]$Message,
        [int]$ExitCode = 1
    )
    $global:LASTEXITCODE = $ExitCode
    if ($script:BashErrorMode -eq 'Bash') {
        [Console]::Error.WriteLine($Message)
    } else {
        Write-Error -Message $Message -ErrorAction Continue
    }
}
```

All `Invoke-Bash*` functions should use `Write-BashError` instead of `Write-Error`
directly. This ensures consistent stderr formatting and exit code setting.

### 2.3 Exit Code Flow

Exit codes flow from the runtime through the worker to the C# host:

```
Invoke-Bash* sets $global:LASTEXITCODE = N
  → worker eval loop reads $LASTEXITCODE after each command
  → worker writes "<<<EXIT:N>>>" to stdout
  → PwshWorker.cs parses the marker and returns the exit code
```

The worker script (`ps-bash-worker.ps1`) wraps each eval in try/catch:

```powershell
try {
    Invoke-Expression $line
    $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 0 }
    [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 1 }
    [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
}
```

On the C# side, `PwshWorker` reads stdout lines and detects the `<<<EXIT:N>>>`
marker to extract the exit code:

```csharp
if (line.StartsWith("<<<EXIT:", StringComparison.Ordinal)
    && line.EndsWith(">>>", StringComparison.Ordinal))
{
    var code = line["<<<EXIT:".Length..^">>>".Length];
    return int.TryParse(code, out var n) ? n : 1;
}
```

### 2.4 Shell Options

The emitter translates `set` flags to PowerShell equivalents:

| Bash | PowerShell | Effect |
|------|-----------|--------|
| `set -e` / `set -o errexit` | `$ErrorActionPreference = 'Stop'` | Terminate on first error |
| `set -u` / `set -o nounset` | `Set-StrictMode -Version Latest` | Error on undefined variables |
| `set -x` / `set -o xtrace` | `Set-PSDebug -Trace 1` | Print commands before execution |

Combined flags are decomposed: `set -euo pipefail` extracts `-e`, `-u`, and
ignores `pipefail` (not implemented). The emitter handles this in `EmitSimple`.

---

## 3. File I/O Error Helpers

Six helper functions centralize file access with consistent error handling.

### 3.1 Read Helpers

| Function | Returns | On Error |
|----------|---------|----------|
| `Read-BashFileBytes -Path $p -Command 'cat'` | UTF-8 string (BOM-skipped, CRLF-normalized) | `$null` + bash-style error |
| `Read-BashFileLines -Path $p -Command 'cat'` | `string[]` (lines without trailing newlines) | `$null` + bash-style error |
| `Read-BashFileRaw -Path $p -Command 'gzip'` | `byte[]` (raw binary) | `$null` + bash-style error |

`Read-BashFileBytes` performs three normalizations:
1. Skips UTF-8 BOM (3-byte `EF BB BF` prefix) if present.
2. Decodes bytes as UTF-8.
3. Replaces `\r\n` with `\n` for Unix-consistent line endings.

`Read-BashFileLines` calls `Read-BashFileBytes`, strips the trailing newline, and
splits on `\n`.

### 3.2 Write Helpers

| Function | Returns | On Error |
|----------|---------|----------|
| `Write-BashFileText -Path $p -Text $t -Command 'sed' [-Append]` | `$true` | `$false` + bash-style error |
| `Write-BashFileRaw -Path $p -Data $bytes -Command 'gzip'` | `$true` | `$false` + bash-style error |

### 3.3 Path Helper

| Function | Returns | On Error |
|----------|---------|----------|
| `Get-BashItem -Path $p -Command 'ls'` | `FileSystemInfo` | `$null` + `cmd: cannot access 'path': message` |

`Get-BashItem` wraps `Get-Item -LiteralPath -Force -ErrorAction Stop` with a
try/catch that normalizes the path (backslashes to forward slashes) in the error
message.

### 3.4 Error Format

All helpers normalize paths from Windows backslashes to forward slashes before
including them in error messages:

```powershell
$normalized = $Path -replace '\\', '/'
Write-BashError -Message "${Command}: ${normalized}: $($_.Exception.Message)"
```

This produces output like `cat: /tmp/nofile: Could not find file` instead of
`cat: C:\Users\...\nofile: Could not find file`.

### 3.5 Windows Edge Cases

The helpers rely on .NET `System.IO.File` methods which handle:

- **Reserved device names** (`nul`, `con`, `prn`, `aux`, `com1`-`com9`,
  `lpt1`-`lpt9`): .NET throws `IOException` or `UnauthorizedAccessException`,
  caught by the try/catch and reported as bash-style errors.
- **Long paths**: .NET Core supports long paths (>260 chars) natively on Windows
  when the application manifest allows it.
- **Permissions**: `UnauthorizedAccessException` is caught and reported with the
  exception message, matching bash's `Permission denied` pattern.

---

## 4. Where ps-bash Intentionally Differs from Bash

### 4.1 Pipeline Error Propagation

**Bash**: Byte-stream pipes. `set -o pipefail` makes the pipeline return the
rightmost non-zero exit code. `$PIPESTATUS` array holds per-command exit codes.

**ps-bash**: PowerShell object pipelines. Each command runs independently. No
`pipefail` implementation. `$PIPESTATUS` is not available. Only the last
command's exit code is reported.

### 4.2 Pipeline Negation

**Bash**: `! pipeline` inverts the exit code (0 becomes 1, non-zero becomes 0).
`$?` reflects the negated result.

**ps-bash**: The parser produces a `Pipeline` node with `Negated=true`. The
emitter prefixes with `!`, but PowerShell's `$?` tracks command success/failure
differently than bash's integer exit code inversion.

### 4.3 Subshell Isolation

**Bash**: `( commands )` forks a subprocess. Variable changes, directory changes,
and traps inside the subshell do not affect the parent.

**ps-bash**: `( commands )` runs in the same process via a `& { }` scriptblock.
Variable assignments inside `$( )` and `( )` leak to the parent scope. Directory
changes persist.

### 4.4 Signal Handling

**Bash**: `trap 'action' SIGNAL` handles `EXIT`, `ERR`, `INT`, `TERM`, `HUP`,
`QUIT`, `PIPE`, and others.

**ps-bash**: `Invoke-BashTrap` supports:
- `EXIT` -- registers via `Register-EngineEvent PowerShell.Exiting`
- `ERR` -- stores the action in `$script:BashTrapHandlers` and sets
  `$global:__BashTrapERR`
- All other signals -- stored but not wired to actual OS signals

There are no equivalents for `SIGTERM`, `SIGINT`, `SIGHUP`, `SIGPIPE`, etc.
`Ctrl+C` handling is managed by PowerShell's own `[Console]::CancelKeyPress`
event, not by `trap INT`.

### 4.5 Background Jobs

**Bash**: `command &` runs in the background. `wait` blocks until completion.
`$!` holds the PID.

**ps-bash**: Not implemented. `&` is parsed as the `Amp` token but the emitter
does not produce background job syntax. `wait` is not mapped.

### 4.6 Process Substitution

**Bash**: `<(cmd)` creates a `/dev/fd/N` file descriptor. The substitution is a
live pipe -- data streams in real time.

**ps-bash**: `<(cmd)` uses `Invoke-ProcessSub`, which writes the command's output
to a temp file under `ps-bash/proc-sub/` and returns the path. The command runs
to completion before the outer command reads the file. Cleanup is the caller's
responsibility.

### 4.7 Here-doc Variable Scope

**Bash**: Variables in heredocs (`<<EOF`) resolve from the current shell scope,
including local variables and positional parameters.

**ps-bash**: The emitter translates `$VAR` inside heredocs to `$env:VAR` via
`TranslateHereDocVars`. Only environment variables are accessible. Shell-local
variables (function locals, loop variables) are not resolved.

### 4.8 Arithmetic Context

**Bash**: `(( expr ))` is a fully parsed arithmetic expression with operator
precedence, variable references without `$`, and assignment side effects.

**ps-bash**: `(( expr ))` is stored as a raw string in `Command.ArithCommand`.
The emitter passes it through with minimal transformation. Complex expressions
with nested parentheses or comma operators may not evaluate correctly.

### 4.9 Exit vs Return

**Bash**: `exit N` terminates the current shell (or subshell). `return N`
returns from a function or sourced script.

**ps-bash**: `exit N` is emitted as `exit N`, which in PowerShell terminates the
entire process (or runspace). There is no subshell boundary to contain it.
`return` works within functions but has different scoping behavior when called
from dot-sourced scripts.

### 4.10 Stderr Capture

**Bash**: Stderr is a separate file descriptor (fd 2) that can be redirected,
piped (`|&`), or captured independently.

**ps-bash**: The worker process starts with `RedirectStandardError = false`.
Stderr from `[Console]::Error.WriteLine` flows directly to the parent console's
stderr. It cannot be captured or redirected by the C# host. `|&` is emitted as
`2>&1 |`, which merges stderr into the PowerShell pipeline.

---

## 5. Error Handling Migration Status

Functions using `Write-BashError` are fully migrated. Functions using bare
`Write-Error` need migration. Functions marked N/A have no error paths (pure
computation or delegation).

| Function | Write-BashError | File I/O Helpers | Exit Code |
|----------|:-:|:-:|:-:|
| `Invoke-BashEcho` | N/A | N/A | N/A |
| `Invoke-BashPrintf` | Yes | N/A | Yes |
| `Invoke-BashLs` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashCat` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashRedirect` | N/A | N/A | N/A |
| `Invoke-BashGrep` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashSort` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashHead` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashTail` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashWc` | Yes | N/A | Yes |
| `Invoke-BashFind` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashStat` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashCp` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashMv` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashRm` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashMkdir` | Yes | N/A | Yes |
| `Invoke-BashRmdir` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashTouch` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashLn` | Yes | N/A | Yes |
| `Invoke-BashPs` | N/A | N/A | N/A |
| `Invoke-BashSed` | Yes | `Read-BashFileBytes`, `Write-BashFileText` | Yes |
| `Invoke-BashAwk` | Yes | N/A | Yes |
| `Invoke-BashCut` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashTr` | N/A | N/A | N/A |
| `Invoke-BashUniq` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashRev` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashNl` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashDiff` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashComm` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashColumn` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashJoin` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashPaste` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashTee` | Yes | `Write-BashFileText` | Yes |
| `Invoke-BashXargs` | Yes | N/A | Yes |
| `Invoke-BashJq` | **Partial** | N/A | Yes |
| `Invoke-BashDate` | Yes | N/A | Yes |
| `Invoke-BashSeq` | N/A | N/A | N/A |
| `Invoke-BashExpr` | Yes | N/A | Yes |
| `Invoke-BashDu` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashTree` | Yes | `Get-BashItem` | Yes |
| `Invoke-BashEnv` | Yes | N/A | Yes |
| `Invoke-BashBasename` | N/A | N/A | N/A |
| `Invoke-BashDirname` | N/A | N/A | N/A |
| `Invoke-BashPwd` | Yes | N/A | Yes |
| `Invoke-BashHostname` | Yes | N/A | Yes |
| `Invoke-BashWhoami` | N/A | N/A | N/A |
| `Invoke-BashFold` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashExpand` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashUnexpand` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashStrings` | Yes | `Read-BashFileBytes` | Yes |
| `Invoke-BashSplit` | Yes | `Read-BashFileLines`, `Write-BashFileText` | Yes |
| `Invoke-BashTac` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashBase64` | Yes | `Read-BashFileBytes` | Yes |
| `Invoke-BashChecksum` | **No** | N/A | No |
| `Invoke-BashMd5sum` | (delegates to Checksum) | N/A | No |
| `Invoke-BashSha1sum` | (delegates to Checksum) | N/A | No |
| `Invoke-BashSha256sum` | (delegates to Checksum) | N/A | No |
| `Invoke-BashFile` | Yes | N/A | Yes |
| `Invoke-BashRg` | Yes | `Read-BashFileLines` | Yes |
| `Invoke-BashGzip` | Yes | `Read-BashFileRaw`, `Write-BashFileRaw` | Yes |
| `Invoke-BashTar` | Yes | `Read-BashFileRaw`, `Write-BashFileRaw` | Yes |
| `Invoke-BashYq` | Yes | N/A | Yes |
| `Invoke-BashXan` | Yes | N/A | Yes |
| `Invoke-BashSleep` | Yes | N/A | Yes |
| `Invoke-BashTime` | Yes | N/A | Yes |
| `Invoke-BashWhich` | Yes | N/A | Yes |
| `Invoke-BashAlias` | Yes | N/A | Yes |
| `Invoke-BashTrap` | N/A | N/A | N/A |
| `Invoke-BashReadlink` | **No** | N/A | No |
| `Invoke-BashMktemp` | N/A | N/A | N/A |
| `Invoke-BashType` | **No** | N/A | No |

### Migration Notes

- **Partial** (`Invoke-BashJq`): Top-level errors (file not found, parse error)
  use `Write-BashError`. Internal filter errors (`unknown filter`, `unmatched [`)
  still use bare `Write-Error`.
- **No** (`Invoke-BashChecksum`, `Invoke-BashReadlink`, `Invoke-BashType`):
  These functions use `Write-Error` directly. They should be migrated to
  `Write-BashError` for consistent stderr formatting and exit code handling.
