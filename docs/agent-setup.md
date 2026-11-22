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
- `/tmp/x` → the temp dir on Windows, literal `/tmp/x` on Linux/macOS (resolved at runtime; mode-independent)

This mode governs paths going **in**. Paths coming **out** (`$PWD`, `pwd`, `~`, `$HOME`, `mktemp`,
`realpath`, `type`) are `C:/Users/foo` in every mode since v0.15.0: forward slashes, and the drive
letter is kept (ps-bash does not print `/c/Users/foo`). An agent can pass such a path back to any command.

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

Working Dockerfiles live in [`docker/windows`](../docker/windows/Dockerfile) and
[`docker/linux`](../docker/linux/Dockerfile). The `Container smoke` workflow
(`.github/workflows/container-smoke.yml`) builds both from a published release and runs
[`docker/smoke.sh`](../docker/smoke.sh) in each, on Nano Server and Server Core (ltsc2022) and on Linux.

A release is self-contained: it carries its own .NET runtime and PowerShell SDK, so the image needs
neither pwsh nor .NET. Install the **whole release folder**: the launcher (`ps-bash.exe`) starts the
host (`ps-bash-host.exe`), which loads the runtime and SDK beside it, so the launcher alone does not
run. The win-x64 release is 59 MB zipped and 143 MB unpacked.

```bash
docker build -f docker/windows/Dockerfile --build-arg PSBASH_VERSION=0.15.1 -t ps-bash:win .
docker build -f docker/windows/Dockerfile --build-arg BASE=mcr.microsoft.com/windows/servercore:ltsc2022 \
  --build-arg RUNAS=ContainerAdministrator -t ps-bash:win-core .
docker build -f docker/linux/Dockerfile --build-arg PSBASH_VERSION=0.15.1 -t ps-bash:linux .
```

Both images set `SHELL` to ps-bash, so every `RUN` line after that is bash, also on Windows.

### Windows: build steps use a private host

A Windows image build fails with `hcsshim::ImportLayer failed in Win32: The system cannot find the
path specified` when a build step used the shared host. That host outlives the command, so it is
killed when the build container stops, and the socket file it listens on (under
`%TEMP%\ps-bash`) stays in the layer. Windows cannot commit a layer that holds a socket file.
With `PSBASH_PER_INVOCATION=1`, ps-bash stops its private host and removes the socket before the
command ends. The Windows Dockerfile therefore declares:

```dockerfile
ARG PSBASH_PER_INVOCATION=1
```

An `ARG` reaches `RUN` steps as an environment variable but is not kept in the image, so containers
run from it still share one host across commands. The same applies to `docker commit` of a running
Windows container: run its ps-bash commands with `PSBASH_PER_INVOCATION=1`, or the commit fails.
Linux layers can hold socket files, so the Linux image needs neither.

### Startup cost

The first ps-bash command in a container starts the host; later ones reuse it. Measured with
`docker exec` on GitHub runners (the numbers include `docker exec` itself):

| | Linux | Nano Server | Server Core |
|---|---:|---:|---:|
| First command (the host starts) | 0.8–0.95 s | 1.6 s | 1.9 s |
| Later commands (the host is up) | 45–70 ms | 70–110 ms | 85–120 ms |
| Every command with `PSBASH_PER_INVOCATION=1` | 0.8–0.95 s | 1.45–1.5 s | 1.6 s |
| `docker exec` alone, for comparison | 40–50 ms | 130 ms | 130 ms |

So a long-running container (an agent sandbox, a dev container) pays the start-up once. Each
Dockerfile `RUN` step is a new container and pays it every time; merge short steps into one `RUN`.

## Quick Reference

| Agent | Config Method | Setting |
|-------|--------------|---------|
| **Claude Code** | settings.json env (restart after) | `CLAUDE_CODE_GIT_BASH_PATH=C:\path\to\bash.exe` (a copy of `ps-bash.exe`, same folder) |
| **OpenCode** | `$SHELL` env var | `$env:SHELL = 'C:\path\to\ps-bash.exe'` |
| **GitHub Copilot** | VS Code terminal profile | `terminal.integrated.defaultProfile.windows` |
| **Gemini CLI** | Not configurable | Run inside ps-bash interactive shell |

