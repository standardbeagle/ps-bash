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

Claude Code supports a `CLAUDE_CODE_SHELL` environment variable that overrides shell detection. Set it to the full path of `ps-bash.exe`.

**Option 1: Environment variable (PowerShell profile)**

```powershell
# Add to $PROFILE
$env:CLAUDE_CODE_SHELL = 'C:\Users\you\.local\bin\ps-bash.exe'
```

**Option 2: Claude Code settings.json**

```jsonc
// ~/.claude/settings.json
{
  "env": {
    "CLAUDE_CODE_SHELL": "C:\\Users\\you\\.local\\bin\\ps-bash.exe"
  }
}
```

**Option 3: Per-project (.claude/settings.local.json)**

```jsonc
{
  "env": {
    "CLAUDE_CODE_SHELL": "C:\\Users\\you\\.local\\bin\\ps-bash.exe"
  }
}
```

Claude Code invokes the shell as `<shell> -c "command"`, which is exactly the ps-bash interface. Once configured, Claude Code will transpile all bash commands through ps-bash.

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
| **Claude Code** | Env var or settings.json | `CLAUDE_CODE_SHELL=C:\path\to\ps-bash.exe` |
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
