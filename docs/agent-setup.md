# Agent Setup Guide

Configure AI coding agents to use `ps-bash` as their shell on Windows. Each agent checks the `SHELL` environment variable or its own config to determine which shell to spawn for command execution.

## Prerequisites

- **PowerShell 7+** installed (`winget install Microsoft.PowerShell`)
- **ps-bash binary** downloaded from [Releases](https://github.com/standardbeagle/ps-bash/releases) or built locally
- **PsBash module** installed (`Install-Module PsBash`) or bundled in the `Modules/` directory next to the binary

## Claude Code

Claude Code reads the `shell` setting from its configuration. Set it to the full path of `ps-bash.exe`.

**Windows (settings.json):**

```jsonc
// %APPDATA%\Claude\settings.json
{
  "shell": "C:\\tools\\ps-bash\\ps-bash.exe"
}
```

**WSL / Linux:**

```jsonc
// ~/.config/claude/settings.json
{
  "shell": "/usr/local/ps-bash/ps-bash"
}
```

Claude Code invokes the shell as `SHELL -c "command"`, which matches the ps-bash interface exactly.

## opencode

opencode uses the `SHELL` environment variable to determine which shell to use for tool execution.

**Windows (PowerShell profile):**

```powershell
# Add to $PROFILE
$env:SHELL = 'C:\tools\ps-bash\ps-bash.exe'
```

**Windows (system environment):**

```powershell
[Environment]::SetEnvironmentVariable('SHELL', 'C:\tools\ps-bash\ps-bash.exe', 'User')
```

**Linux:**

```bash
export SHELL=/usr/local/ps-bash/ps-bash
```

## Gemini CLI

Gemini CLI reads the `SHELL` environment variable. Configuration is identical to opencode.

**Windows (PowerShell profile):**

```powershell
$env:SHELL = 'C:\tools\ps-bash\ps-bash.exe'
```

**Linux / macOS:**

```bash
export SHELL=/usr/local/ps-bash/ps-bash
```

## Docker

### Windows Container (nanoserver)

Uses the full package which bundles PowerShell 7, so no system dependencies are needed.

```dockerfile
FROM mcr.microsoft.com/windows/nanoserver:ltsc2022

COPY ps-bash-full/ C:/ps-bash/

ENV SHELL=C:/ps-bash/ps-bash.exe

# Verify the shell works
RUN C:/ps-bash/ps-bash.exe -c "Write-Host 'ps-bash ready'"
```

### Linux Container (slim)

Uses the slim package with the base PowerShell image which already has `pwsh`.

```dockerfile
FROM mcr.microsoft.com/powershell:latest

COPY ps-bash-slim/ /usr/local/ps-bash/
RUN chmod +x /usr/local/ps-bash/ps-bash

ENV SHELL=/usr/local/ps-bash/ps-bash

# Verify the shell works
RUN /usr/local/ps-bash/ps-bash -c "Write-Host 'ps-bash ready'"
```

### Linux Container (full, no PowerShell base)

```dockerfile
FROM ubuntu:24.04

COPY ps-bash-full/ /usr/local/ps-bash/
RUN chmod +x /usr/local/ps-bash/ps-bash /usr/local/ps-bash/pwsh/pwsh

ENV SHELL=/usr/local/ps-bash/ps-bash
ENV PATH="/usr/local/ps-bash/pwsh:${PATH}"

RUN /usr/local/ps-bash/ps-bash -c "Write-Host 'ps-bash ready'"
```

## Verifying Your Setup

After configuring any agent, verify the shell works:

```bash
# The agent will invoke commands like this:
ps-bash -c "Write-Host 'hello from ps-bash'"

# Check exit code propagation:
ps-bash -c "exit 42"; echo $?
# Should print: 42

# Check transpilation:
PSBASH_DEBUG=1 ps-bash -c "ls -la | grep '.txt'"
# Stderr will show the transpiled PowerShell command
```

## Troubleshooting

**"ps-bash requires PowerShell 7+"** -- Install pwsh: `winget install Microsoft.PowerShell` or use the full package which bundles it.

**"no command specified"** -- The agent is calling ps-bash without `-c`. Check that the agent config points to the correct binary path.

**Commands return unexpected output** -- Set `PSBASH_DEBUG=1` to see the transpiled PowerShell command on stderr. This shows exactly what ps-bash sends to pwsh.
