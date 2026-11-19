# Error Handling Specification

This document describes how ps-bash simulates bash error handling, where it
matches real bash behavior, and where PowerShell semantics intentionally diverge.

Source files:
- `src/PsBash.Module/PsBash.psm1` -- runtime error functions and file I/O helpers
- `src/PsBash.Core/Runtime/Ipc/HostProtocol.cs` -- exit code protocol between worker and host
- `src/PsBash.Host/Server/Connection.cs` -- host-side worker and framed-output failure handling
- `src/PsBash.Transpiler/Parser/PsEmitter.cs` -- `set -e`/`-u`/`-x` translation

---

## 1. How Common Commands Handle Errors

Failures while acquiring or initializing a host worker are returned as a
tagged stderr response (`ps-bash-host worker failure: ...`) followed by exit 1,
so the launcher receives a complete protocol result instead of an unexplained
closed stream. Framed-output transport failures use a distinct internal
exception and still escape for connection recovery; caller cancellation is
also preserved as lifecycle control rather than converted into a command
failure.

Binary cmdlets (nearly every command) report errors via
`FileSystemHelpers.WriteBashError`, which emits exactly one `ErrorRecord` in the
format `command: path: reason` -- matching bash conventions. Because it is an
ordinary error record, PowerShell redirection applies: `2>/dev/null` discards it
and `2>&1` merges it into stdout as its one-line message. The host (`SdkWorker`)
prints the surviving records to stderr inline, in order with the surrounding
stdout, as they are written. The psm1 helpers and job-control functions still
use `Write-BashError` (Section 2.2). Each error sets `$global:LASTEXITCODE` to
the appropriate exit code.

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
| `grep` | `grep: path: No such file or directory` | 2 | Exit 1 = no match, 2 = error. Any unreadable operand makes it 2 even if another operand matched (also under `-s`); only `-q` with a selected line stays 0 |
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
| `xargs` | `xargs: CMD: No such file or directory` | 127 | Command not found (no default-command error any more: a bare `xargs` runs `echo`). Also `xargs: CMD: <msg>` / 126 when found but not runnable |
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

**Operand-count usage errors have one spelling each.** "missing operand" is
`FileSystemHelpers.WriteMissingOperand` / `MissingOperandMessage` (`WriteMissingFileOperand` for the file tools);
"extra operand" is `FileSystemHelpers.WriteExtraOperand` / `ExtraOperandMessage`: `CMD: extra operand ‘X’`, an
optional explanation line, then the tool's usage hint, status 1 (sort, diff 2). The operand is quoted as GNU quotes it:
the locale's marks (`quote()`) for most tools, ASCII `'X'` (`quoteaf`) for cp, mv, ln, install, join, sort, wc and diff
(`QuotesExtraOperandForTheShell`). No cmdlet writes either literal itself (guards `MissingOperandGuardTests`,
`ExtraOperandGuardTests`; oracle `RuntimeCommandParityDifferentialTests.ExtraOperand_OneSpellingPerTool`).

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

psm1 functions should use `Write-BashError` instead of `Write-Error` directly.
Binary cmdlets must NOT call it: they use `FileSystemHelpers.WriteBashError`
(one `ErrorRecord`). Calling both printed every diagnostic twice, and the
`Write-BashError` copy -- then written through `$Host.UI.WriteErrorLine` in Bash mode
-- could not be silenced by `2>/dev/null` (it is an ErrorRecord now; see §4).

### 2.3 Exit Code Flow

Exit codes flow from the runtime through the worker to the C# host:

