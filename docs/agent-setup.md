# How to Configure AI Coding Agents to Use ps-bash

Run bash commands on Windows — no Git Bash, WSL, or Cygwin required. ps-bash transpiles bash to PowerShell and executes it natively. This guide shows you how to set up Claude Code, OpenCode, GitHub Copilot, and Gemini CLI to use ps-bash as their shell.

## Prerequisites

- **PowerShell 7+** installed (`winget install Microsoft.PowerShell`)
- **ps-bash binary** — download from [GitHub Releases](https://github.com/standardbeagle/ps-bash/releases) or install via:

```powershell
# Quick install (Windows)
iwr https://raw.githubusercontent.com/standardbeagle/ps-bash/main/install.ps1 | iex
```

Verify it works:

```powershell
ps-bash -c "echo hello"
# hello
```

## Claude Code

On Windows, Claude Code's Bash tool runs the shell named by `CLAUDE_CODE_GIT_BASH_PATH` — but **only if the file is named `bash.exe`, `sh.exe`, `bash` or `sh`**. Any other name (including `ps-bash.exe`) is silently ignored and Claude Code auto-detects Git Bash instead. `CLAUDE_CODE_SHELL` is no way around it: it is only accepted when it points at a working bash or zsh.

So expose ps-bash under the name `bash.exe`:

**Step 1: create `bash.exe` next to `ps-bash.exe`**

`install-local.ps1` does this on every install. By hand:

```powershell
Copy-Item $env:USERPROFILE\.local\bin\ps-bash.exe $env:USERPROFILE\.local\bin\bash.exe
```

It must be a copy **in the same folder**: the launcher finds `ps-bash.dll` (and, for a self-contained install, the .NET runtime files) beside itself, so a copy elsewhere fails to start. It does not change what `bash` resolves to on your `PATH` as long as `System32` comes first.

**Step 2: point Claude Code at it** (`~/.claude/settings.json`, or per project in `.claude/settings.local.json`)

```jsonc
{
  "env": {
    "CLAUDE_CODE_GIT_BASH_PATH": "C:\\Users\\you\\.local\\bin\\bash.exe",
    "PSBASH_UNIX_PATHS": "1"
  }
}
```

`PSBASH_UNIX_PATHS=1` is required because Claude Code's Bash tool emits MSYS-style paths (e.g. `/c/Users/...` instead of `C:\Users\...`) in its prelude. Without it, redirect targets land in the wrong directory.

**Step 3: restart Claude Code and verify.** The shell is chosen at startup, so an already-running session keeps its old shell. In a new session, `echo $BASH_VERSION` in the Bash tool prints `0.x.0(1)-release` for ps-bash; `4.4.x`/`5.x` means it fell back to Git Bash. `claude -p 'Use the Bash tool to run: echo $BASH_VERSION' --allowedTools Bash` checks it non-interactively.

Claude Code invokes the shell as `<shell> -c "command"` (sourcing its shell snapshot first), which is exactly the ps-bash interface. Once configured, Claude Code will transpile all bash commands through ps-bash.

### Path Mode

ps-bash defaults to **Windows-native paths** — it doesn't rewrite paths that look POSIX-shaped. If you're using a wrapper that emits unix paths (Claude Code, OpenCode, scripts copied from Linux), enable unix-path translation one of two ways:

| Method | Form |
|---|---|
| Env var (recommended for wrappers) | `PSBASH_UNIX_PATHS=1` |
| CLI flag (one-off invocation) | `ps-bash --unix-paths -c "echo > /c/foo.log"` |

The CLI flag overrides the env var. Use `--windows-paths` to force the default off explicitly.

When unix-paths mode is on:
- `/c/Users/foo` → `C:\Users\foo` (any drive letter; case-folded uppercase)
- `/dev/null` → `$null` (always, mode-independent)
- `/tmp/x` → `$env:TEMP\x` (always, mode-independent)

## OpenCode

OpenCode reads the `SHELL` environment variable to determine which shell to use. Set it before launching OpenCode.

**Option 1: Environment variable (PowerShell profile)**

```powershell
# Add to $PROFILE
$env:SHELL = 'C:\Users\you\.local\bin\ps-bash.exe'
```

**Option 2: System environment variable (persists across sessions)**

```powershell
[Environment]::SetEnvironmentVariable('SHELL', 'C:\Users\you\.local\bin\ps-bash.exe', 'User')
```

**Option 3: Inline per-session**

```powershell
$env:SHELL = 'C:\Users\you\.local\bin\ps-bash.exe'
opencode
```

OpenCode's shell selection explicitly reads `process.env.SHELL`. On Windows, `SHELL` is not set by default, so you must set it yourself. Once set, OpenCode will use ps-bash for all bash tool invocations.

## GitHub Copilot (VS Code Agent Mode)

Copilot agent mode executes commands through VS Code's integrated terminal. Configure a custom terminal profile pointing to ps-bash.

**In VS Code settings.json (`Ctrl+Shift+P` → "Open User Settings (JSON)"):**

```jsonc
{
  "terminal.integrated.profiles.windows": {
    "ps-bash": {
      "path": "C:\\Users\\you\\.local\\bin\\ps-bash.exe",
      "icon": "terminal-bash"
    }
  },
  "terminal.integrated.defaultProfile.windows": "ps-bash"
}
```

Copilot will now execute bash commands through ps-bash in agent mode.

## Gemini CLI

Gemini CLI hardcodes its shell selection — `bash -c` on macOS/Linux and `powershell.exe` on Windows. It does not read `$SHELL` or offer a shell configuration setting.

**Workaround:** Use ps-bash in interactive mode as your terminal shell, and run Gemini CLI inside it:

```powershell
# Launch ps-bash as your shell
ps-bash

# Inside ps-bash, run gemini
gemini
```

This works because ps-bash's interactive shell runs external commands directly on the console (since v0.8.7), giving interactive CLI tools like Gemini full terminal access.

## Docker

### Windows Container

```dockerfile
FROM mcr.microsoft.com/windows/nanoserver:ltsc2022
COPY ps-bash.exe C:/ps-bash/
ENV SHELL=C:/ps-bash/ps-bash.exe
RUN C:/ps-bash/ps-bash.exe -c "echo ready"
```

### Linux Container

```dockerfile
FROM mcr.microsoft.com/powershell:latest
COPY ps-bash /usr/local/bin/
RUN chmod +x /usr/local/bin/ps-bash
ENV SHELL=/usr/local/bin/ps-bash
RUN ps-bash -c "echo ready"
```

## Quick Reference

| Agent | Config Method | Setting |
|-------|--------------|---------|
| **Claude Code** | settings.json env (restart after) | `CLAUDE_CODE_GIT_BASH_PATH=C:\path\to\bash.exe` (a copy of `ps-bash.exe`, same folder) |
| **OpenCode** | `$SHELL` env var | `$env:SHELL = 'C:\path\to\ps-bash.exe'` |
| **GitHub Copilot** | VS Code terminal profile | `terminal.integrated.defaultProfile.windows` |
| **Gemini CLI** | Not configurable | Run inside ps-bash interactive shell |

## Verify Your Setup

```powershell
# Test the binary
ps-bash -c "echo hello from ps-bash"

# Test exit code propagation
ps-bash -c "exit 42"
echo $LASTEXITCODE
# 42

# Debug transpilation
$env:PSBASH_DEBUG = '1'
ps-bash -c "ls -la | grep '.txt'"
# Stderr shows the transpiled PowerShell command
```

## Troubleshooting

**"ps-bash requires PowerShell 7+"** — Install pwsh: `winget install Microsoft.PowerShell`

**"no command specified"** — The agent is calling ps-bash without `-c`. Verify the binary path is correct.

**Commands return unexpected output** — Set `$env:PSBASH_DEBUG = '1'` to see the transpiled PowerShell on stderr.

**Interactive tools (claude, copilot) crash in ps-bash shell** — Make sure you're running v0.8.7+. Earlier versions routed all commands through a pipe protocol that broke interactive executables.
