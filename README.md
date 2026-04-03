# PsBash

**Real bash commands. Real PowerShell objects.**

[![CI](https://github.com/standardbeagle/ps-bash/actions/workflows/ci.yml/badge.svg)](https://github.com/standardbeagle/ps-bash/actions/workflows/ci.yml)
[![PSGallery](https://img.shields.io/powershellgallery/v/PsBash.svg?label=PSGallery)](https://www.powershellgallery.com/packages/PsBash)
[![Tests](https://img.shields.io/badge/tests-776%20passing-brightgreen.svg)](#testing)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey.svg)](#cross-platform)
[![Docs](https://img.shields.io/badge/docs-standardbeagle.github.io%2Fps--bash-blue.svg)](https://standardbeagle.github.io/ps-bash/)

```powershell
Install-Module PsBash
```

<picture>
  <source srcset="docs/assets/demo.webp" type="image/webp">
  <img src="docs/assets/demo.gif" alt="ps-bash demo showing typed pipeline objects" width="800">
</picture>

## Why PsBash?

PowerShell ships aliases for `ls`, `cat`, `sort`, `rm` -- but they're lies. `rm -rf` fails. `ls -la` fails. `sort -k3 -rn` fails. They're `Get-ChildItem`, `Get-Content`, and `Sort-Object` wearing a disguise, rejecting every bash flag you throw at them.

PsBash makes the flags work -- and goes further. Every command returns **typed .NET objects** while producing bash-identical text output.

### 1. Parse process data without awk

```powershell
# Before: string parsing
PS> Get-Process | Format-Table -Auto | Out-String | ForEach-Object { $_ -split "`n" } |
>>   Where-Object { $_ -match 'pwsh' }
# Good luck extracting CPU from that

# After: typed objects through the entire pipeline
PS> $top = ps aux | sort -k3 -rn | head 5
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

### 3. Built-in jq, awk, sed -- no external binaries

```powershell
PS> echo '{"db":"postgres","port":5432}' | jq '.db'
"postgres"

PS> cat app.log | grep ERROR | awk '{print $1, $3}' | sort | uniq -c | sort -rn | head 5
      47 2024-01-15 DatabaseError
      23 2024-01-15 TimeoutError
      12 2024-01-15 AuthError

PS> find . -name '*.log' -mtime +7 | xargs rm -f    # actually works on Windows
```

## Feature Comparison

| | **PsBash** | **Git Bash** | **Cygwin** | **WSL** | **Crescendo** |
|---|---|---|---|---|---|
| Install | `Install-Module` | Git for Windows | 50+ packages | Hyper-V/WSL2 | Per-command XML |
| Footprint | **340 KB**, 3 files | ~300 MB (MSYS2 runtime) | ~1-4 GB | ~1-15 GB (full distro) | Module + your wrappers |
| `rm -rf` works | Yes | Yes | Yes | Yes | If you configure it |
| Typed objects | Always | Never (strings) | Never (strings) | Never (strings) | If configured |
| Object pipeline | Types survive `grep \| sort \| head` | Strings only | Strings only | Strings only | Varies |
| PowerShell integration | Native -- objects flow into cmdlets | Separate shell | Separate shell | Separate shell | Native |
| Cross-platform | Win/Lin/Mac | Windows only | Windows only | Windows only | Win/Lin/Mac |
| Commands | 68 built-in | ~80 (GNU coreutils) | ~200+ (full GNU) | All of Linux | Define your own |
| jq/awk/sed | Built-in, zero binaries | awk/sed yes, jq no | Yes (install pkg) | Yes (apt install) | Not included |
| PATH conflicts | None (AllScope aliases) | Shadows PowerShell | Shadows PowerShell | Filesystem boundary | None |
| Startup overhead | ~100 ms (module load) | New process per call | New process per call | ~1s (cold), ~200ms (warm) | ~100 ms |

## Quick Start

```powershell
# Install from PSGallery
Install-Module PsBash

# Or clone for development
git clone https://github.com/standardbeagle/ps-bash.git
Import-Module ./ps-bash/src/PsBash.psd1
```

68 bash commands work immediately:

```powershell
ls -la                                    # LsEntry objects
ps aux | sort -k3 -rn | head 5           # PsEntry objects, sorted by CPU
cat README.md | grep 'bash' | wc -l      # WcResult with .Lines, .Words, .Bytes
find . -name '*.ps1' -type f             # FindEntry objects
echo '{"a":1}' | jq '.a'                 # built-in JSON processor
du -sh * | sort -rh | head 10            # DuEntry objects, human-readable sizes
sed -i 's/foo/bar/g' config.txt          # in-place file editing
tar -czf backup.tar.gz src/              # compression with .NET streams
```

### Permanent Setup

```powershell
# Add to your PowerShell profile
Add-Content $PROFILE "`nImport-Module PsBash"
```

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

Pipeline commands (`grep`, `sort`, `head`, `tail`, `tee`) match against BashText but pass through the **original typed objects**. This is the pipeline bridge -- the core architectural pattern.

## 68 Commands

| Category | Commands |
|----------|----------|
| **Listing** | `ls` `find` `stat` `tree` `du` `pwd` `basename` `dirname` |
| **Files** | `cp` `mv` `rm` `mkdir` `rmdir` `touch` `ln` |
| **Content** | `cat` `head` `tail` `tac` `wc` `nl` `rev` `strings` `fold` `expand` `unexpand` `split` |
| **Search** | `grep` `rg` |
| **Text** | `sed` `awk` `cut` `tr` `uniq` `sort` `column` `join` `paste` `comm` `diff` |
| **Pipeline** | `tee` `xargs` |
| **Data** | `jq` `yq` `xan` |
| **System** | `ps` `env` `date` `hostname` `whoami` `which` `alias` `time` `sleep` |
| **Output** | `echo` `printf` |
| **Encoding** | `base64` `md5sum` `sha1sum` `sha256sum` `file` |
| **Archive** | `gzip` `gunzip` `zcat` `tar` |
| **Math** | `seq` `expr` |

Every command supports `--help` and tab completion for all flags.

## PsBash vs Native PowerShell

| Task | PsBash | Native PowerShell |
|------|--------|-------------------|
| List files | `ls -la` | `Get-ChildItem -Force \| Format-List` |
| Find by name | `find . -name '*.ps1'` | `Get-ChildItem -Recurse -Filter *.ps1` |
| Top 5 CPU | `ps aux \| sort -k3 -rn \| head 5` | `Get-Process \| Sort-Object CPU -Desc \| Select -First 5` |
| Count lines | `wc -l file.txt` | `(Get-Content file.txt).Count` |
| Search files | `grep -r 'TODO' src/` | `Select-String -Path src/* -Pattern TODO -Recurse` |
| JSON field | `cat f.json \| jq '.name'` | `(Get-Content f.json \| ConvertFrom-Json).name` |
| Delete tree | `rm -rf dist/` | `Remove-Item dist/ -Recurse -Force` |
| Disk usage | `du -sh * \| sort -rh` | `Get-ChildItem \| Sort Length -Desc \| Select Name,Length` |

Both sides give you objects. PsBash just lets you type what you already know.

## Testing

```powershell
Invoke-Pester ./tests/PsBash.Tests.ps1
# 776 tests: 775 passing, 1 skipped (Windows-specific)
```

CI runs on Windows, Linux, and macOS via GitHub Actions.

## Cross-Platform

| Feature | Linux/macOS | Windows |
|---------|-------------|---------|
| File permissions | Real POSIX (`rwxr-xr-x`) | Approximated from ACLs |
| `ps` backend | `/proc` filesystem | .NET `System.Diagnostics.Process` |
| `ln -s` | Works normally | Requires Developer Mode |
| `stat` | `/usr/bin/stat` | Pure .NET implementation |

## Documentation

**[standardbeagle.github.io/ps-bash](https://standardbeagle.github.io/ps-bash/)** -- full command reference, object type specs, pipeline cookbook, cross-platform guide.

## License

MIT

## Contributing

Issues and PRs welcome.
