# Runtime Functions Specification

This document describes the PowerShell runtime module (`PsBash.psm1`) that provides
Unix command emulation for the ps-bash transpiler.

## Architecture

The psm1 module is loaded into the `ps-bash-host` runspace managed by `SdkWorker`.
Bash commands transpiled by `PsEmitter` into PowerShell are evaluated via
`Invoke-Expression` inside this worker. The module provides `Invoke-Bash*`
functions that emulate Unix commands, and registers global aliases (e.g. `ls` ->
`Invoke-BashLs`) so transpiled code reads naturally.

Key layers:

1. **PsEmitter** (C#) -- transpiles bash AST nodes into PowerShell expressions.
2. **SdkWorker** (C#) -- owns the PowerShell runspace, imports the module, and
   evaluates transpiled expressions on behalf of the host server.
3. **PsBash.psm1** (PowerShell) -- the runtime library providing 76 `Invoke-Bash*`
   functions (75 commands + 1 internal helper), the BashObject model, escape handling,
   glob expansion, and tab completion.

### Alias Architecture (Two-Tier)

`alias`/`unalias` have different implementations depending on the execution context:

- **Module mode** (`Import-Module PsBash`): `Invoke-BashAlias` stores alias definitions
  in `$script:BashUserAliases` and creates dynamic PowerShell functions via
  `Set-Item -Path "Function:\$name"`. Simple aliases like `alias ll='ls -la'` work
  because the generated function body (`ls -la $args`) calls the module's own aliases.
  Complex bash syntax (pipes, redirections) in alias values will not work.
- **Shell mode** (`ps-bash` interactive): alias management is handled entirely in C#
  by `InteractiveShell`. The shell maintains an alias dictionary, intercepts
  `alias`/`unalias` commands before transpilation, and expands the first word of each
  input line against the dictionary. Full bash syntax in alias values is supported
  because expansion happens before the transpiler sees the input.

## BashObject Model

All command output flows through a uniform object model so that pipeline composition
works correctly.

### Core Properties

Every output object carries a `BashText` property containing the string representation
that downstream commands consume. Objects have `PSTypeName = 'PsBash.TextOutput'`
(or a command-specific type like `PsBash.CatLine`, `PsBash.WcResult`).

### Key Functions

| Function | Purpose |
|---|---|
| `Emit-BashLine -Text $s` | **Primary output function.** Splits text on `\n` and emits one `BashObject` per line. Matches bash semantics where `\n` is a record boundary. Use for stdout-like text output (printf, echo -e, heredocs). |
| `New-BashObject -BashText $s` | Creates a single `PSCustomObject` with `BashText`. Does NOT split. Use for typed/structured objects (LsEntry, CatLine, PsEntry) that are inherently single-line. |
| `Set-BashDisplayProperty $obj` | Adds a `ToString()` ScriptMethod returning `$this.BashText` |
| `Get-BashText -InputObject $obj` | Extracts the string from any pipeline object: returns `.BashText` if present, otherwise stringifies via `"$obj"` |

### Shared C# Helpers (REFACTOR-2 Phase 2)

The arg-parsing and BashObject helpers every leaf `Invoke-Bash*` function
depends on are implemented as AOT-safe static methods on
`PsBash.Cmdlets.BashRuntime` (`src/PsBash.Cmdlets/BashRuntime.cs`). A migrated
binary cmdlet calls them directly with no C#→PowerShell callback. The psm1
functions `New-BashObject`, `Emit-BashLine`, `Set-BashDisplayProperty`,
`Get-BashText`, `ConvertFrom-BashArgs`, `New-FlagDefs`, and
`Expand-EscapeSequences` are now thin wrappers that delegate to
`BashRuntime`, so the script-callable surface is unchanged and the
differential suite proves the C# implementations against the live runtime.
`Write-BashError` and `Resolve-BashGlob` remain pure psm1 functions: both need
runspace/script scope (`$script:BashErrorMode`, the PowerShell path provider)
that a plain static helper cannot reach — only `BashRuntime.FormatBashError`
(the runspace-free message-formatting piece) is shared.

### Migrated Binary Cmdlets (REFACTOR-2 Phase 1 / 1b / 1c / 1d / 3 / 3-follow-on / F6)

Some leaf `Invoke-Bash*` commands are no longer psm1 functions — they are
binary cmdlets in `PsBash.Cmdlets.dll`. The psm1 still carries their
`Set-Alias` lines, which resolve to the cmdlet (a script function would
otherwise shadow a same-named cmdlet). The host load order imports
`PsBash.Cmdlets.dll` before the psm1 runs as a script, so the cmdlets exist
before the psm1 `Set-Alias` lines execute.

| Command | Cmdlet class | Phase | Notes |
|---|---|---|---|
| basename | `InvokeBashBasenameCommand` | 1 | Pure string transform |
| dirname  | `InvokeBashDirnameCommand`  | 1 | Pure string transform |
| printf   | `InvokeBashPrintfCommand`   | 1b | Format engine; `--help` / usage-error delegate to psm1 `Show-BashHelp` / `Write-BashError` via string-bodied `InvokeScript` |
| pwd      | `InvokeBashPwdCommand`      | 1b | Reads `$global:__PsBashCwd` + current location via `SessionState`; `-P` is declared as an explicit `SwitchParameter` because the bare token `-P` prefix-collides with the `-ProgressAction` common parameter |
| wc       | `InvokeBashWcCommand`       | 1c | File + pipeline dual mode; typed `PsBash.WcResult`. `-w` is a declared `SwitchParameter` (prefix-collides with `-WarningAction` / `-WarningVariable`); `-l` / `-c` stay in `Arguments`. Bundled forms like `-lw` are recovered post-parse. `Resolve-BashGlob`'s glob slice is reimplemented in C# via `SessionState.Path` |
| cat      | `InvokeBashCatCommand`      | 1c | File + pipeline dual mode; fast path emits `PsBash.TextOutput`, flagged path emits typed `PsBash.CatLine`. `-E` is a declared `SwitchParameter` (prefix-collides with `-ErrorAction` / `-ErrorVariable`); `-n` / `-b` / `-s` / `-T` stay in `Arguments`. Bundled forms like `-nE` are recovered post-parse. A read error sets `$global:LASTEXITCODE = 1` |
| head     | `InvokeBashHeadCommand`     | 1c | File + pipeline dual mode; value-flag parsing (`-n N` / `-nN` / legacy `-N` / bare positional, `-c N` / `-cN`). File mode emits typed `PsBash.CatLine`. No colliding flags — `-n` / `-c` scanned out of `Arguments` |
| tail     | `InvokeBashTailCommand`     | 1c | File + pipeline dual mode; value-flag parsing including `-n +N` / `-c +N` from-line/byte forms, `-f` follow, `-s SECS`. `-f` follow polls the file via `FileInfo` (`Thread.Sleep`), honoring `PSCmdlet.Stopping`. File mode emits typed `PsBash.CatLine`. No colliding flags |
| ls       | `InvokeBashLsCommand`       | 1d | Directory + file target surface; emits typed `PsBash.LsEntry`. Reimplements the pure helper web in C# (`Get-LsEntryFromFsi`, `ConvertTo-PermissionString`, `Format-BashSize`, `Format-BashDate`, `Format-LsLine`, `Test-IsExecutable`). Owns Tier 2 — the real-filesystem hot path (`System.IO` streaming, `-R` via `SearchOption.AllDirectories`) — plus the uniform sort + format pass. Tier 1 (custom `$script:BashLsProviders`) and Tier 3 (PS-provider fallback: Registry:, Cert:, custom PSDrives) stay in psm1 behind the `Get-BashLsProviderEntries` shim, called via string-bodied `InvokeScript` for any non-filesystem target. **Three colliding flags** are declared as explicit `SwitchParameter`s: `-a` / `-A` prefix-match the cmdlet's own `-Arguments` parameter (one switch binds both — names are case-insensitive — and `.`/`..` are never enumerated so `-A` ≡ `-a` on the filesystem path); `-d` prefix-collides with `-Debug`; `-p` with `-ProgressAction` / `-PipelineVariable`. The rest (`-l -h -R -S -t -r -1 -F -i -s`, `--color`) stay in `Arguments`; bundled forms like `-la` are recovered post-parse. `Resolve-BashGlob`'s glob slice is reimplemented in C# via `SessionState.Path`. The final leaf of REFACTOR-2 Phase 1 |
| jq       | `InvokeBashJqCommand`       | F6 | File + pipeline dual mode JSON query interpreter. Reimplements the psm1 `*-Jq*` helper web in C# inside the static `JqEngine` class: filter parser (top-level `\|` / `,` / `//` splits, bracket-matching, `as $var` bindings), evaluator (identity / dot-path field+index+iterate, array / object construction, string interpolation with `\(expr)` embedding), builtins (`keys` / `values` / `length` / `type` / `not` / `map(.)` / `select(...)` with `>= <= != == > <` comparisons, recursion `..`), `if`/`elif`/`else`/`end`, numeric / string / bool / null literals, and the `ConvertTo-JqJson` emitter (`-c` compact / `-S` sort-keys / `-r` raw). JSON input is parsed via `System.Text.Json` into the nested `OrderedDictionary` / `object[]` graph the oracle's `ConvertFrom-Json -AsHashtable` produced. Flags `-r -c -S -s` (case-sensitive, since `-S` ≠ `-s` in jq) have no PowerShell common-parameter prefix collision, so all four stay in `Arguments`; `-s` slurp wraps the inputs into a single array. **Partial migration**: the psm1 `Invoke-BashYq` still calls `Invoke-JqFilter` / `ConvertTo-JqJson` directly on already-parsed YAML hashtables; those two psm1 helpers therefore remain in place this phase as legacy shims for yq. Their removal is filed as a follow-on (paired with a yq migration). Parity tests in `PsBash.Cmdlets.Tests/InvokeBashJqCommandTests.cs` cover empty input, unicode/emoji JSON, large arrays, missing file, malformed JSON, unknown filter, and an injection-string security probe |
| find     | `InvokeBashFindCommand`     | 3-follow-on | Directory-tree walker emitting typed `PsBash.FindEntry`. Supports `-name -type -size -maxdepth -mtime -empty -print0 -exec` (the psm1 oracle's exact predicate set). The `Get-BashFileInfo` slice (`SizeBytes`, `Permissions`, `LinkCount`, `Owner`, `Group`) is reimplemented in C# inside the cmdlet (`BuildFileInfo`) — duplicating the Phase 1d port from `InvokeBashLsCommand.BuildEntryFromFsi`. The duplication is intentional and minimal: the psm1 `Get-BashFileInfo` stays in place because `Invoke-BashStat` (still a psm1 function) depends on it; consolidating the helper into `BashRuntime` would broaden this task's scope. **`-exec` security (Directive 12):** the command name and each argument are passed as positional `$args` entries through a fixed parameterless script body (`& $args[0] @rest`) — never concatenated into the body — so a path containing `;`, `$(...)`, scriptblock chars, or backticks cannot be re-parsed as PowerShell syntax. Routed via `InvokeCommand.InvokeScript` (no `ScriptBlock` construction — AOT-safe). **No common-parameter collisions:** all predicates are full words (`-name -type -size -maxdepth -mtime -empty -print0 -exec`) that share no prefix with any PowerShell common parameter (`-Verbose -Debug -Warning* -Error* -Information* -Out* -PipelineVariable -ProgressAction -WhatIf -Confirm`); they all stay in `Arguments` and are parsed by a manual switch loop matching the psm1 oracle byte-for-byte. Unsupported predicates (e.g. `-iname`, `-delete`) emit a bash-style error via the psm1 `Write-BashError` shim and continue, exactly as the oracle did. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashFindCommandTests.cs` cover empty directory, unicode filenames, missing target, large tree (>1100 files), malformed size, unsupported-predicate continuation, the `find` alias resolution, and the `-exec` injection probe |
| sed      | `InvokeBashSedCommand`      | 3 | File + pipeline dual mode stream editor. Reimplements `ConvertFrom-SedExpression` (address-prefix + command parsing for `s d D p P N q a i c y`, BRE→.NET metachar escaping, `\1`-`\9`/`\&` → `$1`-`$9`/`$0` backreference translation) and `Test-SedAddress` (line / range / regex / regex-range addresses) in C#, plus the pattern-space cycle engine (multi-line `N`/`D`, restart-cycle, `q` early-quit). File mode supports `-i` in-place rewrite (preserving the source trailing newline) and `-f` script files; pipeline mode preserves original typed objects on a 1:1 line mapping. **Two colliding flags** are declared as explicit `SwitchParameter`s: `-i` prefix-collides with `-InformationAction` / `-InformationVariable`; the value-bearing `-e` (script expression) prefix-collides with `-ErrorAction` / `-ErrorVariable` and is declared as an explicit `string[]` `Expression` parameter (aliased `e`). Because PowerShell parameter names are case-insensitive, `-E` (extended regex) cannot be a distinct parameter from `-e`; the extended-regex bit is recovered independently — `-r` is an explicit `SwitchParameter` (no colliding prefix), and bundled short-flag forms (`-nE`, `-rn`) are recovered from `Arguments` post-parse. `-n` / `-f` have no colliding prefix and stay in `Arguments`. **Known binder limitation:** a *repeated* `-e` flag (`sed -e A -e B`) cannot bind to the single `Expression` parameter — PowerShell's binder rejects it as "specified more than once"; pass multiple expressions as a comma-separated array, use `-f` for a multi-command script, or use a `;`-free single expression. The M1/M2/M3 transpiler path that emits `Invoke-BashSed -e A -e B` from bash `sed -e A -e B` is the one residual gap; the common single-`-e` and operand-expression forms are unaffected |
| whoami   | `InvokeBashWhoamiCommand`   | 4 | Trivial — emits `System.Environment.UserName` as a bare `PsBash.TextOutput` string via `BashRuntime.NewBashObject` fast path. No flags besides `--help`. No pipeline input. No psm1 helper dependencies on the hot path; `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Parity tests in `PsBash.Cmdlets.Tests/InvokeBashWhoamiHostnameCommandTests.cs` cover bare-call, alias resolution, `--help`, and an injection probe |
| hostname | `InvokeBashHostnameCommand` | 4 | Trivial — emits `System.Net.Dns.GetHostName()` as a bare `PsBash.TextOutput` string. Same surface shape as whoami above; on `GetHostName()` failure, delegates the bash-style error to psm1 `Write-BashError` via parameter-bound `InvokeCommand.InvokeScript` (the error-mode switch is psm1-scoped). Parity tests in the same file |
| yes      | `InvokeBashYesCommand`      | 4 | Trivial infinite producer — emits joined-with-space args (or `"y"` when none) as bare `PsBash.TextOutput` strings until `PSCmdlet.Stopping` flips true. Termination matches GNU yes: SIGPIPE / consumer shutdown → in PowerShell, `Select-Object -First N` triggers `StopUpstreamCommandsException` which sets `Stopping=true` and the loop bails cleanly. **Known interaction (filed as separate task):** `yes \| Invoke-BashHead -N` hangs because `InvokeBashHeadCommand` buffers pipeline input in `ProcessRecord` and only processes it in `EndProcessing` — never signaling stop to an infinite upstream. That predates this migration (the psm1 oracle had it too) and is out of scope here. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashYesCommandTests.cs` use `Select-Object -First` to bound the producer |
| realpath | `InvokeBashRealpathCommand` | 4 | Path canonicalization — for each non-flag operand, tries `SessionState.Path.GetResolvedPSPathFromPSPath` (existing-path resolver, equivalent to `Resolve-Path`) and falls back to `GetUnresolvedProviderPathFromPSPath` for missing paths, matching the psm1 oracle's catch-block fallback exactly. **Three colliding flags** declared as explicit `SwitchParameter`s and silently ignored (psm1 oracle never implemented them but accepted any `-`-prefixed token as a no-op): `-e` prefix-collides with `-ErrorAction` / `-ErrorVariable`, `-m` is unambiguous but declared for symmetry, `-s` for consistency. Output goes through `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashRealpathCommandTests.cs` cover existing-file resolution, missing-path fallback, multi-operand emission, flag silent-skip with interleaved operand, alias resolution, `--help`, and an injection probe |
| md5sum / sha1sum / sha256sum | `InvokeBashMd5sumCommand` / `InvokeBashSha1sumCommand` / `InvokeBashSha256sumCommand` | 4 | The three GNU coreutils checksum commands; all delegate to the shared `ChecksumEngine` static helper in `InvokeBashChecksumCommands.cs`. File + pipeline dual mode: with operands, hash each file's bytes and emit a typed `PsBash.TextOutput` PSObject per file with `BashText = "<hex>  <path>"` plus side properties `Hash` / `FileName` / `Algorithm`; with empty operands and pipeline input, concatenate every upstream item's BashText with `\n` separators (plus a trailing `\n`), hash the UTF-8 bytes, emit one PSObject with `FileName = "-"`. Missing files emit a bash-style error via psm1 `Write-BashError` and continue. Glob expansion reuses the same `SessionState.Path.GetResolvedProviderPathFromPSPath` slice that `InvokeBashCatCommand` introduced — no psm1 `Resolve-BashGlob` dependency on the hot path. Hash computation uses `System.Security.Cryptography.IncrementalHash`. No colliding flags (the only documented flag the psm1 oracle accepted was `--help`). Replaces a 62-line shared helper plus three 8-line wrappers (= 86 psm1 lines). Parity tests in `InvokeBashChecksumCommandTests.cs` cross-check hex against `System.Security.Cryptography` ground truth, cover pipeline-mode stdin marker, multi-file emission, missing-file continuation, unicode file content, and an injection probe — 10 tests total |
| mkdir / rmdir / cp / mv / rm | `InvokeBashMkdirCommand` / `InvokeBashRmdirCommand` / `InvokeBashCpCommand` / `InvokeBashMvCommand` / `InvokeBashRmCommand` | 4 | The five GNU coreutils filesystem mutators; all share `FileSystemHelpers.cs` for glob expansion (the same SessionState slice cat/checksum use), bash-style error formatting (delegates to psm1 `Write-BashError` via parameter-bound `InvokeCommand.InvokeScript`), and the `$LASTEXITCODE=1` setter. Each cmdlet reimplements the psm1 oracle's branches byte for byte — mkdir's `-p` parent-chain create, rmdir's `-p` parent-walk-upward, cp's `-r` recursive copy + `-n` no-clobber + `-f` overwrite-existing-dir, mv's into-dir basename-preservation, rm's Windows reserved-device-name guard (CON/PRN/AUX/NUL/COM1-9/LPT1-9), and rm's protected-path refusal (drive root + user profile). **One colliding flag** declared as an explicit `SwitchParameter` on all five: `-v` (verbose) prefix-collides with `-Verbose`. mkdir/rmdir additionally declare `-p` (which prefix-collides with `-ProgressAction` / `-PipelineVariable`). `-r` / `-R` / `-n` / `-f` have no PowerShell common-parameter collision and stay in `Arguments`, recovered by a small post-parse switch. Filesystem operations use `System.IO` directly (`Directory.CreateDirectory`, `File.Copy`, `File.Move`, `Directory.Delete(recursive: true)`) — no PowerShell cmdlet round-trip. Replaces 389 psm1 lines. Parity tests in `InvokeBashFileSystemMutatorTests.cs` cover the happy paths plus refusal branches (missing source, dir without `-r`, drive-root protection, no-clobber, `-f` silencing), verbose-output format, and two filename-injection probes (`$(throw)` and `;rm -rf`) — 24 tests total |
| rev | `InvokeBashRevCommand` | 4-follow-on | File + pipeline dual mode line-reverser. Reverses each input line via `Array.Reverse` on the line's `char[]` — exact byte-for-byte parity with the psm1 oracle (same `.ToCharArray()` + `[Array]::Reverse` slice). Pipeline mode splits a multi-line `BashText` item on `\n` (after trailing-newline trim) and reverses each sub-line; file mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` (matching `StreamReader.ReadLine()` semantics — trailing newline does not yield a spurious empty final line). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and set `$global:LASTEXITCODE = 1`. The psm1 oracle's only flag is `--help`; no PowerShell common-parameter prefix collisions — `Arguments` catch-all is sufficient. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). Replaces a 37-line psm1 function. Parity tests in `InvokeBashRevCommandTests.cs` cover empty input, single-/multi-line pipeline, multi-line item split, file mode with CRLF, unicode (non-ASCII chars), missing-file error continuation, empty file, single-newline file, `--help`, alias resolution, and an injection probe per Directive 12 |
| strings | `InvokeBashStringsCommand` | 4-follow-on | File + pipeline dual mode printable-run extractor. Scans each operand file (or `\n`-joined pipeline content) for runs of ASCII printable bytes (`\x20`-`\x7E`) at least `-n N` chars long (default 4; also accepts `--bytes=N`), matching the GNU binutils `strings` regex `[\x20-\x7E]{N,}` byte for byte. Files are read via `File.ReadAllText` with CRLF normalization and BOM-aware UTF-8 decoding — exactly the psm1 oracle's `Read-BashFileBytes` slice, reimplemented in C# in the cmdlet (no callback to psm1 on the hot path). Multi-byte UTF-8 chars decode to .NET `char` code units outside the ASCII printable range, so non-ASCII characters split runs the same way the oracle did. Glob expansion routes through the same `SessionState.Path` slice `InvokeBashCatCommand` introduced; missing files emit a bash-style error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and continue. The psm1 oracle's only flag besides `--help` is `-n N` / `--bytes=N`; `-n` has no PowerShell common-parameter prefix collision, so it stays in `Arguments` and is parsed by the manual value-flag scan. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). Replaces a 43-line psm1 function. Parity tests in `InvokeBashStringsCommandTests.cs` cover known printable run inside a binary blob, no-printable-run shortfall, `-n` threshold gating, multi-file emission, unicode split-at-non-ASCII, pipeline input, missing-file continuation, `--help`, alias resolution, and an injection probe per Directive 12 — 10 tests total |
| tac | `InvokeBashTacCommand` | 4-follow-on | File + pipeline dual mode reverse-cat. Reverses the list of input lines, matching GNU coreutils `tac`. Pipeline mode trims trailing newlines off each pipeline item's `BashText`, splits on `\n`, and accumulates lines; file mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` (matching `StreamReader.ReadLine()` — no spurious trailing empty line). After collection, when `-s SEP` / `--separator=SEP` is set, the lines are joined with `\n`, split on `SEP`, the chunks reversed, and each emitted; otherwise the line list itself is reversed via `List<string>.Reverse()`. The `Read-BashFileLines` psm1 helper the oracle called is small enough to inline (one `File.ReadAllText` + replace + split). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and set `$global:LASTEXITCODE = 1`. **No colliding flags** — `-s` has no PowerShell common-parameter prefix collision (no `-S*` common params), so it stays in `Arguments` and is parsed by the manual value-flag scan. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). Replaces a 54-line psm1 function. Parity tests in `InvokeBashTacCommandTests.cs` cover empty pipeline, multiple pipeline items, multi-line item split, file mode (3-line, CRLF, unicode, empty file), `-s` separator, `--separator=` long form, missing-file error continuation, `--help`, alias resolution, and an injection probe per Directive 12 — 13 tests total |
| readlink | `InvokeBashReadlinkCommand` | 4-follow-on | Symlink-target reader. Default (no `-f`): for each operand, resolve via `SessionState.Path.GetUnresolvedProviderPathFromPSPath` and probe with `File.Exists` / `Directory.Exists`; on miss, emit a bash-style "readlink: PATH: No such file or directory" error via `FileSystemHelpers.WriteBashError` and continue. On hit, emit `FileSystemInfo.LinkTarget` (the .NET API that reads the symlink target string) when non-empty, else `FullName` — matching the psm1 oracle's `if ($item.Target) { $item.Target } else { $item.FullName }` branch byte-for-byte. `-f` (canonicalize) branch routes through `GetResolvedPSPathFromPSPath` and emits `ProviderPath`; on miss, same error contract. Output is a typed `PsBash.ReadlinkOutput` PSObject with `Path` + `BashText` properties (the psm1 oracle's exact shape). **No colliding flags** — `-f` is declared as an explicit `SwitchParameter` for clean binder routing (it has no PowerShell common-parameter prefix collision but the declaration keeps the bare token out of `Arguments`); the psm1 oracle's case-sensitive `-ceq '-f'` match is preserved by the binder's case-insensitive but exact-name match (no other flags exist to confuse). Parity tests in `InvokeBashReadlinkCommandTests.cs` cover regular-file fallthrough to FullName, true symlink target resolution (SkippableFact — requires Developer Mode or admin on Windows), missing path with no output, missing operand with no output, `-f` canonicalize branch (existing + missing), multi-operand (existing + missing), unicode filenames, alias resolution, `--help`, and an injection probe with `$(throw'pwn').txt` — 11 tests total |
| uname | `InvokeBashUnameCommand` | 4-follow-on | Trivial system-info printer. Reimplements the psm1 oracle byte-for-byte: `-s` (default) emits `MINGW64_NT-{Major.Minor.Build}` from `Environment.OSVersion.Version`; `-n` emits lowercased `Environment.MachineName`; `-r` emits `{Major.Minor.Build}`; `-m` emits `x86_64` / `i686` based on `Environment.Is64BitProcess`; `-a` emits the four fields plus the literal `MINGW64` trailer. The MSYS/MINGW-style values are kept regardless of host platform so transpiled bash scripts that grep for these tokens stay portable. Bundled short-flag forms (`-snr`, `-snrm`) are accepted via a manual scan against the oracle's `^-([snrma]+)$` predicate; field order is always s/n/r/m regardless of bundle order. **One colliding flag** declared as an explicit `SwitchParameter`: bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter (the only declared parameter starting with 'a') under PowerShell parameter binding, producing a "Missing an argument for parameter 'Arguments'" error otherwise. Bundled forms containing `a` (`-snra`, `-am`, etc.) do not prefix-match `-Arguments` and continue to land in `Arguments` for post-parse decoding. `-s -n -r -m` have no PowerShell common-parameter prefix collision and stay in `Arguments`. Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput` (bare-string fast path). No pipeline input, no file operands, no psm1 helper dependencies on the hot path; `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 52-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashUnameCommandTests.cs` cover each individual flag, `-a` combined, two bundled forms (`-snr`, `-snrm`), no-flag default, unknown-flag silent drop, `--help`, alias resolution, and two Directive-12 injection probes (`$(throw 'pwn')` arg, `-s;rm` dash-prefixed lookalike) — 13 tests total |
| env | `InvokeBashEnvCommand` | 4-follow-on | Environment-variable printer (GNU coreutils `env` / `printenv`). No-args: enumerates `Environment.GetEnvironmentVariables()` and emits one typed `PsBash.EnvEntry` PSObject per variable (Name / Value / BashText="NAME=VALUE"), sorted by name using `StringComparer.Ordinal` — exact parity with the psm1 oracle's `Sort-Object` step on the key set. One-name form: emits a single entry for that variable, or a bash-style "env: 'NAME': not set" error via `FileSystemHelpers.WriteBashError` (matching the oracle byte-for-byte) and returns with no output. **No colliding flags** — the only flag the psm1 oracle ever accepted was `--help`, which delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Aliases `env` and `printenv` stay in psm1 and resolve to this cmdlet automatically. Replaces a 34-line psm1 function. Parity tests in `InvokeBashEnvCommandTests.cs` cover no-args-emits-all, sort-order, one-name-typed-entry, missing-name-error-no-output, `env` alias, `printenv` alias, `--help`, and an injection probe with `$(throw "pwn");rm -rf /` confirming the literal name reaches the not-set error path unevaluated — 8 tests total |
| mktemp | `InvokeBashMktempCommand` | 4-follow-on | Temp-file/dir creator. Builds a unique name under `{Path.GetTempPath()}/ps-bash/proc-sub/` via `Path.GetRandomFileName()`. With a template operand (`myapp.XXXXXX`), strips the trailing `X+` run, takes the basename as a prefix (the oracle discards the template's directory portion — preserved), and appends `Path.GetRandomFileName()`. `-d` switches to directory creation via `Directory.CreateDirectory`; default creates an empty file via `File.WriteAllText(path, "")`. **One colliding flag** declared as an explicit `SwitchParameter`: `-d` prefix-collides with `-Debug` (same hazard `mkdir` handled). The psm1 oracle accepted no other flags — any other `-`-prefixed token (`-u`, `--suffix=X`) is swallowed as a template candidate, last wins; preserved here as deliberate parity, not a feature. Output is a typed `PsBash.MktempOutput` PSObject with `Path` + `BashText` properties, matching the oracle's `[PSCustomObject]@{PSTypeName='PsBash.MktempOutput'; ...}` shape. Parity tests in `InvokeBashMktempCommandTests.cs` cover default file creation, `-d` directory branch, template prefix derivation, template-with-directory-portion (basename-only), `-u`-as-template oracle parity, `--help`, alias resolution, typed-output PSTypeName check, and a `$(throw)` injection probe — 9 tests total |
| tput | `InvokeBashTputCommand` | 4-follow-on | Terminal-capability query. Two-path: (1) native passthrough — resolves `tput` via parameter-bound `InvokeCommand.InvokeScript("Get-Command tput -CommandType Application ...")` and, when present, shells out via `System.Diagnostics.Process` with `UseShellExecute=false` / `RedirectStandardOutput=true` and operands bound through `ProcessStartInfo.ArgumentList` (Directive 12: no shell, no string concatenation into a script body); emits captured stdout via `BashRuntime.EmitBashLines` when the child exits 0. (2) Fallback emulator — emulates the psm1 oracle's switch byte-for-byte for `cols` / `lines` (via `Host.UI.RawUI.WindowSize` with a `System.Console.WindowWidth` / `.WindowHeight` fallback for non-interactive runspaces), `clear` / unknown (silent), `bold` (`\x1B[1m`), `sgr0` (`\x1B[0m`), `setaf N` (`\x1B[38;5;Nm` — 256-color form, matching the oracle's `"`e[38;5;${color}m"`). **No colliding flags** — the psm1 oracle's only flag besides `--help` is `--` (end-of-flags); declared `Arguments` catch-all parses it as a defensive no-op. Replaces a 37-line psm1 function. Parity tests in `InvokeBashTputCommandTests.cs` cover `cols` / `lines` (positive int), `bold` / `sgr0` / `setaf 4` (exact ANSI bytes), empty operand list (silent), unknown capability (silent), `--help`, alias resolution, and a `$()`-injection probe per Directive 12 — 10 tests total |
| touch / ln / sleep / which | `InvokeBashTouchCommand` / `InvokeBashLnCommand` / `InvokeBashSleepCommand` / `InvokeBashWhichCommand` | 4 | Four small file-metadata and utility commands. **Touch** supports `-d DATE` (`DateTime.TryParse` — bash-style invalid-date error on failure), `-a` (access-time only), `-m` (mod-time only), `-c` (no-create), `-v` (no-op for arg compat); creates an empty file via `File.Create(...).Dispose()` for missing operands and sets timestamps via `File.SetLastWriteTime` / `SetLastAccessTime`. **Ln** supports `-s` (symbolic — `File.CreateSymbolicLink` / `Directory.CreateSymbolicLink` depending on whether the target exists as a dir; defaults to file-symlink for missing targets), `-f` (force — remove existing link first), `-v` (verbose). The non-`-s` hard-link path still delegates to psm1 `New-Item -ItemType HardLink` via parameter-bound `InvokeCommand.InvokeScript` because System.IO has no hard-link API in stdlib. **Sleep** parses each operand as a decimal with optional `s/m/h/d` suffix, sums them, then sleeps in 100 ms chunks polling `PSCmdlet.Stopping` so Ctrl-C / pipeline-stop interrupts within one chunk. **Which** delegates command resolution to PowerShell `Get-Command` via parameter-bound `InvokeCommand.InvokeScript` (the only way to see aliases / functions / cmdlets registered in the runspace); supports `-a` (case-sensitive — `-A` is not the all-flag). **Colliding flags** declared as `SwitchParameter`s: touch `-a` / `-c` / `-v` (vs `Arguments` `a`-prefix / `-Confirm` / `-Verbose`); ln `-v`; sleep none; which none. Replaces 4 functions × ~50–90 psm1 lines each. Parity test coverage to be added — direct smoke test of typical invocations succeeds end-to-end |
| time | `InvokeBashTimeCommand` | 4-follow-on | Wall-clock timer that wraps an inner command, matching the bash `time` builtin / GNU `time`. Reproduces the psm1 oracle byte-for-byte: missing-args → "time: missing command" bash error and no output object; happy path → invoke the wrapped command via `InvokeCommand.InvokeScript` with a fixed parameterless body (`& $args[0] @rest 2>&1`) and the wrapped command name + args bound positionally through `$args` (Directive 12 — user-controlled tokens never concatenate into the script body, so a name containing `;` / `$()` / scriptblock chars / backticks stays a literal command-name lookup and fails the usual CommandNotFoundException path); collected `ErrorRecord` items route through `FileSystemHelpers.WriteBashError` (forcing `ExitCode = 1`) while non-error items contribute their `BashText` (via `BashRuntime.GetBashText`) to the joined-with-`\n` result text. Emits one typed `PsBash.TimeOutput` PSObject (`RealTime` / `Command` / `ExitCode` / `BashText`) and writes `"real    {seconds:N3}s"` to `[Console]::Error` (matching the oracle's `[Console]::Error.WriteLine`). **No colliding flags** — `time` accepts no flags besides `--help`. Replaces a 58-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashTimeCommandTests.cs` cover wrap-echo happy path (TimeOutput shape + ExitCode=0), no-args (no output), unknown-command (ExitCode=1, command name preserved), alias resolution, `--help`, and two Directive-12 injection probes (`$(throw "pwn")` command name, `echo;rm` semicolon-bearing name) — 7 tests total |
| unexpand | `InvokeBashUnexpandCommand` | 4-follow-on | File + pipeline dual mode space-to-tab converter (GNU coreutils `unexpand`; the inverse of `expand`). Reproduces the psm1 oracle's two modes byte-for-byte: **default (leading-only)** counts L leading spaces and emits `floor(L/tabWidth)` tabs + `L%tabWidth` remainder spaces + the rest of the line unchanged (partial leading runs that don't reach a tabstop stay as spaces); **`-a` / `--all`** walks every character — on each space, increments a column counter and a space-run counter, and when `col % tabWidth == 0` AND `spaceRun >= 2`, emits one tab and resets the run; partial runs at end of line stay as literal spaces. Flag surface: `-t N` / `-tN` / `--tabs=N` (tab width, default 8), `-a` / `--all`, `--first-only` (default mode — preserved for arg-compat). **One colliding flag** declared as an explicit `SwitchParameter`: bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter under PowerShell parameter binding — same hazard `uname` handled. `-t` has no PowerShell common-parameter prefix collision and is scanned out of `Arguments` by the manual value-flag loop (separated, joined, and `--tabs=` forms all decoded). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via `FileSystemHelpers.WriteBashError` and continue. Files read via `File.ReadAllText` with CRLF normalization; trailing newline does not produce a spurious empty final line (rev/strings pattern). Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces an 87-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashUnexpandCommandTests.cs` cover 8-leading-spaces → 1 tab, 16 → 2 tabs, partial-run preservation, 10 → tab+2 remainder, `-t 4` / `-t4` / `--tabs=4`, `-a` interior-run-at-boundary, `-a` single-space preservation, default mode interior-spaces preserved, no-spaces unchanged, multi-line file mode, multi-item pipeline, missing-file continuation, `--help`, alias resolution, unicode tail preservation, and a `$(throw)` injection probe per Directive 12 — 18 tests total |

`echo` was **not** migrated: its `-e` / `-n` / `-E` short flags prefix-collide
with PowerShell common parameters (`-e` is ambiguous with `-ErrorAction` /
`-ErrorVariable`) under `PSCmdlet` parameter binding. Unlike `cat -E` /
`wc -w` — where a single colliding flag is salvageable by declaring it as an
explicit `SwitchParameter` (an exact param-name match beats a common-parameter
prefix match) — `echo` has *two* colliding flags (`-e` and `-E`) plus `-e`
still races `-ErrorAction` in abbreviation scenarios, so it is not cleanly
salvageable. The psm1 `param()` form takes no common parameters, so `$args`
receives the flags literally — `echo` therefore stays a psm1 function
permanently (rationale comment block at the `Invoke-BashEcho` definition).

`ls` was migrated in REFACTOR-2 Phase 1d (the final leaf of Phase 1). It was
the hardest of the deferred set — typed `LsEntry` objects, `-R` recursion, and
a large helper web. The pure helpers (`Get-LsEntryFromFsi`,
`ConvertTo-PermissionString`, `Format-BashSize`, `Format-BashDate`,
`Format-LsLine`, `Test-IsExecutable`) are reimplemented in C# inside
`InvokeBashLsCommand`. The provider-tier helpers that touch module-scoped psm1
state (`Register-BashLsProvider` / `$script:BashLsProviders` /
`Get-LsEntryFromPsItem`, plus `Get-BashFileInfo` which is still used by `find`
and `stat`) stay in psm1; the cmdlet reaches Tier 1 / Tier 3 through the
`Get-BashLsProviderEntries` shim. `Format-LsGrid` / `Get-LsDisplayName` were
already display-layer dead code (the `PsBash.LsEntry` ps1xml view renders
`BashText` directly) and were left untouched in psm1.

`sed` was migrated in REFACTOR-2 Phase 3. Its custom parser
(`ConvertFrom-SedExpression`) and address matcher (`Test-SedAddress`) are pure
string/regex transforms with a small psm1-helper surface (`Resolve-BashGlob`,
`Read-BashFileBytes`, `Write-BashFileText`, `Write-BashError`, `Show-BashHelp`),
all already reimplemented in C# by the Phase 1c/1d cmdlets — a clean
cost/benefit, so it migrated.

#### REFACTOR-2 Phase 3 — awk / jq / find decisions

Phase 3 was explicitly split-friendly (each command "likely warrants its own
task"). After a per-command cost/benefit audit, the Phase 3 outcome is **sed
migrated**, with the rest decided as follows:

- **`awk` stays a psm1 function — permanently.** `Invoke-BashAwk` is the entry
  point to a near-complete AWK language interpreter: ~950+ lines across twelve
  psm1 functions (`ConvertFrom-AwkProgram`, `Split-AwkFields`, `Read-AwkBlock`,
  `Test-AwkPattern`, `Resolve-AwkExpression`, `Expand-AwkString`,
  `Invoke-AwkAction`, `Split-AwkStatements`, `Split-AwkFuncArgs`,
  `Format-AwkPrintf`, `Resolve-AwkStringFunc`). It is a recursive-descent
  expression evaluator with its own variable scope, field model, string-function
  library, and `printf` engine. The original Phase 3 task text names awk as the
  prime "may stay in psm1 indefinitely" candidate. Reimplementing an interpreter
  of this size in C# is a multi-week effort whose only payoff is cold-start /
  AOT cost — the migration cost vastly outweighs the benefit, and a port would
  be a large new bug surface against a psm1 oracle that already passes its
  differential cases. **Decision: awk's sub-scope is closed; it remains a psm1
  function.**
- **`jq` migrated in REFACTOR-2 Phase F6 (follow-on from Phase 3).**
  `Invoke-BashJq` is now a binary cmdlet (`InvokeBashJqCommand`); the
  `*-Jq*` helper web is reimplemented in C# inside the static `JqEngine`
  class, with JSON parsing via `System.Text.Json` producing the same
  nested-hashtable / `object[]` graph the oracle's `ConvertFrom-Json
  -AsHashtable` produced. The psm1 `Invoke-BashYq` still calls
  `Invoke-JqFilter` / `ConvertTo-JqJson` directly on already-parsed YAML
  hashtables, so those two psm1 helpers remain in place this phase as
  legacy shims for yq; their removal is filed as a follow-on paired with
  a yq migration. See the cmdlet's row in the Migrated Binary Cmdlets
  table above for the flag surface and security-probe coverage.
- **`find` migrated in REFACTOR-2 Phase 3 follow-on.** `Invoke-BashFind` is
  now a binary cmdlet (`InvokeBashFindCommand`). The two wrinkles flagged
  during the Phase 3 audit (arbitrary `-exec` and the `Get-BashFileInfo`
  helper web) were both resolved by carrying the patterns the earlier phases
  established: `-exec` routes through `InvokeCommand.InvokeScript` with a
  fixed parameterless script body and positional `$args` splatting, so
  user-controlled tokens stay literal (Directive 12); the `Get-BashFileInfo`
  slice is reimplemented in C# inside the cmdlet (`BuildFileInfo`),
  duplicating the Phase 1d port from `InvokeBashLsCommand`. The duplication
  is deliberate — the psm1 `Get-BashFileInfo` is kept in place because
  `Invoke-BashStat` still depends on it, and consolidating into
  `BashRuntime` would broaden scope. See the cmdlet's row in the Migrated
  Binary Cmdlets table above for the flag surface, the failure-surface
  coverage, and the `-exec` injection probe.

### Output Strategy

```
Source produces text with \n  →  Emit-BashLine  →  one BashObject per line in pipeline
Source produces typed object  →  New-BashObject  →  one typed object in pipeline
Consumer receives items      →  pass through original objects (preserves type)
```

### Example

```powershell
# Text output — use Emit-BashLine (splits on \n)
Emit-BashLine -Text "line1`nline2`n"
# Emits TWO objects: BashText="line1\n" and BashText="line2\n"

# Typed output — use New-BashObject (single object)
$obj = New-BashObject -BashText "hello world`n"
# Emits ONE object with BashText="hello world\n"
```

## Pipeline Object Preservation

Consumer commands (grep, sed, tail, awk, sort, etc.) should **pass original objects
through** the pipeline, NOT create new BashObjects. This preserves typed properties
(e.g., `LsEntry.Name`, `CatLine.Content`) through pipe chains like `ls | grep .txt`.

Sources are responsible for emitting one object per line using `Emit-BashLine`. This
matches bash semantics where stdout is a byte stream and `\n` is the record separator.
The "pipe" (PowerShell pipeline) delivers individual line-objects to consumers.

**Defensive split for edge cases:** If a consumer receives a multi-line BashText item
(from an external source or legacy code), it should split only that item while passing
single-line items through unchanged:

```powershell
foreach ($item in $pipelineInput) {
    $text = Get-BashText -InputObject $item
    if ($text -match "`n" -and $text -ne "`n") {
        # Multi-line edge case: split into new BashObjects
        foreach ($subLine in ($text -replace "`n$",'' -split "`n")) {
            # process $subLine
        }
    } else {
        # Single-line: pass original $item (preserves LsEntry, CatLine, etc.)
    }
}
```

**DO NOT** unconditionally flatten all input into `$allLines` — this destroys typed objects.

## Command Reference

| Command | Function | Key Flags | Arg Parsing | Pipeline | File |
|---|---|---|---|---|---|
| echo | Invoke-BashEcho | `-n`, `-e`, `-E` | ConvertFrom-BashArgs | No | No |
| printf | Invoke-BashPrintf | (format + args) | Positional | No | No |
| ls | Invoke-BashLs | `-l`, `-a`, `-A`, `-h`, `-R`, `-S`, `-t`, `-r`, `-1`, `-p`, `-d`, `-F`, `--color`, `-i`, `-s` | Binary cmdlet (`-a`/`-A`, `-d`, `-p` are declared SwitchParameters; rest via ConvertFromBashArgs; bundles recovered post-parse) | No | Yes |
| cat | Invoke-BashCat | `-n`, `-b`, `-s`, `-E`, `-T` | Binary cmdlet (`-E` is a declared SwitchParameter; rest via ConvertFromBashArgs) | Yes | Yes |
| grep | Invoke-BashGrep | `-i`, `-v`, `-n`, `-c`, `-r`, `-l`, `-E`, `-A`, `-B`, `-C` | Manual loop | Yes | Yes |
| rg | Invoke-BashRg | `-i`, `-w`, `-c`, `-l`, `-n`, `-N`, `-o`, `-v`, `-F`, `-g`, `-A`, `-B`, `-C`, `--hidden` | Manual loop | Yes | Yes |
| sort | Invoke-BashSort | `-r`, `-n`, `-u`, `-f`, `-k`, `-t`, `-h`, `-V`, `-M`, `-c` | Manual loop | Yes | Yes |
| head | Invoke-BashHead | `-n`, `-c` | Binary cmdlet (manual value-flag scan) | Yes | Yes |
| tail | Invoke-BashTail | `-n`, `-c`, `-f`, `-s` | Binary cmdlet (manual value-flag scan) | Yes | Yes |
| wc | Invoke-BashWc | `-l`, `-w`, `-c` | Binary cmdlet (`-w` is a declared SwitchParameter; rest via ConvertFromBashArgs) | Yes | Yes |
| find | Invoke-BashFind | `-name`, `-type`, `-size`, `-maxdepth`, `-mtime`, `-empty` | Manual loop | No | Yes |
| stat | Invoke-BashStat | `-c`, `-t`, `--printf` | Manual loop | No | Yes |
| cp | Invoke-BashCp | `-r`, `-v`, `-n`, `-f` | ConvertFrom-BashArgs | No | Yes |
| mv | Invoke-BashMv | `-v`, `-n`, `-f` | ConvertFrom-BashArgs | No | Yes |
| rm | Invoke-BashRm | `-r`, `-f`, `-v` | ConvertFrom-BashArgs | No | Yes |
| mkdir | Invoke-BashMkdir | `-p`, `-v` | ConvertFrom-BashArgs | No | Yes |
| rmdir | Invoke-BashRmdir | `-p`, `-v` | ConvertFrom-BashArgs | No | Yes |
| touch | Invoke-BashTouch | `-d`, `-a`, `-m`, `-c` | Manual loop | No | Yes |
| ln | Invoke-BashLn | `-s`, `-f`, `-v` | Manual loop | No | Yes |
| ps | Invoke-BashPs | `-e`/`-A`, `-f`, `-u`, `-p`, `--sort`, `-o` | Manual loop | No | No |
| sed | Invoke-BashSed | `-n`, `-i`, `-E`, `-e` | Manual loop | Yes | Yes |
| awk | Invoke-BashAwk | `-F`, `-v` | Manual loop | Yes | Yes |
| cut | Invoke-BashCut | `-d`, `-f`, `-c` | Manual loop | Yes | Yes |
| tr | Invoke-BashTr | `-d`, `-s` | Manual loop | Yes | No |
| uniq | Invoke-BashUniq | `-c`, `-d` | Manual loop | Yes | Yes |
| rev | Invoke-BashRev | (none) | Positional | Yes | Yes |
| nl | Invoke-BashNl | `-ba` | Manual loop | Yes | Yes |
| diff | Invoke-BashDiff | `-u` | Manual loop | No | Yes |
| comm | Invoke-BashComm | `-1`, `-2`, `-3` | ConvertFrom-BashArgs | No | Yes |
| column | Invoke-BashColumn | `-t`, `-s` | Manual loop | Yes | Yes |
| join | Invoke-BashJoin | `-t`, `-1`, `-2` | Manual loop | No | Yes |
| paste | Invoke-BashPaste | `-d`, `-s` | Manual loop | Yes | Yes |
| tee | Invoke-BashTee | `-a` | ConvertFrom-BashArgs | Yes | Yes |
| xargs | Invoke-BashXargs | `-I`, `-n` | Manual loop | Yes | No |
| jq | Invoke-BashJq | `-r`, `-c`, `-S`, `-s` | Manual loop | Yes | Yes |
| date | Invoke-BashDate | `-d`, `-u`, `-r`, `+FORMAT` | Manual loop | No | No |
| seq | Invoke-BashSeq | `-s`, `-w` | Manual loop | No | No |
| expr | Invoke-BashExpr | (expression tokens) | Positional | No | No |
| du | Invoke-BashDu | `-h`, `-s`, `-a`, `-c`, `-d` | Manual loop | No | Yes |
| tree | Invoke-BashTree | `-a`, `-d`, `-L`, `-I`, `--dirsfirst` | Manual loop | No | Yes |
| env | Invoke-BashEnv | (none) | Positional | No | No |
| basename | Invoke-BashBasename | `-s` | Manual loop | No | No |
| dirname | Invoke-BashDirname | (none) | Positional | No | No |
| pwd | Invoke-BashPwd | `-P` | Binary cmdlet (`-P` is a declared SwitchParameter) | No | No |
| hostname | Invoke-BashHostname | (none) | None | No | No |
| whoami | Invoke-BashWhoami | (none) | None | No | No |
| fold | Invoke-BashFold | `-w`, `-s`, `-b` | Manual loop | Yes | Yes |
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
| gzip | Invoke-BashGzip | `-d`, `-c`, `-k`, `-f`, `-v`, `-l`, `-1`..`-9` | Manual loop | Yes | Yes |
| tar | Invoke-BashTar | `-c`, `-x`, `-t`, `-f`, `-z`, `-v`, `-C`, `--exclude` | Manual loop | No | Yes |
| yq | Invoke-BashYq | `-r`, `-o` | Manual loop | Yes | Yes |
| xan | Invoke-BashXan | `-d`, subcommands: `headers`, `count`, `select`, `search`, `table` | Manual loop | Yes | Yes |
| sleep | Invoke-BashSleep | (duration) | Positional | No | No |
| time | Invoke-BashTime | (command) | Positional | No | No |
| which | Invoke-BashWhich | `-a` | Manual loop | No | No |
| alias | Invoke-BashAlias | `-p`, `-u`, `-a` | Manual loop | No | No |
| unset | Invoke-BashUnset | `-v`, `-f` | Manual loop | No | No |
| pushd | Invoke-BashPushd | `+N` | Manual loop | No | No |
| popd | Invoke-BashPopd | `+N` | Manual loop | No | No |
| dirs | Invoke-BashDirs | `-c`, `-p`, `-v` | Manual loop | No | No |
| yes | Invoke-BashYes | `STRING` | Positional | Yes | No |
| tput | Invoke-BashTput | `CAPNAME` | Manual loop | No | No |
| shopt | Invoke-BashShopt | `-s`, `-u`, `-p`, `-q` | Manual loop | No | No |
| type | Invoke-BashType | `-t`, `-a`, `-p` | Manual loop | No | No |
| command | Invoke-BashCommand | `-v` | Manual loop | No | No |
| source | Invoke-BashSource | (none) | Positional | No | Yes |
| shift | Invoke-BashShift | `N` | Manual loop | No | No |
| realpath | Invoke-BashRealpath | (none) | Positional | No | No |

Additional aliases: `printenv` -> `Invoke-BashEnv`, `gunzip` -> `Invoke-BashGzip`,
`zcat` -> `Invoke-BashGzip`, `.` -> `Invoke-BashSource`.

## Arg Parsing Pattern

All `Invoke-Bash*` functions follow one of two arg-parsing strategies.

### Strategy 1: ConvertFrom-BashArgs (simple boolean flags)

Used when all flags are simple on/off switches with no value arguments.

```powershell
function Invoke-BashFoo {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $defs = New-FlagDefs -Entries @(
        '-a', 'description of -a'
        '-b', 'description of -b'
    )
    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $flagA = $parsed.Flags['-a']   # $true / $false
    $operands = $parsed.Operands   # List[string]
}
```

`ConvertFrom-BashArgs` handles `--` (end of flags), bundled short flags (`-ab`), and
collects non-flag arguments into `Operands`.

### Strategy 2: Manual while loop (value-bearing flags)

Used when flags take a value argument (e.g. `-n 10`, `-F,`, `-A2`).

```powershell
function Invoke-BashBar {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $operands = [System.Collections.Generic.List[string]]::new()
    $someValue = $null

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-n') {
            $i++
            if ($i -lt $Arguments.Count) { $someValue = $Arguments[$i] }
            $i++; continue
        }
        $operands.Add($arg)
        $i++
    }
}
```

Both strategies support `--` to end flag parsing. Value flags often support joined form
(e.g. `-n10` as well as `-n 10`).

### Pipeline vs File Mode

Commands that accept both pipeline and file input follow this pattern:

```powershell
# Pipeline mode: no file operands, pipeline has data
if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
    # process $pipelineInput via Get-BashText
    return
}

