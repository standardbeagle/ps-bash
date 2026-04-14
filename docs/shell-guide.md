# Using ps-bash

Ps-bash has two modes: the **PsBash module** and the **ps-bash shell**. They share the same 76 bash commands and typed object pipeline, but serve different use cases.

## PsBash Module

The PowerShell module (`Install-Module PsBash`) adds bash commands directly into your PowerShell session. Use this when you want bash commands available alongside `Get-ChildItem`, `ForEach-Object`, and everything else PowerShell offers.

```powershell
Install-Module PsBash
ls -la                          # LsEntry objects
ps aux | sort -k3 -rn | head 5  # PsEntry objects, sorted by CPU
cat README.md | grep 'bash'     # text search, typed objects
```

### How it works

Every command returns a `PSCustomObject` with typed .NET properties and a `.BashText` string property. The console renders BashText (looks like real bash). Your code accesses typed properties.

```
ls -la
  → Invoke-BashLs
  → [LsEntry] objects
      .Name         = "README.md"        [string]
      .SizeBytes    = 4521               [int]
      .Permissions  = "-rw-r--r--"       [string]
      .Owner        = "beagle"           [string]
      .LastModified = 2024-04-02...      [DateTime]
      .BashText     = "-rw-r--r-- 1..."  [string]
```

Pipeline commands (`grep`, `sort`, `head`, `tail`, `tee`) match against BashText but pass through the **original typed objects**. This is the pipeline bridge.

### Mixing with PowerShell

Since PsBash commands are just PowerShell functions, they work seamlessly with native cmdlets:

```powershell
# Bash to PowerShell
$files = ls -la | grep '.ps1' | sort -k5 -h
$files | Where-Object { $_.SizeBytes -gt 50KB }
$files | ForEach-Object { Write-Host $_.Name }

# Bash command, PowerShell comparison
$top = ps aux | sort -k3 -rn | head 5
$top | Where-Object { $_.CPU -gt 5.0 } | ForEach-Object { Stop-Process $_.PID -WhatIf }

# PowerShell in, bash out
Get-Content errors.log | grep 'FATAL' | sort | uniq -c
```

### Setup

```powershell
# Install from PSGallery
Install-Module PsBash

# Add to your PowerShell profile for permanent access
Add-Content $PROFILE "`nImport-Module PsBash"
```

## ps-bash Shell

The standalone `ps-bash` binary is a full interactive shell. Use it as your terminal shell on Windows — it gives you bash syntax, aliases, a colored prompt, and runs external tools like `claude`, `copilot`, and `git` directly.

### How it works

The shell runs a persistent PowerShell worker process behind the scenes. When you type a command:

```
Input
  │
  ├─ Is it an alias? ───→ Expand, re-evaluate
  │
  ├─ Is it a bash builtin or mapped command?
  │   (cd, ls, grep, echo, sort, find, ps, ...)
  │   ───→ Transpile to PowerShell, execute in worker
  │
  ├─ Is it a pipeline or has redirects?
  │   ───→ Transpile the whole pipeline, execute in worker
  │
  └─ Otherwise (external tool)
      (claude, git, npm, dotnet, python, code, ...)
      ───→ Process.Start() directly on the console
```

**Transpiled commands** go through `BashParser → PsEmitter → PwshWorker`. They run inside the persistent PowerShell session, so variables and working directory persist between commands.

**External tools** bypass the worker entirely. They get the real console — stdin, stdout, Ctrl+C, ANSI colors, raw mode, alternate screen buffer. This is why `claude`, `copilot`, and `git` work interactively.

### Setup

1. **Install the binary:**

```powershell
iwr https://raw.githubusercontent.com/standardbeagle/ps-bash/main/install.ps1 | iex
```

2. **Add to Windows Terminal** — Settings → Add new profile:

| Setting | Value |
|---------|-------|
| Name | ps-bash |
| Command line | `C:\Users\you\.local\bin\ps-bash.exe` |
| Starting directory | `C:\Users\you` |

3. **Create `~/.psbashrc`:**

```bash
# ~/.psbashrc — sourced on interactive shell launch
alias ll='ls -al'
alias la='ls -a'
alias grep='grep --color=auto'
alias cdsp='claude --dangerously-skip-permissions'

export EDITOR="code"
if [ -f ~/.env ]; then set -a; source ~/.env; set +a; fi
```

4. **Launch** — Open the ps-bash Windows Terminal tab.

### The prompt

```
andyb@my-pc:~/projects/my-app (main) $
```

- **`andyb@my-pc`** — user@host (green, bold)
- **`~/projects/my-app`** — working directory with `~` substitution (cyan, bold)
- **`(main)`** — git branch (green = clean, red = dirty)
- **`$`** — `$` for users, `#` for admin (magenta, bold)

### Multi-line input

The shell detects incomplete constructs and shows a continuation prompt:

```bash
andyb@pc:~/app $ if [ -f package.json ]; then
> echo "found it"
> fi
found it

andyb@pc:~ $ greet() {
> echo "Hello, $1"
> }

andyb@pc:~ $ greet World
Hello, World
```