## What v0.15.0 changes for agents

- **`pipefail` with `&&`.** Claude Code's Bash tool runs with `pipefail`. Under it,
  `scp a b && ssh h cmd | grep x` failed the whole script with a PowerShell parse error. It runs now.
- **Typographic quotes.** A pasted `’`, `“”` or `–` in a command (`echo it’s`) failed the whole
  script. These are ordinary characters now, as in bash.
- **PowerShell aliases.** `gci`, `iex`, `r` and `write` are no longer commands in bash code:
  exit 127 unless a function or PATH program has that name. Full cmdlet names still work.
- **Exported variables only.** `x=1; bash -c 'echo $x'` prints an empty line. Use `export x=1`,
  or `x=1 bash -c '...'`. A program started plainly (`prog args`) still inherits every variable.
- **Nested shells.** `bash -c '...'`, `sh -c` and `bash script.sh` stream their output as it is
  written and are no longer killed after `PSBASH_TIMEOUT`.
- **Background jobs.** `cmd &` output appears as the job runs. It used to appear at `wait`.
- **Busy hosts.** When every executor slot of the session has been busy for 2 s (several long
  builds), the next command runs on a private host that exits with it, instead of waiting. A
  session runs at most 2 such hosts at once; past that, a command waits for a slot.
  `PSBASH_EXEC_OVERFLOW_MS` sets the 2 s wait and `PSBASH_EXEC_OVERFLOW_MAX` the host limit.

Open items an agent can hit are in [Known Issues](known-issues.md).

## Background host processes

`ps-bash -c` hands each command to a warm background `ps-bash-host`, one per agent session, so
commands don't pay PowerShell's startup cost. Seeing one `ps-bash-host` per running agent is
expected. Each host exits a few seconds after its agent does, or after 10 idle minutes.

If you start the agent from PowerShell and want exactly one host for the whole session, set a
session name first:

```powershell
$env:PSBASH_SESSION = "claude"
claude
```

To leave nothing running between commands, at the cost of a cold start each time, set
`$env:PSBASH_PER_INVOCATION = "1"` instead. Full details: the
[Host Daemon reference](https://dev.standardbeagle.com/ps-bash/reference/host-daemon/).

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

**Many `ps-bash-host` processes in Task Manager** — Before v0.11.6, an agent whose process
name ps-bash didn't recognize could leave one idle host per command for 10 minutes
([#7](https://github.com/standardbeagle/ps-bash/issues/7)). Upgrade, or set
`$env:PSBASH_SESSION = "claude"` before starting the agent. See
[Background host processes](#background-host-processes).

**A mistyped command takes seconds or minutes on WSL** — WSL appends the Windows `PATH` by
default (`/mnt/c/Windows/System32`, ...). PowerShell lists every `PATH` directory before it
reports a command as missing, and over the WSL file bridge that costs about 9 s for
`System32` alone. ps-bash prints a one-time `ps-bash: note: PATH has N Windows directories
under /mnt` hint when this happens. Fix it in `/etc/wsl.conf`, then run `wsl --shutdown`:

```ini
[interop]
appendWindowsPath = false
```

Windows tools stay reachable by full path (`/mnt/c/Windows/System32/notepad.exe`) or by adding
the few you need back to `PATH`.

**Interactive tools (claude, copilot) crash in ps-bash shell** — Make sure you're running v0.8.7+. Earlier versions routed all commands through a pipe protocol that broke interactive executables.

## Detached processes (`nohup`)

`nohup cmd > log 2>&1 &` starts `cmd` in its own process: no pipe of the calling shell is inherited (a tool
call waiting for EOF returns at once), `$!` is the real pid, and `cmd` keeps running after the shell and its
host exit. Exit codes follow GNU nohup (125 own failure, 127 not found). A plain `cmd &` is a shell job and
ends with the host.

Under `tman` (`tman dev`, `tman test`) the supervised process tree is killed when the run ends, detached
children included: that is tman's containment, not a nohup failure. To see a detached process survive, start it
from a plain terminal, not through tman.