# File mode: operands are file paths, resolved via Resolve-BashGlob
foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
    # read file, process lines
}
```

`Resolve-BashGlob` expands `*` and `?` patterns and resolves relative paths against
PowerShell's `$PWD`.

## Escape Sequence Handling

`Expand-EscapeSequences` converts C-style escape sequences in string literals. It is
used by `echo -e`, `printf`, and `tr` operands.

### Replacement Chain

1. Replace `\\` with a sentinel: `\0ESCAPED_BACKSLASH\0`
2. Replace `\n` -> newline, `\t` -> tab, `\r` -> CR, `\a` -> bell, `\b` -> backspace,
   `\f` -> form feed, `\v` -> vertical tab
3. Replace sentinel back to literal `\`

The sentinel pattern uses NUL characters (`\0`) as delimiters to avoid collisions with
any valid input text. This two-pass approach ensures that `\\n` produces a literal
backslash followed by `n` rather than a newline.

### Usage in Commands

- **echo -e**: Expands escapes in the joined operand text.
- **printf**: Expands escapes in the format string (after `%%` sentinel replacement).
- **tr**: Expands escapes in both SET1 and SET2 operands before character class expansion.

## Temp File Strategy

All temp files are written under a `ps-bash/` subdirectory of the system temp path
(`[System.IO.Path]::GetTempPath()`).

### ModuleExtractor

Path: `ps-bash/module-{version}/`

Extracts the embedded module files (`PsBash.psd1`, `PsBash.psm1`,
`PsBash.Format.ps1xml`) from the assembly's manifest resources into a version-stamped
directory. A `.extracted` marker file signals that extraction completed successfully.

Cache invalidation: if the assembly file's `LastWriteTimeUtc` is newer than the
marker's timestamp, the marker is deleted and files are re-extracted on next access.

### Host Worker

Path: `ps-bash/module-{version}/`

Uses the same version-stamped directory as `ModuleExtractor`. The host process
loads the extracted module into its runspace before executing transpiled scripts.

### Invoke-ProcessSub

Path: `ps-bash/proc-sub/{random-filename}`

Used for process substitution (`<(command)`). Creates a temp file with a random name,
writes the scriptblock's output to it, and returns the path. On error, the temp file
is cleaned up. On success, the caller is responsible for cleanup.

## Adding a New Command

To add a new `Invoke-Bash*` function:

1. **Define the function** following the naming convention `Invoke-Bash{Name}`.

2. **Choose an arg parsing strategy**:
   - Simple boolean flags: use `ConvertFrom-BashArgs` with `New-FlagDefs`.
   - Value-bearing flags: use the manual while loop pattern.

3. **Collect pipeline input** at the top of the function:
   ```powershell
   $Arguments = [string[]]$args
   $pipelineInput = @($input)
   ```

4. **Preserve pipeline objects**: when processing pipeline input, pass original
   objects through (preserving typed properties like LsEntry.Name). Use the
   defensive split pattern for multi-line edge cases (see Pipeline Object Preservation).

5. **Support file mode** if applicable: use `Resolve-BashGlob` on operands, read files
   with BOM-aware UTF-8 decoding, normalize `\r\n` to `\n`.

6. **Emit output** using the right function:
   - `Emit-BashLine -Text $s` for text output (splits on `\n`, one object per line)
   - `New-BashObject` for typed objects (LsEntry, CatLine — single-line, preserves type)

7. **Register the alias** at the bottom of the module:
   ```powershell
   Set-Alias -Name 'foo' -Value 'Invoke-BashFoo' -Force -Scope Global -Option AllScope
   ```

8. **Add help and completion metadata** to `$script:BashHelpSpecs` and
   `$script:BashFlagSpecs`.

## `install` Command

`Invoke-BashInstall` copies files and sets attributes, with special handling for
in-use binaries on Windows.

### Supported flags

| Flag | Description |
|------|-------------|
| `-d` | Create directories |
| `-D` | Create leading path components |
| `-m` | Set mode (tracked, not enforced on Windows) |
| `-v` | Verbose output |
| `-s` | Strip (no-op on Windows) |
| `-t` | Target directory |
| `-S` | Swap suffix (default: `.old`) |

### Windows binary swap

When the destination file already exists and is locked:

1. Move existing file to `{dest}{suffix}`
2. Copy new file to destination
3. Schedule deferred deletion of the old file (via `MoveFileEx` with `MOVEFILE_DELAY_UNTIL_REBOOT`)

This reproduces the Unix `install` behavior where a running binary can be replaced
because the inode remains open; on Windows the equivalent is rename-then-copy.