Works for `if/fi`, `for/do/done`, `while/do/done`, `case/esac`, functions, braces, parens, and unclosed quotes.

### Aliases

```bash
alias ll='ls -al'          # define
alias                       # list all
alias ll                    # show one
unalias ll                  # remove
unalias -a                  # remove all
```

Aliases expand the first word of input before the transpiler sees it. So `ll` becomes `ls -al`, then `ls` is mapped to `Invoke-BashLs`.

### Autosuggestions

As you type, the shell suggests commands from your history in gray (dimmed) text after your cursor:

```
andyb@pc:~/app $ git comm
                    it -m "fix parser bug"
```

Press `Right` or `End` to accept the full suggestion, or keep typing to ignore it.

**Key behaviors:**
- Suggestions match the **beginning** of history entries (prefix match)
- Commands from your **current directory** are suggested first
- Most recent commands are preferred when multiple matches exist
- Suggestions are **disabled** when the tab completion menu is active
- Configure via `~/.psbash/config.toml`:

```toml
[completion]
autosuggestions = true   # Enable (default) or disable
```

This is fish shell's most praised feature — it dramatically reduces typing for repeated commands. See [docs/specs/autosuggestions.md](specs/autosuggestions.md) for full details.

### Environment variables

```bash
export EDITOR="code"        # set in the worker
export PATH="$HOME/.local/bin:$PATH"
```

### Ctrl+C handling

- **Transpiled commands**: Cancels execution, prints `^C`, restarts the worker
- **External tools**: Passes through to the child process — `claude` and `git` handle it themselves

### Exit

`exit`, `exit N`, `logout`, or `Ctrl+D`.

## Choosing Between the Module and the Shell

Use this guide to decide which mode fits your workflow.

### Use the module when:

- You already live in PowerShell and want bash commands added to it
- You're writing scripts that mix bash and PowerShell idioms
- You want typed .NET objects from bash commands to pipe into cmdlets
- You're in VS Code's integrated PowerShell terminal
- You're in a CI/CD pipeline running PowerShell

```powershell
# Your PowerShell profile
Import-Module PsBash

# Now bash commands are just PowerShell functions
ls -la | Where-Object { $_.SizeBytes -gt 1MB }
```

### Use the shell when:

- You want a dedicated terminal that speaks bash natively
- You're running AI coding agents (Claude Code, OpenCode, Copilot)
- You want aliases, `.psbashrc`, and a bash-like prompt
- You're switching from WSL/Git Bash and want a Windows-native equivalent
- You want to run `claude`, `copilot`, or `gemini` interactively

```bash
# Launch ps-bash as your shell
ps-bash

# Inside: bash commands work
ll                           # alias: ls -al
cd ~/projects && git status  # cd + git

# External tools work too
claude                       # full interactive session
```

### Use both when:

- Module for scripts and VS Code, shell for terminal
- They share the same command implementations — what you learn in one transfers to the other

### Comparison

| | **Module** | **Shell** |
|---|---|---|
| Install | `Install-Module PsBash` | Download binary |
| Access | Inside PowerShell sessions | Standalone terminal |
| Prompts | Your PowerShell prompt | `user@host:cwd (branch) $` |
| Aliases | PowerShell aliases | `alias` builtin + `.psbashrc` |
| Startup file | `$PROFILE` | `~/.psbashrc` |
| External tools | Via PowerShell (`& claude`) | Direct console execution |
| History | PSReadLine (rich) | `Console.ReadLine` (basic) |
| Tab completion | Full (flags, files) | None yet |
| AI agent use | No | Yes (`SHELL=ps-bash`) |
| Multi-line | PSReadLine handles it | Built-in `> ` continuation |
| Variable sharing | Native PS variables | Variables in the worker session |
| `.psbashrc` | Not sourced | Sourced on startup |
| `Ctrl+C` | PS handles it | Worker restart / child signal |

## Mixing Bash and PowerShell in the Shell

Since the shell's worker is a persistent PowerShell session, you can freely mix syntaxes:

### Bash commands

```bash
ls -la | grep '.cs' | sort -k5 -h
cat README.md | wc -l
find . -name '*.ps1' -type f
```

### PowerShell commands

Anything the bash parser doesn't recognize passes through to PowerShell:

```
Get-Process | Where-Object { $_.CPU -gt 5 }
$PSVersionTable
Write-Host "hello"
Get-Date -Format 'yyyy-MM-dd'
```

### Bash to PowerShell bridge

```bash
# bash produces typed objects, PowerShell filters them
ls -la | Where-Object { $_.SizeBytes -gt 100KB }

# bash processes, PowerShell stops them
$top = ps aux | sort -k3 -rn | head 5
$top | ForEach-Object { Stop-Process $_.PID -WhatIf }
```

### Pipeline matrix

| From | To | Example |
|------|----|---------|
| Bash → Bash | `ls \| grep foo` | Fully transpiled, typed objects flow |
| PS → PS | `Get-Process \| Sort-Object CPU` | Passed through verbatim |
| Bash → PS | `ls -la \| Where-Object { $_.SizeBytes -gt 1MB }` | Bash objects, PS filter |
| PS → Bash | `Get-Content log \| grep ERROR` | PS output, bash filter |

