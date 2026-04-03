# PsBash

**Real bash commands. Real PowerShell objects.**

[![CI](https://github.com/standardbeagle/ps-bash/actions/workflows/ci.yml/badge.svg)](https://github.com/standardbeagle/ps-bash/actions/workflows/ci.yml)
[![PowerShell 7+](https://img.shields.io/badge/PowerShell-7%2B-blue.svg)](https://github.com/PowerShell/PowerShell)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Tests: 775](https://img.shields.io/badge/tests-775%20passing-brightgreen.svg)](#testing)
[![Commands: 68](https://img.shields.io/badge/commands-68-blue.svg)](#command-reference)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey.svg)](#cross-platform)

Use the bash commands you already know — `ls -la`, `grep -r`, `ps aux | sort -k3 -rn | head 5` — and get **typed PowerShell objects** underneath. Same flags, same output, but every result has structured properties you can access programmatically.

## The Problem

You know bash. You think in `ls | grep | sort | head`. But PowerShell makes you write `Get-ChildItem | Where-Object | Sort-Object | Select-Object -First`. And bash on Windows gives you strings, not objects.

## The Solution

PsBash gives you both worlds:

```powershell
# Looks like bash, works like bash
PS> ls -la | grep '.ps1' | sort -k5 -h

-rw-r--r-- 1 beagle wheel   141234 Apr  2 09:46 PsBash.psm1
-rw-r--r-- 1 beagle wheel     4521 Apr  2 00:57 PsBash.psd1
-rw-r--r-- 1 beagle wheel    89234 Apr  2 09:46 PsBash.Tests.ps1

# But underneath? Typed objects with real properties.
PS> $files = ls -la | grep '.ps1' | sort -k5 -h
PS> $files.Name
PsBash.psm1
PsBash.psd1
PsBash.Tests.ps1

PS> $files.SizeBytes | Measure-Object -Sum | Select -Expand Sum
234989

PS> $files[0].Permissions
-rw-r--r--
```

The key insight: **`grep` matches against the text output but passes through the original typed objects.** The pipeline preserves structure while behaving like bash on the surface.

## Quick Start

```powershell
# Clone and import
git clone https://github.com/standardbeagle/ps-bash.git
Import-Module ./ps-bash/src/PsBash.psd1

# Start using bash commands immediately
ls -la
cat README.md | head -20
find . -name '*.ps1' -type f
ps aux | sort -k3 -rn | head 5
echo '{"name":"ps-bash"}' | jq '.name'
du -sh * | sort -rh | head 10
```

### Permanent Installation

```powershell
# Copy to your modules directory
$dest = if ($IsWindows) {
    "$HOME\Documents\PowerShell\Modules\PsBash"
} else {
    "$HOME/.local/share/powershell/Modules/PsBash"
}
Copy-Item ./ps-bash/src -Destination $dest -Recurse

# Load on every shell start
Add-Content $PROFILE "`nImport-Module PsBash"
```

**Requirements:** PowerShell 7+ (cross-platform). No external dependencies.

## How It Works

### The BashText Bridge

Every PsBash command returns a PowerShell object with a `.BashText` property containing the exact string a real bash command would output. The terminal renders `BashText` via `Format.ps1xml`, so everything *looks* like bash. But you can always reach into the object:

```
┌──────────────────────────────────────────────────────┐
│  ls -la                                              │
│  ↓                                                   │
│  Invoke-BashLs                                       │
│  ↓                                                   │
│  [LsEntry] objects                                   │
│  ├── .Name         = "README.md"                     │
│  ├── .SizeBytes    = 4521                            │
│  ├── .Permissions  = "-rw-r--r--"                    │
│  ├── .Owner        = "beagle"                        │
│  ├── .LastModified = [DateTime]                      │
│  └── .BashText     = "-rw-r--r-- 1 beagle wheel..." │
│                                                      │
│  Terminal shows → BashText (looks like real bash)     │
│  Code accesses → typed properties                    │
└──────────────────────────────────────────────────────┘
```

### The Pipeline Bridge

Pipeline commands (`grep`, `sort`, `head`, `tail`, `tee`) match against BashText but **pass through the original typed objects**:

```powershell
# grep matches text, but returns LsEntry objects — not strings
PS> (ls -la | grep '.ps1')[0].GetType().Name
PSCustomObject  # It's an LsEntry, not a string

# sort reorders by column text, but preserves object types
PS> (ps aux | sort -k3 -rn | head 3)[0].CPU
45.2  # Real decimal, not a string you'd have to parse

# tee writes BashText to file, passes objects through
PS> ls -la | tee listing.txt | grep '.ps1'
# listing.txt has text; grep receives LsEntry objects
```

## Command Reference

### Output
| Command | Description |
|---------|-------------|
| `echo` | Display text with `-n` `-e` `-E` flags |
| `printf` | Formatted output with `%s` `%d` `%f` specifiers |

### File Listing & Navigation
| Command | Description | Object Type |
|---------|-------------|-------------|
| `ls` | List directory contents | `LsEntry` |
| `find` | Search file hierarchy | `FindEntry` |
| `stat` | File status with `-c` format | `StatEntry` |
| `tree` | Directory tree | `TreeEntry` |
| `du` | Disk usage | `DuEntry` |
| `pwd` | Working directory | `TextOutput` |
| `basename` | Strip directory | `TextOutput` |
| `dirname` | Strip filename | `TextOutput` |

### File Operations
| Command | Description |
|---------|-------------|
| `cp` | Copy files (`-r` `-v` `-n` `-f`) |
| `mv` | Move/rename (`-v` `-n` `-f`) |
| `rm` | Remove (`-r` `-f` `-v`) |
| `mkdir` | Create directories (`-p` `-v`) |
| `rmdir` | Remove empty directories |
| `touch` | Create/update timestamps |
| `ln` | Hard/symbolic links (`-s` `-f`) |

### File Content
| Command | Description | Object Type |
|---------|-------------|-------------|
| `cat` | Concatenate files | `CatLine` |
| `head` | First N lines (passthrough) | *preserves input type* |
| `tail` | Last N lines (passthrough) | *preserves input type* |
| `tac` | Reverse file | `TextOutput` |
| `wc` | Count lines/words/bytes | `WcResult` |
| `nl` | Number lines | `TextOutput` |
| `rev` | Reverse characters | `TextOutput` |
| `strings` | Printable strings from binary | `TextOutput` |
| `fold` | Wrap lines at width | `TextOutput` |
| `expand` | Tabs to spaces | `TextOutput` |
| `unexpand` | Spaces to tabs | `TextOutput` |
| `split` | Split file into pieces | `TextOutput` |

### Search & Match
| Command | Description | Object Type |
|---------|-------------|-------------|
| `grep` | Pattern match (pipeline bridge) | `GrepMatch` or *input type* |
| `rg` | Recursive search (ripgrep-style) | `RgMatch` |

### Text Processing
| Command | Description |
|---------|-------------|
| `sed` | Stream editor (`s///`, addresses, `-i` in-place) |
| `awk` | Pattern processing (`$1`..`$NF`, `BEGIN`/`END`, builtins) |
| `cut` | Extract columns (`-d` `-f` `-c`) |
| `tr` | Translate/delete characters |
| `uniq` | Deduplicate lines (`-c` `-d`) |
| `sort` | Sort lines (pipeline bridge, preserves types) |
| `column` | Columnate output (`-t` `-s`) |
| `join` | Join files on key |
| `paste` | Merge lines (`-d` `-s`) |
| `comm` | Compare sorted files |
| `diff` | Compare files (`-u` unified) |

### Pipeline Tools
| Command | Description |
|---------|-------------|
| `tee` | Split to file + stdout (preserves types) |
| `xargs` | Build commands from stdin (`-I` `-n`) |

### Data Processing
| Command | Description |
|---------|-------------|
| `jq` | JSON processor (`.field`, `select()`, `map()`, pipes) |
| `yq` | YAML processor (same filter syntax as jq) |
| `xan` | CSV toolkit (`headers`, `select`, `search`, `table`) |

### System Information
| Command | Description | Object Type |
|---------|-------------|-------------|
| `ps` | Process listing | `PsEntry` |
| `env` | Environment variables | `EnvEntry` |
| `date` | Date/time formatting | `DateOutput` |
| `hostname` | System hostname | `TextOutput` |
| `whoami` | Current user | `TextOutput` |
| `which` | Locate command | `WhichOutput` |
| `alias` | Manage aliases | `AliasOutput` |
| `time` | Measure execution | `TimeOutput` |
| `sleep` | Delay (`s`/`m`/`h`/`d` suffixes) | — |

### Encoding & Hashing
| Command | Description |
|---------|-------------|
| `base64` | Encode/decode (`-d` `-w`) |
| `md5sum` | MD5 digest (`-c` check mode) |
| `sha1sum` | SHA1 digest |
| `sha256sum` | SHA256 digest |
| `file` | Detect file type (`-b` `-i` MIME) |

### Compression
| Command | Description |
|---------|-------------|
| `gzip` | Compress (`-d` `-c` `-k` `-l` `-1`..`-9`) |
| `gunzip` | Decompress |
| `zcat` | Decompress to stdout |
| `tar` | Archive (`-c` `-x` `-t` `-z` `--exclude`) |

### Math & Sequences
| Command | Description |
|---------|-------------|
| `seq` | Number sequences (`-s` `-w`) |
| `expr` | Arithmetic and string expressions |

## Comparison

### PsBash vs Native PowerShell

| Task | PsBash | Native PowerShell |
|------|--------|-------------------|
| List files | `ls -la` | `Get-ChildItem -Force \| Format-List` |
| Find by name | `find . -name '*.ps1'` | `Get-ChildItem -Recurse -Filter *.ps1` |
| Top CPU | `ps aux \| sort -k3 -rn \| head 5` | `Get-Process \| Sort-Object CPU -Desc \| Select -First 5` |
| Count lines | `wc -l file.txt` | `(Get-Content file.txt).Count` |
| Search files | `grep -r 'TODO' src/` | `Select-String -Path src/* -Pattern TODO -Recurse` |
| JSON field | `cat data.json \| jq '.name'` | `(Get-Content data.json \| ConvertFrom-Json).name` |
| File size sort | `du -sh * \| sort -rh` | `Get-ChildItem \| Sort Length -Desc \| Select Name,Length` |
| Disk usage | `df -h` | `Get-PSDrive -PSProvider FileSystem` |

### PsBash vs Bash

Same commands, same flags, same output format. The difference: PsBash results are typed objects. No more `awk '{print $5}'` to extract the size — just use `.SizeBytes`.

### PsBash vs WSL

| | PsBash | WSL |
|---|--------|-----|
| Overhead | None (native PowerShell) | Full Linux VM |
| Objects | Typed, pipeline-compatible | Strings only |
| Platforms | Windows, Linux, macOS | Windows only |
| Integration | Native .NET access | Filesystem boundary |

## Features

- **Tab completion** for all bash flags — type `ls -` and press Tab
- **`--help`** on every command — `grep --help` shows flags with descriptions
- **Cross-platform** file metadata — Linux uses real POSIX permissions, Windows approximates from ACLs
- **Case-sensitive flags** — `-e` and `-E` are different (uses `StringComparer.Ordinal`)
- **AllScope aliases** override PowerShell builtins — `ls` means PsBash's `ls`, not `Get-ChildItem`

## Testing

```powershell
# Run the full test suite
Invoke-Pester ./tests/PsBash.Tests.ps1

# 776 tests: 775 passing, 1 skipped (Windows-specific)
```

Tests cover every command, every flag, pipeline bridge behavior, cross-platform edge cases, and error handling.

## Cross-Platform

PsBash runs on Windows, Linux, and macOS. CI tests all three via GitHub Actions.

| Feature | Linux/macOS | Windows |
|---------|-------------|---------|
| File permissions | Real POSIX (`rwxr-xr-x`) | Approximated from ACLs |
| `ps` implementation | Reads `/proc` filesystem | Uses .NET `System.Diagnostics.Process` |
| `ln -s` | Works normally | Requires Developer Mode |
| `stat` | Uses `/usr/bin/stat` | Pure .NET implementation |

## License

MIT

## Contributing

Issues and PRs welcome at [github.com/standardbeagle/ps-bash](https://github.com/standardbeagle/ps-bash).
