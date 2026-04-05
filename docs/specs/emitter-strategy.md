# Emitter Strategy Specification

This document describes how `PsEmitter` translates parsed bash AST nodes into
PowerShell text. The emitter lives in `src/PsBash.Core/Parser/PsEmitter.cs`.

---

## 1. The Passthrough Principle

The emitter's sole job for pipe-target commands (grep, head, cut, awk, etc.) is
to **map the bash command name to an `Invoke-Bash*` PowerShell function and
forward every argument verbatim**. The runtime functions defined in
`PsBash.psm1` are responsible for parsing flags and implementing behaviour.

The emitter **MUST NOT**:

- Translate bash flags to PowerShell named parameters (e.g., do not convert
  `-d ","` to `-Delimiter ","`).
- Do partial flag extraction (e.g., do not pull `-n 5` out of `head` args and
  re-emit it as `-n 5` while dropping other args).
- Assume which flags a command supports -- forward all of them and let the
  runtime sort it out.

The only exception is `grep`, which needs special quoting of its pattern
argument (see Section 3 below).

---

## 2. Pipe-Target Mapping (`TryEmitMappedCommand`)

`TryEmitMappedCommand` is called for every command after the first in a
pipeline. It inspects the command name and dispatches to the appropriate
emitter.

| Bash command | PowerShell function   | Emitter method     |
|--------------|-----------------------|--------------------|
| `grep`       | `Invoke-BashGrep`     | `EmitGrep`         |
| `head`       | `Invoke-BashHead`     | `EmitPassthrough`  |
| `tail`       | `Invoke-BashTail`     | `EmitPassthrough`  |
| `wc`         | `Invoke-BashWc`       | `EmitPassthrough`  |
| `sort`       | `Invoke-BashSort`     | `EmitPassthrough`  |
| `uniq`       | `Invoke-BashUniq`     | (no args emitted)  |
| `sed`        | `Invoke-BashSed`      | `EmitPassthrough`  |
| `awk`        | `Invoke-BashAwk`      | `EmitPassthrough`  |
| `cut`        | `Invoke-BashCut`      | `EmitPassthrough`  |
| `xargs`      | `Invoke-BashXargs`    | `EmitPassthrough`  |
| `tr`         | `Invoke-BashTr`       | `EmitPassthrough`  |
| `tee`        | `Tee-Object`          | `EmitTee`          |

Commands not in this table are emitted via the general `Emit` path (i.e., they
are not rewritten).

`uniq` maps to `Invoke-BashUniq` with no arguments. `tee` maps to the native
PowerShell `Tee-Object` cmdlet, forwarding the first argument as the file path.

---

## 3. `EmitGrep` -- The One Non-Passthrough Mapping

`EmitGrep` is intentionally not a pure passthrough. It wraps the pattern
argument in double quotes to prevent PowerShell from misinterpreting regex
metacharacters. The logic:

1. Walk args left to right.
2. Flags starting with `-` (before the pattern is found) are expanded
   character-by-character: `v` -> `-NotMatch`, `i` -> `-CaseInsensitive`,
   `r` -> `-Recurse`.
3. The first non-flag argument is the pattern, emitted as `"pattern"`.
4. Remaining arguments are forwarded as-is (file paths, etc.).

Result: `Invoke-BashGrep [-NotMatch] [-CaseInsensitive] [-Recurse] "pattern" [files...]`

---

## 4. `EmitPassthrough`

```
EmitPassthrough(cmdlet, args) -> "cmdlet arg1 arg2 ..."
```

1. If `args` is empty, return just the cmdlet name.
2. Otherwise, iterate args, call `EmitWord` on each, and space-join them after
   the cmdlet name.
3. If an emitted arg triggers `NeedsPassthroughQuoting`, wrap it in double
   quotes.

**`NeedsPassthroughQuoting`** returns `true` when an argument:

- Starts with `-` (is a flag), AND
- Contains a comma (`,`) -- PowerShell array separator -- OR
- Contains braces (`{` or `}`) -- PowerShell scriptblock delimiters.
- Already-quoted arguments (starting with `"` or `'`) are skipped.

Example: `awk -F,` emits as `Invoke-BashAwk "-F,"` to prevent PowerShell from
splitting on the comma. `xargs -I{}` emits as `Invoke-BashXargs "-I{}"`.

---

## 5. `EmitSimple` -- The Main Command Emitter

`EmitSimple` handles `Command.Simple` nodes (single commands that are not
pipelines or control flow). It processes several special forms before falling
through to general word emission.

### HereDoc

If the command has a heredoc attached:

- **Expanding** (`<<EOF`): emits `@"\n{body}\n"@ | cmd` with bash variable
  references (`$VAR`, `${VAR}`) translated to `$env:VAR` via
  `TranslateHereDocVars`.
- **Literal** (`<<'EOF'`): emits `@'\n{body}\n'@ | cmd` with no variable
  translation.

The heredoc body is piped into the command.

### Here-string (`<<<`)

Not handled as a separate case in `EmitSimple` -- here-strings are parsed as
heredocs with the word as the body, so they follow the same heredoc path.

### Input redirect (`< file`)

`< file` on a command becomes `Get-Content file | cmd`. The redirect is removed
from the command's redirect list, and the rest of the command is re-emitted via
recursive `EmitSimple`.

### `declare`

