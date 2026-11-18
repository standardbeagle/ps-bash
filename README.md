# PsBash

**Real bash commands on Windows. No WSL, Git Bash, or Cygwin required.**

PsBash transpiles bash to PowerShell — `rm -rf`, `ls -la`, `grep -r`, `sort -k3` all work natively. It runs whole scripts: functions, arrays, traps, `set -e`, `pipefail`, here-docs, process substitution and background jobs. Every command returns **typed .NET objects** while producing bash-identical text output. Includes a standalone shell binary that AI coding agents (Claude Code, OpenCode, GitHub Copilot) can use as a drop-in bash replacement.

**v0.15.0:** the bash spec suite (4,826 cases, expected output recorded from bash 5.2)
had 140 cases marked as expected failures in v0.14.5. It has 79 now, and each mark names
its reason. Three changes can break existing scripts. See [What's new](#whats-new-in-v0150)
and [Breaking changes](#breaking-changes-in-v0150).

[![CI](https://github.com/standardbeagle/ps-bash/actions/workflows/ci.yml/badge.svg)](https://github.com/standardbeagle/ps-bash/actions/workflows/ci.yml)
[![PSGallery](https://img.shields.io/powershellgallery/v/PsBash.svg?label=PSGallery)](https://www.powershellgallery.com/packages/PsBash)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey.svg)](#cross-platform)

## Quick Install

**As a PowerShell module:**

```powershell
Install-Module PsBash
```

**As a standalone shell binary (no .NET runtime required):**

```powershell
# Windows
iwr https://raw.githubusercontent.com/standardbeagle/ps-bash/main/install.ps1 | iex

# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/standardbeagle/ps-bash/main/install.sh | bash
```

Release archives are built for Windows x64, Linux x64 and macOS arm64. The installers
stop with an error on any other platform.

On Windows, `-InstallDir` must be empty or marked as a PsBash package by this installer.
Move an older unmarked installation aside before upgrading. Upgrades retain the old
package in a unique `.previous-*` directory; existing backups are preserved.

<picture>
  <source srcset="docs/assets/demo.webp" type="image/webp">
  <img src="docs/assets/demo.gif" alt="ps-bash demo showing typed pipeline objects" width="800">
</picture>

## What's new in v0.15.0

**Each line below behaved differently from bash in v0.14.5. In v0.15.0 it does what bash does:**

```bash
x=1; sh -c 'echo "[$x]"'                 # printed [1]. Now [], x is not exported
f() { echo x; exit 3; }
f | cat; echo "after ${PIPESTATUS[*]}"   # exit ended the whole script. Now: x, after 3 0
{ printf a; printf 'b\n'; } | sed s/x/y/ # printed a and b on two lines. Now ab
set -o pipefail
make build && make test | tee log        # under pipefail this was a parse error
echo it’s                                # a pasted ’ was a parse error
yes | while read l; do break; done       # never ended. Now stops at the break
{ echo a; cat out.txt >&2; } > out.txt   # printed nothing. Now a
echo "${PWD##*/}"                        # Windows: printed the whole C:\... path. Now the last component
```

**Checked against bash.** The spec suite under `spec/` holds 4,826 cases in 221 files;
2,538 of them come from the Oils spec tests. Expected output and exit status are
recorded from bash 5.2, and each case runs through ps-bash. 79 cases carry an XFAIL
(expected failure) mark, down from 140 in v0.14.5. A marked case that starts passing
fails the suite, so a fix has to remove its mark. Each mark names its reason:

- 58 are Windows facts: drive-letter paths, no `/bin`, a case-insensitive environment.
- 12 are interactive features ps-bash does not have: readline `bind`, prompt counters,
  job control.
- 9 are other known differences: a ps-bash command runs ahead of a `PATH` file of the
  same name (4), there is no SIGPIPE (2), a background job starts late on a loaded
  host (2), and `help` prints its own text (1).

The differential tests (`src/PsBash.Differential.Tests`) compare ps-bash's output and
exit status with bash's, which is recorded in checked-in cassettes. The list of
differences is [docs/specs/intentional-differences.md](docs/specs/intentional-differences.md).

**Background jobs.** A job that writes to the shell's own output prints as it runs, in
launch order, instead of at `wait`. A job gets its own copy of the shell's variables at the `&`.
`kill -USR1 $$` from a nested shell runs the script's trap.

**Processes.** Stopping a command (Ctrl+C, `timeout`) ends its whole process tree,
including grandchildren whose parent already exited. A nested `bash -c` or `sh -c`
streams its output as it is written, and ps-bash puts no time limit on it. A program
started with `2>&1` keeps its write order.

**macOS.** Scripts no longer die with status 138 and no output. Signal numbers, wait
statuses, `stat` data and terminal size come from one per-platform layer, which fixes
`kill`, `trap`, `ls -l`, `stat`, `find` and `umask` on macOS.
One limit: a SIGUSR1 sent from outside ps-bash does not reach a `trap … USR1` on macOS,
because the .NET runtime uses that signal itself.

**Busy machines.** A command that finds every host of its session busy for 2 seconds
runs on a private host that ends with it, instead of waiting for one to free. At most 2
such overflow hosts run per session (`PSBASH_EXEC_OVERFLOW_MAX`), so memory stays bounded.

## Breaking changes in v0.15.0

Three changes move ps-bash toward bash and can change what an existing script does.

### PowerShell aliases no longer run in bash code

A bash script used to resolve PowerShell's built-in aliases (about 135 of them) before
functions and programs. It no longer does. `gci`, `iex`, `r` and `write` now behave as
in bash: a function or a program on `PATH` with that name runs, otherwise the command
is not found (exit 127). `where` runs `where.exe`.

```bash
gci            # before: ran Get-ChildItem. Now: gci: command not found (127)
iex 'echo x'   # before: evaluated its argument as PowerShell. Now: not found (127)
write -- x     # before: ran Write-Output. Now: an ordinary command word, as in bash
```

Full cmdlet names still work (`Get-ChildItem`). Importing the module into your own
PowerShell session leaves that session's aliases alone.

### A child process sees exported variables only

```bash
x=1
sh -c 'echo "[$x]"'    # before: [1]   now: []
export x
sh -c 'echo "[$x]"'    # [1]
```

This holds for nested shells (`sh -c`, `bash script.sh`), programs started with `2>&1`,
`&>` or `|&`, and for `env`, `printenv` and `compgen -e`. Fix a script that relied on
the old behavior with `export NAME` or a prefix assignment (`NAME=v cmd`).

Not covered yet: a program started without a stderr merge (`prog args`, also inside
`$( )`, `xargs` or `find -exec`) and any `.cmd` or `.bat` file still inherit unexported
variables.

### Paths use forward slashes on Windows

Every path ps-bash hands a script is `C:/Users/x` instead of `C:\Users\x`: `$PWD`, `$OLDPWD`,
`~`, `$HOME`, `mktemp`, `realpath`, `readlink`, `type`, `which`, `$0` and glob results.
The drive letter stays; ps-bash never prints `/c/Users/x`.

```bash
echo "$PWD"          # before: C:\Users\x\proj   now: C:/Users/x/proj
echo "${PWD##*/}"    # before: the whole path    now: proj
```

Input is unchanged: `C:/x`, `C:\x`, `/c/x` and `/mnt/c/x` all name the same file. `PATH`
keeps its Windows form (`;` between entries). A script that matched on `\` in one of
these values needs `/` instead.

## Why PsBash?

PowerShell ships aliases for `ls`, `cat`, `sort`, `rm` — but they're lies. `rm -rf` fails. `ls -la` fails. `sort -k3 -rn` fails. They're `Get-ChildItem`, `Get-Content`, and `Sort-Object` wearing a disguise, rejecting every bash flag you throw at them.

PsBash makes the flags work — and goes further. Every command returns **typed .NET objects** while producing bash-identical text output.

### 1. Parse process data without awk

```powershell
# Before: string parsing
PS> Get-Process | Format-Table -Auto | Out-String | ForEach-Object { $_ -split "`n" } |
>>   Where-Object { $_ -match 'pwsh' }
# Good luck extracting CPU from that

# After: typed objects through the entire pipeline
PS> $top = ps aux | sort -k3 -rn | head -5
PS> $top[0].ProcessName   # pwsh
PS> $top[0].CPU           # 12.4     (decimal, not a string)
PS> $top[0].PID           # 1847     (integer, not a substring)
PS> $top | Where-Object { $_.CPU -gt 5.0 } | ForEach-Object { Stop-Process $_.PID -WhatIf }
```

### 2. Pipeline bridge: objects survive grep and sort

```powershell
PS> ls -la | grep '.ps1' | sort -k5 -h

-rw-r--r-- 1 beagle wheel     4521 Apr  2 00:57 PsBash.psd1
-rw-r--r-- 1 beagle wheel    89234 Apr  2 09:46 PsBash.Tests.ps1
-rw-r--r-- 1 beagle wheel   141234 Apr  2 09:46 PsBash.psm1

# grep matched the TEXT, but passed through the TYPED OBJECTS:
PS> $files = ls -la | grep '.ps1' | sort -k5 -h
PS> $files[0].GetType().Name       # PSCustomObject (LsEntry)
PS> $files.Name                    # PsBash.psd1, PsBash.Tests.ps1, PsBash.psm1
PS> $files.SizeBytes | Measure-Object -Sum | Select -Expand Sum
234989
```

### 3. Built-in jq, awk, sed — no external binaries

```powershell
PS> echo '{"db":"postgres","port":5432}' | jq '.db'
"postgres"

PS> cat app.log | grep ERROR | awk '{print $1, $3}' | sort | uniq -c | sort -rn | head -5
      47 2024-01-15 DatabaseError
      23 2024-01-15 TimeoutError
      12 2024-01-15 AuthError

PS> find . -name '*.log' -mtime +7 | xargs rm -f    # actually works on Windows
```

## Two Modes: Module and Shell

PsBash can be used as a **PowerShell module** or a **standalone shell**. They share the same commands and typed object pipeline.

### Module — bash commands inside PowerShell

```powershell
Install-Module PsBash
ls -la | grep '.cs' | sort -k5 -h        # typed objects through the pipeline
$procs = ps aux | sort -k3 -rn | head -5  # .PID, .CPU are real .NET types
```

Best for: scripts, VS Code terminal, CI/CD, mixing bash with PowerShell cmdlets.

### Shell — standalone bash terminal on Windows

```bash
# Launch as your shell
ps-bash

# Inside: bash aliases, .psbashrc, colored prompt, external tools
ll                           # alias: ls -al
cd ~/projects && git status  # cd + external git
claude                       # full interactive AI session
```

Best for: Windows Terminal, AI coding agents (Claude Code, OpenCode), replacing WSL/Git Bash.

→ **See the full [Shell Guide](docs/shell-guide.md)** for setup, configuration, mixing bash + PowerShell, and limitations.

## Using ps-bash inside PowerShell

Install the `PsBash` module, which bundles the binary cmdlets. PowerShell 7.4 or later is required:

```powershell
Install-Module PsBash
Import-Module PsBash
```

Quickstart:

```powershell
# Run bash through the separately installed shell executable
ps-bash -c 'ls -la | grep .ps1'

# Source a bash script into the current PowerShell session
Invoke-BashSource ./my-script.sh

# Test if a bash command is valid syntax
Test-BashSyntax 'echo hello | wc -l'
```

→ See the [Shell Guide](docs/shell-guide.md) for details on dynamic-env tools and advanced usage.

`Invoke-BashEval` is experimental and excluded from default builds. `Format-Styled` and
`Show-Styled` require an optional Strata build dependency and are absent from builds
without it. The shell, source and syntax commands above do not require those features.

`set -e`, `set -u`, `set -o pipefail` and `set -C` are supported; their limits are listed
in [docs/known-issues.md](docs/known-issues.md). The launcher takes `-e`, `-u`, `-x`, `-v`,
`-o` and `-O` with `-c`, a script file or `-s`, as bash does. An unsupported launcher
flag exits 2.

## AI Coding Agent Setup

Ps-bash works as a drop-in bash replacement for AI coding agents on Windows. Agents invoke `ps-bash -c "command"` and it transpiles bash to PowerShell transparently.

### Compact Output Mode

Agent runs can opt into compact output mode with `--compact-output` or
`PSBASH_COMPACT_OUTPUT=1`. The launcher normalizes the setting and passes it to
the host/output layer as `PSBASH_COMPACT_OUTPUT=1`; without either setting,
behavior is unchanged. An explicit CLI flag wins over the environment, so
`--no-compact-output` disables compaction for a single invocation even when the
environment variable is set. `--caveman` and `--wenyan` are accepted aliases for
the same opt-in mode.

When enabled, ps-bash keeps command identity, exit code, timeout status, stream
labels, stderr presence, file/line diagnostics, and useful tail context while
collapsing repeated lines and verbose progress/listing noise.

Use compact mode for agent and CI log capture where token volume matters. Leave
it off when you need exact byte-for-byte stdout/stderr, full progress logs, or
human-facing interactive output.

```powershell
ps-bash --compact-output -c "dotnet test"
$env:PSBASH_COMPACT_OUTPUT = '1'
ps-bash -c "git status"
ps-bash --no-compact-output -c "git status"
```

### Quick Setup for Claude Code

Claude Code only accepts a Windows shell whose file is named `bash.exe` (or
`sh.exe`); it silently falls back to Git Bash for any other name. So give it
ps-bash under that name — a copy of `ps-bash.exe` placed **in the same folder**
(it keeps loading `ps-bash.dll` beside it). `install-local.ps1` creates it for you:

```powershell
Copy-Item $env:USERPROFILE\.local\bin\ps-bash.exe $env:USERPROFILE\.local\bin\bash.exe
```

Then in `~/.claude/settings.json`, and restart Claude Code (it picks its shell at startup):

```json
{
  "env": {
    "CLAUDE_CODE_GIT_BASH_PATH": "C:\\Users\\you\\.local\\bin\\bash.exe",
    "PSBASH_UNIX_PATHS": "1"
  }
}
```

Verify from a new session: `echo $BASH_VERSION` in the Bash tool prints
`0.x.0(1)-release` (ps-bash), not `4.4.x`/`5.x` (Git Bash).

### Quick Setup for OpenCode

```powershell
$env:SHELL = 'C:\Users\you\.local\bin\ps-bash.exe'
opencode
```

### Quick Setup for GitHub Copilot (VS Code)

In VS Code `settings.json`:

```json
{
  "terminal.integrated.profiles.windows": {
    "ps-bash": {
      "path": "C:\\Users\\you\\.local\\bin\\ps-bash.exe"
    }
  },
  "terminal.integrated.defaultProfile.windows": "ps-bash"
}
```

### Supported Agents

| Agent | Config Method | Setting |
|-------|--------------|---------|
| **Claude Code** | `settings.json` env (restart after) | `CLAUDE_CODE_GIT_BASH_PATH=C:\path\to\bash.exe` (a copy of `ps-bash.exe`, same folder) |
| **OpenCode** | `$SHELL` environment variable | `$env:SHELL = 'C:\path\to\ps-bash.exe'` |
| **GitHub Copilot** | VS Code terminal profile | `terminal.integrated.defaultProfile.windows` |
| **Gemini CLI** | Not configurable (hardcoded shell) | Run inside ps-bash interactive shell |

See [docs/agent-setup.md](docs/agent-setup.md) for detailed per-agent configuration, Docker setup, and troubleshooting.

## Feature Comparison

| | **PsBash** | **Git Bash** | **Cygwin** | **WSL** | **Crescendo** |
|---|---|---|---|---|---|
| Install | `Install-Module` | Git for Windows | 50+ packages | Hyper-V/WSL2 | Per-command XML |
| Footprint | One module, or one self-contained package | ~300 MB (MSYS2 runtime) | ~1-4 GB | ~1-15 GB (full distro) | Module + your wrappers |
| `rm -rf` works | Yes | Yes | Yes | Yes | If you configure it |
| Typed objects | Always | Never (strings) | Never (strings) | Never (strings) | If configured |
| Object pipeline | Types survive `grep \| sort \| head` | Strings only | Strings only | Strings only | Varies |
| PowerShell integration | Native — objects flow into cmdlets | Separate shell | Separate shell | Separate shell | Native |
| Cross-platform | Win/Lin/Mac | Windows only | Windows only | Windows only | Win/Lin/Mac |
| Commands | 118 built-in | ~80 (GNU coreutils) | ~200+ (full GNU) | All of Linux | Define your own |
| jq/awk/sed | Built-in, zero binaries | awk/sed yes, jq no | Yes (install pkg) | Yes (apt install) | Not included |
| PATH conflicts | None (AllScope aliases) | Shadows PowerShell | Shadows PowerShell | Filesystem boundary | None |
| AI agent shell | Yes (`ps-bash -c`) | No | No | No | No |
| Interactive mode | Yes (aliases, prompt, external tools) | Yes (full bash) | Yes (full bash) | Yes (full Linux) | No |

## Commands

The module registers 118 bash and Unix command names as cmdlets, listed below. The
`ps-bash` shell adds bash's builtins on top: `read`, `declare`, `local`, `export`,
`eval`, `set` and the others.

| Category | Commands |
|----------|----------|
| **Listing** | `ls` `find` `stat` `tree` `du` `pwd` `basename` `dirname` `realpath` `readlink` `dirs` |
| **Files** | `cp` `mv` `rm` `mkdir` `rmdir` `touch` `ln` `chmod` `install` `mktemp` |
| **Content** | `cat` `head` `tail` `less` `more` `tac` `wc` `nl` `rev` `strings` `fold` `expand` `unexpand` `split` `od` |
| **Search** | `grep` `rg` |
| **Text** | `sed` `awk` `cut` `tr` `uniq` `sort` `shuf` `column` `join` `paste` `comm` `diff` |
| **Pipeline** | `tee` `xargs` `yes` |
| **Data** | `jq` `yq` `xan` |
| **System** | `ps` `kill` `env` `printenv` `date` `hostname` `uname` `id` `whoami` `which` `nohup` `timeout` `flock` `time` `times` `sleep` `tput` `umask` `ulimit` `ping` `traceroute` |
| **Shell** | `cd` `pushd` `popd` `source` `command` `type` `hash` `alias` `unalias` `unset` `shift` `getopts` `let` `test` `[` `trap` `shopt` `mapfile` `readarray` `compgen` `history` `fc` `bash` `sh` |
| **Jobs** | `jobs` `fg` `bg` `wait` |
| **Output** | `echo` `printf` |
| **Encoding** | `base64` `md5sum` `sha1sum` `sha256sum` `file` |
| **Archive** | `gzip` `gunzip` `zcat` `tar` |
| **Math** | `seq` `expr` |

A real option that ps-bash does not implement is refused with exit 2 and a message.
The refused options are listed in
[intentional-differences.md](docs/specs/intentional-differences.md#not-implemented-refused-exit-2),
with per-command detail in [runtime-command-reference.md](docs/specs/runtime-command-reference.md).

## PsBash vs Native PowerShell

| Task | PsBash | Native PowerShell |
|------|--------|-------------------|
| List files | `ls -la` | `Get-ChildItem -Force \| Format-List` |
| Find by name | `find . -name '*.ps1'` | `Get-ChildItem -Recurse -Filter *.ps1` |
| Top 5 CPU | `ps aux \| sort -k3 -rn \| head -5` | `Get-Process \| Sort-Object CPU -Desc \| Select -First 5` |
| Count lines | `wc -l file.txt` | `(Get-Content file.txt).Count` |
| Search files | `grep -r 'TODO' src/` | `Get-ChildItem src -Recurse -File \| Select-String TODO` |
| JSON field | `cat f.json \| jq '.name'` | `(Get-Content f.json \| ConvertFrom-Json).name` |
| Delete tree | `rm -rf dist/` | `Remove-Item dist/ -Recurse -Force` |
| Disk usage | `du -sh * \| sort -rh` | `Get-ChildItem \| Sort Length -Desc \| Select Name,Length` |

Both sides give you objects. PsBash just lets you type what you already know.

## How It Works

Every PsBash command returns a PSCustomObject with a `.BashText` property. The terminal renders BashText (looks like real bash). Your code accesses typed properties.

```
  ls -la
    |
  Invoke-BashLs
    |
  [LsEntry] objects
    .Name         = "README.md"
    .SizeBytes    = 4521             [int]
    .Permissions  = "-rw-r--r--"     [string]
    .Owner        = "beagle"         [string]
    .LastModified = 2024-04-02...    [DateTime]
    .BashText     = "-rw-r--r-- 1 beagle wheel  4521 Apr  2 ..."
```

Pipeline commands (`grep`, `sort`, `head`, `tail`, `tee`) match against BashText but pass through the **original typed objects**. This is the pipeline bridge — the core architectural pattern.

The standalone `ps-bash` binary adds a transpiler layer:

```
bash input → BashLexer → BashParser → PsEmitter → IpcWorker → ps-bash-host/SdkWorker → PowerShell SDK
```

The parser tokenizes and parses bash into an AST modeled on Oils syntax.asdl. The emitter maps bash commands to `Invoke-Bash*` cmdlets, forwarding all arguments unchanged. The cmdlets parse the flags themselves.

## Testing

The first two layers check behavior against bash; the third tests ps-bash's own parts:

- **Spec suite** (`spec/`): 4,826 cases in 221 files, 2,538 of them imported from the
  Oils spec tests (`spec/oils`). Expected output and status are recorded from bash 5.2.
  79 cases carry an XFAIL mark with a reason id.
- **Differential tests** (`src/PsBash.Differential.Tests`): ps-bash's output and exit
  status must match bash's, recorded in checked-in cassettes (`PSBASH_ORACLE_LIVE=1`
  runs bash live instead).
- **Unit and guard tests** per project, plus the Pester suite in `tests/`.

Build and test through `tman` (see [CLAUDE.md](CLAUDE.md)), which serializes builds and
test runs:

```bash
tman test                                   # every suite
tman test-proj src/PsBash.Differential.Tests --filter "FullyQualifiedName~Pipefail"
tman pester                                 # the Pester gate
```

CI runs on Windows, Linux, and macOS via GitHub Actions.

## Cross-Platform

| Feature | Linux/macOS | Windows |
|---------|-------------|---------|
| File permissions | Real POSIX (`rwxr-xr-x`) | Synthesized: `0644` files, `0755` directories and executables |
| `ln -s` | Works normally | Needs the symlink privilege (Developer Mode) |
| Paths a script sees | `/home/you/x` | `C:/Users/you/x` |
| `x=1; X=2` | Two variables | One variable (the environment is case-insensitive) |
| `PATH` separator | `:` | `;` |

Signal numbers use bash's Linux numbering on every OS, so `kill -l`, `trap … USR1` and a
`128+N` exit status read the same on macOS as on Linux. bash on macOS uses macOS's
numbers (USR1 is 30 there).

## Documentation

**[dev.standardbeagle.com/ps-bash](https://dev.standardbeagle.com/ps-bash/)** — full command reference, object type specs, pipeline cookbook, cross-platform guide.

- **[Production Readiness Review](docs/production-readiness-review-2026-10-18.md)** — verified blockers, existing defenses, and release evidence
- **[Production User Journeys](docs/planning/production-user-journeys.md)** — current shell use cases and proposed remote-session acceptance criteria
- **[Interactive Shell Guide](docs/shell-guide.md)** — running ps-bash as a shell, .psbashrc config, mixing bash and PowerShell, command routing
- **[Agent Setup Guide](docs/agent-setup.md)** — configure Claude Code, OpenCode, Copilot, and Gemini CLI to use ps-bash
- **[Parser Grammar](docs/specs/parser-grammar.md)** — tokens, AST nodes, grammar productions
- **[Emitter Strategy](docs/specs/emitter-strategy.md)** — passthrough principle, pipe mappings
- **[Runtime Functions](docs/specs/runtime-functions.md)** — BashObject model, arg-parsing patterns, adding a command
- **[Command Reference](docs/specs/runtime-command-reference.md)** — per-command flag / arg-parsing lookup table
- **[Migrated Binary Cmdlets](docs/specs/runtime-migrated-cmdlets.md)** — REFACTOR-2 binary-cmdlet migration history

## License

MIT

## Contributing

Issues and PRs welcome.
