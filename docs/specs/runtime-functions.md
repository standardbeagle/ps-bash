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
| split | `InvokeBashSplitCommand` | 4-follow-on | File + pipeline dual mode partitioner. Splits the input into pieces of `-l N` lines (`--lines=N`; default 1000) named `{PREFIX}{suffix}` written to the current working directory (`SessionState.Path.CurrentLocation.Path`, matching the oracle's `Join-Path $PWD`). Default suffix is alphabetic (`aa`, `ab`, …, `zz`, `aaa`, …) reproduced byte-for-byte from the oracle's base-26 decomposition loop; `-d` / `--numeric-suffixes` switches to zero-padded numeric. `-a N` / `--suffix-length=N` sets suffix length (default 2). Operand surface: `FILE [PREFIX]`; bare `-` reads pipeline; with no operands and pipeline input, falls back to stdin with the default `x` prefix; with neither, emits a bash-style "split: missing operand" error. File mode reads via `File.ReadAllLines` (matches the oracle's `Read-BashFileLines` / `StreamReader.ReadLine` semantics — no spurious trailing empty line for a file ending in a newline); each piece is written via `File.WriteAllText` with a `\n` separator and a trailing `\n`, matching the oracle. **Two colliding flags** declared as explicit parameters: `-d` is a `SwitchParameter` (prefix-collides with `-Debug`); `-a` is an explicit `int? A` parameter (the bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter — same hazard `ls` / `uname` hit; declared without an `[Alias("a")]` because case-insensitive binder makes `-a` ≡ `-A` and a same-name alias raises "conflicts with the parameter alias" at module load). `-l` has no PowerShell common-parameter prefix collision and stays in `Arguments`; long-form `--lines=` / `--suffix-length=` / `--numeric-suffixes` likewise pass through to `Arguments` and are recovered by the manual value-flag scan. Replaces a 95-line psm1 function. Parity tests in `InvokeBashSplitCommandTests.cs` cover default 1000-line single-piece, `-l 10` five-piece, `-d` numeric suffix, `-a 3` three-char suffix, custom PREFIX operand, missing input file (no output files), pipeline input with default `x` prefix, `--help`, alias resolution, and a Directive-12 injection probe with `$(throw'pwn');pfx-` as the prefix operand — 10 tests total |
| readlink | `InvokeBashReadlinkCommand` | 4-follow-on | Symlink-target reader. Default (no `-f`): for each operand, resolve via `SessionState.Path.GetUnresolvedProviderPathFromPSPath` and probe with `File.Exists` / `Directory.Exists`; on miss, emit a bash-style "readlink: PATH: No such file or directory" error via `FileSystemHelpers.WriteBashError` and continue. On hit, emit `FileSystemInfo.LinkTarget` (the .NET API that reads the symlink target string) when non-empty, else `FullName` — matching the psm1 oracle's `if ($item.Target) { $item.Target } else { $item.FullName }` branch byte-for-byte. `-f` (canonicalize) branch routes through `GetResolvedPSPathFromPSPath` and emits `ProviderPath`; on miss, same error contract. Output is a typed `PsBash.ReadlinkOutput` PSObject with `Path` + `BashText` properties (the psm1 oracle's exact shape). **No colliding flags** — `-f` is declared as an explicit `SwitchParameter` for clean binder routing (it has no PowerShell common-parameter prefix collision but the declaration keeps the bare token out of `Arguments`); the psm1 oracle's case-sensitive `-ceq '-f'` match is preserved by the binder's case-insensitive but exact-name match (no other flags exist to confuse). Parity tests in `InvokeBashReadlinkCommandTests.cs` cover regular-file fallthrough to FullName, true symlink target resolution (SkippableFact — requires Developer Mode or admin on Windows), missing path with no output, missing operand with no output, `-f` canonicalize branch (existing + missing), multi-operand (existing + missing), unicode filenames, alias resolution, `--help`, and an injection probe with `$(throw'pwn').txt` — 11 tests total |
| uname | `InvokeBashUnameCommand` | 4-follow-on | Trivial system-info printer. Reimplements the psm1 oracle byte-for-byte: `-s` (default) emits `MINGW64_NT-{Major.Minor.Build}` from `Environment.OSVersion.Version`; `-n` emits lowercased `Environment.MachineName`; `-r` emits `{Major.Minor.Build}`; `-m` emits `x86_64` / `i686` based on `Environment.Is64BitProcess`; `-a` emits the four fields plus the literal `MINGW64` trailer. The MSYS/MINGW-style values are kept regardless of host platform so transpiled bash scripts that grep for these tokens stay portable. Bundled short-flag forms (`-snr`, `-snrm`) are accepted via a manual scan against the oracle's `^-([snrma]+)$` predicate; field order is always s/n/r/m regardless of bundle order. **One colliding flag** declared as an explicit `SwitchParameter`: bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter (the only declared parameter starting with 'a') under PowerShell parameter binding, producing a "Missing an argument for parameter 'Arguments'" error otherwise. Bundled forms containing `a` (`-snra`, `-am`, etc.) do not prefix-match `-Arguments` and continue to land in `Arguments` for post-parse decoding. `-s -n -r -m` have no PowerShell common-parameter prefix collision and stay in `Arguments`. Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput` (bare-string fast path). No pipeline input, no file operands, no psm1 helper dependencies on the hot path; `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 52-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashUnameCommandTests.cs` cover each individual flag, `-a` combined, two bundled forms (`-snr`, `-snrm`), no-flag default, unknown-flag silent drop, `--help`, alias resolution, and two Directive-12 injection probes (`$(throw 'pwn')` arg, `-s;rm` dash-prefixed lookalike) — 13 tests total |
| env | `InvokeBashEnvCommand` | 4-follow-on | Environment-variable printer (GNU coreutils `env` / `printenv`). No-args: enumerates `Environment.GetEnvironmentVariables()` and emits one typed `PsBash.EnvEntry` PSObject per variable (Name / Value / BashText="NAME=VALUE"), sorted by name using `StringComparer.Ordinal` — exact parity with the psm1 oracle's `Sort-Object` step on the key set. One-name form: emits a single entry for that variable, or a bash-style "env: 'NAME': not set" error via `FileSystemHelpers.WriteBashError` (matching the oracle byte-for-byte) and returns with no output. **No colliding flags** — the only flag the psm1 oracle ever accepted was `--help`, which delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Aliases `env` and `printenv` stay in psm1 and resolve to this cmdlet automatically. Replaces a 34-line psm1 function. Parity tests in `InvokeBashEnvCommandTests.cs` cover no-args-emits-all, sort-order, one-name-typed-entry, missing-name-error-no-output, `env` alias, `printenv` alias, `--help`, and an injection probe with `$(throw "pwn");rm -rf /` confirming the literal name reaches the not-set error path unevaluated — 8 tests total |
| mktemp | `InvokeBashMktempCommand` | 4-follow-on | Temp-file/dir creator. Builds a unique name under `{Path.GetTempPath()}/ps-bash/proc-sub/` via `Path.GetRandomFileName()`. With a template operand (`myapp.XXXXXX`), strips the trailing `X+` run, takes the basename as a prefix (the oracle discards the template's directory portion — preserved), and appends `Path.GetRandomFileName()`. `-d` switches to directory creation via `Directory.CreateDirectory`; default creates an empty file via `File.WriteAllText(path, "")`. **One colliding flag** declared as an explicit `SwitchParameter`: `-d` prefix-collides with `-Debug` (same hazard `mkdir` handled). The psm1 oracle accepted no other flags — any other `-`-prefixed token (`-u`, `--suffix=X`) is swallowed as a template candidate, last wins; preserved here as deliberate parity, not a feature. Output is a typed `PsBash.MktempOutput` PSObject with `Path` + `BashText` properties, matching the oracle's `[PSCustomObject]@{PSTypeName='PsBash.MktempOutput'; ...}` shape. Parity tests in `InvokeBashMktempCommandTests.cs` cover default file creation, `-d` directory branch, template prefix derivation, template-with-directory-portion (basename-only), `-u`-as-template oracle parity, `--help`, alias resolution, typed-output PSTypeName check, and a `$(throw)` injection probe — 9 tests total |
| tput | `InvokeBashTputCommand` | 4-follow-on | Terminal-capability query. Two-path: (1) native passthrough — resolves `tput` via parameter-bound `InvokeCommand.InvokeScript("Get-Command tput -CommandType Application ...")` and, when present, shells out via `System.Diagnostics.Process` with `UseShellExecute=false` / `RedirectStandardOutput=true` and operands bound through `ProcessStartInfo.ArgumentList` (Directive 12: no shell, no string concatenation into a script body); emits captured stdout via `BashRuntime.EmitBashLines` when the child exits 0. (2) Fallback emulator — emulates the psm1 oracle's switch byte-for-byte for `cols` / `lines` (via `Host.UI.RawUI.WindowSize` with a `System.Console.WindowWidth` / `.WindowHeight` fallback for non-interactive runspaces), `clear` / unknown (silent), `bold` (`\x1B[1m`), `sgr0` (`\x1B[0m`), `setaf N` (`\x1B[38;5;Nm` — 256-color form, matching the oracle's `"`e[38;5;${color}m"`). **No colliding flags** — the psm1 oracle's only flag besides `--help` is `--` (end-of-flags); declared `Arguments` catch-all parses it as a defensive no-op. Replaces a 37-line psm1 function. Parity tests in `InvokeBashTputCommandTests.cs` cover `cols` / `lines` (positive int), `bold` / `sgr0` / `setaf 4` (exact ANSI bytes), empty operand list (silent), unknown capability (silent), `--help`, alias resolution, and a `$()`-injection probe per Directive 12 — 10 tests total |
| expand | `InvokeBashExpandCommand` | 4-follow-on | File + pipeline dual mode tab-to-space expander (GNU coreutils `expand`). Reimplements the psm1 oracle's column-tracked replacement loop byte-for-byte in C# inside the cmdlet: track current column per line; on `\t`, append `tabWidth - col % tabWidth` spaces and advance the column; on any other char, append it and advance the column by one. Supports `-t N` (separate value), `-tN` (joined digits-only), and `--tabs=N` long form — the exact flag set the oracle parsed. The psm1 oracle does not implement multi-stop tab lists (`-t 4,8,12`); the cmdlet preserves that parity (`int.Parse` on a comma string throws, matching the oracle's `[int]"4,8,12"` cast). Pipeline mode (no operands + pipeline input present) splits each item's `BashText` on `\n` after trailing-newline trim. File mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` (matching `StreamReader.ReadLine()` semantics — same slice as Rev/Strings); glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`. Missing files emit a bash-style `expand: PATH: No such file or directory` error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and set `$global:LASTEXITCODE = 1`. **No colliding flags** — `-t` does not prefix-match any PowerShell common parameter, so `Arguments` catch-all + manual scan suffices; no `SwitchParameter` declarations needed. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). Replaces a 59-line psm1 function. Parity tests in `InvokeBashExpandCommandTests.cs` cover empty pipeline, default 8-column stops (whole + partial col advance), `-t 4` / `-t4` / `--tabs=4`, multi-tab column tracking, no-tabs passthrough, multi-line pipeline split, file mode (ASCII + CRLF + unicode), missing-file continuation, `--help`, alias resolution, and an injection probe per Directive 12 — 16 tests total |
| fold | `InvokeBashFoldCommand` | 4-follow-on | File + pipeline dual mode line wrapper (GNU coreutils `fold`). Wraps each input line at a fixed column width — default 80, override via `-w N` / `-wN` / `--width=N`. Hard-wrap by default; `-s` enables soft-wrap (when the wrap point falls mid-word, walk backward via `String.LastIndexOf(' ', chunkEnd-1, width)` to the previous space within the window and break just after it — matching the psm1 oracle's `LastIndexOf` slice byte-for-byte; if no space exists in the window, hard-break at width, GNU behavior). `-b` (bytes) is accepted for arg compatibility and behaves identically to the default char path for ASCII (the only path the psm1 oracle ever supported). Pipeline mode splits a multi-line `BashText` item on `\n` (after trailing-newline trim); file mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` (StreamReader.ReadLine semantics — trailing newline does not yield a spurious empty final line). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and set `$global:LASTEXITCODE = 1`. **One colliding flag** declared as an explicit value-bearing parameter: `-w` prefix-collides with `-WarningAction` / `-WarningVariable`, so it is declared as a `string? Width` parameter (alias `w`) that captures the standard `-w N` form (the empty-output-on-test failure mode that diagnoses this collision exactly). The joined `-wN` and `--width=N` forms continue to flow through `Arguments` and are recovered by the manual value-flag scan. `-s` has no common-parameter prefix overlap but is declared as a `SwitchParameter` for symmetry (no functional change). `-b` (bytes, accepted as a no-op for ASCII) stays in `Arguments`. Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. Replaces a 73-line psm1 function. Parity tests in `InvokeBashFoldCommandTests.cs` cover empty input, short / exact-width / longer-than-width lines (hard wrap), `-w` separated / `-wN` joined / `--width=` long forms, `-s` soft-wrap success, `-s` no-space-in-window hard fallback, file mode + CRLF normalization, pipeline mode + multi-line item split, missing-file continuation, `-b` no-op, `--help`, alias resolution, and a Directive-12 injection probe — 16 tests total |
| nl | `InvokeBashNlCommand` | 4-follow-on | File + pipeline dual mode line numberer. GNU coreutils `nl`: default numbers non-empty lines only (empty lines emit a bare empty line, unnumbered); `-ba` (also accepted as the split form `-b a` per the psm1 oracle) numbers all lines including empty. Output format `{0,6}\t{1}` — 6-column right-aligned number, tab, then the line — produced via `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). File mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` using `StreamReader.ReadLine()` semantics (no spurious trailing empty line). Pipeline mode splits multi-line `BashText` items on `\n` (after trailing-newline trim) — exact parity with the oracle's defensive-split path. Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via `FileSystemHelpers.WriteBashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and continue — the oracle did not set `$LASTEXITCODE=1` for nl (its `Read-BashFileLines` returned `$null` silently on miss), preserved here. **No colliding flags** — `-ba` and `-b` share no prefix with any PowerShell common parameter (`-Verbose -Debug -Confirm -WhatIf -Error* -Warning* -Information* -Out* -Progress* -PipelineVariable`); both stay in `Arguments` and are parsed by the manual scan. Replaces a 82-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashNlCommandTests.cs` cover three-line file (default numbering), mid-file empty lines (skipped numbering, bare emit), `-ba` numbers-all, pipeline mode (default + `-ba`), CRLF normalization, unicode, missing-file error continuation, `--help`, `nl` alias resolution, the split `-b a` oracle parity form, and a `$(throw 'INJECTED')` operand probe per Directive 12 — 13 tests total |
| shuf | `InvokeBashShufCommand` | 4-follow-on | Random-shuffle producer (GNU coreutils `shuf`). Three input modes matching the psm1 oracle byte-for-byte: **echo** (`-e a b c` — args as items), **range** (`-i LO-HI` — emit integers LO..HI rendered as strings), and **file / pipeline** (default — first positional operand is a file path; absent operands fall back to pipeline `BashText`). `-n N` (and `--head-count=N`) caps post-shuffle output. **One colliding flag** declared as an explicit `SwitchParameter`: `-e` prefix-collides with `-ErrorAction` / `-ErrorVariable`. The echo-mode item collection covers both PS-binder consumption of `-e` (case a — items appear bare in `Arguments`) and a literal `-e` carried through `ValueFromRemainingArguments` (case b — items collected between `-e` and the next `-`-prefixed token, oracle parity). `-n` / `-i` / `--head-count=` have no PS common-parameter prefix collision and stay in `Arguments`, parsed by a manual scan. Shuffle uses unseeded `System.Random` (Fisher-Yates), matching the oracle's `[System.Random]::new()` non-determinism; tests assert that output is a permutation of input (multiset equality), never an exact ordering. Missing file operands route through `FileSystemHelpers.ResolveOperandPaths` and a `try { File.ReadAllText }` swallow, matching the oracle's `Get-Content -ErrorAction SilentlyContinue` (no output, no error, no items added). Unknown `-`-prefixed flag emits the bash-style `shuf: invalid option '<flag>'` error via psm1 `Write-BashError` and sets `$global:LASTEXITCODE=1`. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`) — each item is a single line, so the observable shape matches the oracle's per-item `Emit-BashLine -Text`. Replaces an 88-line psm1 function. Parity tests in `InvokeBashShufCommandTests.cs` cover empty input, echo mode, `-n` cap, `-n 0` empty, `-n` > input emits all, range mode, range + `-n`, pipeline mode, file mode, unicode (combining marks + CJK + emoji), missing-file silent skip, `--help`, alias resolution, and a Directive-12 injection probe — 14 tests total |
| base64 | `InvokeBashBase64Command` | 4-follow-on | File + pipeline dual mode base64 encoder/decoder (GNU coreutils). Reimplements the psm1 oracle byte-for-byte: encode path uses `Convert.ToBase64String` over `File.ReadAllBytes` (file mode, first operand only — later operands ignored, matching the oracle's `$operands[0]` indexing) or over UTF-8 bytes of `\n`-joined pipeline `BashText` with a trailing-newline guarantee (pipeline mode); decode path uses `Convert.FromBase64String` after `.Trim()` on the file/pipeline text. Encoded output wraps at `-w N` columns (default 76; `-w 0` disables wrap) by joining wrap-sized substrings with `Environment.NewLine` and stripping trailing CR/LF — exact mirror of the oracle's `StringBuilder.AppendLine` + `TrimEnd("`r","`n")` slice (line ending stays platform-native on purpose; the worker normalizes on serialization). Decoded output strips a single trailing `\n` (the oracle's `$output -replace "`n$",''`). File decode reads via `File.ReadAllText` with CRLF normalization (the oracle's `Read-BashFileBytes` slice, reimplemented in C# in the cmdlet — no callback to psm1 on the hot path). **Two colliding flags** declared as explicit parameters with single-letter names so the binder routes the bare token by exact-name match (not by `[Alias]` — aliases lose to common-parameter prefix matches under the cmdlet binder): `-d` prefix-collides with `-Debug`, so the parameter is literally named `D` (declared `SwitchParameter`); `-w` prefix-collides with `-WarningAction` / `-WarningVariable`, so the parameter is literally named `W` (declared nullable `int` so unset falls back to the manual scan's default of 76). The long forms `--decode` and `--wrap=N` are recovered post-parse from `Arguments` by a manual scan. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe); file-read failures go through `FileSystemHelpers.WriteBashError`. Empty operand + empty pipeline returns no output, matching the oracle. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput` bare-string fast path; the wrapped encoded string is one object whose `BashText` carries the embedded line endings). Replaces an 80-line psm1 function. Parity tests in `InvokeBashBase64CommandTests.cs` cover empty input, ASCII encode round-trip, decode round-trip, unicode bytes (héllo + emoji) over file mode, `-w 0` no-wrap, `-w 10` narrow wrap with chunk-length verification, decode-trims-whitespace, missing-file error continuation, alias resolution, `--help`, and a `$(throw)` injection probe per Directive 12 — 13 tests total |
| tee | `InvokeBashTeeCommand` | 4-follow-on | Pipeline-and-file tee (GNU coreutils `tee`). Copies pipeline `BashText` to stdout (by re-emitting the original typed pipeline items at the end, preserving downstream consumers' object identity) and to every named file operand. Reproduces the psm1 oracle's three-step structure byte-for-byte: collect each pipeline item's BashText via `BashRuntime.GetBashText`, build a single file body using the oracle's trailing-newline heuristic (if the first item's BashText already ends in `\n` — the echo/printf shape — concatenate parts directly; otherwise join with `\n` and append one trailing `\n` — the ls/grep-from-pipeline shape), then write to each operand via `File.WriteAllText` (default) or `File.AppendAllText` (with `-a`). A missing parent directory yields a bash-style `tee: PATH: No such file or directory` error via `FileSystemHelpers.WriteBashError` and the operand is skipped, matching the oracle's `Test-Path -LiteralPath $parentDir` branch. Empty / null operands are filtered (oracle's `Where-Object`). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths` (the same SessionState slice cat / paste / mutators use), so literal paths fall through unchanged and a wildcard with no match passes through literally. **One colliding flag** declared as an explicit `SwitchParameter`: bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter under PowerShell parameter binding — same hazard the `ls` and `uname` migrations hit. `--` end-of-flags is recovered post-parse and continues to flow through `Arguments`. Replaces a 75-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashTeeCommandTests.cs` cover single-file overwrite, `-a` append, multi-file emission, pipeline pass-through to a downstream consumer (proves typed-object preservation), empty pipeline (empty file written), missing parent directory error continuation, trailing-newline heuristic (already-`\n`-terminated items concatenate, do not double-newline), unicode content round-trip, `--help`, `tee` alias resolution, `--` double-dash with a `-a-literal.txt` filename, and a Directive-12 injection probe with `$(throw 'INJECTED')` in the path — 12 tests total |
| paste | `InvokeBashPasteCommand` | 4-follow-on | Multi-file line merger (GNU coreutils `paste`). Reads each operand file fully (CRLF-normalized, `StreamReader.ReadLine()` semantics — a trailing newline does NOT yield a spurious empty final line; a file that is exactly `\n` yields one empty line), then in normal mode zips files row by row joined by the delimiter (default tab); shorter files pad with empty strings up to `maxLines`. `-s` (serial) emits one line per file with the file's own lines joined by the delimiter. **Oracle bit-for-bit parity note:** the psm1 oracle stored the entire `-d` value as a single string and joined fields with it; GNU paste cycles a multi-char delimiter per character in the row direction, but the oracle never did, and we preserve the oracle's behavior. **No colliding flags** — `-d` / `-s` have no PowerShell common-parameter prefix collision (`-Debug` requires `-d` to be ambiguous with `-Arguments`, but here `-d` is consumed by the manual scan from the `Arguments` catch-all). `-dDELIM` joined form (`-cmatch '^-d(.+)$'` in the oracle) and `--` end-of-flags are recovered post-parse. Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; a file-read failure emits a bash-style error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and the cmdlet returns early — matching the oracle's `if ($null -eq $fileLines) { return }`. Pipeline input is intentionally ignored (the oracle never consumed it). Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). Replaces an 88-line psm1 function. Parity tests in `InvokeBashPasteCommandTests.cs` cover no-operands, two-file equal length, two-file different length (shorter padded empty), single-char `-d` delim, multi-char `-d ":,"` delim oracle-literal, `-s` serial mode, `-s -d` combined, pipeline-input-ignored, missing-file, unicode content, `--help`, alias resolution, joined `-d,` form, empty file, and a `$(throw 'pwn');echo pwned` injection probe per Directive 12 — 15 tests total |
| touch / ln / sleep / which | `InvokeBashTouchCommand` / `InvokeBashLnCommand` / `InvokeBashSleepCommand` / `InvokeBashWhichCommand` | 4 | Four small file-metadata and utility commands. **Touch** supports `-d DATE` (`DateTime.TryParse` — bash-style invalid-date error on failure), `-a` (access-time only), `-m` (mod-time only), `-c` (no-create), `-v` (no-op for arg compat); creates an empty file via `File.Create(...).Dispose()` for missing operands and sets timestamps via `File.SetLastWriteTime` / `SetLastAccessTime`. **Ln** supports `-s` (symbolic — `File.CreateSymbolicLink` / `Directory.CreateSymbolicLink` depending on whether the target exists as a dir; defaults to file-symlink for missing targets), `-f` (force — remove existing link first), `-v` (verbose). The non-`-s` hard-link path still delegates to psm1 `New-Item -ItemType HardLink` via parameter-bound `InvokeCommand.InvokeScript` because System.IO has no hard-link API in stdlib. **Sleep** parses each operand as a decimal with optional `s/m/h/d` suffix, sums them, then sleeps in 100 ms chunks polling `PSCmdlet.Stopping` so Ctrl-C / pipeline-stop interrupts within one chunk. **Which** delegates command resolution to PowerShell `Get-Command` via parameter-bound `InvokeCommand.InvokeScript` (the only way to see aliases / functions / cmdlets registered in the runspace); supports `-a` (case-sensitive — `-A` is not the all-flag). **Colliding flags** declared as `SwitchParameter`s: touch `-a` / `-c` / `-v` (vs `Arguments` `a`-prefix / `-Confirm` / `-Verbose`); ln `-v`; sleep none; which none. Replaces 4 functions × ~50–90 psm1 lines each. Parity test coverage to be added — direct smoke test of typical invocations succeeds end-to-end |
| time | `InvokeBashTimeCommand` | 4-follow-on | Wall-clock timer that wraps an inner command, matching the bash `time` builtin / GNU `time`. Reproduces the psm1 oracle byte-for-byte: missing-args → "time: missing command" bash error and no output object; happy path → invoke the wrapped command via `InvokeCommand.InvokeScript` with a fixed parameterless body (`& $args[0] @rest 2>&1`) and the wrapped command name + args bound positionally through `$args` (Directive 12 — user-controlled tokens never concatenate into the script body, so a name containing `;` / `$()` / scriptblock chars / backticks stays a literal command-name lookup and fails the usual CommandNotFoundException path); collected `ErrorRecord` items route through `FileSystemHelpers.WriteBashError` (forcing `ExitCode = 1`) while non-error items contribute their `BashText` (via `BashRuntime.GetBashText`) to the joined-with-`\n` result text. Emits one typed `PsBash.TimeOutput` PSObject (`RealTime` / `Command` / `ExitCode` / `BashText`) and writes `"real    {seconds:N3}s"` to `[Console]::Error` (matching the oracle's `[Console]::Error.WriteLine`). **No colliding flags** — `time` accepts no flags besides `--help`. Replaces a 58-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashTimeCommandTests.cs` cover wrap-echo happy path (TimeOutput shape + ExitCode=0), no-args (no output), unknown-command (ExitCode=1, command name preserved), alias resolution, `--help`, and two Directive-12 injection probes (`$(throw "pwn")` command name, `echo;rm` semicolon-bearing name) — 7 tests total |
| file | `InvokeBashFileCommand` | 4-follow-on | Magic-byte file-type detector (GNU coreutils `file`). Reproduces the psm1 oracle byte-for-byte: reads the first 16 bytes of each operand and walks a fixed magic-byte table — PNG (`89 50 4E 47`), JPEG (`FF D8`), PDF (`25 50 44 46`), Zip (`50 4B 03 04`), ELF (`7F 45 4C 46`), GIF (`47 49 46 38`), RIFF (`52 49 46 46`) — emitting the matching type description + MIME type on a hit. On no magic-byte match, reads the full file via `File.ReadAllBytes` and applies the oracle's text predicate (`b < 0x07` OR (`0x0E <= b <= 0x1F` AND `b != 0x1B`) → non-text); all-text content reports as `ASCII text` (`text/plain`), otherwise `data` (`application/octet-stream`). UTF-8 multi-byte continuation bytes (≥ 0x80) pass the predicate, so non-ASCII text registers as `ASCII text` — preserved as-is from the oracle. Flag surface: `-b` / `--brief` (omit `PATH: ` prefix), `-i` / `--mime` (emit MIME type), `-L` / `--dereference` (accepted but no-op — `File.OpenRead` follows symlinks by default, same as the oracle). **One colliding flag** declared as an explicit `SwitchParameter`: `-i` prefix-collides with `-InformationAction` / `-InformationVariable` per the playbook collision table. `-b` and `-L` have no common-parameter prefix collision and stay in `Arguments`; both literal `-i` and `--mime` long form are also recovered from `Arguments` for parity with the oracle's `-ceq '-i' -or -eq '--mime'` switch. Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing operands emit a bash-style `file: cannot open 'PATH' (No such file or directory)` error via `FileSystemHelpers.WriteBashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and continue. Output is a typed `PsBash.TextOutput` PSObject with `BashText` + `FileName` + `FileType` + `MimeType` side properties, matching the oracle's `[PSCustomObject]` shape (constructed manually rather than via `BashRuntime.NewBashObject` because that helper does not carry per-output side properties). Replaces a 96-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashFileCommandTests.cs` cover plain ASCII text, binary control-byte payload → `data`, empty file → `ASCII text` (oracle parity — an empty byte run passes the text predicate), PNG magic-byte detection, `-b` brief, `-i` MIME, combined `-b -i`, multi-operand emission, missing-file error continuation, typed-output side-property roundtrip, `--help`, alias resolution, and a Directive-12 injection probe with `$(throw'pwn');run.txt` — 13 tests total |
| seq | `InvokeBashSeqCommand` | 4-follow-on | GNU coreutils `seq` integer / decimal sequence generator. Reproduces the psm1 oracle's three operand forms (`seq LAST` / `seq FIRST LAST` / `seq FIRST INCR LAST`) byte-for-byte, including the integer-detection short circuit (`first == floor(first) && increment == floor(increment) && last == floor(last)`), max-decimal-places across operands for the `F{N}` invariant-culture format, the `±1e-9` epsilon comparison in the loop condition, and the `-w` zero-pad width (integer mode only — `max(|first|,|last|).ToString().Length`). Flag surface: `-s SEP` / `--separator SEP` / `--separator=SEP` (joined output — single bare string via `BashRuntime.NewBashObject` fast path), `-w` / `--equal-width` (zero-pad). **One colliding flag** declared as an explicit `SwitchParameter`: `-w` prefix-collides with `-WarningAction` / `-WarningVariable` — the standard remedy from the playbook collision table. `-s` has no `-S*` common-parameter prefix collision and stays in `Arguments`, parsed by the manual value-flag scan (separated, long-form, and `--separator=` forms all decoded). Default branch emits per-value typed `PsBash.SeqOutput` PSObjects (`Value` / `Index` / `BashText`). Defensive guard: a zero `increment` exits the loop early (the psm1 oracle would infinite-loop in that case; same caller-visible "no progress" outcome). `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 116-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashSeqCommandTests.cs` cover one-arg / two-arg / three-arg forms, negative step descent, `-s ','` separator, `--separator=-` long form, `-w` zero-pad (1..10 → `01`..`10`), decimal step (`1 0.5 2` → `1.0 1.5 2.0`), no-args default (emit `1`), typed-output PSTypeName check, typed-output `Value` / `Index`, `--help`, alias resolution, and a `$(throw)` separator injection probe per Directive 12 — 14 tests total |
| du | `InvokeBashDuCommand` | 4-follow-on | Disk-usage estimator (GNU coreutils `du`). Reproduces the psm1 oracle byte-for-byte: for each operand, resolve via `SessionState.Path.GetUnresolvedProviderPathFromPSPath` and probe with `Directory.Exists` / `File.Exists` (matching the oracle's `Get-BashItem` slice — on miss, a bash-style `du: cannot access 'PATH': No such file or directory` error via `FileSystemHelpers.WriteBashError` and continue). For a file operand emit one `PsBash.DuEntry` PSObject (sized via `Ceiling(bytes/1024)` 1024-byte blocks); for a directory, enumerate the root + all descendants via `DirectoryInfo.EnumerateDirectories("*", SearchOption.AllDirectories)`, compute per-directory file-size sum, then bottom-up accumulate via deepest-first `OrderByDescending(d => d.FullName.Length)` so each directory's reported size includes all descendants — exact mirror of the oracle's `Get-ChildItem -Force -Recurse -Directory` + `Sort-Object { $_.FullName.Length } -Descending` chain. `-s` (summary) emits only the root entry; `-a` (include files) appends one `DuEntry` per descendant file via `EnumerateFiles("*", SearchOption.AllDirectories)`; `-c` (grand total) emits a final entry with `Path = "total"` and `IsTotal = true`; `-d N` (depth limit) drops entries whose path-segment-delta from the root exceeds N. Human-readable sizes via the oracle's `Format-BashSize` ladder reimplemented in C# (`< 1024` → bare byte count; otherwise scale by 1024 through `K M G T P`, emit `"{N}{unit}"` when scaled >= 10 else `"{N.N}{unit}"`, both via `Math.Ceiling` to match the oracle exactly). Output rows are sorted by `Path` ordinal at the end of each operand's pass (oracle: `Sort-Object { $_.Path }`). **Three colliding flags** declared as explicit parameters per the playbook collision table: `-d N` (depth) prefix-collides with `-Debug` — declared as nullable `int? D`; `-a` (all-files) prefix-matches the cmdlet's own `-Arguments` parameter — declared as `SwitchParameter A`; `-c` (grand total) prefix-collides with `-Confirm` — declared as `SwitchParameter C`. `-s` / `-h` have no PowerShell common-parameter prefix collision and stay in `Arguments`; the joined `-dN` form (oracle: `^-d(\d+)$`) is also recovered from `Arguments` by the manual scan. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 209-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashDuCommandTests.cs` cover single-file operand, single-directory recursion, nested-tree accumulation, `-s` summary, `-h` human-readable formatting (`2.0K`), `-a` file emission, `-c` grand total, `-d 1` depth limit, `-d0` joined form, multi-operand, no-operand `.` default, alias resolution, `--help`, and a Directive-12 injection probe with `$(throw 'pwn');missing` as the operand confirming the literal-path-no-such-file branch (no exception, no output object) — 14 tests total |
| expr | `InvokeBashExprCommand` | 4-follow-on | Arithmetic / string evaluator (GNU coreutils `expr`). Reimplements the psm1 oracle's dispatch order byte-for-byte: keyword forms `length STR` / `substr STR POS LEN` / `index STR CHARS` / `match STR REGEX` first; then infix `OP1 OP OP2` where both sides matching `^-?\d+$` route through 64-bit integer math (`+ - * / %` plus six comparison ops, with `/` using truncate-toward-zero via `Math.Truncate((double)l/r)` to match the oracle), else string compare with `=` / `!=` case-sensitive (oracle's `-ceq` / `-cne`) and `<` / `<=` / `>=` / `>` case-insensitive ordinal (oracle's PowerShell string `-lt` / `-le` / `-ge` / `-gt`); else single-operand echo. `match` translates POSIX BRE `\(...\)` to .NET `(...)` exactly like the oracle's two `-replace` passes and anchors at start. Error paths (missing operand, division by zero, unknown operator, non-integer infix arg) route through psm1 `Write-BashError` with `-ExitCode 2` (GNU `expr`'s "error in expression" code) via a parameter-bound `param($m,$c) Write-BashError -Message $m -ExitCode $c` script body — no `ScriptBlock` construction, AOT-safe. Output is a typed `PsBash.ExprOutput` PSObject with `Value` (boxed `long` when result matches `^-?\d+$`, else `string`) and `BashText` properties — exact oracle shape. **No PowerShell common-parameter prefix collisions** — expr operands are digits, operators, and arbitrary strings; the cmdlet declares only the catch-all `Arguments` parameter (`ValueFromRemainingArguments=true`). Replaces a 118-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashExprCommandTests.cs` cover arithmetic (+ - * / % with negative operand), six comparisons (numeric + string), all four string ops (`length` ASCII + unicode, `substr` with length clamp, `index` hit + miss, `match` with anchored regex + no-match), single-operand echo, three error paths (missing operand, divide-by-zero, unknown operator — each verifying `$global:LASTEXITCODE = 2` and no output object), `--help`, alias resolution, typed-PSObject shape (numeric → `long`, non-numeric → `string`), and a Directive-12 injection probe (`$(throw 'pwn');rm -rf /` as the single operand emits a literal string with no nested evaluation) — 30 tests total |
| comm | `InvokeBashCommCommand` | 4-follow-on | Two-pointer walk over two sorted files emitting a 3-column tab-prefixed output: column 1 = only in file1, column 2 = only in file2, column 3 = in both. Digit flags `-1` / `-2` / `-3` (and bundles `-12` / `-13` / `-23` / `-123`) suppress the matching column and remove its leading tab from later columns. Comparison uses `string.CompareOrdinal`, mirroring the psm1 oracle's `[string]::Compare(..., Ordinal)` slice byte-for-byte. File reads route through inline CRLF-normalized `File.ReadAllText` + `\n` split (the same `StreamReader.ReadLine()` semantics as Rev/Strings — trailing newline does not yield a spurious empty final line, but a file of exactly `"\n"` does yield one empty line). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style `comm: PATH: No such file or directory` error via `FileSystemHelpers.WriteBashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and the cmdlet returns with no further output (matching the oracle's early-return-on-null contract). Missing-operand (< 2 operands) emits `comm: missing operand` and returns. **No colliding flags** — `-1` / `-2` / `-3` are digit-prefixed tokens; no PowerShell common parameter starts with a digit, so they stay in `Arguments` and are parsed by the manual `^-[123]+$` digit-bundle predicate. Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. Replaces a 102-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashCommCommandTests.cs` cover disjoint files, overlapping files (all three columns), `-1` / `-2` / `-3` individual suppression, `-12` and `-123` bundles, identical files, both-empty / one-empty pair, CRLF normalization, unicode (`é` ordinal sort), missing operand, missing file, `--help`, alias resolution, and a Directive-12 injection probe — 16 tests total |
| join | `InvokeBashJoinCommand` | 4-follow-on | Relational two-file join on a common key column (GNU coreutils `join`). Reimplements the psm1 oracle byte-for-byte: read both files into line arrays, build a `Dictionary<string, List<string[]>>` keyed by the file-2 join field (Ordinal comparer, preserving insertion order for duplicate keys), iterate file-1 lines in order, and for each matching file-2 row emit `key + delim + file1-rest + delim + file2-rest`. File reads inline `Read-BashFileLines`'s slice (`File.ReadAllText` + CRLF normalization + `\n` split with StreamReader.ReadLine trailing-newline semantics). Path resolution uses `SessionState.Path.GetUnresolvedProviderPathFromPSPath` — no glob expansion, matching the oracle exactly. Flag surface: `-t SEP` (delimiter, default single space), joined form `-tC` (single-char delimiter via `arg.Length==3` exact match), `-1 N` (key column for file 1, 1-based, default 1), `-2 N` (key column for file 2, default 1), `--` end-of-flags, `--help`. Missing files emit a bash-style `join: PATH: No such file or directory` error via `FileSystemHelpers.WriteBashError` and return; `< 2` operands emit `join: missing operand` and return. **No colliding flags** — `-t`, `-1`, `-2` have no prefix overlap with any PowerShell common parameter, so all stay in `Arguments` and are parsed by a manual value-flag scan. Output: bare `PsBash.TextOutput` strings via `BashRuntime.NewBashObject`. Replaces a 116-line psm1 function. Parity tests in `InvokeBashJoinCommandTests.cs` cover default key-column join, `-1 2` key-in-column-2-of-file-1, `-t ','` custom delimiter, no-match no-output, missing-file error, missing-operand, empty files, CRLF normalization, `--help`, alias resolution, and a `$(throw)` injection probe per Directive 12 — 11 tests total |
| cut | `InvokeBashCutCommand` | 4-follow-on | File + pipeline dual mode column / byte extractor (GNU coreutils `cut`). Reimplements the psm1 oracle byte-for-byte: per-line `-c LIST` selects character positions (1-based, out-of-range silently dropped) and `-f LIST` splits the line on `-d DELIM` (default tab) and selects the listed fields joined back by the same delimiter. List parsing splits on `,` and matches each part against `^(\d+)-(\d+)$` (inclusive range) or `[int]$part` (single index) — matching the oracle's `$parseSpec` slice exactly; open ranges (`N-` / `-M`) are not supported (the oracle throws, we throw `FormatException` and route via `Write-BashError`). Pipeline mode (no operands + pipeline input present) splits each item's `BashText` on `\n` after trailing-newline trim. File mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` using `StreamReader.ReadLine()` semantics (no spurious trailing empty line — same Rev/Strings pattern). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style `cut: PATH: No such file or directory` error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and continue. **Two colliding flags** declared as explicit value-bearing parameters with single-letter names: `-d` prefix-collides with `-Debug` (declared as `string? D`); `-c` prefix-collides with `-Confirm` (declared as `string? C`). `-f` has no PowerShell common-parameter prefix collision and is recovered from `Arguments` by a manual scan; the joined short forms `-dC` / `-fLIST` / `-cLIST` (oracle's `^-d(.)$` / `^-f(.+)$` / `^-c(.+)$` patterns) are also recovered from `Arguments` post-parse. `--` ends flag parsing. Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. Replaces a 154-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashCutCommandTests.cs` cover empty pipeline, single-field tab-delim, field-range, comma-list, custom-delim, char-range, char comma-list, empty-line, missing-delim-line edge case (oracle parity: `-f 1` returns whole line, `-f 2` returns empty), file mode, CRLF normalization, unicode char positions, missing-file continuation, `--help`, alias resolution, and a Directive-12 injection probe — 16 tests total |
| shopt | `InvokeBashShoptCommand` | 4-follow-on | Bash `shopt` builtin — list / set / unset / query shell options. Owns a static `Dictionary<string, bool>` of the 14 well-known options (`extglob`, `globstar`, `dotglob`, `nullglob`, `nocaseglob`, `expand_aliases`, `cmdhist`, `histappend`, `checkwinsize`, `progcomp`, `login_shell`, `interactive_comments`, `sourcepath`, `hostcomplete`) with the same defaults as the psm1 oracle's `$script:BashShoptOptions`. The state was exclusive to `Invoke-BashShopt` in psm1 (no other function read or wrote `BashShoptOptions`), so consolidating ownership into the cmdlet preserves single-source semantics. Flag surface: `-s` (set), `-u` (unset), `-p` (print all as `shopt -s NAME` lines sorted ordinal), `-q` (quiet — accepted for arg-compat, no observable effect per oracle); bare option name queries `NAME on|off`. **One colliding flag** declared as a literal `SwitchParameter P`: `-p` prefix-collides with `-PipelineVariable` / `-ProgressAction` per the playbook collision table; literal parameter name `P` beats common-parameter prefix-matching via exact-name match. `-s` / `-u` / `-q` have no PowerShell common-parameter prefix overlap and stay in `Arguments`. Unknown option name routes through `FileSystemHelpers.WriteBashError` with the oracle's exact `bash: shopt: NAME: invalid shell option name` text. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 60-line psm1 block (`$script:BashShoptOptions` table + the function). Parity tests in `PsBash.Cmdlets.Tests/InvokeBashShoptCommandTests.cs` cover no-args (no output), `-p` listing (14 lines, ordinal-sorted), query known option (`on` and `off` branches), `-s` toggle, `-u` toggle, unknown option (no success output), alias resolution, `--help`, the `-p` exact-name binder regression guard, and a `$(throw)` injection probe per Directive 12 — 11 tests total |
| tr | `InvokeBashTrCommand` | 4-follow-on | Pipeline-only character translator / deleter / squeezer (GNU coreutils `tr`). Reimplements the psm1 oracle's per-line transform engine byte-for-byte in C#: SET expansion translates POSIX classes (`[:alpha:]` / `[:digit:]` / `[:alnum:]` / `[:upper:]` / `[:lower:]` / `[:space:]` / `[:punct:]`) into the oracle's exact char sets, then ranges (`a-z`) expand to the inclusive character sequence; both SETs run through `BashRuntime.ExpandEscapeSequences` first, so `\n` / `\t` / `\r` / `\a` / `\b` / `\f` / `\v` / `\\` parse identically. Three transform modes match the oracle dispatch order: **delete** (`-d` / `--delete` — drop chars in SET1; with `-c` complement, keep chars in SET1), **squeeze-only** (`-s` with one SET — collapse runs of in-set chars), and **translate** (two SETs — map SET1 chars to SET2 by index, last-char extension for the short-SET2 tail, complement extension to 256-char ASCII space, optional `-t` / `--truncate-set1`, optional `-s` post-squeeze on SET2). Pipeline collection joins all input items with `\n` and strips one trailing `\n` before splitting on `\n` for the per-line emit loop — same shape the oracle produced via the `StringBuilder` + `EndsWith` + `Split` slice. Each transformed line is emitted via `BashRuntime.NewBashObject` (default `PsBash.TextOutput` bare-string fast path). Empty pipeline → no output (oracle parity). **Two colliding flags** declared as explicit `SwitchParameter`s: `-d` prefix-collides with `-Debug` (declared as `D`); `-c` prefix-collides with `-Confirm` (declared as `C`). `-s` and `-t` have no PowerShell common-parameter prefix collision and stay in `Arguments`; bundled forms (`-ds`, `-cs`, `-dt`, etc.) are recovered by the manual post-parse scan (matching the oracle's per-char dispatch over `arg.Substring(1)`). Long forms `--complement` / `--truncate-set1` / `--delete` / `--squeeze-repeats` are recovered the same way. **Pipeline-only**: the psm1 oracle never accepted file operands — non-flag positional tokens are always SET1 / SET2 — preserved here. Replaces a 230-line psm1 function. Parity tests in `InvokeBashTrCommandTests.cs` cover empty pipeline, uppercase / lowercase range translation, `-d` delete spaces, `-s` squeeze runs, `[:digit:]` class deletion, `[:lower:]`→`[:upper:]` class translation, escape-sequence expansion in SET (`\n` literal in single-quoted PowerShell stays literal), SET2-extension via last-char repeat, `--help`, `tr` alias resolution, a Directive-12 injection probe with `$(throw 'INJECTED');pwned` as SET1 to `-d`, and multi-item pipeline per-line translation — 13 tests total |
| type | `InvokeBashTypeCommand` | 4-follow-on | Bash `type` builtin — classify a command name as alias / function / builtin / file, or in `-p` mode emit a bash-style `declare` line for a variable's current value. Reimplements the psm1 oracle's dispatch order byte-for-byte. Two colliding flags declared as explicit `SwitchParameter`s: `-a` prefix-matches the cmdlet's own `-Arguments` catch-all (declared as `A`); `-p` prefix-matches `-PipelineVariable` / `-ProgressAction` (declared as `P`). `-t` stays in `Arguments`. Output: typed `PsBash.TypeOutput` PSObjects with `Command` / `Kind` / `BashText` properties. Not-found cases set `$LASTEXITCODE = 1` via `FileSystemHelpers.SetLastExitCode`. `--help` / `Get-Alias` / `Get-Command` / `ConvertTo-Json -Compress` all route through parameter-bound `InvokeCommand.InvokeScript` (AOT-safe — no `ScriptBlock` construction). Replaces a 118-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashTypeCommandTests.cs` — 13 tests total |
| unexpand | `InvokeBashUnexpandCommand` | 4-follow-on | File + pipeline dual mode space-to-tab converter (GNU coreutils `unexpand`; the inverse of `expand`). Reproduces the psm1 oracle's two modes byte-for-byte: **default (leading-only)** counts L leading spaces and emits `floor(L/tabWidth)` tabs + `L%tabWidth` remainder spaces + the rest of the line unchanged (partial leading runs that don't reach a tabstop stay as spaces); **`-a` / `--all`** walks every character — on each space, increments a column counter and a space-run counter, and when `col % tabWidth == 0` AND `spaceRun >= 2`, emits one tab and resets the run; partial runs at end of line stay as literal spaces. Flag surface: `-t N` / `-tN` / `--tabs=N` (tab width, default 8), `-a` / `--all`, `--first-only` (default mode — preserved for arg-compat). **One colliding flag** declared as an explicit `SwitchParameter`: bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter under PowerShell parameter binding — same hazard `uname` handled. `-t` has no PowerShell common-parameter prefix collision and is scanned out of `Arguments` by the manual value-flag loop (separated, joined, and `--tabs=` forms all decoded). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via `FileSystemHelpers.WriteBashError` and continue. Files read via `File.ReadAllText` with CRLF normalization; trailing newline does not produce a spurious empty final line (rev/strings pattern). Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces an 87-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashUnexpandCommandTests.cs` cover 8-leading-spaces → 1 tab, 16 → 2 tabs, partial-run preservation, 10 → tab+2 remainder, `-t 4` / `-t4` / `--tabs=4`, `-a` interior-run-at-boundary, `-a` single-space preservation, default mode interior-spaces preserved, no-spaces unchanged, multi-line file mode, multi-item pipeline, missing-file continuation, `--help`, alias resolution, unicode tail preservation, and a `$(throw)` injection probe per Directive 12 — 18 tests total |
| date | `InvokeBashDateCommand` | 4-follow-on | GNU coreutils `date`. Reproduces the psm1 oracle byte-for-byte: default emits a `"Thu Jan  2 15:04:05 MST 2006"`-style local datetime built from a `DateTimeOffset.Now`; `-d STRING` / `--date STRING` / `--date=STRING` parses via `DateTimeOffset.Parse(value, InvariantCulture)` with a try/catch routing failures to `bash-style date: invalid date 'STRING'` via psm1 `Write-BashError`; `-u` / `--utc` / `--universal` calls `ToUniversalTime()` and sets `TimeZone="UTC"`; `-r FILE` / `--reference FILE` resolves via `SessionState.Path.GetUnresolvedProviderPathFromPSPath`, probes `File.Exists` / `Directory.Exists`, and uses `LastWriteTime` (missing-path error matches the oracle exactly). `+FORMAT` runs through a private `ConvertDateFormat` per-char switch reproducing the psm1 `Convert-DateFormat` helper byte-for-byte for `%Y %y %m %d %H %M %S %s %F %T %w %A %B %Z %a %b %e %j %p %n %t %%` and preserving unknown `%X` as literal. Output is a typed `PsBash.DateOutput` PSObject with `Year` / `Month` / `Day` / `Hour` / `Minute` / `Second` / `Epoch` / `DayOfWeek` / `TimeZone` / `DateTime` / `BashText` properties — exact oracle shape. **One colliding flag** declared as an explicit value-bearing parameter with a single-letter name: `-d` prefix-collides with `-Debug`, declared literally as `D` (same pattern `cut` / `base64` used). The long forms `--date` / `--date=` continue to flow through `Arguments` and are recovered by the manual scan. `-u` / `-r` / `+FORMAT` have no PowerShell common-parameter prefix collision and stay in `Arguments`. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 174-line psm1 block (function + `Convert-DateFormat` helper). Parity tests in `PsBash.Cmdlets.Tests/InvokeBashDateCommandTests.cs` cover typed-output PSTypeName check, default `BashText` shape (six space-sep fields), `-d` parsed-date Y/M/D, `+%Y-%m-%d` exact format, `+%s` epoch for `1970-01-01T00:00:00Z`, `-u` UTC TimeZone, `+%%` literal percent, unknown `%Q` spec preserved, invalid `-d` no-output-with-error, missing `-r` no-output-with-error, `--help`, `date` alias resolution, and a Directive-12 injection probe with `+$(throw 'pwn')%Y` confirming the literal `$(throw 'pwn')` reaches the format engine unevaluated — 13 tests total |
| tree | `InvokeBashTreeCommand` | 4-follow-on | Recursive directory-tree printer with box-drawing prefix (`├── │   └──`). Reproduces the psm1 oracle byte-for-byte: builds a typed `PsBash.TreeEntry` PSObject for the root, walks the tree depth-first via `DirectoryInfo.GetFileSystemInfos()` plus a manual sort (alphabetic by default, dirs-before-files under `--dirsfirst`), filters dotfiles unless `-a`, filters by glob via `WildcardPattern.IsMatch` under `-I PATTERN`, drops files under `-d`, honors `-L N` depth (per-level guard), and emits a final summary `PsBash.TreeEntry` with `BashText = "{N} directories, {M} files"` (or `"{N} directories"` under `-d`) with singular `directory` / `file` forms when counts are exactly 1. **Three colliding flags** declared as explicit parameters: `-d` prefix-collides with `-Debug` → `SwitchParameter D`; bare `-a` prefix-matches the cmdlet's own `-Arguments` → `SwitchParameter A`; `-I PATTERN` prefix-collides with `-InformationAction` / `-InformationVariable` → `string? I`. `-L N` / `-LN` and `--dirsfirst` have no PowerShell common-parameter prefix collision and stay in `Arguments`, parsed by the manual scan; the same scan also decodes bundled short forms (`-ad`, `-da`) recovered post-parse. Root path resolved via `SessionState.Path.GetUnresolvedProviderPathFromPSPath`; a missing target emits a bash-style `tree: cannot access 'PATH': No such file or directory` error via `FileSystemHelpers.WriteBashError` and the cmdlet returns with no further output. **Directive 12:** the `-I PATTERN` value is fed only to `WildcardPattern.IsMatch` — a pattern containing `$(throw 'pwn')` arrives as a literal glob string and is never re-parsed as PowerShell. Replaces a 175-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashTreeCommandTests.cs` cover empty dir, one-level (alpha order), nested (indented prefix), `-L 1` depth limit, `-d` dirs-only summary form, `-a` show-hidden, `-I '*.tmp'` exclude, `--dirsfirst` sort, summary singular/plural pluralization, alias resolution, `--help`, typed-output PSTypeName, and a Directive-12 injection probe on the `-I` pattern — 13 tests total |
| mapfile / readarray | `InvokeBashMapfileCommand` | 4-follow-on | Bash `mapfile` / `readarray` builtin — read pipeline lines into an array variable in the caller's scope. Pipeline-only (the psm1 oracle never accepted file operands; the non-flag operand is the destination variable name). Empty lines are dropped (oracle parity: `if ($line -ne '') { $lines.Add }`). Default destination name is `MAPFILE`. Flag surface: `-n N` cap, `-O ORIGIN` start index with empty-string prefix (`@(1..$origin | ForEach-Object { '' })` slice), `-s N` skip first N (cmdlet addition; oracle did not implement), `-t` strip trailing `\r`/`\n`, `-d DELIM` consumed but ignored (oracle parity — always splits on `\n`). **Two colliding flags** declared as explicit parameters per the playbook collision table: `-O` prefix-matches `-OutVariable` / `-OutBuffer` (declared as `int? O`); `-d` prefix-matches `-Debug` (declared as `string? D`). `-n` / `-s` / `-t` have no PowerShell common-parameter prefix overlap and stay in `Arguments`; the manual scan also recovers joined `-nN` / `-OORIGIN` / `-sN` / `-dDELIM` forms. Writes back via `SessionState.PSVariable.Set` (the runspace-scope equivalent of the oracle's `Set-Variable -Name $varName -Value $result`). No stdout (variable side-effect only). The destination-variable token is checked for PowerShell scriptblock metacharacters (`$ ( ; { ` " `) per Directive 12 — a hit emits a bash-style `mapfile: '<NAME>': not a valid identifier` error via `FileSystemHelpers.WriteBashError` and skips the assignment, defeating injection through the variable-name path. `--help` delegates to psm1 `Show-BashHelp`. Aliases `mapfile` and `readarray` are added in psm1 and resolve to this cmdlet. Replaces an 86-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashMapfileCommandTests.cs` cover basic stdin, `-n 2`, `-O 5` origin (verify indices 0..4 empty then 5..N populated), `-t` strip, `-d` accepted-but-ignored, `-s 1` skip, custom array name, both `mapfile` and `readarray` aliases, `--help`, empty pipeline, and a Directive-12 injection probe with `$(throw 'PWNED')` as the array variable name — 12 tests total |
| gzip / gunzip / zcat | `InvokeBashGzipCommand` | 4-follow-on | File-mode (de)compressor backed by `System.IO.Compression.GZipStream` (GNU coreutils `gzip`). Reproduces the psm1 oracle byte-for-byte across all six branches: default compress (write `{PATH}.gz`, remove source unless `-k`), `-d` decompress (strip `.gz` suffix, remove source unless `-k`), `-c` to stdout (UTF-8 string for decompress, base64 string for compress — the oracle picked base64 so raw bytes survive PowerShell's string pipeline), `-v` ratio line per file, `-l` listing (emits typed `PsBash.GzipListOutput` PSObjects with `CompressedSize` / `UncompressedSize` / `Ratio` / `FileName` side properties), and `-1`..`-9` compression level (1 → `CompressionLevel.Fastest`, 9 → `CompressionLevel.SmallestSize`, else `Optimal` — exact .NET ladder the oracle used). Alias dispatch matches the oracle's `$MyInvocation.InvocationName -eq 'gunzip'` / `'zcat'` slice via `PSCmdlet.MyInvocation.InvocationName` (gunzip boosts `-d`, zcat boosts `-dc`). Operand resolution routes through `FileSystemHelpers.ResolveOperandPaths` (same SessionState slice cat / checksum / mutators use); a missing path emits a bash-style `gzip: PATH: No such file or directory` via `FileSystemHelpers.WriteBashError` and the cmdlet continues with subsequent operands. `< 1` operands emits `gzip: missing file operand` and returns. **Three colliding flags** declared as explicit `SwitchParameter`s with literal single-letter names so the binder routes the bare token by exact parameter-name match (beats common-parameter prefix match): `-d` prefix-collides with `-Debug` (declared as `D`); `-c` prefix-collides with `-Confirm` (declared as `C`); `-v` prefix-collides with `-Verbose` (declared as `V`). `-f` (force, accepted but no-op past the implicit overwrite of `File.WriteAllBytes`) is also declared as `SwitchParameter F` for symmetry. `-k` / `-l` / `-1`..`-9` have no PowerShell common-parameter prefix collision and stay in `Arguments`, parsed by the manual post-parse scan; bundled short forms (`-dk`, `-cv`, `-9v`, etc.) decode per the oracle's `foreach ($ch in $arg.Substring(1).ToCharArray())` loop. The `-c` compress branch emits a base64 string (one `PsBash.TextOutput`) so `gzip -c FILE \| base64 -d` round-trips identically to the oracle. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a ~164-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashGzipCommandTests.cs` cover compress + decompress round-trip (source removal + restoration), `-c` base64 emission with source preservation, `-k` keep, `-dc` decompress to stdout, `-l` typed listing output with side properties, `-9` and `-1` level paths (GZipStream round-trip), `gunzip` alias resolution (default `-d`), `zcat` alias resolution (default `-dc`), `--help`, missing-file error continuation, and a Directive-12 injection probe with `$(throw 'pwn').gz` as the operand confirming the literal-path-no-such-file branch (no exception, no output) — 12 tests total |
| ps | `InvokeBashPsCommand` | 4-follow-on | Process lister (GNU coreutils / BSD `ps`). Reproduces the psm1 oracle's flag surface byte-for-byte: `aux` / `-aux` BSD-all, `-e` / `-A` all-processes, `-f` full-format, `-u USER` user filter, `-p PID` single-PID filter, `--sort COL` / `--sort=-COL` descending prefix, `-o COL,COL,...` custom output. Cross-platform enumeration: Linux walks `/proc/[pid]` directly (the oracle's `Get-LinuxProcEntry` slice — stat fields, status Uid, cmdline, tty major/minor decoding, /proc/uptime boot-time math, /proc/meminfo total memory) reimplemented in C# inside the cmdlet; Windows / macOS use `System.Diagnostics.Process.GetProcesses()` plus platform-specific batch metadata lookup — Windows pulls `Win32_Process` CIM rows (CommandLine / Owner / ParentProcessId) through a single parameter-bound `InvokeCommand.InvokeScript` call (AOT-safe; no `ScriptBlock` construction), macOS shells `/bin/ps -axo pid=,user=,ppid=,tty=`. Output: typed `PsBash.PsEntry` PSObjects with the oracle's full property set (PID, PPID, User, CPU, Memory, MemoryMB, VSZ, RSS, TTY, Stat, Start, Time, Command, CommandLine, ProcessName, WorkingSet, BashText). BashText carries the format-mode rendered line — `Format-PsAuxLine` aux format under `-f` / `aux`, `Format-PsCustomLine` custom-column format under `-o`, default `{0,7} {1,-7} {2,8} {3}` PID/TTY/TIME/COMMAND otherwise — all reimplemented in C# with culture-invariant `string.Format`. **Four colliding flags** declared as explicit parameters per the playbook collision table: `-e` prefix-collides with `-ErrorAction` / `-ErrorVariable` → `SwitchParameter E`; `-A` case-folds to `-a` under the case-insensitive binder and prefix-matches the cmdlet's own `-Arguments` → `SwitchParameter A`; `-p` prefix-collides with `-PipelineVariable` / `-ProgressAction` → `string? P` (value-bearing PID); `-o` prefix-collides with `-OutVariable` / `-OutBuffer` → `string? O` (value-bearing column list). `-f` / `-u` / `--sort` / `aux` have no PowerShell common-parameter prefix collision and stay in `Arguments`. **Directive 12:** the `-p` value parses via `int.TryParse` with `InvariantCulture` — a non-integer (literal `$(throw 'pwn')`) silently falls through to no filter, no exception, no eval. The `-o` value is split on comma and each token routed through the column switch's default branch (emits `"?"` for unknown tokens) — no `ScriptBlock` construction, no `InvokeExpression`. Helpers `Get-LinuxProcEntry` / `Get-DotNetProcEntry` / `Format-PsAuxLine` / `Format-PsCustomLine` remain defined in psm1 for any out-of-tree caller but are no longer reached by the cmdlet. Replaces a 215-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashPsCommandTests.cs` — 12 tests total |

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
| ps | Invoke-BashPs | `-e`/`-A`, `-f`, `-u`, `-p`, `--sort`, `-o` | Binary cmdlet (`-e` / `-A` declared as `SwitchParameter`s `E` / `A`; `-p` / `-o` declared as nullable `string`s `P` / `O`; `-f` / `-u` / `--sort` / `aux` stay in `Arguments`) | No | No |
| sed | Invoke-BashSed | `-n`, `-i`, `-E`, `-e` | Manual loop | Yes | Yes |
| awk | Invoke-BashAwk | `-F`, `-v` | Manual loop | Yes | Yes |
| cut | Invoke-BashCut | `-d`, `-f`, `-c` | Binary cmdlet (`-d` and `-c` are declared value-bearing parameters `D` / `C`; `-f` and joined `-dC`/`-fLIST`/`-cLIST` stay in `Arguments`) | Yes | Yes |
| tr | Invoke-BashTr | `-d`, `-s` | Manual loop | Yes | No |
| cut | Invoke-BashCut | `-d`, `-f`, `-c` | Manual loop | Yes | Yes |
| tr | Invoke-BashTr | `-d`, `-s` | Binary cmdlet (`-d`/`-c` declared as SwitchParameters; `-s`/`-t` and bundled forms stay in `Arguments`) | Yes | No |
| uniq | Invoke-BashUniq | `-c`, `-d` | Manual loop | Yes | Yes |
| rev | Invoke-BashRev | (none) | Positional | Yes | Yes |
| nl | Invoke-BashNl | `-ba` | Manual loop | Yes | Yes |
| diff | Invoke-BashDiff | `-u` | Manual loop | No | Yes |
| comm | Invoke-BashComm | `-1`, `-2`, `-3` | Binary cmdlet (digit-bundle scan in `Arguments`; no colliding flags) | No | Yes |
| column | Invoke-BashColumn | `-t`, `-s` | Manual loop | Yes | Yes |
| join | Invoke-BashJoin | `-t`, `-1`, `-2` | Binary cmdlet (manual value-flag scan) | No | Yes |
| paste | Invoke-BashPaste | `-d`, `-s` | Manual loop | Yes | Yes |
| tee | Invoke-BashTee | `-a` | Binary cmdlet (`-a` is a declared SwitchParameter; `--` end-of-flags recovered post-parse) | Yes | Yes |
| xargs | Invoke-BashXargs | `-I`, `-n` | Manual loop | Yes | No |
| jq | Invoke-BashJq | `-r`, `-c`, `-S`, `-s` | Manual loop | Yes | Yes |
| date | Invoke-BashDate | `-d`, `-u`, `-r`, `+FORMAT` | Binary cmdlet (`-d` declared as value-bearing parameter `D`; `-u` / `-r` / `+FORMAT` stay in `Arguments`) | No | No |
| seq | Invoke-BashSeq | `-s`, `-w` | Manual loop | No | No |
| expr | Invoke-BashExpr | (expression tokens) | Positional | No | No |
| du | Invoke-BashDu | `-h`, `-s`, `-a`, `-c`, `-d` | Manual loop | No | Yes |
| tree | Invoke-BashTree | `-a`, `-d`, `-L`, `-I`, `--dirsfirst` | Binary cmdlet (`-a` / `-d` declared as SwitchParameters; `-I` declared as a value-bearing string parameter; `-L` and `--dirsfirst` stay in `Arguments`) | No | Yes |
| du | Invoke-BashDu | `-h`, `-s`, `-a`, `-c`, `-d` | Binary cmdlet (`-a` / `-c` declared as `SwitchParameter`s; `-d N` as nullable `int? D`; `-h` / `-s` and joined `-dN` stay in `Arguments`) | No | Yes |
| tree | Invoke-BashTree | `-a`, `-d`, `-L`, `-I`, `--dirsfirst` | Manual loop | No | Yes |
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
| tar | Invoke-BashTar | `-c`, `-x`, `-t`, `-f`, `-z`, `-v`, `-C`, `--exclude` | Manual loop | No | Yes |
| yq | Invoke-BashYq | `-r`, `-o` | Manual loop | Yes | Yes |
| xan | Invoke-BashXan | `-d`, subcommands: `headers`, `count`, `select`, `search`, `table` | Binary cmdlet (`-d` is a declared value-bearing parameter `D`; subcommand keyword stays in `Arguments`) | Yes | Yes |
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
| type | Invoke-BashType | `-t`, `-a`, `-p` | Binary cmdlet (`-a` and `-p` are declared SwitchParameters `A` / `P`; `-t` stays in `Arguments`) | No | No |
| command | Invoke-BashCommand | `-v` | Manual loop | No | No |
| source | Invoke-BashSource | (none) | Positional | No | Yes |
| shift | Invoke-BashShift | `N` | Manual loop | No | No |
| realpath | Invoke-BashRealpath | (none) | Positional | No | No |
| mapfile / readarray | Invoke-BashMapfile | `-n`, `-O`, `-s`, `-t`, `-d` | Binary cmdlet (`-O` declared as `int? O`, `-d` declared as `string? D`; `-n` / `-s` / `-t` stay in `Arguments`) | Yes | No |

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
| split | `InvokeBashSplitCommand` | 4-follow-on | File + pipeline dual mode partitioner. Splits the input into pieces of `-l N` lines (`--lines=N`; default 1000) named `{PREFIX}{suffix}` written to the current working directory (`SessionState.Path.CurrentLocation.Path`, matching the oracle's `Join-Path $PWD`). Default suffix is alphabetic (`aa`, `ab`, …, `zz`, `aaa`, …) reproduced byte-for-byte from the oracle's base-26 decomposition loop; `-d` / `--numeric-suffixes` switches to zero-padded numeric. `-a N` / `--suffix-length=N` sets suffix length (default 2). Operand surface: `FILE [PREFIX]`; bare `-` reads pipeline; with no operands and pipeline input, falls back to stdin with the default `x` prefix; with neither, emits a bash-style "split: missing operand" error. File mode reads via `File.ReadAllLines` (matches the oracle's `Read-BashFileLines` / `StreamReader.ReadLine` semantics — no spurious trailing empty line for a file ending in a newline); each piece is written via `File.WriteAllText` with a `\n` separator and a trailing `\n`, matching the oracle. **Two colliding flags** declared as explicit parameters: `-d` is a `SwitchParameter` (prefix-collides with `-Debug`); `-a` is an explicit `int? A` parameter (the bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter — same hazard `ls` / `uname` hit; declared without an `[Alias("a")]` because case-insensitive binder makes `-a` ≡ `-A` and a same-name alias raises "conflicts with the parameter alias" at module load). `-l` has no PowerShell common-parameter prefix collision and stays in `Arguments`; long-form `--lines=` / `--suffix-length=` / `--numeric-suffixes` likewise pass through to `Arguments` and are recovered by the manual value-flag scan. Replaces a 95-line psm1 function. Parity tests in `InvokeBashSplitCommandTests.cs` cover default 1000-line single-piece, `-l 10` five-piece, `-d` numeric suffix, `-a 3` three-char suffix, custom PREFIX operand, missing input file (no output files), pipeline input with default `x` prefix, `--help`, alias resolution, and a Directive-12 injection probe with `$(throw'pwn');pfx-` as the prefix operand — 10 tests total |
| readlink | `InvokeBashReadlinkCommand` | 4-follow-on | Symlink-target reader. Default (no `-f`): for each operand, resolve via `SessionState.Path.GetUnresolvedProviderPathFromPSPath` and probe with `File.Exists` / `Directory.Exists`; on miss, emit a bash-style "readlink: PATH: No such file or directory" error via `FileSystemHelpers.WriteBashError` and continue. On hit, emit `FileSystemInfo.LinkTarget` (the .NET API that reads the symlink target string) when non-empty, else `FullName` — matching the psm1 oracle's `if ($item.Target) { $item.Target } else { $item.FullName }` branch byte-for-byte. `-f` (canonicalize) branch routes through `GetResolvedPSPathFromPSPath` and emits `ProviderPath`; on miss, same error contract. Output is a typed `PsBash.ReadlinkOutput` PSObject with `Path` + `BashText` properties (the psm1 oracle's exact shape). **No colliding flags** — `-f` is declared as an explicit `SwitchParameter` for clean binder routing (it has no PowerShell common-parameter prefix collision but the declaration keeps the bare token out of `Arguments`); the psm1 oracle's case-sensitive `-ceq '-f'` match is preserved by the binder's case-insensitive but exact-name match (no other flags exist to confuse). Parity tests in `InvokeBashReadlinkCommandTests.cs` cover regular-file fallthrough to FullName, true symlink target resolution (SkippableFact — requires Developer Mode or admin on Windows), missing path with no output, missing operand with no output, `-f` canonicalize branch (existing + missing), multi-operand (existing + missing), unicode filenames, alias resolution, `--help`, and an injection probe with `$(throw'pwn').txt` — 11 tests total |
| uname | `InvokeBashUnameCommand` | 4-follow-on | Trivial system-info printer. Reimplements the psm1 oracle byte-for-byte: `-s` (default) emits `MINGW64_NT-{Major.Minor.Build}` from `Environment.OSVersion.Version`; `-n` emits lowercased `Environment.MachineName`; `-r` emits `{Major.Minor.Build}`; `-m` emits `x86_64` / `i686` based on `Environment.Is64BitProcess`; `-a` emits the four fields plus the literal `MINGW64` trailer. The MSYS/MINGW-style values are kept regardless of host platform so transpiled bash scripts that grep for these tokens stay portable. Bundled short-flag forms (`-snr`, `-snrm`) are accepted via a manual scan against the oracle's `^-([snrma]+)$` predicate; field order is always s/n/r/m regardless of bundle order. **One colliding flag** declared as an explicit `SwitchParameter`: bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter (the only declared parameter starting with 'a') under PowerShell parameter binding, producing a "Missing an argument for parameter 'Arguments'" error otherwise. Bundled forms containing `a` (`-snra`, `-am`, etc.) do not prefix-match `-Arguments` and continue to land in `Arguments` for post-parse decoding. `-s -n -r -m` have no PowerShell common-parameter prefix collision and stay in `Arguments`. Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput` (bare-string fast path). No pipeline input, no file operands, no psm1 helper dependencies on the hot path; `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 52-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashUnameCommandTests.cs` cover each individual flag, `-a` combined, two bundled forms (`-snr`, `-snrm`), no-flag default, unknown-flag silent drop, `--help`, alias resolution, and two Directive-12 injection probes (`$(throw 'pwn')` arg, `-s;rm` dash-prefixed lookalike) — 13 tests total |
| env | `InvokeBashEnvCommand` | 4-follow-on | Environment-variable printer (GNU coreutils `env` / `printenv`). No-args: enumerates `Environment.GetEnvironmentVariables()` and emits one typed `PsBash.EnvEntry` PSObject per variable (Name / Value / BashText="NAME=VALUE"), sorted by name using `StringComparer.Ordinal` — exact parity with the psm1 oracle's `Sort-Object` step on the key set. One-name form: emits a single entry for that variable, or a bash-style "env: 'NAME': not set" error via `FileSystemHelpers.WriteBashError` (matching the oracle byte-for-byte) and returns with no output. **No colliding flags** — the only flag the psm1 oracle ever accepted was `--help`, which delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Aliases `env` and `printenv` stay in psm1 and resolve to this cmdlet automatically. Replaces a 34-line psm1 function. Parity tests in `InvokeBashEnvCommandTests.cs` cover no-args-emits-all, sort-order, one-name-typed-entry, missing-name-error-no-output, `env` alias, `printenv` alias, `--help`, and an injection probe with `$(throw "pwn");rm -rf /` confirming the literal name reaches the not-set error path unevaluated — 8 tests total |
| mktemp | `InvokeBashMktempCommand` | 4-follow-on | Temp-file/dir creator. Builds a unique name under `{Path.GetTempPath()}/ps-bash/proc-sub/` via `Path.GetRandomFileName()`. With a template operand (`myapp.XXXXXX`), strips the trailing `X+` run, takes the basename as a prefix (the oracle discards the template's directory portion — preserved), and appends `Path.GetRandomFileName()`. `-d` switches to directory creation via `Directory.CreateDirectory`; default creates an empty file via `File.WriteAllText(path, "")`. **One colliding flag** declared as an explicit `SwitchParameter`: `-d` prefix-collides with `-Debug` (same hazard `mkdir` handled). The psm1 oracle accepted no other flags — any other `-`-prefixed token (`-u`, `--suffix=X`) is swallowed as a template candidate, last wins; preserved here as deliberate parity, not a feature. Output is a typed `PsBash.MktempOutput` PSObject with `Path` + `BashText` properties, matching the oracle's `[PSCustomObject]@{PSTypeName='PsBash.MktempOutput'; ...}` shape. Parity tests in `InvokeBashMktempCommandTests.cs` cover default file creation, `-d` directory branch, template prefix derivation, template-with-directory-portion (basename-only), `-u`-as-template oracle parity, `--help`, alias resolution, typed-output PSTypeName check, and a `$(throw)` injection probe — 9 tests total |
| tput | `InvokeBashTputCommand` | 4-follow-on | Terminal-capability query. Two-path: (1) native passthrough — resolves `tput` via parameter-bound `InvokeCommand.InvokeScript("Get-Command tput -CommandType Application ...")` and, when present, shells out via `System.Diagnostics.Process` with `UseShellExecute=false` / `RedirectStandardOutput=true` and operands bound through `ProcessStartInfo.ArgumentList` (Directive 12: no shell, no string concatenation into a script body); emits captured stdout via `BashRuntime.EmitBashLines` when the child exits 0. (2) Fallback emulator — emulates the psm1 oracle's switch byte-for-byte for `cols` / `lines` (via `Host.UI.RawUI.WindowSize` with a `System.Console.WindowWidth` / `.WindowHeight` fallback for non-interactive runspaces), `clear` / unknown (silent), `bold` (`\x1B[1m`), `sgr0` (`\x1B[0m`), `setaf N` (`\x1B[38;5;Nm` — 256-color form, matching the oracle's `"`e[38;5;${color}m"`). **No colliding flags** — the psm1 oracle's only flag besides `--help` is `--` (end-of-flags); declared `Arguments` catch-all parses it as a defensive no-op. Replaces a 37-line psm1 function. Parity tests in `InvokeBashTputCommandTests.cs` cover `cols` / `lines` (positive int), `bold` / `sgr0` / `setaf 4` (exact ANSI bytes), empty operand list (silent), unknown capability (silent), `--help`, alias resolution, and a `$()`-injection probe per Directive 12 — 10 tests total |
| expand | `InvokeBashExpandCommand` | 4-follow-on | File + pipeline dual mode tab-to-space expander (GNU coreutils `expand`). Reimplements the psm1 oracle's column-tracked replacement loop byte-for-byte in C# inside the cmdlet: track current column per line; on `\t`, append `tabWidth - col % tabWidth` spaces and advance the column; on any other char, append it and advance the column by one. Supports `-t N` (separate value), `-tN` (joined digits-only), and `--tabs=N` long form — the exact flag set the oracle parsed. The psm1 oracle does not implement multi-stop tab lists (`-t 4,8,12`); the cmdlet preserves that parity (`int.Parse` on a comma string throws, matching the oracle's `[int]"4,8,12"` cast). Pipeline mode (no operands + pipeline input present) splits each item's `BashText` on `\n` after trailing-newline trim. File mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` (matching `StreamReader.ReadLine()` semantics — same slice as Rev/Strings); glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`. Missing files emit a bash-style `expand: PATH: No such file or directory` error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and set `$global:LASTEXITCODE = 1`. **No colliding flags** — `-t` does not prefix-match any PowerShell common parameter, so `Arguments` catch-all + manual scan suffices; no `SwitchParameter` declarations needed. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). Replaces a 59-line psm1 function. Parity tests in `InvokeBashExpandCommandTests.cs` cover empty pipeline, default 8-column stops (whole + partial col advance), `-t 4` / `-t4` / `--tabs=4`, multi-tab column tracking, no-tabs passthrough, multi-line pipeline split, file mode (ASCII + CRLF + unicode), missing-file continuation, `--help`, alias resolution, and an injection probe per Directive 12 — 16 tests total |
| fold | `InvokeBashFoldCommand` | 4-follow-on | File + pipeline dual mode line wrapper (GNU coreutils `fold`). Wraps each input line at a fixed column width — default 80, override via `-w N` / `-wN` / `--width=N`. Hard-wrap by default; `-s` enables soft-wrap (when the wrap point falls mid-word, walk backward via `String.LastIndexOf(' ', chunkEnd-1, width)` to the previous space within the window and break just after it — matching the psm1 oracle's `LastIndexOf` slice byte-for-byte; if no space exists in the window, hard-break at width, GNU behavior). `-b` (bytes) is accepted for arg compatibility and behaves identically to the default char path for ASCII (the only path the psm1 oracle ever supported). Pipeline mode splits a multi-line `BashText` item on `\n` (after trailing-newline trim); file mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` (StreamReader.ReadLine semantics — trailing newline does not yield a spurious empty final line). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and set `$global:LASTEXITCODE = 1`. **One colliding flag** declared as an explicit value-bearing parameter: `-w` prefix-collides with `-WarningAction` / `-WarningVariable`, so it is declared as a `string? Width` parameter (alias `w`) that captures the standard `-w N` form (the empty-output-on-test failure mode that diagnoses this collision exactly). The joined `-wN` and `--width=N` forms continue to flow through `Arguments` and are recovered by the manual value-flag scan. `-s` has no common-parameter prefix overlap but is declared as a `SwitchParameter` for symmetry (no functional change). `-b` (bytes, accepted as a no-op for ASCII) stays in `Arguments`. Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. Replaces a 73-line psm1 function. Parity tests in `InvokeBashFoldCommandTests.cs` cover empty input, short / exact-width / longer-than-width lines (hard wrap), `-w` separated / `-wN` joined / `--width=` long forms, `-s` soft-wrap success, `-s` no-space-in-window hard fallback, file mode + CRLF normalization, pipeline mode + multi-line item split, missing-file continuation, `-b` no-op, `--help`, alias resolution, and a Directive-12 injection probe — 16 tests total |
| nl | `InvokeBashNlCommand` | 4-follow-on | File + pipeline dual mode line numberer. GNU coreutils `nl`: default numbers non-empty lines only (empty lines emit a bare empty line, unnumbered); `-ba` (also accepted as the split form `-b a` per the psm1 oracle) numbers all lines including empty. Output format `{0,6}\t{1}` — 6-column right-aligned number, tab, then the line — produced via `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). File mode reads via `File.ReadAllText` with CRLF normalization and splits on `\n` using `StreamReader.ReadLine()` semantics (no spurious trailing empty line). Pipeline mode splits multi-line `BashText` items on `\n` (after trailing-newline trim) — exact parity with the oracle's defensive-split path. Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via `FileSystemHelpers.WriteBashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and continue — the oracle did not set `$LASTEXITCODE=1` for nl (its `Read-BashFileLines` returned `$null` silently on miss), preserved here. **No colliding flags** — `-ba` and `-b` share no prefix with any PowerShell common parameter (`-Verbose -Debug -Confirm -WhatIf -Error* -Warning* -Information* -Out* -Progress* -PipelineVariable`); both stay in `Arguments` and are parsed by the manual scan. Replaces a 82-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashNlCommandTests.cs` cover three-line file (default numbering), mid-file empty lines (skipped numbering, bare emit), `-ba` numbers-all, pipeline mode (default + `-ba`), CRLF normalization, unicode, missing-file error continuation, `--help`, `nl` alias resolution, the split `-b a` oracle parity form, and a `$(throw 'INJECTED')` operand probe per Directive 12 — 13 tests total |
| shuf | `InvokeBashShufCommand` | 4-follow-on | Random-shuffle producer (GNU coreutils `shuf`). Three input modes matching the psm1 oracle byte-for-byte: **echo** (`-e a b c` — args as items), **range** (`-i LO-HI` — emit integers LO..HI rendered as strings), and **file / pipeline** (default — first positional operand is a file path; absent operands fall back to pipeline `BashText`). `-n N` (and `--head-count=N`) caps post-shuffle output. **One colliding flag** declared as an explicit `SwitchParameter`: `-e` prefix-collides with `-ErrorAction` / `-ErrorVariable`. The echo-mode item collection covers both PS-binder consumption of `-e` (case a — items appear bare in `Arguments`) and a literal `-e` carried through `ValueFromRemainingArguments` (case b — items collected between `-e` and the next `-`-prefixed token, oracle parity). `-n` / `-i` / `--head-count=` have no PS common-parameter prefix collision and stay in `Arguments`, parsed by a manual scan. Shuffle uses unseeded `System.Random` (Fisher-Yates), matching the oracle's `[System.Random]::new()` non-determinism; tests assert that output is a permutation of input (multiset equality), never an exact ordering. Missing file operands route through `FileSystemHelpers.ResolveOperandPaths` and a `try { File.ReadAllText }` swallow, matching the oracle's `Get-Content -ErrorAction SilentlyContinue` (no output, no error, no items added). Unknown `-`-prefixed flag emits the bash-style `shuf: invalid option '<flag>'` error via psm1 `Write-BashError` and sets `$global:LASTEXITCODE=1`. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`) — each item is a single line, so the observable shape matches the oracle's per-item `Emit-BashLine -Text`. Replaces an 88-line psm1 function. Parity tests in `InvokeBashShufCommandTests.cs` cover empty input, echo mode, `-n` cap, `-n 0` empty, `-n` > input emits all, range mode, range + `-n`, pipeline mode, file mode, unicode (combining marks + CJK + emoji), missing-file silent skip, `--help`, alias resolution, and a Directive-12 injection probe — 14 tests total |
| base64 | `InvokeBashBase64Command` | 4-follow-on | File + pipeline dual mode base64 encoder/decoder (GNU coreutils). Reimplements the psm1 oracle byte-for-byte: encode path uses `Convert.ToBase64String` over `File.ReadAllBytes` (file mode, first operand only — later operands ignored, matching the oracle's `$operands[0]` indexing) or over UTF-8 bytes of `\n`-joined pipeline `BashText` with a trailing-newline guarantee (pipeline mode); decode path uses `Convert.FromBase64String` after `.Trim()` on the file/pipeline text. Encoded output wraps at `-w N` columns (default 76; `-w 0` disables wrap) by joining wrap-sized substrings with `Environment.NewLine` and stripping trailing CR/LF — exact mirror of the oracle's `StringBuilder.AppendLine` + `TrimEnd("`r","`n")` slice (line ending stays platform-native on purpose; the worker normalizes on serialization). Decoded output strips a single trailing `\n` (the oracle's `$output -replace "`n$",''`). File decode reads via `File.ReadAllText` with CRLF normalization (the oracle's `Read-BashFileBytes` slice, reimplemented in C# in the cmdlet — no callback to psm1 on the hot path). **Two colliding flags** declared as explicit parameters with single-letter names so the binder routes the bare token by exact-name match (not by `[Alias]` — aliases lose to common-parameter prefix matches under the cmdlet binder): `-d` prefix-collides with `-Debug`, so the parameter is literally named `D` (declared `SwitchParameter`); `-w` prefix-collides with `-WarningAction` / `-WarningVariable`, so the parameter is literally named `W` (declared nullable `int` so unset falls back to the manual scan's default of 76). The long forms `--decode` and `--wrap=N` are recovered post-parse from `Arguments` by a manual scan. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe); file-read failures go through `FileSystemHelpers.WriteBashError`. Empty operand + empty pipeline returns no output, matching the oracle. Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput` bare-string fast path; the wrapped encoded string is one object whose `BashText` carries the embedded line endings). Replaces an 80-line psm1 function. Parity tests in `InvokeBashBase64CommandTests.cs` cover empty input, ASCII encode round-trip, decode round-trip, unicode bytes (héllo + emoji) over file mode, `-w 0` no-wrap, `-w 10` narrow wrap with chunk-length verification, decode-trims-whitespace, missing-file error continuation, alias resolution, `--help`, and a `$(throw)` injection probe per Directive 12 — 13 tests total |
| paste | `InvokeBashPasteCommand` | 4-follow-on | Multi-file line merger (GNU coreutils `paste`). Reads each operand file fully (CRLF-normalized, `StreamReader.ReadLine()` semantics — a trailing newline does NOT yield a spurious empty final line; a file that is exactly `\n` yields one empty line), then in normal mode zips files row by row joined by the delimiter (default tab); shorter files pad with empty strings up to `maxLines`. `-s` (serial) emits one line per file with the file's own lines joined by the delimiter. **Oracle bit-for-bit parity note:** the psm1 oracle stored the entire `-d` value as a single string and joined fields with it; GNU paste cycles a multi-char delimiter per character in the row direction, but the oracle never did, and we preserve the oracle's behavior. **No colliding flags** — `-d` / `-s` have no PowerShell common-parameter prefix collision (`-Debug` requires `-d` to be ambiguous with `-Arguments`, but here `-d` is consumed by the manual scan from the `Arguments` catch-all). `-dDELIM` joined form (`-cmatch '^-d(.+)$'` in the oracle) and `--` end-of-flags are recovered post-parse. Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; a file-read failure emits a bash-style error via psm1 `Write-BashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and the cmdlet returns early — matching the oracle's `if ($null -eq $fileLines) { return }`. Pipeline input is intentionally ignored (the oracle never consumed it). Output uses `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). Replaces an 88-line psm1 function. Parity tests in `InvokeBashPasteCommandTests.cs` cover no-operands, two-file equal length, two-file different length (shorter padded empty), single-char `-d` delim, multi-char `-d ":,"` delim oracle-literal, `-s` serial mode, `-s -d` combined, pipeline-input-ignored, missing-file, unicode content, `--help`, alias resolution, joined `-d,` form, empty file, and a `$(throw 'pwn');echo pwned` injection probe per Directive 12 — 15 tests total |
| touch / ln / sleep / which | `InvokeBashTouchCommand` / `InvokeBashLnCommand` / `InvokeBashSleepCommand` / `InvokeBashWhichCommand` | 4 | Four small file-metadata and utility commands. **Touch** supports `-d DATE` (`DateTime.TryParse` — bash-style invalid-date error on failure), `-a` (access-time only), `-m` (mod-time only), `-c` (no-create), `-v` (no-op for arg compat); creates an empty file via `File.Create(...).Dispose()` for missing operands and sets timestamps via `File.SetLastWriteTime` / `SetLastAccessTime`. **Ln** supports `-s` (symbolic — `File.CreateSymbolicLink` / `Directory.CreateSymbolicLink` depending on whether the target exists as a dir; defaults to file-symlink for missing targets), `-f` (force — remove existing link first), `-v` (verbose). The non-`-s` hard-link path still delegates to psm1 `New-Item -ItemType HardLink` via parameter-bound `InvokeCommand.InvokeScript` because System.IO has no hard-link API in stdlib. **Sleep** parses each operand as a decimal with optional `s/m/h/d` suffix, sums them, then sleeps in 100 ms chunks polling `PSCmdlet.Stopping` so Ctrl-C / pipeline-stop interrupts within one chunk. **Which** delegates command resolution to PowerShell `Get-Command` via parameter-bound `InvokeCommand.InvokeScript` (the only way to see aliases / functions / cmdlets registered in the runspace); supports `-a` (case-sensitive — `-A` is not the all-flag). **Colliding flags** declared as `SwitchParameter`s: touch `-a` / `-c` / `-v` (vs `Arguments` `a`-prefix / `-Confirm` / `-Verbose`); ln `-v`; sleep none; which none. Replaces 4 functions × ~50–90 psm1 lines each. Parity test coverage to be added — direct smoke test of typical invocations succeeds end-to-end |
| time | `InvokeBashTimeCommand` | 4-follow-on | Wall-clock timer that wraps an inner command, matching the bash `time` builtin / GNU `time`. Reproduces the psm1 oracle byte-for-byte: missing-args → "time: missing command" bash error and no output object; happy path → invoke the wrapped command via `InvokeCommand.InvokeScript` with a fixed parameterless body (`& $args[0] @rest 2>&1`) and the wrapped command name + args bound positionally through `$args` (Directive 12 — user-controlled tokens never concatenate into the script body, so a name containing `;` / `$()` / scriptblock chars / backticks stays a literal command-name lookup and fails the usual CommandNotFoundException path); collected `ErrorRecord` items route through `FileSystemHelpers.WriteBashError` (forcing `ExitCode = 1`) while non-error items contribute their `BashText` (via `BashRuntime.GetBashText`) to the joined-with-`\n` result text. Emits one typed `PsBash.TimeOutput` PSObject (`RealTime` / `Command` / `ExitCode` / `BashText`) and writes `"real    {seconds:N3}s"` to `[Console]::Error` (matching the oracle's `[Console]::Error.WriteLine`). **No colliding flags** — `time` accepts no flags besides `--help`. Replaces a 58-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashTimeCommandTests.cs` cover wrap-echo happy path (TimeOutput shape + ExitCode=0), no-args (no output), unknown-command (ExitCode=1, command name preserved), alias resolution, `--help`, and two Directive-12 injection probes (`$(throw "pwn")` command name, `echo;rm` semicolon-bearing name) — 7 tests total |
| file | `InvokeBashFileCommand` | 4-follow-on | Magic-byte file-type detector (GNU coreutils `file`). Reproduces the psm1 oracle byte-for-byte: reads the first 16 bytes of each operand and walks a fixed magic-byte table — PNG (`89 50 4E 47`), JPEG (`FF D8`), PDF (`25 50 44 46`), Zip (`50 4B 03 04`), ELF (`7F 45 4C 46`), GIF (`47 49 46 38`), RIFF (`52 49 46 46`) — emitting the matching type description + MIME type on a hit. On no magic-byte match, reads the full file via `File.ReadAllBytes` and applies the oracle's text predicate (`b < 0x07` OR (`0x0E <= b <= 0x1F` AND `b != 0x1B`) → non-text); all-text content reports as `ASCII text` (`text/plain`), otherwise `data` (`application/octet-stream`). UTF-8 multi-byte continuation bytes (≥ 0x80) pass the predicate, so non-ASCII text registers as `ASCII text` — preserved as-is from the oracle. Flag surface: `-b` / `--brief` (omit `PATH: ` prefix), `-i` / `--mime` (emit MIME type), `-L` / `--dereference` (accepted but no-op — `File.OpenRead` follows symlinks by default, same as the oracle). **One colliding flag** declared as an explicit `SwitchParameter`: `-i` prefix-collides with `-InformationAction` / `-InformationVariable` per the playbook collision table. `-b` and `-L` have no common-parameter prefix collision and stay in `Arguments`; both literal `-i` and `--mime` long form are also recovered from `Arguments` for parity with the oracle's `-ceq '-i' -or -eq '--mime'` switch. Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing operands emit a bash-style `file: cannot open 'PATH' (No such file or directory)` error via `FileSystemHelpers.WriteBashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and continue. Output is a typed `PsBash.TextOutput` PSObject with `BashText` + `FileName` + `FileType` + `MimeType` side properties, matching the oracle's `[PSCustomObject]` shape (constructed manually rather than via `BashRuntime.NewBashObject` because that helper does not carry per-output side properties). Replaces a 96-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashFileCommandTests.cs` cover plain ASCII text, binary control-byte payload → `data`, empty file → `ASCII text` (oracle parity — an empty byte run passes the text predicate), PNG magic-byte detection, `-b` brief, `-i` MIME, combined `-b -i`, multi-operand emission, missing-file error continuation, typed-output side-property roundtrip, `--help`, alias resolution, and a Directive-12 injection probe with `$(throw'pwn');run.txt` — 13 tests total |
| seq | `InvokeBashSeqCommand` | 4-follow-on | GNU coreutils `seq` integer / decimal sequence generator. Reproduces the psm1 oracle's three operand forms (`seq LAST` / `seq FIRST LAST` / `seq FIRST INCR LAST`) byte-for-byte, including the integer-detection short circuit (`first == floor(first) && increment == floor(increment) && last == floor(last)`), max-decimal-places across operands for the `F{N}` invariant-culture format, the `±1e-9` epsilon comparison in the loop condition, and the `-w` zero-pad width (integer mode only — `max(|first|,|last|).ToString().Length`). Flag surface: `-s SEP` / `--separator SEP` / `--separator=SEP` (joined output — single bare string via `BashRuntime.NewBashObject` fast path), `-w` / `--equal-width` (zero-pad). **One colliding flag** declared as an explicit `SwitchParameter`: `-w` prefix-collides with `-WarningAction` / `-WarningVariable` — the standard remedy from the playbook collision table. `-s` has no `-S*` common-parameter prefix collision and stays in `Arguments`, parsed by the manual value-flag scan (separated, long-form, and `--separator=` forms all decoded). Default branch emits per-value typed `PsBash.SeqOutput` PSObjects (`Value` / `Index` / `BashText`). Defensive guard: a zero `increment` exits the loop early (the psm1 oracle would infinite-loop in that case; same caller-visible "no progress" outcome). `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 116-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashSeqCommandTests.cs` cover one-arg / two-arg / three-arg forms, negative step descent, `-s ','` separator, `--separator=-` long form, `-w` zero-pad (1..10 → `01`..`10`), decimal step (`1 0.5 2` → `1.0 1.5 2.0`), no-args default (emit `1`), typed-output PSTypeName check, typed-output `Value` / `Index`, `--help`, alias resolution, and a `$(throw)` separator injection probe per Directive 12 — 14 tests total |
| du | `InvokeBashDuCommand` | 4-follow-on | Disk-usage estimator (GNU coreutils `du`). Reproduces the psm1 oracle byte-for-byte: for each operand, resolve via `SessionState.Path.GetUnresolvedProviderPathFromPSPath` and probe with `Directory.Exists` / `File.Exists` (matching the oracle's `Get-BashItem` slice — on miss, a bash-style `du: cannot access 'PATH': No such file or directory` error via `FileSystemHelpers.WriteBashError` and continue). For a file operand emit one `PsBash.DuEntry` PSObject (sized via `Ceiling(bytes/1024)` 1024-byte blocks); for a directory, enumerate the root + all descendants via `DirectoryInfo.EnumerateDirectories("*", SearchOption.AllDirectories)`, compute per-directory file-size sum, then bottom-up accumulate via deepest-first `OrderByDescending(d => d.FullName.Length)` so each directory's reported size includes all descendants — exact mirror of the oracle's `Get-ChildItem -Force -Recurse -Directory` + `Sort-Object { $_.FullName.Length } -Descending` chain. `-s` (summary) emits only the root entry; `-a` (include files) appends one `DuEntry` per descendant file via `EnumerateFiles("*", SearchOption.AllDirectories)`; `-c` (grand total) emits a final entry with `Path = "total"` and `IsTotal = true`; `-d N` (depth limit) drops entries whose path-segment-delta from the root exceeds N. Human-readable sizes via the oracle's `Format-BashSize` ladder reimplemented in C# (`< 1024` → bare byte count; otherwise scale by 1024 through `K M G T P`, emit `"{N}{unit}"` when scaled >= 10 else `"{N.N}{unit}"`, both via `Math.Ceiling` to match the oracle exactly). Output rows are sorted by `Path` ordinal at the end of each operand's pass (oracle: `Sort-Object { $_.Path }`). **Three colliding flags** declared as explicit parameters per the playbook collision table: `-d N` (depth) prefix-collides with `-Debug` — declared as nullable `int? D`; `-a` (all-files) prefix-matches the cmdlet's own `-Arguments` parameter — declared as `SwitchParameter A`; `-c` (grand total) prefix-collides with `-Confirm` — declared as `SwitchParameter C`. `-s` / `-h` have no PowerShell common-parameter prefix collision and stay in `Arguments`; the joined `-dN` form (oracle: `^-d(\d+)$`) is also recovered from `Arguments` by the manual scan. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 209-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashDuCommandTests.cs` cover single-file operand, single-directory recursion, nested-tree accumulation, `-s` summary, `-h` human-readable formatting (`2.0K`), `-a` file emission, `-c` grand total, `-d 1` depth limit, `-d0` joined form, multi-operand, no-operand `.` default, alias resolution, `--help`, and a Directive-12 injection probe with `$(throw 'pwn');missing` as the operand confirming the literal-path-no-such-file branch (no exception, no output object) — 14 tests total |
| expr | `InvokeBashExprCommand` | 4-follow-on | Arithmetic / string evaluator (GNU coreutils `expr`). Reimplements the psm1 oracle's dispatch order byte-for-byte: keyword forms `length STR` / `substr STR POS LEN` / `index STR CHARS` / `match STR REGEX` first; then infix `OP1 OP OP2` where both sides matching `^-?\d+$` route through 64-bit integer math (`+ - * / %` plus six comparison ops, with `/` using truncate-toward-zero via `Math.Truncate((double)l/r)` to match the oracle), else string compare with `=` / `!=` case-sensitive (oracle's `-ceq` / `-cne`) and `<` / `<=` / `>=` / `>` case-insensitive ordinal (oracle's PowerShell string `-lt` / `-le` / `-ge` / `-gt`); else single-operand echo. `match` translates POSIX BRE `\(...\)` to .NET `(...)` exactly like the oracle's two `-replace` passes and anchors at start. Error paths (missing operand, division by zero, unknown operator, non-integer infix arg) route through psm1 `Write-BashError` with `-ExitCode 2` (GNU `expr`'s "error in expression" code) via a parameter-bound `param($m,$c) Write-BashError -Message $m -ExitCode $c` script body — no `ScriptBlock` construction, AOT-safe. Output is a typed `PsBash.ExprOutput` PSObject with `Value` (boxed `long` when result matches `^-?\d+$`, else `string`) and `BashText` properties — exact oracle shape. **No PowerShell common-parameter prefix collisions** — expr operands are digits, operators, and arbitrary strings; the cmdlet declares only the catch-all `Arguments` parameter (`ValueFromRemainingArguments=true`). Replaces a 118-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashExprCommandTests.cs` cover arithmetic (+ - * / % with negative operand), six comparisons (numeric + string), all four string ops (`length` ASCII + unicode, `substr` with length clamp, `index` hit + miss, `match` with anchored regex + no-match), single-operand echo, three error paths (missing operand, divide-by-zero, unknown operator — each verifying `$global:LASTEXITCODE = 2` and no output object), `--help`, alias resolution, typed-PSObject shape (numeric → `long`, non-numeric → `string`), and a Directive-12 injection probe (`$(throw 'pwn');rm -rf /` as the single operand emits a literal string with no nested evaluation) — 30 tests total |
| column | `InvokeBashColumnCommand` | 4-follow-on | File + pipeline dual mode table formatter (util-linux `column`). Plain mode (no `-t`) emits each input line unchanged via `BashRuntime.NewBashObject` (default `PsBash.TextOutput`). `-t` (table) mode trims each non-empty line, splits on whitespace (or the regex-escaped `-s SEP` delimiter — separated `-s SEP` and joined `-sSEP` forms, the latter requiring exactly one char per the oracle's `^-s(.)$` pattern), computes per-column max widths, and emits each row with all but the trailing column padded via `String.PadRight(width)`; the output column separator is hard-coded to two spaces, matching the oracle byte-for-byte (no `-o` output-separator flag — the oracle never accepted one). Empty pipeline / empty file produce no output; an empty line becomes a single empty field. Pipeline mode splits a multi-line `BashText` item on `\n` (after trailing-newline trim); file mode reads via `File.ReadAllText` with CRLF normalization and `StreamReader.ReadLine()` trailing-newline semantics (the rev/strings/comm slice — a file ending in `\n` does not yield a spurious empty final line, but a file of exactly `"\n"` yields one empty line). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit `column: PATH: No such file or directory` via `FileSystemHelpers.WriteBashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and the cmdlet continues with remaining operands, matching the oracle's `Read-BashFileLines` null-swallow contract. **No colliding flags** — `-t` / `-s` share no prefix with any PowerShell common parameter (no `-T*` / `-S*` exist); both stay in `Arguments` and are parsed by a manual case-sensitive scan, matching the oracle's `-ceq` comparisons. `--` end-of-flags is recovered post-parse. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeScript`. Replaces a 121-line psm1 function. Parity tests in `InvokeBashColumnCommandTests.cs` cover plain-mode passthrough, `-t` column alignment, `-t -s ','` separated separator, `-t -s,` joined separator, pipeline mode (plain + `-t`), empty pipeline, empty file, missing-file error continuation, CRLF normalization, unicode, ragged rows (last column unpadded per row), `--help`, alias resolution, and a Directive-12 `$(throw)` operand injection probe — 15 tests total |
| comm | `InvokeBashCommCommand` | 4-follow-on | Two-pointer walk over two sorted files emitting a 3-column tab-prefixed output: column 1 = only in file1, column 2 = only in file2, column 3 = in both. Digit flags `-1` / `-2` / `-3` (and bundles `-12` / `-13` / `-23` / `-123`) suppress the matching column and remove its leading tab from later columns. Comparison uses `string.CompareOrdinal`, mirroring the psm1 oracle's `[string]::Compare(..., Ordinal)` slice byte-for-byte. File reads route through inline CRLF-normalized `File.ReadAllText` + `\n` split (the same `StreamReader.ReadLine()` semantics as Rev/Strings — trailing newline does not yield a spurious empty final line, but a file of exactly `"\n"` does yield one empty line). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style `comm: PATH: No such file or directory` error via `FileSystemHelpers.WriteBashError` (parameter-bound `InvokeCommand.InvokeScript`, AOT-safe) and the cmdlet returns with no further output (matching the oracle's early-return-on-null contract). Missing-operand (< 2 operands) emits `comm: missing operand` and returns. **No colliding flags** — `-1` / `-2` / `-3` are digit-prefixed tokens; no PowerShell common parameter starts with a digit, so they stay in `Arguments` and are parsed by the manual `^-[123]+$` digit-bundle predicate. Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. Replaces a 102-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashCommCommandTests.cs` cover disjoint files, overlapping files (all three columns), `-1` / `-2` / `-3` individual suppression, `-12` and `-123` bundles, identical files, both-empty / one-empty pair, CRLF normalization, unicode (`é` ordinal sort), missing operand, missing file, `--help`, alias resolution, and a Directive-12 injection probe — 16 tests total |
| join | `InvokeBashJoinCommand` | 4-follow-on | Relational two-file join on a common key column (GNU coreutils `join`). Reimplements the psm1 oracle byte-for-byte: read both files into line arrays, build a `Dictionary<string, List<string[]>>` keyed by the file-2 join field (Ordinal comparer, preserving insertion order for duplicate keys), iterate file-1 lines in order, and for each matching file-2 row emit `key + delim + file1-rest + delim + file2-rest`. File reads inline `Read-BashFileLines`'s slice (`File.ReadAllText` + CRLF normalization + `\n` split with StreamReader.ReadLine trailing-newline semantics). Path resolution uses `SessionState.Path.GetUnresolvedProviderPathFromPSPath` — no glob expansion, matching the oracle exactly. Flag surface: `-t SEP` (delimiter, default single space), joined form `-tC` (single-char delimiter via `arg.Length==3` exact match), `-1 N` (key column for file 1, 1-based, default 1), `-2 N` (key column for file 2, default 1), `--` end-of-flags, `--help`. Missing files emit a bash-style `join: PATH: No such file or directory` error via `FileSystemHelpers.WriteBashError` and return; `< 2` operands emit `join: missing operand` and return. **No colliding flags** — `-t`, `-1`, `-2` have no prefix overlap with any PowerShell common parameter, so all stay in `Arguments` and are parsed by a manual value-flag scan. Output: bare `PsBash.TextOutput` strings via `BashRuntime.NewBashObject`. Replaces a 116-line psm1 function. Parity tests in `InvokeBashJoinCommandTests.cs` cover default key-column join, `-1 2` key-in-column-2-of-file-1, `-t ','` custom delimiter, no-match no-output, missing-file error, missing-operand, empty files, CRLF normalization, `--help`, alias resolution, and a `$(throw)` injection probe per Directive 12 — 11 tests total |
| type | `InvokeBashTypeCommand` | 4-follow-on | Bash `type` builtin — classify a command name as alias / function / builtin / file, or in `-p` mode emit a bash-style `declare` line for a variable's current value. Reimplements the psm1 oracle's dispatch order byte-for-byte. Two colliding flags declared as explicit `SwitchParameter`s: `-a` prefix-matches the cmdlet's own `-Arguments` catch-all (declared as `A`); `-p` prefix-matches `-PipelineVariable` / `-ProgressAction` (declared as `P`). `-t` stays in `Arguments`. Output: typed `PsBash.TypeOutput` PSObjects with `Command` / `Kind` / `BashText` properties. Not-found cases set `$LASTEXITCODE = 1` via `FileSystemHelpers.SetLastExitCode`. `--help` / `Get-Alias` / `Get-Command` / `ConvertTo-Json -Compress` all route through parameter-bound `InvokeCommand.InvokeScript` (AOT-safe — no `ScriptBlock` construction). Replaces a 118-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashTypeCommandTests.cs` — 13 tests total |
| unexpand | `InvokeBashUnexpandCommand` | 4-follow-on | File + pipeline dual mode space-to-tab converter (GNU coreutils `unexpand`; the inverse of `expand`). Reproduces the psm1 oracle's two modes byte-for-byte: **default (leading-only)** counts L leading spaces and emits `floor(L/tabWidth)` tabs + `L%tabWidth` remainder spaces + the rest of the line unchanged (partial leading runs that don't reach a tabstop stay as spaces); **`-a` / `--all`** walks every character — on each space, increments a column counter and a space-run counter, and when `col % tabWidth == 0` AND `spaceRun >= 2`, emits one tab and resets the run; partial runs at end of line stay as literal spaces. Flag surface: `-t N` / `-tN` / `--tabs=N` (tab width, default 8), `-a` / `--all`, `--first-only` (default mode — preserved for arg-compat). **One colliding flag** declared as an explicit `SwitchParameter`: bare token `-a` prefix-matches the cmdlet's own `-Arguments` parameter under PowerShell parameter binding — same hazard `uname` handled. `-t` has no PowerShell common-parameter prefix collision and is scanned out of `Arguments` by the manual value-flag loop (separated, joined, and `--tabs=` forms all decoded). Glob expansion routes through `FileSystemHelpers.ResolveOperandPaths`; missing files emit a bash-style error via `FileSystemHelpers.WriteBashError` and continue. Files read via `File.ReadAllText` with CRLF normalization; trailing newline does not produce a spurious empty final line (rev/strings pattern). Output uses `BashRuntime.NewBashObject` with default `PsBash.TextOutput`. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces an 87-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashUnexpandCommandTests.cs` cover 8-leading-spaces → 1 tab, 16 → 2 tabs, partial-run preservation, 10 → tab+2 remainder, `-t 4` / `-t4` / `--tabs=4`, `-a` interior-run-at-boundary, `-a` single-space preservation, default mode interior-spaces preserved, no-spaces unchanged, multi-line file mode, multi-item pipeline, missing-file continuation, `--help`, alias resolution, unicode tail preservation, and a `$(throw)` injection probe per Directive 12 — 18 tests total |
| date | `InvokeBashDateCommand` | 4-follow-on | GNU coreutils `date`. Reproduces the psm1 oracle byte-for-byte: default emits a `"Thu Jan  2 15:04:05 MST 2006"`-style local datetime built from a `DateTimeOffset.Now`; `-d STRING` / `--date STRING` / `--date=STRING` parses via `DateTimeOffset.Parse(value, InvariantCulture)` with a try/catch routing failures to `bash-style date: invalid date 'STRING'` via psm1 `Write-BashError`; `-u` / `--utc` / `--universal` calls `ToUniversalTime()` and sets `TimeZone="UTC"`; `-r FILE` / `--reference FILE` resolves via `SessionState.Path.GetUnresolvedProviderPathFromPSPath`, probes `File.Exists` / `Directory.Exists`, and uses `LastWriteTime` (missing-path error matches the oracle exactly). `+FORMAT` runs through a private `ConvertDateFormat` per-char switch reproducing the psm1 `Convert-DateFormat` helper byte-for-byte for `%Y %y %m %d %H %M %S %s %F %T %w %A %B %Z %a %b %e %j %p %n %t %%` and preserving unknown `%X` as literal. Output is a typed `PsBash.DateOutput` PSObject with `Year` / `Month` / `Day` / `Hour` / `Minute` / `Second` / `Epoch` / `DayOfWeek` / `TimeZone` / `DateTime` / `BashText` properties — exact oracle shape. **One colliding flag** declared as an explicit value-bearing parameter with a single-letter name: `-d` prefix-collides with `-Debug`, declared literally as `D` (same pattern `cut` / `base64` used). The long forms `--date` / `--date=` continue to flow through `Arguments` and are recovered by the manual scan. `-u` / `-r` / `+FORMAT` have no PowerShell common-parameter prefix collision and stay in `Arguments`. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a 174-line psm1 block (function + `Convert-DateFormat` helper). Parity tests in `PsBash.Cmdlets.Tests/InvokeBashDateCommandTests.cs` cover typed-output PSTypeName check, default `BashText` shape (six space-sep fields), `-d` parsed-date Y/M/D, `+%Y-%m-%d` exact format, `+%s` epoch for `1970-01-01T00:00:00Z`, `-u` UTC TimeZone, `+%%` literal percent, unknown `%Q` spec preserved, invalid `-d` no-output-with-error, missing `-r` no-output-with-error, `--help`, `date` alias resolution, and a Directive-12 injection probe with `+$(throw 'pwn')%Y` confirming the literal `$(throw 'pwn')` reaches the format engine unevaluated — 13 tests total |
| tree | `InvokeBashTreeCommand` | 4-follow-on | Recursive directory-tree printer with box-drawing prefix (`├── │   └──`). Reproduces the psm1 oracle byte-for-byte: builds a typed `PsBash.TreeEntry` PSObject for the root, walks the tree depth-first via `DirectoryInfo.GetFileSystemInfos()` plus a manual sort (alphabetic by default, dirs-before-files under `--dirsfirst`), filters dotfiles unless `-a`, filters by glob via `WildcardPattern.IsMatch` under `-I PATTERN`, drops files under `-d`, honors `-L N` depth (per-level guard), and emits a final summary `PsBash.TreeEntry` with `BashText = "{N} directories, {M} files"` (or `"{N} directories"` under `-d`) with singular `directory` / `file` forms when counts are exactly 1. **Three colliding flags** declared as explicit parameters: `-d` prefix-collides with `-Debug` → `SwitchParameter D`; bare `-a` prefix-matches the cmdlet's own `-Arguments` → `SwitchParameter A`; `-I PATTERN` prefix-collides with `-InformationAction` / `-InformationVariable` → `string? I`. `-L N` / `-LN` and `--dirsfirst` have no PowerShell common-parameter prefix collision and stay in `Arguments`, parsed by the manual scan; the same scan also decodes bundled short forms (`-ad`, `-da`) recovered post-parse. Root path resolved via `SessionState.Path.GetUnresolvedProviderPathFromPSPath`; a missing target emits a bash-style `tree: cannot access 'PATH': No such file or directory` error via `FileSystemHelpers.WriteBashError` and the cmdlet returns with no further output. **Directive 12:** the `-I PATTERN` value is fed only to `WildcardPattern.IsMatch` — a pattern containing `$(throw 'pwn')` arrives as a literal glob string and is never re-parsed as PowerShell. Replaces a 175-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashTreeCommandTests.cs` cover empty dir, one-level (alpha order), nested (indented prefix), `-L 1` depth limit, `-d` dirs-only summary form, `-a` show-hidden, `-I '*.tmp'` exclude, `--dirsfirst` sort, summary singular/plural pluralization, alias resolution, `--help`, typed-output PSTypeName, and a Directive-12 injection probe on the `-I` pattern — 13 tests total |
| mapfile / readarray | `InvokeBashMapfileCommand` | 4-follow-on | Bash `mapfile` / `readarray` builtin — read pipeline lines into an array variable in the caller's scope. Pipeline-only (the psm1 oracle never accepted file operands; the non-flag operand is the destination variable name). Empty lines are dropped (oracle parity: `if ($line -ne '') { $lines.Add }`). Default destination name is `MAPFILE`. Flag surface: `-n N` cap, `-O ORIGIN` start index with empty-string prefix (`@(1..$origin | ForEach-Object { '' })` slice), `-s N` skip first N (cmdlet addition; oracle did not implement), `-t` strip trailing `\r`/`\n`, `-d DELIM` consumed but ignored (oracle parity — always splits on `\n`). **Two colliding flags** declared as explicit parameters per the playbook collision table: `-O` prefix-matches `-OutVariable` / `-OutBuffer` (declared as `int? O`); `-d` prefix-matches `-Debug` (declared as `string? D`). `-n` / `-s` / `-t` have no PowerShell common-parameter prefix overlap and stay in `Arguments`; the manual scan also recovers joined `-nN` / `-OORIGIN` / `-sN` / `-dDELIM` forms. Writes back via `SessionState.PSVariable.Set` (the runspace-scope equivalent of the oracle's `Set-Variable -Name $varName -Value $result`). No stdout (variable side-effect only). The destination-variable token is checked for PowerShell scriptblock metacharacters (`$ ( ; { ` " `) per Directive 12 — a hit emits a bash-style `mapfile: '<NAME>': not a valid identifier` error via `FileSystemHelpers.WriteBashError` and skips the assignment, defeating injection through the variable-name path. `--help` delegates to psm1 `Show-BashHelp`. Aliases `mapfile` and `readarray` are added in psm1 and resolve to this cmdlet. Replaces an 86-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashMapfileCommandTests.cs` cover basic stdin, `-n 2`, `-O 5` origin (verify indices 0..4 empty then 5..N populated), `-t` strip, `-d` accepted-but-ignored, `-s 1` skip, custom array name, both `mapfile` and `readarray` aliases, `--help`, empty pipeline, and a Directive-12 injection probe with `$(throw 'PWNED')` as the array variable name — 12 tests total |
| gzip / gunzip / zcat | `InvokeBashGzipCommand` | 4-follow-on | File-mode (de)compressor backed by `System.IO.Compression.GZipStream` (GNU coreutils `gzip`). Reproduces the psm1 oracle byte-for-byte across all six branches: default compress (write `{PATH}.gz`, remove source unless `-k`), `-d` decompress (strip `.gz` suffix, remove source unless `-k`), `-c` to stdout (UTF-8 string for decompress, base64 string for compress — the oracle picked base64 so raw bytes survive PowerShell's string pipeline), `-v` ratio line per file, `-l` listing (emits typed `PsBash.GzipListOutput` PSObjects with `CompressedSize` / `UncompressedSize` / `Ratio` / `FileName` side properties), and `-1`..`-9` compression level (1 → `CompressionLevel.Fastest`, 9 → `CompressionLevel.SmallestSize`, else `Optimal` — exact .NET ladder the oracle used). Alias dispatch matches the oracle's `$MyInvocation.InvocationName -eq 'gunzip'` / `'zcat'` slice via `PSCmdlet.MyInvocation.InvocationName` (gunzip boosts `-d`, zcat boosts `-dc`). Operand resolution routes through `FileSystemHelpers.ResolveOperandPaths` (same SessionState slice cat / checksum / mutators use); a missing path emits a bash-style `gzip: PATH: No such file or directory` via `FileSystemHelpers.WriteBashError` and the cmdlet continues with subsequent operands. `< 1` operands emits `gzip: missing file operand` and returns. **Three colliding flags** declared as explicit `SwitchParameter`s with literal single-letter names so the binder routes the bare token by exact parameter-name match (beats common-parameter prefix match): `-d` prefix-collides with `-Debug` (declared as `D`); `-c` prefix-collides with `-Confirm` (declared as `C`); `-v` prefix-collides with `-Verbose` (declared as `V`). `-f` (force, accepted but no-op past the implicit overwrite of `File.WriteAllBytes`) is also declared as `SwitchParameter F` for symmetry. `-k` / `-l` / `-1`..`-9` have no PowerShell common-parameter prefix collision and stay in `Arguments`, parsed by the manual post-parse scan; bundled short forms (`-dk`, `-cv`, `-9v`, etc.) decode per the oracle's `foreach ($ch in $arg.Substring(1).ToCharArray())` loop. The `-c` compress branch emits a base64 string (one `PsBash.TextOutput`) so `gzip -c FILE \| base64 -d` round-trips identically to the oracle. `--help` delegates to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript` (AOT-safe). Replaces a ~164-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashGzipCommandTests.cs` cover compress + decompress round-trip (source removal + restoration), `-c` base64 emission with source preservation, `-k` keep, `-dc` decompress to stdout, `-l` typed listing output with side properties, `-9` and `-1` level paths (GZipStream round-trip), `gunzip` alias resolution (default `-d`), `zcat` alias resolution (default `-dc`), `--help`, missing-file error continuation, and a Directive-12 injection probe with `$(throw 'pwn').gz` as the operand confirming the literal-path-no-such-file branch (no exception, no output) — 12 tests total |
| ps | `InvokeBashPsCommand` | 4-follow-on | Process lister (GNU coreutils / BSD `ps`). Reproduces the psm1 oracle's flag surface byte-for-byte: `aux` / `-aux` BSD-all, `-e` / `-A` all-processes, `-f` full-format, `-u USER` user filter, `-p PID` single-PID filter, `--sort COL` / `--sort=-COL` descending prefix, `-o COL,COL,...` custom output. Cross-platform enumeration: Linux walks `/proc/[pid]` directly (the oracle's `Get-LinuxProcEntry` slice — stat fields, status Uid, cmdline, tty major/minor decoding, /proc/uptime boot-time math, /proc/meminfo total memory) reimplemented in C# inside the cmdlet; Windows / macOS use `System.Diagnostics.Process.GetProcesses()` plus platform-specific batch metadata lookup — Windows pulls `Win32_Process` CIM rows (CommandLine / Owner / ParentProcessId) through a single parameter-bound `InvokeCommand.InvokeScript` call (AOT-safe; no `ScriptBlock` construction), macOS shells `/bin/ps -axo pid=,user=,ppid=,tty=`. Output: typed `PsBash.PsEntry` PSObjects with the oracle's full property set (PID, PPID, User, CPU, Memory, MemoryMB, VSZ, RSS, TTY, Stat, Start, Time, Command, CommandLine, ProcessName, WorkingSet, BashText). BashText carries the format-mode rendered line — `Format-PsAuxLine` aux format under `-f` / `aux`, `Format-PsCustomLine` custom-column format under `-o`, default `{0,7} {1,-7} {2,8} {3}` PID/TTY/TIME/COMMAND otherwise — all reimplemented in C# with culture-invariant `string.Format`. **Four colliding flags** declared as explicit parameters per the playbook collision table: `-e` prefix-collides with `-ErrorAction` / `-ErrorVariable` → `SwitchParameter E`; `-A` case-folds to `-a` under the case-insensitive binder and prefix-matches the cmdlet's own `-Arguments` → `SwitchParameter A`; `-p` prefix-collides with `-PipelineVariable` / `-ProgressAction` → `string? P` (value-bearing PID); `-o` prefix-collides with `-OutVariable` / `-OutBuffer` → `string? O` (value-bearing column list). `-f` / `-u` / `--sort` / `aux` have no PowerShell common-parameter prefix collision and stay in `Arguments`. **Directive 12:** the `-p` value parses via `int.TryParse` with `InvariantCulture` — a non-integer (literal `$(throw 'pwn')`) silently falls through to no filter, no exception, no eval. The `-o` value is split on comma and each token routed through the column switch's default branch (emits `"?"` for unknown tokens) — no `ScriptBlock` construction, no `InvokeExpression`. Helpers `Get-LinuxProcEntry` / `Get-DotNetProcEntry` / `Format-PsAuxLine` / `Format-PsCustomLine` remain defined in psm1 for any out-of-tree caller but are no longer reached by the cmdlet. Replaces a 215-line psm1 function. Parity tests in `PsBash.Cmdlets.Tests/InvokeBashPsCommandTests.cs` — 12 tests total |

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
| ps | Invoke-BashPs | `-e`/`-A`, `-f`, `-u`, `-p`, `--sort`, `-o` | Binary cmdlet (`-e` / `-A` declared as `SwitchParameter`s `E` / `A`; `-p` / `-o` declared as nullable `string`s `P` / `O`; `-f` / `-u` / `--sort` / `aux` stay in `Arguments`) | No | No |
| sed | Invoke-BashSed | `-n`, `-i`, `-E`, `-e` | Manual loop | Yes | Yes |
| awk | Invoke-BashAwk | `-F`, `-v` | Manual loop | Yes | Yes |
| cut | Invoke-BashCut | `-d`, `-f`, `-c` | Manual loop | Yes | Yes |
| tr | Invoke-BashTr | `-d`, `-s` | Manual loop | Yes | No |
| uniq | Invoke-BashUniq | `-c`, `-d` | Manual loop | Yes | Yes |
| rev | Invoke-BashRev | (none) | Positional | Yes | Yes |
| nl | Invoke-BashNl | `-ba` | Manual loop | Yes | Yes |
| diff | Invoke-BashDiff | `-u` | Manual loop | No | Yes |
| comm | Invoke-BashComm | `-1`, `-2`, `-3` | Binary cmdlet (digit-bundle scan in `Arguments`; no colliding flags) | No | Yes |
| column | Invoke-BashColumn | `-t`, `-s` | Binary cmdlet (manual scan; no colliding flags) | Yes | Yes |
| join | Invoke-BashJoin | `-t`, `-1`, `-2` | Binary cmdlet (manual value-flag scan) | No | Yes |
| paste | Invoke-BashPaste | `-d`, `-s` | Manual loop | Yes | Yes |
| tee | Invoke-BashTee | `-a` | ConvertFrom-BashArgs | Yes | Yes |
| xargs | Invoke-BashXargs | `-I`, `-n` | Manual loop | Yes | No |
| jq | Invoke-BashJq | `-r`, `-c`, `-S`, `-s` | Manual loop | Yes | Yes |
| date | Invoke-BashDate | `-d`, `-u`, `-r`, `+FORMAT` | Binary cmdlet (`-d` declared as value-bearing parameter `D`; `-u` / `-r` / `+FORMAT` stay in `Arguments`) | No | No |
| seq | Invoke-BashSeq | `-s`, `-w` | Manual loop | No | No |
| expr | Invoke-BashExpr | (expression tokens) | Positional | No | No |
| du | Invoke-BashDu | `-h`, `-s`, `-a`, `-c`, `-d` | Manual loop | No | Yes |
| tree | Invoke-BashTree | `-a`, `-d`, `-L`, `-I`, `--dirsfirst` | Binary cmdlet (`-a` / `-d` declared as SwitchParameters; `-I` declared as a value-bearing string parameter; `-L` and `--dirsfirst` stay in `Arguments`) | No | Yes |
| du | Invoke-BashDu | `-h`, `-s`, `-a`, `-c`, `-d` | Binary cmdlet (`-a` / `-c` declared as `SwitchParameter`s; `-d N` as nullable `int? D`; `-h` / `-s` and joined `-dN` stay in `Arguments`) | No | Yes |
| tree | Invoke-BashTree | `-a`, `-d`, `-L`, `-I`, `--dirsfirst` | Manual loop | No | Yes |
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
| tar | Invoke-BashTar | `-c`, `-x`, `-t`, `-f`, `-z`, `-v`, `-C`, `--exclude` | Manual loop | No | Yes |
| yq | Invoke-BashYq | `-r`, `-o` | Manual loop | Yes | Yes |
| xan | Invoke-BashXan | `-d`, subcommands: `headers`, `count`, `select`, `search`, `table` | Binary cmdlet (`-d` is a declared value-bearing parameter `D`; subcommand keyword stays in `Arguments`) | Yes | Yes |
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
| type | Invoke-BashType | `-t`, `-a`, `-p` | Binary cmdlet (`-a` and `-p` are declared SwitchParameters `A` / `P`; `-t` stays in `Arguments`) | No | No |
| command | Invoke-BashCommand | `-v` | Manual loop | No | No |
| source | Invoke-BashSource | (none) | Positional | No | Yes |
| shift | Invoke-BashShift | `N` | Manual loop | No | No |
| realpath | Invoke-BashRealpath | (none) | Positional | No | No |
| mapfile / readarray | Invoke-BashMapfile | `-n`, `-O`, `-s`, `-t`, `-d` | Binary cmdlet (`-O` declared as `int? O`, `-d` declared as `string? D`; `-n` / `-s` / `-t` stay in `Arguments`) | Yes | No |

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