### Variables

```bash
# bash variable assignment
files=$(find . -name '*.cs' -type f)

# access in PowerShell context
$files | Measure-Object -Line

# PowerShell variable (set in the worker)
$procs = Get-Process
# use in next bash command's $() substitution
```

Variables persist across commands because the worker session is long-lived.

## Limitations

These are the known gaps in priority order. Each includes an estimated effort and what would be involved in fixing it.

### 1. No command history — **Small effort**

The shell uses .NET's `Console.ReadLine()` which has no history buffer. Pressing Up does nothing.

**Fix:** Replace `Console.ReadLine()` with a readline library that supports history, reverse-search (`Ctrl+R`), and line editing. Options:
- Use `System.ReadLine` NuGet package
- Port a minimal GNU readline wrapper
- Use PowerShell's PSReadLine via the worker

**Impact:** This is the single biggest UX gap. Every shell needs Up-arrow history.

### 2. No tab completion — **Medium effort**

The module has full tab completion for flags (`ls -[Tab]`), but the shell has none. No command completion, no file completion, no flag completion.

**Fix:** The worker already has the PsBash module loaded with `TabExpansion` definitions. Wire `Console.TreatControlCAsInput` + `Tab` key detection to request completions from the worker via a protocol message, then render inline.

### 3. Glob expansion inconsistent across commands — **Small effort**

`ls *.txt` works because `Invoke-BashLs` uses `-Filter`. `cat *.txt` works because PowerShell resolves it. But `rm *.bak` or `mv *.tmp /tmp/` may not expand as expected because glob resolution depends on the individual command implementation.

**Fix:** Add a shell-level glob expansion step before sending to the worker — expand `*.txt` to the matching files using `Directory.GetFiles()`, then pass the expanded list to the command.

### 4. First-word-only alias expansion — **Small effort**

Only the first word of input is checked for alias expansion. `sudo ll` won't expand `ll`. `git commit -m "fix" && ll` won't expand `ll` after `&&`.

**Fix:** After splitting on `&&`, `||`, `;`, and `|`, expand aliases on the first word of each segment.

### 5. `FOO=bar command` falls back to worker — **Small effort**

`EDITOR=nano crontab -e` has env pairs, so `TryRunDirect` rejects it and sends it through the worker. The worker handles env vars then runs the command — but it runs inside the pipe protocol, not on the console. Interactive tools launched this way won't get a terminal.

**Fix:** In `TryRunDirect`, extract env pairs, set them as environment variables on the `ProcessStartInfo`, and still run directly.

### 6. Variable/glob arguments fall back to worker — **Small effort**

`git log $BRANCH` or `rm *.bak` contain non-literal word parts, so `TryRunDirect` rejects them. The worker handles them fine, but the command loses direct console access.

**Fix:** For globs, expand before checking. For variables, resolve them by sending a quick `$var` query to the worker first. Or accept the fallback for variable cases (the worker handles them correctly).

### 7. No customizable prompt — **Small effort**

The prompt format is hardcoded in C#. Users can't configure colors, layout, or add custom segments.

**Fix:** Support `PROMPT_COMMAND` or a `.psbashrc` prompt function. The shell evaluates the function each cycle and uses the result instead of the built-in prompt.

### 8. No `PROMPT_COMMAND` / preexec hook — **Small effort**

No mechanism to run code before each prompt display (for custom window titles, tmux integration, etc.).

**Fix:** After each command completes and before displaying the prompt, evaluate a `PROMPT_COMMAND` variable from the worker.

### 9. Worker state not synced to shell — **Medium effort**

The PowerShell worker has its own `$PWD`, `$LASTEXITCODE`, variables. The shell tracks `_lastDir` separately by parsing `cd` commands. If the worker's CWD drifts (e.g., a PowerShell function changes it), the prompt shows the wrong directory.

**Fix:** After each command, query the worker's `$PWD` and sync `_lastDir`. This also fixes `pushd`/`popd` prompt tracking.

---

## Priority Summary

| # | Limitation | Effort | Impact |
|---|-----------|--------|--------|
| 1 | No command history | Small | Critical UX — every shell needs this |
| 2 | No tab completion | Medium | Major UX — expected in any modern shell |
| 3 | Glob expansion inconsistent | Small | High — `rm *.bak` should just work |
| 4 | First-word-only alias expansion | Small | Medium — `sudo ll` is common |
| 5 | `FOO=bar cmd` falls back to pipe | Small | Medium — needed for env-prefixed tools |
| 6 | Variable/glob args fall back | Small | Low — worker handles it correctly |
| 7 | No customizable prompt | Small | Medium — personalization matters |
| 8 | No preexec hook | Small | Low — niche feature |
| 9 | Worker state not synced | Medium | Medium — causes subtle prompt bugs |

Items 1, 3, 4 are the highest-impact, smallest-effort wins. They would make the shell feel substantially more complete.
