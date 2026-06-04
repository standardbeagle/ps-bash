# Command Reference

> Part of the [Runtime Functions Specification](./runtime-functions.md). One row per emulated command: the `Invoke-Bash*` function, key flags, arg-parsing strategy, and whether it accepts pipeline / file input. Commands implemented as binary cmdlets are documented in detail in [Migrated Binary Cmdlets](./runtime-migrated-cmdlets.md).

| Command | Function | Key Flags | Arg Parsing | Pipeline | File |
|---|---|---|---|---|---|
| echo | Invoke-BashEcho | `-n`, `-e`, `-E` | ConvertFrom-BashArgs | No | No |
| printf | Invoke-BashPrintf | (format + args) | Positional | No | No |
| ls | Invoke-BashLs | `-l`, `-a`, `-A`, `-h`, `-R`, `-S`, `-t`, `-r`, `-1`, `-p`, `-d`, `-F`, `--color`, `-i`, `-s` | Binary cmdlet (`-a`/`-A`, `-d`, `-p` are declared SwitchParameters; rest via ConvertFromBashArgs; bundles recovered post-parse) | No | Yes |
| cat | Invoke-BashCat | `-n`, `-b`, `-s`, `-E`, `-T` | Binary cmdlet (`-E` is a declared SwitchParameter; rest via ConvertFromBashArgs) | Yes | Yes |
| grep | Invoke-BashGrep | `-i`, `-v`, `-n`, `-c`, `-r`, `-l`, `-E`, `-F`, `-w`, `-A`, `-B`, `-C`, `-e`, `-m`, `-q`, `-o`, `-H`, `-h` | Binary cmdlet (`-i`/`-v`/`-c`/`-w` declared as SwitchParameters `I`/`V`/`C`/`W`; `-e` declared as value-bearing `string[] E`; `-A`/`-B` declared as nullable `int? A`/`int? B`; other flags + bundled forms stay in `Arguments`) | Yes | Yes |
| rg | Invoke-BashRg | `-i`, `-w`, `-c`, `-l`, `-n`, `-N`, `-o`, `-v`, `-F`, `-g`, `-A`, `-B`, `-C`, `--hidden` | Binary cmdlet (`-i`/`-v`/`-c`/`-w` declared as SwitchParameters `I`/`V`/`C`/`W`; `-A`/`-B` declared as nullable `int? A`/`int? B`; other flags + bundled forms stay in `Arguments`; native `rg.exe` passthrough when on PATH, else internal regex engine) | Yes | Yes |
| sort | Invoke-BashSort | `-r`, `-n`, `-u`, `-f`, `-k`, `-t`, `-h`, `-V`, `-M`, `-c` | Binary cmdlet (`-c` / `-d` / `-V` declared as `SwitchParameter`s `C` / `D` / `V`; `-r` / `-n` / `-u` / `-f` / `-h` / `-M` / `-b` / `-s` plus value-bearing `-k` / `-t` stay in `Arguments`) | Yes | Yes |
| head | Invoke-BashHead | `-n`, `-c` | Binary cmdlet (manual value-flag scan) | Yes | Yes |
| tail | Invoke-BashTail | `-n`, `-c`, `-f`, `-s` | Binary cmdlet (manual value-flag scan) | Yes | Yes |
| wc | Invoke-BashWc | `-l`, `-w`, `-c` | Binary cmdlet (`-w` is a declared SwitchParameter; rest via ConvertFromBashArgs) | Yes | Yes |
| find | Invoke-BashFind | `-name`/`-iname`, `-path`/`-ipath`, `-regex`/`-iregex`, `-type`, `-size`, `-maxdepth`/`-mindepth`, `-mtime`, `-newer`, `-empty`, `-print0`, `-prune`, `-delete`, `-depth`, `-exec`; boolean expression operators `-a`/`-and`, `-o`/`-or`, `-not`/`!`, `( )` grouping, `-true`/`-false` | Binary cmdlet (predicates compiled to leaf delegates, combined by a per-item boolean-expression evaluator with GNU precedence `! > AND > OR`; implicit-AND of sequential predicates is byte-identical to the old flat filter). **Binder note:** the infix `-o` (vs `-OutVariable`/`-OutBuffer`) and `-a` (vs the cmdlet's own `-Arguments`) are position-critical, so the emitter force-quotes them (`PsEmitter.FindForceQuoteFlags`) rather than declaring switch decoys, which would lose position. Bare `!` works (the parser keeps a post-command-word `!` as a literal operand). | No | Yes |
| stat | Invoke-BashStat | `-c`, `-t`, `--printf` | Binary cmdlet (`-c FORMAT` declared as value-bearing parameter `C`; `-t` / `--printf=` stay in `Arguments`) | No | Yes |
| cp | Invoke-BashCp | `-r`, `-v`, `-n`, `-f` | ConvertFrom-BashArgs | No | Yes |
| mv | Invoke-BashMv | `-v`, `-n`, `-f` | ConvertFrom-BashArgs | No | Yes |
| rm | Invoke-BashRm | `-r`, `-f`, `-v` | ConvertFrom-BashArgs | No | Yes |
| mkdir | Invoke-BashMkdir | `-p`, `-v` | ConvertFrom-BashArgs | No | Yes |
| rmdir | Invoke-BashRmdir | `-p`, `-v` | ConvertFrom-BashArgs | No | Yes |
| touch | Invoke-BashTouch | `-d`, `-a`, `-m`, `-c` | Manual loop | No | Yes |
| ln | Invoke-BashLn | `-s`, `-f`, `-v` | Manual loop | No | Yes |
| ps | Invoke-BashPs | `-e`/`-A`, `-f`, `-u`, `-p`, `--sort`, `-o` | Binary cmdlet (`-e` / `-A` declared as `SwitchParameter`s `E` / `A`; `-p` / `-o` declared as nullable `string`s `P` / `O`; `-f` / `-u` / `--sort` / `aux` stay in `Arguments`) | No | No |
| sed | Invoke-BashSed | `-n`, `-i`, `-E`, `-e` | Manual loop | Yes | Yes |
| awk | Invoke-BashAwk | `-F`, `-v` | Manual loop | Yes | Yes |
| cut | Invoke-BashCut | `-d`, `-f`, `-c` | Binary cmdlet (`-d` and `-c` are declared value-bearing parameters `D` / `C`; `-f` and joined `-dC`/`-fLIST`/`-cLIST` stay in `Arguments`) | Yes | Yes |
| tr | Invoke-BashTr | `-d`, `-s` | Binary cmdlet (`-d`/`-c` declared as SwitchParameters; `-s`/`-t` and bundled forms stay in `Arguments`) | Yes | No |
| uniq | Invoke-BashUniq | `-c`, `-d`, `-u`, `-i`, `-f`, `-s`, `-w` | Binary cmdlet (`-c`/`-d`/`-i` declared SwitchParameters; `-u`/`-f`/`-s`/`-w` and bundled forms stay in `Arguments`) | Yes | Yes |
| rev | Invoke-BashRev | (none) | Positional | Yes | Yes |
| nl | Invoke-BashNl | `-ba` | Manual loop | Yes | Yes |
| diff | Invoke-BashDiff | `-u` | Manual loop | No | Yes |
| comm | Invoke-BashComm | `-1`, `-2`, `-3` | Binary cmdlet (digit-bundle scan in `Arguments`; no colliding flags) | No | Yes |
| column | Invoke-BashColumn | `-t`, `-s` | Binary cmdlet (manual scan; no colliding flags) | Yes | Yes |
| join | Invoke-BashJoin | `-t`, `-1`, `-2` | Binary cmdlet (manual value-flag scan) | No | Yes |
| paste | Invoke-BashPaste | `-d`, `-s` | Manual loop | Yes | Yes |
| tee | Invoke-BashTee | `-a` | Binary cmdlet (`-a` is a declared SwitchParameter; `--` end-of-flags recovered post-parse) | Yes | Yes |
| xargs | Invoke-BashXargs | `-I`, `-n`, `-L`, `-r`, `-t`, `-0`, `-P` | Binary cmdlet (`-I REPLACE` declared as value-bearing parameter `I`; `-P N` declared as `int? P`; `-n`/`-L`/`-r`/`-t`/`-0`/`-p` stay in `Arguments`) | Yes | No |
| jq | Invoke-BashJq | `-r`, `-c`, `-S`, `-s` | Manual loop | Yes | Yes |
| date | Invoke-BashDate | `-d`, `-u`, `-r`, `+FORMAT` | Binary cmdlet (`-d` declared as value-bearing parameter `D`; `-u` / `-r` / `+FORMAT` stay in `Arguments`) | No | No |
| seq | Invoke-BashSeq | `-s`, `-w` | Manual loop | No | No |
| expr | Invoke-BashExpr | (expression tokens) | Positional | No | No |
| du | Invoke-BashDu | `-h`, `-s`, `-a`, `-c`, `-d` | Binary cmdlet (`-a` / `-c` declared as `SwitchParameter`s; `-d N` as nullable `int? D`; `-h` / `-s` and joined `-dN` stay in `Arguments`) | No | Yes |
| tree | Invoke-BashTree | `-a`, `-d`, `-L`, `-I`, `--dirsfirst` | Binary cmdlet (`-a` / `-d` declared as SwitchParameters; `-I` declared as a value-bearing string parameter; `-L` and `--dirsfirst` stay in `Arguments`) | No | Yes |
| env | Invoke-BashEnv | (none) | Positional | No | No |
| basename | Invoke-BashBasename | `-s` | Manual loop | No | No |
| dirname | Invoke-BashDirname | (none) | Positional | No | No |
| pwd | Invoke-BashPwd | `-P` | Binary cmdlet (`-P` is a declared SwitchParameter) | No | No |
| hostname | Invoke-BashHostname | (none) | None | No | No |
| whoami | Invoke-BashWhoami | (none) | None | No | No |
| fold | Invoke-BashFold | `-w`, `-s`, `-b` | Binary cmdlet (`-w` is a declared value-bearing parameter; `-s` is a declared SwitchParameter; `-b` and joined `-wN`/`--width=N` stay in `Arguments`) | Yes | Yes |
| expand | Invoke-BashExpand | `-t` | Manual loop | Yes | Yes |
| unexpand | Invoke-BashUnexpand | `-t`, `-a` | Manual loop | Yes | Yes |
| strings | Invoke-BashStrings | `-n` | Manual loop | Yes | Yes |
| split | Invoke-BashSplit | `-l`, `-d`, `-a` | Manual loop | Yes | Yes |
| tac | Invoke-BashTac | `-s` | Manual loop | Yes | Yes |
| base64 | Invoke-BashBase64 | `-d`, `-w` | Manual loop | Yes | Yes |
| md5sum | Invoke-BashMd5sum | `-c`, `-b` | (delegates to Invoke-BashChecksum) | Yes | Yes |
| sha1sum | Invoke-BashSha1sum | `-c`, `-b` | (delegates to Invoke-BashChecksum) | Yes | Yes |
| sha256sum | Invoke-BashSha256sum | `-c`, `-b` | (delegates to Invoke-BashChecksum) | Yes | Yes |
| file | Invoke-BashFile | `-b`, `-i`, `-L` | Manual loop | No | Yes |
| gzip | Invoke-BashGzip | `-d`, `-c`, `-k`, `-f`, `-v`, `-l`, `-1`..`-9` | Binary cmdlet (`-d`/`-c`/`-v`/`-f` declared as SwitchParameters `D`/`C`/`V`/`F`; `-k`/`-l`/`-1`..`-9` stay in `Arguments`) | Yes | Yes |
| tar | Invoke-BashTar | `-c`, `-x`, `-t`, `-f`, `-z`, `-v`, `--directory=DIR`, `--exclude=PATTERN` | Binary cmdlet (`-c` / `-v` declared as `SwitchParameter`s `C` / `V`; `-f FILE` declared as nullable `string? F`; `-x` / `-t` / `-z` and bundled forms stay in `Arguments`. **Known gap:** the case-insensitive binder collapses bash `-c` (create) and `-C` (change-dir) onto the same `C` switch — callers must use `--directory=DIR` for the change-dir flag) | No | Yes |
| yq | Invoke-BashYq | `-r`, `-o` | Binary cmdlet (`-o` declared as value-bearing parameter `O` — prefix-collides with `-OutVariable` / `-OutBuffer`; `-r` and `--output-format` stay in `Arguments`; delegates YAML parse + jq filter + output to psm1 helpers via param-bound InvokeScript) | Yes | Yes |
| xan | Invoke-BashXan | `-d`, subcommands: `headers`, `count`, `select`, `search`, `table` | Binary cmdlet (`-d` is a declared value-bearing parameter `D`; subcommand keyword stays in `Arguments`) | Yes | Yes |
| sleep | Invoke-BashSleep | (duration) | Positional | No | No |
| time | Invoke-BashTime | (command) | Positional | No | No |
| which | Invoke-BashWhich | `-a` | Manual loop | No | No |
| ping | Invoke-BashPing | `-c`/`-n` count, `-W` timeout, `-t` | Binary cmdlet (manual scan; managed `System.Net.NetworkInformation.Ping`). Emits one `PsBash.PingReply` object per probe — `BashText` native-style line + `Seq`/`Address`/`Time`/`Ttl`/`Status` + latency `class` (ok/slow/high/timeout). Pipe to `Show-Styled` (auto `net` sheet) for the interactive viewer | No | No |
| tracert / traceroute | Invoke-BashTraceroute | `-m`/`-h` maxhops, `-w`/`-W` timeout | Binary cmdlet (manual scan). Prefers the native `traceroute`/`tracert` binary (TTL tracing needs raw sockets), wrapping each hop line as a `PsBash.TraceHop` object (`BashText` + `Hop`/`Address`/`Time` + latency `class`); managed `Ping` TTL fallback when no native binary (needs `cap_net_raw`). Auto `net` sheet | No | No |
| alias | Invoke-BashAlias | `-p`, `-u`, `-a` | Binary cmdlet (`-p` / `-a` declared as `SwitchParameter`s `P` / `A`; `-u` stays in `Arguments`; psm1-scoped `$script:BashUserAliases` accessed via parameter-bound `InvokeScript`) | No | No |
| trap | Invoke-BashTrap | `-p`, `-l` | Binary cmdlet (`-p` declared as `SwitchParameter P`; `-l` stays in `Arguments`; psm1-scoped `$script:BashTrapHandlers` accessed via parameter-bound `InvokeScript`; ERR/EXIT also publish `$global:__BashTrapERR` / `$global:__BashTrapEXIT` scriptblocks) | No | No |
| unset | Invoke-BashUnset | `-v`, `-f` | Manual loop | No | No |
| pushd | Invoke-BashPushd | `+N` | Binary cmdlet (no declared switches; shares PowerShell's built-in location stack via `Push-Location` / `Pop-Location -Stack`) | No | No |
| popd | Invoke-BashPopd | `+N` | Binary cmdlet (no declared switches; shares PowerShell's built-in location stack) | No | No |
| dirs | Invoke-BashDirs | `-c`, `-p`, `-v` | Binary cmdlet (`-c` / `-p` / `-v` declared as `SwitchParameter`s `C` / `P` / `V`; shares PowerShell's built-in location stack) | No | No |
| yes | Invoke-BashYes | `STRING` | Positional | Yes | No |
| tput | Invoke-BashTput | `CAPNAME` | Manual loop | No | No |
| shopt | Invoke-BashShopt | `-s`, `-u`, `-p`, `-q` | Manual loop | No | No |
| type | Invoke-BashType | `-t`, `-a`, `-p` | Binary cmdlet (`-a` and `-p` are declared SwitchParameters `A` / `P`; `-t` stays in `Arguments`) | No | No |
| command | Invoke-BashCommand | `-v`, `-V`, `-p` | Binary cmdlet (`-v` declared as `SwitchParameter V` — `-V` collapses onto same switch under case-insensitive binder, matching the oracle which treated them identically; `-p` declared as `SwitchParameter P` — accepted but ignored, oracle parity) | No | No |
| source | Invoke-BashSource | (none) | Positional | No | Yes |
| shift | Invoke-BashShift | `N` | Manual loop | No | No |
| realpath | Invoke-BashRealpath | (none) | Positional | No | No |
| id | Invoke-BashId | `-u`, `-g`, `-G`, `-n`, `-r` | Binary cmdlet (no declared switches; manual case-sensitive scan distinguishes `-g` vs `-G`; Windows uses `WindowsIdentity`, Unix shells out to `/usr/bin/id`) | No | No |
| mapfile / readarray | Invoke-BashMapfile | `-n`, `-O`, `-s`, `-t`, `-d` | Binary cmdlet (`-O` declared as `int? O`, `-d` declared as `string? D`; `-n` / `-s` / `-t` stay in `Arguments`) | Yes | No |
| less | Invoke-BashLess | `-N`, `-i`, `-S` | Binary cmdlet (`-i` declared as `SwitchParameter I`; `-N` / `-S` declared as `SwitchParameter`s for clean binder routing; rest stay in `Arguments`. Non-interactive: pass-through; interactive: shells out to native `less` via `Process` with operands bound through `ProcessStartInfo.ArgumentList`) | Yes | Yes |
| more | Invoke-BashMore | `-N`, `+NUM` | Binary cmdlet (`-N` declared as `SwitchParameter N`; `+NUM` positional stays in `Arguments`. Non-interactive: emits all input lines as `PsBash.TextOutput`; the oracle's interactive paging loop is not exercised in SDK runspaces) | Yes | Yes |
| test / `[` | Invoke-BashTest | `-e -f -d -r -w -x -s -L -h -z -n`, `= != -eq -ne -lt -le -gt -ge`, `! -a -o` | Binary cmdlet (colliding bare flags `-e` / `-d` / `-w` / `-a` / `-o` declared as `SwitchParameter`s `E` / `D` / `W` / `A` / `O` and re-injected into the operand list post-parse; rest stay in `Arguments`) | No | No |
| read | Invoke-BashRead | `-r`, `-p`, `-a`, `-n`, `-N`, `-t`, `-s` | Binary cmdlet (`-p` declared as `string? P`, `-a` declared as `string? A`; `-r`/`-n`/`-N`/`-t`/`-s` stay in `Arguments`) | Yes | No |
| browse | Invoke-BashBrowse | `--list`, `-PassThru`, `-Inspect`, `-Select`, `-Action`, `-Exec`, `-Force` | Binary cmdlet (all long-name flags; no colliding short flags. Non-interactive list path renders typed `PsBash.BrowseRow` PSObjects via psm1 `ConvertTo-BrowseRow`; interactive TTY path hands off to psm1 `Invoke-BrowseInteractive`; optional `Out-GridView` enhancement when present and stdin is a real terminal) | Yes | No |

Additional aliases: `printenv` -> `Invoke-BashEnv`, `gunzip` -> `Invoke-BashGzip`,
`zcat` -> `Invoke-BashGzip`, `.` -> `Invoke-BashSource`.
