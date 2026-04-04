# Installing ps-bash

ps-bash is distributed in two flavors:

- **Slim** (~6 MB) -- requires PowerShell 7+ already installed on the system
- **Full** (~80 MB) -- includes a bundled copy of PowerShell 7 (no system dependencies)

## Package Contents

### Slim

```
ps-bash[.exe]           # AOT native binary
ps-bash-worker.ps1      # persistent worker script
Modules/
  ps-bash/              # PowerShell module (cmdlets)
```

### Full

```
ps-bash[.exe]
ps-bash-worker.ps1
Modules/
  ps-bash/
pwsh/                   # bundled PowerShell 7
  pwsh[.exe]
  ...
```

## Manual Installation

Download the appropriate archive for your platform from the
[GitHub Releases](https://github.com/standardbeagle/ps-bash/releases) page.

### Windows

```powershell
# Extract to a permanent location
Expand-Archive ps-bash-slim-win-x64.zip -DestinationPath C:\tools\ps-bash

# Add to PATH (current user)
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
[Environment]::SetEnvironmentVariable('PATH', "$userPath;C:\tools\ps-bash", 'User')
```

### Linux

```bash
tar xzf ps-bash-slim-linux-x64.tar.gz -C /usr/local/ps-bash
ln -sf /usr/local/ps-bash/ps-bash /usr/local/bin/ps-bash
```

### macOS

```bash
tar xzf ps-bash-slim-osx-arm64.tar.gz -C /usr/local/ps-bash
ln -sf /usr/local/ps-bash/ps-bash /usr/local/bin/ps-bash
```

## Package Manager Installation (Planned)

The following package manager support is planned for post-stable release.

### winget (Windows)

```
winget install standardbeagle.ps-bash
```

Expected install path: `C:\Program Files\ps-bash\`

### Scoop (Windows)

```
scoop bucket add standardbeagle https://github.com/standardbeagle/scoop-bucket
scoop install ps-bash
```

Expected install path: `%USERPROFILE%\scoop\apps\ps-bash\current\`

### Homebrew (macOS / Linux)

```
brew tap standardbeagle/tap
brew install ps-bash
```

Expected install path: `/opt/homebrew/Cellar/ps-bash/<version>/` (macOS ARM)
or `/home/linuxbrew/.linuxbrew/Cellar/ps-bash/<version>/` (Linux)

## Setting ps-bash as Default Shell

After installation, set the `SHELL` environment variable so AI coding agents
use ps-bash automatically:

```bash
# Linux / macOS
export SHELL=/usr/local/bin/ps-bash

# Windows (PowerShell)
[Environment]::SetEnvironmentVariable('SHELL', 'C:\tools\ps-bash\ps-bash.exe', 'User')
```

### Agent-Specific Configuration

**Claude Code** -- set `SHELL` environment variable before launching.

**opencode** -- add to settings:
```json
{ "shell": "C:\\tools\\ps-bash\\ps-bash.exe" }
```

**Gemini CLI / Codex CLI / Copilot CLI** -- set `SHELL` environment variable.

## Full Package (Bundled pwsh)

Use the full package when PowerShell 7 is not available on the target system,
such as minimal Docker images, air-gapped environments, or Windows containers.

The bundled `pwsh/` directory is detected automatically by the binary's
side-by-side locator (priority 4 in the pwsh resolution chain).

No additional configuration is needed -- just extract and run.