```
Invoke-Bash* sets $global:LASTEXITCODE = N
  → worker eval loop reads $LASTEXITCODE after each command
  → worker writes "<<<EXIT:N>>>" to stdout
  → the host protocol parser reads the marker and returns the exit code
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

On the C# side, the worker protocol reader detects the `<<<EXIT:N>>>` marker
to extract the exit code:

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
| `set -e` / `set -o errexit` | `$ErrorActionPreference = 'Stop'` + per-statement status checks (emitter-strategy.md `set`) | Terminate on first failing command, with bash's exemptions |
| `set -u` / `set -o nounset` | `$global:__BashNounset` + checked plain variable reads | "x: unbound variable", script ends with status 1 (positional parameters are not checked) |
| `set -C` / `set -o noclobber` | Supported | `>` onto an existing file: `cannot overwrite existing file`, status 1; `>|`/`>>` allowed |
| `set -x` / `set -o xtrace` | `Set-PSDebug -Trace 1` | Print commands before execution |

Combined flags are decomposed: `set -euo pipefail` extracts `-e`, `-u` and
`pipefail`. A script that mentions `pipefail` or `PIPESTATUS` is emitted in pipefail mode
(`PsEmitter._pipefailMode`): every stage's status is recorded (`Invoke-BashPipeStage`) and
`$global:PIPESTATUS` is set after the pipeline. A stage a later stage stops (`nosuchcmd 2>&1 | head -1`) records the
status its OWN commands set (`FileSystemHelpers.SetLastExitCode` -> `InvokeBashPipeStageCommand.NoteStatus`), not
`$LASTEXITCODE`, which the stopping stage has already overwritten: 127 there, as in bash (the host's not-found handler
sets and notes 127 BEFORE writing the message, since the stop happens inside that write). The stopping stage itself
(head inside its Invoke-BashPipeStage) gets the engine's stop request out of its steppable pipeline raw, naming a command
the outer pipeline does not have, so the outer pipeline would end NONE of its commands (`… | head -c 3 | od -c` printed
nothing, every status stayed 0): the stage records itself done and asks again as itself (`UpstreamStop.RequestedBy`;
guard `PipeStageUpstreamStopTests`).

### 2.5 Errors that discard the input line

A bad substitution (`${x!y}`) or an invalid indirect name (`${!r}` with `r` unset, `r='a b'`, `r='x:-foo'`) is
bash's DISCARD: the rest of the current input line is dropped, even from inside a function or loop that line called,
status 1, and the shell reads the next line (a `-c` string, a script file, eval and sourced text alike; a `$( )` ends
only itself). In an interactive shell (`bash -i -c`) an unbound variable or `${x:?}` under `set -u` does the same.
When the text can raise one (its emission holds `Get-BashIndirectValue` / `Write-BashExpansionError`, or it is
interactive under nounset), every top-level command runs in `PsBuild.DiscardLineScope`: the runtime throws
`BashParamErrorException.Discard` while `BashLineDiscard.Armed`, the scope catches it, and the line mark
(`$global:__BashFailGlob`) drops the commands after it on its line. Outside any scope (a function defined by text
that had none) the runtime keeps the older per-command abandon mark.

### 2.6 Diagnostic prefix

A diagnostic from the shell itself carries bash's source prefix (error.c `error_prolog`). A non-interactive shell writes
`NAME: line N: `. NAME is `${BASH_SOURCE[0]}` if it is set (the script or sourced file), else `$0` (`bash` under
`bash -c`, `me` under `bash -c '…' me`). An interactive shell writes `bash: `. ps-bash builds the prefix in ONE place:
`PsBash.Cmdlets.ShellDiagnostic.Prefix`. Its callers are:

- emitted text: `PsBuild.DiagnosticPrefix`, `PsBuild.ShellDiagnosticLine`
- the host's CommandNotFoundAction handler (`SdkRunspaceSetup.ps1`)
- `Resolve-BashCommand` (psm1)
- `Invoke-BashCommand`

`CommandNotFoundReport.Describe` builds only the reason after the prefix. Guard: Core.Tests
`ShellDiagnosticPrefixGuardTests` fails on a line that spells "command not found" without the primitive.

**The line N** is `VarFrameStack.Line`.

- *Recording the line.* The emitted code records the line before each statement and each hoisted
  `if`/`elif`/`while`/`until` condition that may raise the diagnostic (`PsEmitter.RecordLineFor`,
  `PsBuild.SetCallLine`).
  - A statement "may raise it" when it names a non-builtin, non-mapped, non-function command or a run-time name.
    `command x`, `builtin x`, `exec x` and a run-time `eval` count too.
  - A script that reads `BASH_LINENO` records the line before every statement.
  - Every host command starts on line 1 (`VarFrameStack.ResetFor`). So a top-level line-1 statement of the script
    records nothing. The exception is a loop that reaches past line 1 (`RunsPastLine1`): its line-1 statements run
    again after later lines were recorded.
- *Which line a command gets.* bash numbers a simple command when it reads the token AFTER its first word, so the line
  is where that token ends (`Command.Simple.ReportLine`, computed by the parser).
  - `x \<nl> arg` on line 2 is line 3.
  - `x 'a<nl>b'` on line 9 is line 10.
  - `x a \<nl> b` on line 4 stays line 4.
  - `$LINENO` and the DEBUG trap keep the start line (`Command.Simple.Line`).
  - The same line is the call line a function reports in `BASH_LINENO`.
- *Function bodies* record absolute script lines. `VarFrameStack.Enter` zeroes `LineBase`, and `Exit` puts the
  caller's line back.
- *A static `eval`* emits its text with the eval's line as base (`PsEmitter._lineBase`). Its line k is the eval's line
  + k − 1.
- *A run-time `eval`* runs in `PsBuild.EvalLinesScope` (`VarFrameStack.BeginEvalLines`: base = current line − 1).
- *A sourced file* runs in `PsBuild.SourceLinesScope` (base 0: its own lines, also inside an eval).
- *After an eval or a sourced file*, both scopes restore the base and the caller's line. bash's
  `eval "$three_lines"; x` on line 1 reports line 1.
- *A cmdlet called directly* has recorded nothing (line 0). Its prefix leaves the line out (`NAME: `).

Covered: command not found, from any site (all four). Differential `ShellDiagnosticLineDifferentialTests` tests the
script file, `-c`, `$0`, sourced files, conditions, evals and continuations.

Not yet through the primitive: the other shell diagnostics still hard-code `bash: ` or `ps-bash: ` (no NAME, no line).

- builtin errors, e.g. `cd: …`, `declare: …`, `type: …`, `alias: …`, `wait: …`, `local: …`, `builtin: …`
- the `${x:?}`, unbound-variable and bad-substitution messages (`Write-BashExpansionError`, `PsBuild` param errors)
- bad array subscript
- readonly variable
- not a valid identifier
- ambiguous redirect
- eval and source parse errors
- `[: missing ]`

Routing each one through `ShellDiagnostic.Prefix` is mechanical. It changes expectations across Cmdlets.Tests, Pester
and the spec corpus, so that work is tracked separately.

Known differences from bash:

- A function defined under `bash -c` reports `environment:` in bash, because that is its `${BASH_SOURCE[0]}`. A
  function defined in a sourced file names that file. ps-bash's `BASH_SOURCE` has no per-function frames, so both
  print the caller's name.
- A function defined inside a run-time eval numbers its body from 1.

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

**ps-bash**: PowerShell object pipelines, with bash's status rules on top:
`${PIPESTATUS[@]}` holds every stage's status (`false | true | (exit 3)` gives
`1 0 3`; a plain command records its own), and under `set -o pipefail` a failing
stage fails the pipeline (`set -o pipefail; false | true; echo $?` is `1`). Oracle
tests: `BashParityGapDifferentialTests.Pipefail_*`, `spec/oils/pipeline.spec.sh`.
One difference remains: no stage dies of `SIGPIPE`, so a writer whose reader
left reports 0, not 141 (`yes | head -1` gives `0 0`; see
`intentional-differences.md`).

### 4.2 Pipeline Negation

**Bash**: `! pipeline` inverts the exit code (0 becomes 1, non-zero becomes 0).
`$?` reflects the negated result.

**ps-bash**: Same. The parser produces a `Pipeline` node with `Negated=true` and
the emitter inverts the recorded status: `! true; echo $?` is `1`, `! false;
echo $?` is `0`.

### 4.3 Subshell Isolation

**Bash**: `( commands )` forks a subprocess. Variable changes, directory changes,
and traps inside the subshell do not affect the parent.

**ps-bash**: `( commands )` and `$( )` run in the same process, with the shell
state saved before and restored after (`BashShellState.Save` / `Restore`).
`x=1; (x=2; cd /); echo $x` prints `1` and the directory is unchanged;
`(exit 3); echo $?` prints `3` and the script goes on.

### 4.4 Signal Handling

**Bash**: `trap 'action' SIGNAL` handles `EXIT`, `ERR`, `INT`, `TERM`, `HUP`,
`QUIT`, `PIPE`, and others.

**ps-bash**: `trap` handles `EXIT`, `ERR`, `RETURN`, `DEBUG` (before each simple
command, from the command after the `trap`, on the same line too; a script that is nothing but the `trap` is a prompt
hook, `PsEmitter.SetsDebugTrapForOtherCommands`; `DebugTrapDifferentialTests`) and signal names. The ERR trap is not
inherited by a function, a subshell or a `$( )` unless `set -E`: each starts with none, a trap it sets fires there,
and a function that set one leaves it in force for its caller (bash `maybe_set_error_trap`; `Cmdlets/ErrTrapInheritance.cs`,
entered from `VarFrameStack.EnterFunction` and `BashShellState.Save`). A signal
trap runs when the shell is signalled (`trap 'echo got' USR1; kill -USR1
$BASHPID` prints `got`); a signal to a ps-bash shell is delivered as a message
to the script its host runs, not as an OS signal (`background-jobs.md`,
`intentional-differences.md`). Signal numbers are bash's Linux numbers on every
OS (`kill -l`, `trap -l`, a `128+N` status; `Platform/OsSignals.cs`,
`platform-dependencies.md` §Signals). `SIGPIPE` is never raised (§4.1).

### 4.5 Background Jobs

**Bash**: `command &` runs in the background. `wait` blocks until completion.
`$!` holds the PID.

**ps-bash**: Implemented: `cmd &`, `$!`, `wait` (`sleep 0 & wait $!; echo $?`
is `0`), `jobs`, `fg`, `bg`, `kill %N`. A job runs in the host in its own
runspace, or in its own host process with `PSBASH_BG_MODE=process`. The design,
its limits and live job output are in `background-jobs.md`.

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

**ps-bash**: arithmetic commands, arithmetic `for` clauses, and `$((...))`
substitutions store an `ArithmeticSyntax` value with a typed `ArithmeticExpr`
tree. The parser preserves precedence, associativity, assignments, and lazy
conditional/logical branches. The original source is retained for the shared
`Invoke-BashArith` runtime evaluator handoff; unsupported parameter-expansion
forms fail during arithmetic parsing instead of being reinterpreted as
PowerShell.

### 4.9 Exit vs Return

**Bash**: `exit N` terminates the current shell (or subshell). `return N`
returns from a function or sourced script.

**ps-bash**: `exit N` ends the script with status N. Inside `( )`, `$( )` or a
compound pipe stage it ends only that scope, as in bash (`(exit 3); echo $?` is `3`).
`return` works within functions but has different scoping behavior when called
from dot-sourced scripts.

### 4.10 Stderr Capture

**Bash**: Stderr is a separate file descriptor (fd 2) that can be redirected,
piped (`|&`), or captured independently.

**ps-bash**: Cmdlet diagnostics are PowerShell `ErrorRecord`s, so `2>/dev/null`
discards them and `2>&1` / `|&` (emitted as `2>&1 |`) merge them into the
pipeline as one line of text each. Records that survive redirection are streamed
by `SdkWorker` to a STDERR-tagged IPC frame inline, in order with stdout.
`cmd >&2` output and the psm1 `Write-BashError` (Bash mode) are ErrorRecords too:
`Write-BashHostStderr` writes each line to the error stream (through the separate
`Write-BashStderrRecord`, so the CALLER's `$?` stays true: writing stderr is not a
failure), so the same redirection applies. Nothing writes `$Host.UI.WriteErrorLine`,
which no redirection can see (`{ echo e >&2; } 2>/dev/null` used to print `e`).

A stderr record's BYTES follow one rule, `PsBash.Core.StderrRecord.Payload`: a line
(LF-terminated) unless the record is marked unterminated — `Write-BashHostStderr`
sets `TargetObject` to `StderrRecord.NoTrailingNewlineMarker` when its input carries
`NoTrailingNewline`, so `printf x >&2` writes exactly `x` (terminal, `2> f`, `2>&1`
alike). STDERR IPC frames carry exact bytes: the host adds the LF to every line it
sends (records via the rule, host text via `deliverError`), and the launcher writes
the payload raw — it used to append a "missing" LF, giving every record a newline.

---

## 5. Error Handling Migration Status

> Historical (psm1-era) table. Most of these commands are now binary cmdlets
> that report via `FileSystemHelpers.WriteBashError` (one `ErrorRecord`), not
> `Write-BashError`; read "Yes" below as "uses the bash-style error sink".

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
