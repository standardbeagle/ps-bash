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

There are no exceptions -- every mapped command uses the same `EmitPassthrough`
path.

---

## 2. Command Mapping (`TryEmitMappedCommand`)

`TryEmitMappedCommand` is called in two contexts:

1. **Pipe targets** -- every command after the first in a pipeline.
2. **Standalone commands** -- any `Command.Simple` whose command name is in the
   mapping table and is **not** a PowerShell builtin alias (see Section 2.2).

It inspects the command name and dispatches to `EmitPassthrough`. All 66 mapped
commands use the same `EmitPassthrough` path with no exceptions.

### 2.1 Mapped Commands (66 total)

| Bash command | PowerShell function      |
|--------------|--------------------------|
| `grep`       | `Invoke-BashGrep`        |
| `head`       | `Invoke-BashHead`        |
| `tail`       | `Invoke-BashTail`        |
| `wc`         | `Invoke-BashWc`          |
| `sort`       | `Invoke-BashSort`        |
| `uniq`       | `Invoke-BashUniq`        |
| `sed`        | `Invoke-BashSed`         |
| `awk`        | `Invoke-BashAwk`         |
| `cut`        | `Invoke-BashCut`         |
| `xargs`      | `Invoke-BashXargs`       |
| `tr`         | `Invoke-BashTr`          |
| `tee`        | `Invoke-BashTee`         |
| `echo`       | `Invoke-BashEcho`        |
| `printf`     | `Invoke-BashPrintf`      |
| `cat`        | `Invoke-BashCat`         |
| `ls`         | `Invoke-BashLs`          |
| `find`       | `Invoke-BashFind`        |
| `stat`       | `Invoke-BashStat`        |
| `cp`         | `Invoke-BashCp`          |
| `mv`         | `Invoke-BashMv`          |
| `rm`         | `Invoke-BashRm`          |
| `mkdir`      | `Invoke-BashMkdir`       |
| `rmdir`      | `Invoke-BashRmdir`       |
| `touch`      | `Invoke-BashTouch`       |
| `ln`         | `Invoke-BashLn`          |
| `ps`         | `Invoke-BashPs`          |
| `rev`        | `Invoke-BashRev`         |
| `nl`         | `Invoke-BashNl`          |
| `diff`       | `Invoke-BashDiff`        |
| `comm`       | `Invoke-BashComm`        |
| `column`     | `Invoke-BashColumn`      |
| `join`       | `Invoke-BashJoin`        |
| `paste`      | `Invoke-BashPaste`       |
| `jq`         | `Invoke-BashJq`          |
| `date`       | `Invoke-BashDate`        |
| `seq`        | `Invoke-BashSeq`         |
| `expr`       | `Invoke-BashExpr`        |
| `du`         | `Invoke-BashDu`          |
| `tree`       | `Invoke-BashTree`        |
| `env`        | `Invoke-BashEnv`         |
| `basename`   | `Invoke-BashBasename`    |
| `dirname`    | `Invoke-BashDirname`     |
| `pwd`        | `Invoke-BashPwd`         |
| `hostname`   | `Invoke-BashHostname`    |
| `whoami`     | `Invoke-BashWhoami`      |
| `fold`       | `Invoke-BashFold`        |
| `expand`     | `Invoke-BashExpand`      |
| `unexpand`   | `Invoke-BashUnexpand`    |
| `strings`    | `Invoke-BashStrings`     |
| `split`      | `Invoke-BashSplit`       |
| `tac`        | `Invoke-BashTac`         |
| `base64`     | `Invoke-BashBase64`      |
| `md5sum`     | `Invoke-BashMd5sum`      |
| `sha1sum`    | `Invoke-BashSha1sum`     |
| `sha256sum`  | `Invoke-BashSha256sum`   |
| `file`       | `Invoke-BashFile`        |
| `rg`         | `Invoke-BashRg`          |
| `gzip`       | `Invoke-BashGzip`        |
| `tar`        | `Invoke-BashTar`         |
| `yq`         | `Invoke-BashYq`          |
| `xan`        | `Invoke-BashXan`         |
| `sleep`      | `Invoke-BashSleep`       |
| `time`       | `Invoke-BashTime`        |
| `which`      | `Invoke-BashWhich`       |

Commands not in this table are emitted via the general `Emit` path (i.e., they
are not rewritten).

### 2.2 Standalone Mapping

When a mapped command appears as a standalone statement (not a pipe target),
`EmitSimple` also routes it through `TryEmitMappedCommand` -- unless the command
name is in the `PsBuiltinAliases` set. PowerShell has built-in aliases for
common commands (`echo`, `cat`, `ls`, `cd`, `pwd`, `mkdir`, `cp`, `mv`, `rm`,
`sort`, `diff`, `sleep`) that resolve correctly without rewriting. These are
excluded from standalone mapping to avoid unnecessary `Invoke-Bash*` calls when
the native alias already does the right thing.

For all other mapped commands used standalone (e.g., `grep file.txt`, `awk '{print}'
data.csv`), the emitter rewrites them to `Invoke-Bash*` calls so the runtime
function handles flag parsing.

---

## 3. `EmitPassthrough`

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

## 4. `EmitSimple` -- The Main Command Emitter

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
- `set -u` / `set -o nounset` -> `Set-StrictMode -Version Latest`
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

## 5. `EmitPipeline`

`EmitPipeline` processes `Command.Pipeline` nodes:

1. The **first** command is always emitted via the general `Emit` dispatcher
   (not mapped).
2. **Subsequent** commands (pipe targets) are first tried through
   `TryEmitMappedCommand`. If the command is recognized, the mapped form is
   used. Otherwise, the general `Emit` path is used.
3. Pipe operators: `|` emits as ` | `, `|&` emits as ` 2>&1 | `.

Note: standalone commands are also mapped via `EmitSimple` (see Section 2.2),
so mapping applies to both pipe targets and standalone invocations.

---

## 6. Other Emitters

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

## 7. Anti-patterns

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