- `declare -A name` -> `$name = @{}` (empty hashtable)
- `declare -a name` -> `$name = @()` (empty array)
- `declare -i name` -> `[int]$name = 0`

### `read`

- `read [-r] [-p "prompt"] VAR` -> `$VAR = Read-Host ["prompt"]`
- Flags other than `-p` are ignored; the last non-flag word is the variable.

### `set`

- `set -e` / `set -o errexit` -> `$ErrorActionPreference = 'Stop'`
- `set -x` / `set -o xtrace` -> `Set-PSDebug -Trace 1`
- Combined flags (e.g., `set -euo pipefail`) are decomposed.

### `source` / `.`

- `source file.sh` -> `. file.ps1` (extension rewritten)

### Env pairs

If a command has leading environment variable assignments
(`NAME=value cmd args`), they are emitted as `$env:NAME = "value"; cmd args`.

### General fallback

Words are emitted in order via `EmitWord`, redirects appended via
`EmitRedirect`.

---

## 6. `EmitPipeline`

`EmitPipeline` processes `Command.Pipeline` nodes:

1. The **first** command is always emitted via the general `Emit` dispatcher
   (not mapped).
2. **Subsequent** commands (pipe targets) are first tried through
   `TryEmitMappedCommand`. If the command is recognized, the mapped form is
   used. Otherwise, the general `Emit` path is used.
3. Pipe operators: `|` emits as ` | `, `|&` emits as ` 2>&1 | `.

This means command mapping only applies to pipe targets, never to the first
command in a pipeline.

---

## 7. Other Emitters

### `EmitWord`

Walks `CompoundWord` parts and concatenates their emitted text. Handles brace
expansion (e.g., `file{1,2,3}.txt` -> `@('file1.txt','file2.txt','file3.txt')`).
Calls `TransformWordPath` on the final result.

### `EmitDoubleQuoted`

Emits `"..."` with inner parts (literals, variable subs, command subs) rendered
inside the quotes.

### `EmitBracedVar`

Handles `${VAR...}` parameter expansions:

- `${VAR:-default}` -> `($env:VAR ?? "default")`
- `${VAR:=default}` -> `($env:VAR ?? ($env:VAR = "default"))`
- `${VAR:+alt}` -> `($env:VAR ? "alt" : "")`
- `${VAR:?msg}` -> `($env:VAR ?? $(throw "msg"))`
- `${#VAR}` -> `$env:VAR.Length`
- `${VAR%%pat}` / `${VAR%pat}` -> `-replace` suffix removal
- `${VAR##pat}` / `${VAR#pat}` -> `-replace` prefix removal
- `${VAR//find/rep}` / `${VAR/find/rep}` -> `-replace`
- `${VAR:offset:length}` -> `.Substring(offset, length)`
- `${VAR^^}` / `${VAR,,}` / `${VAR^}` / `${VAR,}` -> case conversion
- `${arr[n]}` -> `$arr[n]`, `${arr[@]}` -> `$arr`, `${#arr[@]}` -> `$arr.Count`
- `${!arr[@]}` -> `$arr.Keys`

### `EmitRedirect`

Maps redirects to PowerShell: `>file`, `>>file`, `2>&1`, etc. Calls
`TransformRedirectTarget` on the target.

### `TransformWordPath` / `TransformRedirectTarget`

- `/dev/null` -> `$null`
- `/tmp/file` -> `$env:TEMP\file`

### `EmitSimpleVar`

Maps bash special variables to PowerShell equivalents:

- `$?` -> `$LASTEXITCODE`
- `$@` / `$*` -> `$args`
- `$#` -> `$args.Count`
- `$0` -> `$MyInvocation.MyCommand.Name`
- `$$` / `$!` -> `$PID`
- `$1`..`$9` -> `$args[0]`..`$args[8]`
- `$HOME`, `$LASTEXITCODE`, `$null`, `$true`, `$false` -> kept as-is
- Loop variables -> `$var` (not `$env:var`)
- Everything else -> `$env:NAME`

---

## 8. Anti-patterns

Historical emitter implementations translated bash flags into PowerShell named
parameters. This caused persistent bugs because the emitter's translation logic
diverged from what the runtime `Invoke-Bash*` functions actually expected.

Examples of what went wrong:

| Command | Old (broken) emitter output | Problem |
|---------|---------------------------|---------|
| `head -n 5` | `Select-Object -First 5` | Bypassed the runtime entirely; `Invoke-BashHead` was never called. Broke when head had to handle `-c` or combined flags. |
| `cut -d, -f1` | `Invoke-BashCut -Delimiter , -Field 1` | Emitter parsed `-d` and `-f` and re-emitted them as named PS params. When the runtime changed its param names or parsing, emitter had to change too. Two parsers doing the same work. |
| `wc -l` | `Measure-Object -Line \| Select-Object -ExpandProperty Lines` | Completely native PS translation; no runtime function involved. Could not handle `wc -w`, `wc -c`, or bare `wc`. |
| `sort -r` | `Invoke-BashSort -r` | Partial extraction -- only `-r` was recognized. Other sort flags (`-n`, `-k`, `-t`) were silently dropped. |

The fix in every case was the same: **stop translating flags in the emitter,
forward everything to the runtime function, and let the runtime handle it.**

The passthrough pattern (`EmitPassthrough`) was introduced to enforce this
discipline. Adding a new mapped command requires only a switch case and a single
`EmitPassthrough` call -- no flag parsing logic in the emitter.
