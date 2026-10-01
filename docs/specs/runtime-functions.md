# Runtime Functions Specification

This document describes the PowerShell runtime module (`PsBash.psm1`) that provides
Unix command emulation for the ps-bash transpiler.

This spec is split across three files:

- **runtime-functions.md** (this file) — runtime architecture, the BashObject model, arg-parsing patterns, escape handling, temp-file strategy, and how to add a new command.
- **[runtime-command-reference.md](./runtime-command-reference.md)** — the per-command flag / arg-parsing lookup table.
- **[runtime-migrated-cmdlets.md](./runtime-migrated-cmdlets.md)** — the REFACTOR-2 binary-cmdlet migration history.

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
3. **PsBash.Cmdlets.dll** (C#) -- **where the commands now live**: ~100 binary
   `Invoke-Bash*` cmdlets, the compiled line-stream cores behind the fused lane,
   `Format-Styled` / `Show-Styled`, and the shared `BashRuntime` helpers.
4. **PsBash.psm1** (PowerShell) -- what did **not** migrate: the BashObject model,
   escape handling, glob expansion, tab-completion registration, the jq/YAML engines,
   `ls` formatting, the `browse` adapter registry, and the job-control commands.

### Where a command actually lives (REFACTOR-2 is essentially complete)

The psm1 is no longer the bulk of the runtime. Of its top-level functions, only
**five** are leaf `Invoke-Bash*` commands (job control) plus two thin PROXIES (below);
every other emulated command is a binary cmdlet in `PsBash.Cmdlets.dll`.

| Still a psm1 function | Why it did not migrate |
|---|---|
| `Invoke-BashBackground`, `Invoke-BashWait`, `Invoke-BashJobs`, `Invoke-BashFg`, `Invoke-BashBg` | Job control owns runspace-pool state (`Get-BashBgRunspacePool`, `$script:` job table) that a stateless cmdlet cannot hold. |
| `Invoke-BashSed`, `Invoke-BashGrep` | **Proxies, not the implementations.** `InvokeBashSedCommand` / `InvokeBashGrepCommand` are the real commands and parse their own argv with the shared ordered parser. The psm1 functions exist only for DIRECT PowerShell calls: the binder rejects a repeated value parameter (`-e A -e B`) and captures colliding bundles (`-ve`) *before* the cmdlet body runs, so each proxy (via `ConvertTo-BashLiteralArgs`) re-delivers every argument as a LITERAL string — splatting a string array never recreates parameter tokens (only a splatted `$args` does) — and the whole argv lands, in order, in the cmdlet's `Arguments`. A PowerShell comma-list after `-e`/`--regexp` (`-e a,b`) becomes repeated `-e`. **No flag is interpreted in the proxy** (the old ones split `-ftemplate.txt` at its `e` and read `grep -- -e f` as an option). They have no `[CmdletBinding()]` on purpose — common parameters would prefix-match `-e`. |

A handful of cmdlets still call **back into** psm1 helpers via parameter-bound
`InvokeScript` (`jq`/`yq` reuse the psm1 jq filter + YAML engines; `alias`/`trap` reach
`$script:`-scoped state; `browse` uses the psm1 adapter registry). Those are delegation
seams, not psm1 implementations of the command.

Consequence for the tables that follow: **every row in
[runtime-command-reference.md](./runtime-command-reference.md) is a binary cmdlet
unless the row says otherwise.** An "Arg Parsing" cell reading `Manual loop` /
`Positional` describes the *strategy* inside the cmdlet, not a psm1 function.

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

**Binary cmdlets do not call `Write-BashError`.** They report through
`FileSystemHelpers.WriteBashError`, which emits exactly ONE `ErrorRecord` on the
cmdlet's error stream (and sets `$LASTEXITCODE`). PowerShell redirection then
decides its fate — `2>/dev/null` discards it, `2>&1` merges it into stdout as its
one-line message — and the host (`SdkWorker`) streams the surviving records to
stderr **inline, in order with the surrounding stdout** (via `Streams.Error`
`DataAdded`), not after the run. Calling `Write-BashError` as well used to print
every diagnostic twice and made it immune to `2>/dev/null`, because Bash mode
writes through `$Host.UI.WriteErrorLine`. `Write-BashError` remains only for the
psm1 helpers (`Get-BashItem`, `Read-BashFile*`) and the job-control functions.

**Diagnostics name operands as typed.** GNU quotes the argv element, never the resolved full path
(`cat: nosuchfile: No such file or directory`). Every diagnostic goes through
`FileSystemHelpers.WriteStderr`, which calls `OperandDisplay.Rewrite`: an operand resolved through
`FileSystemHelpers.ResolveOperandPaths` / `ResolveOperands` was remembered (resolved path -> text as
typed, so `./x` stays `./x`), and any other path below the working directory loses the directory
prefix (relative, forward slashes). A reader therefore just formats `{cmd}: {path}: {strerror}` with
whatever path it holds; the wording is per tool (head/tail `cannot open 'x' for reading`, tac `failed
to open`, sed `can't read x`, rev `cannot open x`, strings `'x': No such file`, awk gawk-style
`fatal: cannot open file \`x' for reading`, gzip -d names `x.gz`). The strerror text comes from
`FileSystemHelpers.ReadErrorMessage` (also maps Windows' invalid-name error for an unmatched `*`/`?`
to "No such file or directory"). Known gap: GNU shell-quotes names with special characters
(`cat: '*.zz': ...`). Tests: `ReaderOperandDisplayTests` (table over the commands, GNU 9.4 oracle).

### Migrated Binary Cmdlets

Leaf `Invoke-Bash*` commands are progressively migrated from psm1 functions to
binary cmdlets in `PsBash.Cmdlets.dll`. The full per-command migration table
(phase, flag surface, colliding-flag handling, security probes) and the
awk / jq / find / echo / ls / sed migration decisions are in
**[Migrated Binary Cmdlets](./runtime-migrated-cmdlets.md)**.

### Output Strategy

```
Source produces text with \n  →  Emit-BashLine  →  one BashObject per line in pipeline
Source produces typed object  →  New-BashObject  →  one typed object in pipeline
Filter receives items        →  pass the original objects through (preserves type)
Transformer receives items   →  fresh text objects (BashRuntime.TextRecord)
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

## Pipeline record kinds (filters vs transformers)

**Producers keep their typed objects** (`ls` -> `PsBash.LsEntry`, `find`, `ps`, `stat`, `du`,
`date`, `ping`, ...). What a **consumer** emits depends on what it does to a line. The rule is
about identity, not about "text vs objects":

| Kind | Definition | Emits | Commands |
|---|---|---|---|
| **Filter** | every output line IS one of its input lines (selected, reordered, deduplicated, passed as-is) | the **original upstream object** | `grep` (plain match), `rg` (plain), `head`/`tail` (`-n`), `sort`, `uniq` (plain, `-d`, `-u`, `-D` — each run member is emitted as its own original object), `tac`, `shuf`, `cat` (no flags), `tee`, `less`, `more` |
| **Transformer** | the line's text changes | a **fresh text record** (`BashRuntime.TextRecord`: a bare string, or a `NoTrailingNewline` object), never the upstream object, whose type no longer describes the text | `sed`, `tr`, `cut`, `awk`, `rev`, `nl`, `fold`, `expand`, `unexpand`, `paste`, `join`, `comm`, `strings`, `base64`, `grep -o/-n/-H/context`, `uniq -c`, `cat -n/-b/-E/-T`, `head -c`/`tail -c` |

`ls | grep .txt` therefore still yields `PsBash.LsEntry`; `ls | cut -c1-3` yields plain text. A
mode of a filter command that rewrites the line (`grep -n`, `cat -n`, `uniq -c`) is a
transformer for that invocation. Only a command that operates on the object itself (mutates file
attributes, say) may pass a *modified* object on; none of the bash commands do.

Sources emit one object per line via `Emit-BashLine`; `printf` is the exception and emits ONE
multi-line object. A consumer that receives a multi-line BashText item splits **that item** into
text lines and passes single-line items through unchanged (never flatten the whole input: that
destroys the typed objects of a filter).

### Line terminators must survive the object choice

A record renders as `BashText` + `\n` unless it carries `NoTrailingNewline` (`printf`,
`echo -n`: the bytes are exact). The one definition of "unterminated" is
`BashRuntime.IsUnterminated(item)`: flag set **and** text not already ending in `\n`
(`printf '%s\n'` embeds its terminator). A multi-line record is unterminated only on its LAST
line. GNU tools differ on the missing final newline (oracle: `printf 'b\na' | cmd | od -c`):

| Copy it through (`b\na`) | Always terminate the last line |
|---|---|
| head, tail, cat, tee, rev, tr, sed, less, more, `head -c`, `tail -c` (exact slice), `fold`, `expand`, `unexpand` | grep, sort, uniq, shuf, cut, nl, awk, `uniq -c`, `strings`, `paste`, `join`, `comm`, `column` |
| `tac` glues: `ab\n` (the unterminated record is emitted first, keeping its flag) | `base64`: bytes in = bytes out (`printf 'b\na' \| base64` is `Ygph`, not `YgphCg==`); `-w0` writes no final newline; `-d` writes the decoded bytes exactly |
| `split` writes no stdout; its LAST piece file copies the input's missing final newline (`printf 'a b\nc d' \| split -l1` leaves `xab` = `c d`) | |

Group 2 audit (oracle-checked): `column -t`, `xargs` (its own stdout; the child's objects pass through
unchanged, exactly as the child emitted them), `xan`, `yq` already terminate like GNU/jq and emit fresh
text. `jq` is a transformer: fresh text; new `-j`/`--join-output` (no newline after each result, exact
bytes), `-R`/`--raw-input` (each line a string; `-Rs` = the exact bytes as one string) and bundled
short flags (`-Rr`). `diff` tracks the unterminated last line of each file: it compares unequal to the
same text with a newline and is followed by GNU's `\ No newline at end of file` line (normal, `-u`, `-c`).

Group 3: `tail -c N FILE` now uses `ByteSliceRecords` like the pipeline form and `head -c FILE` (it
used to terminate every line: `printf 'a\nc' > f; tail -c 1 f` printed `c\n`, GNU `c`). `uniq -D` used
to print the run's FIRST line N times as fresh text; it now emits every member's original object
(differs with `-f/-s/-w`). Fused `cat FILE` needed no change: `CatFileStage` declines a file without a
final newline, so the real cmdlets run and the bytes match GNU (`cat u1 u2` concatenates raw bytes,
`cat u1 | head -n5` adds no byte); pinned by `Group3_FusedCatFile_*`.

`BashRuntime.RecordLines(item)` is the one record splitter for transformers (each line + whether
it is the unterminated last line); file readers use `BashFileSystem.ReadTextLines`
(`HasTrailingNewline`) for the same flag.

Helpers (all in `BashRuntime`, never re-derive): `IsUnterminated`, `TextRecord(text,
unterminated)` (fresh text; the last piece of a split record inherits the flag),
`PassTerminated(item)` (the original object minus a stale flag, for filters that always
terminate: `sort`/`shuf` can move a flagged record anywhere), `ByteSliceRecords(slice)`
(`head -c`/`tail -c`), `RecordStreamText(items)` (the byte stream of a record sequence). The sed
rewrite (R21, `printf 'x.y\n' | sed ...; echo Z` printing `x-yZ`) is the bug class: a flag copied
onto text that no longer means "no newline".

Tests: `PipelineRecordKindTests` (per-command bytes vs GNU, filter keeps `LsEntry`, transformer
emits text).

The fused lane (`Invoke-BashFusedPipeline`) and `Invoke-ProcessSubPipeline` are unaffected: the
fused lane only ever carries strings (its allowlist has no typed producer), and the process-sub
route hands the producer's objects to a consumer that follows the rules above.

## Memory / streaming

Retained memory is bounded by the working set, not the stream length, on these paths (each has a
test that asserts streaming by ORDER or a retained-count seam, not a memory number):

| Path | Bound | Test |
|---|---|---|
| Host output/error collection (`SdkWorker`) | drained by removal (`ReadAll`), so the collection holds only what arrived since the last drain; retained output = batcher (32 KB) + format buffer. `PeakRetainedOutput` is the seam | `SdkWorkerBatchingTests.OutputCollection_DoesNotRetainProcessedItems` |
| `seq` (cmdlet and fused core) | lazy `IEnumerable`; operand parsing / zero-increment errors stay eager. `seq 1 100000000 \| head -n 1` returns at once. (`-s SEP` still yields ONE joined record by definition.) | `SeqStreamingTests` |
| `> file` / `>> file` (`Invoke-BashRedirect`) | target opened in `BeginProcessing` (truncate/append, created even when empty, a missing directory fails before the command runs), each record written through a 64 KB-buffered stream. Matches bash ordering: `cat f > f` leaves `f` empty | `RedirectStreamingTests` |
| `tail` on a pipe | `-n +N` / `-c +N` stream (skip, then pass through); `-n N` is a lazily-grown ring of N records; `-c N` is a ring of the last >= N bytes of record text (O(N + one record)). Filters still pass original objects | `TailStreamingTests` |
| `awk` on stdin | BEGIN in `BeginProcessing`, each record fed to the machine in `ProcessRecord`, END in `EndProcessing`. **Only a program with a main-input `getline`** (plain `getline [var]`, or a literal `getline < "-"` / `"/dev/stdin"`; decided statically, `AwkProgram.UsesMainInput`) **buffers stdin** (O(input)) and runs BEGIN, the main loop and END as a pull loop in `EndProcessing`, because that getline pulls the next record from inside a rule and the cmdlet is push-driven; output stays on the cmdlet thread, no worker thread. `getline < file` and `cmd \| getline` programs keep streaming. File operands are pulled lazily through `AwkMainInput` (one file open at a time). Output redirections are bounded too: `> file` streams through a 64 KB buffer, `print \| cmd` data goes to a temp file (disk, not memory; the command's own output is collected in memory at close), and awk's own stdout is held only while an output pipe is open, capped at 64K characters (`AwkStdout`) | `AwkStreamingTests`, `AwkGetlineTests` |

Still O(input) by nature: `sort`, `tac`, `uniq -c` group state, `tail`'s rings (O(N)), and any
consumer that must see every line before emitting.

## Command Reference

The full per-command table — `Invoke-Bash*` function, key flags, arg-parsing
strategy, and pipeline / file support — is in
**[Command Reference](./runtime-command-reference.md)**.

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

## Shared argument parser (`PsBash.Cmdlets.Args`)

The ordered getopt-style parser that replaces per-cmdlet hand scans. Pure and AOT-safe (no
`PSCmdlet`/`SessionState`/reflection), so it is unit-tested in isolation
(`ArgParserTests`). **Migrated so far: `tee`, `cp`, `mv`, `rm`, `mkdir`, `rmdir`, `ln`, `touch`, `head`, `tail`, `wc`, `cat`, `tac`, `nl`, `uniq`, `fold`, `expand`, `unexpand`, `paste`, `join`, `comm`, `split`, `strings`, `base64`, `stat`, `file`, `cut`, `sort`, `grep`, `sed`, `rg`, `find`, `ls`, `du`, `tree`, `column`, `gzip`, `tar`, `md5sum`, `sha1sum`, `sha256sum`; plus the bash BUILTINS `echo`, `printf`, `test` with their own scanners (batch 6 below).** `BashRuntime.ConvertFromBashArgs` is
untouched — its contract (unknown flag becomes an operand) differs.

**API** (`src/PsBash.Cmdlets/Args/`):

- `OptSpec(Id, Short, Long, Kind)` with `OptKind { Flag, Value, OptionalValue }`; several specs
  may share an `Id` (`-r`/`-R` = "recursive"). `Short` is `'\0'` for none; `Long` has no `--`.
- `OptSpecSet` — built ONCE as `static readonly`: the specs, `validButUnsupported` names (as typed:
  `"-i"`, `"--interactive"`), `allowAbbrev` (getopt_long unique-prefix long options; an ambiguous
  prefix is an error listing candidates in GNU's `long_options[]` TABLE order, not alphabetical:
  `--re` in cp is `'--recursive' '--remove-destination' '--reflink'`. Pass `longOptionOrder:` (the full
  long-name order, read from the oracle by probing `cmd --<letter>`); names it omits follow in
  declaration order: specs, then valid-but-unsupported, then `help`/`version`; candidates that name the SAME option — same Id and kind, e.g. grep `--colo` = color/colour, `--fixed` = fixed-regexp/fixed-strings — are not ambiguous, as in getopt_long — so NEVER group unrelated options under one catch-all id; `OptSpecSharedIdGuardTests` fails any id with 2+ long names that is not an oracle-verified alias in its allowlist), `numericShorthandId` (`head -5`), `bundleDigitsId` (grep's `-NUM`: a digit run anywhere in a bundle — `-5`, `-1n`, `-n12` — is one value option; each argv element's digits are ONE number), `gnuInfoOptions`
  (`--help`/`--version` join abbreviation, so `--ver` is ambiguous with `--verbose`).
- `ArgParser.Parse(ReadOnlySpan<string> argv, OptSpecSet)` → `ParsedArgs`: `Tokens` in ORIGINAL
  order (`ArgTokKind` Operand/Option/DoubleDash; a bundle `-abc` is one token per letter), the
  first `Error` (`Unrecognized`, `ValidButUnsupported`, `MissingValue`, `Ambiguous`,
  `UnexpectedValue`), and `Has/Last/All/Operands()`. Scanning stops at the first error.
- Errors are classified DURING the scan, so nothing after `--` is ever an option or an error;
  a lone `-` is an operand; a value option ends its bundle (`-abn5`) and, like getopt, takes the
  next element even if it starts with `-`; options may follow operands (GNU permutation).
- `ArgError.Message(cmd)` holds the GNU wording; `FileSystemHelpers.TryWriteParseError` is the
  cmdlet-side sink (message + `ParsedArgs.ErrorExitCode`).
  **Exit status is per command**: `OptSpecSet(usageExitCode:)` (default **1** — GNU coreutils
  `EXIT_FAILURE` for tee/cp/mv/rm/mkdir/rmdir/ln/touch; set 2 for ls/grep/diff-style tools) covers
  unknown option, missing argument, ambiguous prefix and `--flag=x` on a flag. A valid-but-unsupported
  option (ps-bash's own refusal of a real GNU flag) is always **2** (`ArgError.UnsupportedExitCode`),
  so scripts can tell "ps-bash cannot do this" from "you typed it wrong". `TryHandleInfoOptions` acts on an abbreviated
  `--vers`/`--he`.

**Emitter opt-in.** `PsEmitter.OrderedArgCommands` (tee, cp, mv, rm, mkdir, rmdir, ln, touch, head, tail, wc, cat, tac, nl, uniq, fold, expand, unexpand, paste, join, comm, split, strings, base64, stat, file, cut, sort, grep, sed, rg, find, ls, echo, printf, test, du, tree, column, gzip, tar, md5sum, sha1sum, sha256sum, awk, plus the command-running wrappers xargs, time, env, command, bash — these keep their manual scans, which stop at the first operand, and only take the emitter quoting so the INNER command's flags are safe): for these, `EmitPassthrough`
single-quotes EVERY dash-leading literal word and `--` (via `PsBuild.SingleQuote`; quoted and mixed
words like `--x="a b"` collapse to one literal). No flag is then a PowerShell parameter token, so
each reaches `[ValueFromRemainingArguments] Arguments` verbatim and in order — no prefix collision
(`-i`/`-e`/`-p`), no binder-swallowed `--`, no decoy losing position. Dynamic words (`-$x`) keep the
normal path; the RC-7 unquoted-variable splat path shares the same arg renderer. Add a command to
the set only AFTER its cmdlet is migrated, and add it to
`CommonParameterCollisionGuardTests.EmitterForceQuoted` (all colliding letters).

**Decoy caveat (direct calls).** The cmdlets keep their single-letter decoy switches
(`Invoke-BashTee -a f`, `Invoke-BashCp -v a b` — Pester and interactive PowerShell bind them, the
transpiler never does). Re-inject them with `BashRuntime.PrependDecoys(Arguments, ...)` BEFORE
`Parse`; prepending is safe because a decoy can only bind ahead of any `--`. A direct call with a
colliding bare letter that has NO decoy (`Invoke-BashTee -i`) still fails in the binder — quote it.

**Also in `Args/`:** `GnuNumber.TryParse` (head/tail `NUM`: sign, digits, multiplier suffix `b k K m M G T ... kB MB KiB`; lowercase only b/k/m; saturates at int.MaxValue).

**Batch 3a (head, tail, wc, cat, tac, nl, uniq) and the fused lane.** Each cmdlet exposes `internal static <Cmd>Args Plan(string[])` (ScanArgs + value validation: NUM, `-s`, nl style/format/width, uniq `-f/-s/-w`/METHOD) and the compiled line-stream core (`LineStreamRegistry.TryCreate`) calls it FIRST, then applies its own narrower certification — so a core can never accept an argv its cmdlet rejects (`LineStreamArgAgreementTests`). `FusedLane.StageIsUnbounded` treats every `--follow` abbreviation as unbounded (`IsFollowSpelling`), since `tail --fo` now parses as follow. The emitter builds the `-Stages` argv from static values, so single-quoting in the fallback text never reaches the cores. head/tail declare `digitOptionWording:` so a digit in option position (`-q5`, `-12x`) is GNU's `invalid trailing option -- N` / `option used in invalid context -- N` (`ArgErrorKind.MisplacedDigit`), and rewrite the obsolete first argument (`-NUM[bkmlcqvz]*` for head, `[-+]NUM[bcl][f]` for tail, with tail's arity rule) into ordinary options BEFORE the scan (`TryExpandObsoleteNum` / `TryExpandObsolete`). Legacy ps-bash extensions kept (Pester-pinned): a bare leading number is the count for head/tail (`head 5`, `tail 5`).

**Batch 3b (fold, expand, unexpand, paste, join, comm, split, strings, base64).** Same `Plan(argv)` / `ScanArgs` seam per cmdlet (pure, tested by `<Cmd>ArgScanTests`); end-to-end behavior incl. direct-call decoys in `Batch3bArgBehaviorTests`. Usage errors exit **1** (GNU coreutils; `strings` = binutils, also 1); valid-but-unsupported exit 2. Notable rules: counts/widths are decimal-only where GNU says so (`fold -w`, `base64 -w`, `strings -n`, `split -a/-l`), GNU SIZE suffixes only where GNU allows them (`split -b` via `GnuNumber`); `expand`/`unexpand` `-t` goes through `TabStopList` (uniform N, ascending list, last element `/N` or `+N`, several `-t` concatenate; past the last plain stop `expand` emits one space and `unexpand` converts nothing); `unexpand -t` implies `-a`, `--first-only` overrides it in any order, obsolete `-NUM` does not imply `-a`; `paste -d LIST` cycles single-character delimiters per column (serial: per line, restarting per file) with `\n \t \r \b \f \v \\ \0` escapes, and a `-` operand shares ONE stdin cursor (`paste - -` pairs lines); `join -o/-e` and `comm --check-order/--nocheck-order` are refused (they were silently ignored / swallowed); `comm --output-delimiter=STR` is implemented (an empty STR is NUL, as GNU 9.4). Value decoys (`fold -w`, `split -a`, `base64 -w`, `paste -d`, `join -a/-v`) are re-injected as `-x VALUE` pairs by each cmdlet's `ArgsWithDecoys`. 
**Batch 4 (stat, file, cut, sort).** Same `Plan`/`ScanArgs` seam (`<Cmd>ArgScanTests` + `CutGnuBehaviorTests`, `SortGnuBehaviorTests`). Usage errors exit **1** for stat/file/cut (GNU; `file` prints its usage text) and **2 for sort** (GNU `SORT_FAILURE`; `--sort=`/`--check=` argmatch errors are 1, `-c` disorder 1); valid-but-unsupported exit 2. Notable rules and fixes (all oracle-checked):
- **stat**: `-c/--format`, `--printf`, `-t`, `--cached=MODE` (validated, no effect); the LAST of `-c/--printf` wins and a format beats `-t`; dangling `-c` and unknown long options are errors (they were file names); `-L`/`-f` refused.
- **file** (file-5.45, its own getopt table): `-i` is `type; charset=ENC` (was type only), new `--mime-type`/`--mime-encoding`, `-F`, `-0`, `-h`/`-L` (GNU default no longer follows symlinks), `empty`/`directory`/`Unicode text, UTF-8 text` classifications; `-n -p -S` accepted no-ops, `-N` disables name padding; no operand is a usage error.
- **cut**: `CutPlan` is the one per-line engine (cmdlet + fused `CutStage` share `Plan` and `Apply`). The list is sorted/merged (overlap only, adjacent ranges stay separate for `--output-delimiter`), a delimiter-less line prints whole in field mode (even under `--complement`), `-b` counts UTF-8 bytes, `-c` counts characters (GNU 9.4 also counts bytes: deliberate, documented divergence for non-ASCII), `--complement`, `-n` ignored, `-d` one character and fields-only, `-s` fields-only, one list only, GNU list errors; `-z` refused.
- **sort**: `SortEngine` is shared by the cmdlet and the fused `SortStage` (which now certifies every argv `Plan` accepts). GNU key semantics: per-key options `b d f g h i M n r V` REPLACE the globals (a key with its own option does not inherit `-r`), keys are positional (`-k1.2,1.3` = characters 2-3 of the line), `-n` reads `-?digits[.digits]` (not `+`), `-g` a float prefix (text sorts below numbers), `-h` a number plus 1024-based unit, `-V` = dpkg `verrevcmp` (+ dot names first; no `.tar.gz` suffix stripping), `-u` keeps the first line of each run and uses no last-resort compare, `-c` uses the full comparison (`-cu`: equal is disorder), `-m` is a real k-way merge, `-i`, `-C`, `--check[=..]`, `--sort=WORD` (abbreviations). `-S`/`--buffer-size`, `-T`, `--parallel=N`, `--batch-size=N`, `--compress-program` are validated and ACCEPTED AS NO-OPS (they tune how GNU sorts, not what it prints; this sort never spills). Refused (exit 2): `-R`/`--random-sort`/`--random-source`, `-z`, `--debug`, `--files0-from`. An unreadable input is fatal (`sort: cannot read: F: ...`, exit 2, no output); `-` reads stdin; multi-character `-t` is an error; `-t '\0'` is NUL.
**Batch 5 (grep, sed, rg, find: the search/edit tools).** Same `Plan`/`ScanArgs` seam. All four are on `OrderedArgCommands`, so the whole argv arrives single-quoted and in order; `Invoke-BashGrep`/`Invoke-BashSed` keep a psm1 proxy ONLY for direct PowerShell calls (see "Where a command actually lives": it forwards literal strings, it interprets nothing).
- **grep** (GNU grep 3.11; `GrepArgScanTests`, `GrepGnuBehaviorTests`; usage errors exit **2**): repeated `-e`/`-f`/`--include`/`--exclude`/`--exclude-dir`/`--exclude-from`, `-ie PAT`/`-ePAT`/`-fFILE` bundles, `-A/-B/-C N` and `-NUM` (A/B beat C/NUM in any order; `-12 -3` is 3, not 123), unique-prefix long options in GNU table order, `--color[=WHEN]` (accepted, never colours; a bad WHEN is exit 2), `-y` = `-i`, `-V`, `--line-buffered`/`-U` accepted no-ops, `-l`/`-L` and `-h`/`-H` last-wins. Two DIFFERENT matchers among `-E -F -G -P` are `grep: conflicting matchers specified` (exit 2; the oracle refuses them, it is not "last wins"). Bad context length `grep: X: invalid context length argument` and bad `-m` `grep: invalid max count` (exit 2; a negative `-m` means unlimited). A missing `-f`/`--exclude-from` file is fatal (exit 2). Fixed on the way: context lines use `-` (`2-b2`, `f:2-b2`), non-adjacent groups (and every later file's first group) are divided by `--`, printed whenever ANY context option was given (even `-A0`), `-o` prints no context lines, and `-H` on stdin is `(standard input):` (was `<stdin>:`). The fused `GrepStage` calls `Plan` first (`LineStreamArgAgreementTests`). Still refused (exit 2): `-z -Z -a -b -d -D -T -u -I`, `--label`, `--binary-files`, `--group-separator`, `--no-group-separator`, `--null*`, `--text`. The old raw-line `-E` scan is gone: it fired on the proxy's own `-E $patternArr` call and silently turned every `-e` search into ERE.

- **sed** (GNU sed 4.9; `SedArgScanTests`, `SedGnuBehaviorTests`; usage errors exit **1**): repeated `-e`/`-f` kept in command-line order, `-ne`/`-nE`/`-i.bak` bundles, unique-prefix long options (`--s` = silent, sandbox, separate), `-s`, `-z`, `-l N`, `-u -b --posix --sandbox --follow-symlinks` accepted no-ops, `--debug` refused. `-i`'s suffix is ATTACHED only (`-i -e x` = no suffix, `-in` = suffix `n`) and now makes a real backup (`NAME+SUFFIX`; `*` = the base name, `-ibak/*.o`). Script errors (unknown command, bad `s`, bad `y`) exit 1 (were 2), a missing `-f` file is `couldn't open file` exit 4, `-i` without a file `no input files` exit 4, an unreadable operand exit 2 with the rest still processed. Fixed on the way: several files are ONE stream (line numbers and `$` span them; `-s`/`-i` separate them), a missing final newline of an earlier file is supplied between files, an empty file prints nothing, `-` reads the pipeline, `-e 'a\' -e text` joins into one command, `#n` first line = `-n`, `-z` works, a file whose lines all vanish is written empty. The psm1 `Invoke-BashSed` proxy no longer bundles `-e`/maps `-E`; the raw-line `-e`/`-E` scans are gone. The fused `SedStage` calls `Plan` first.

- **rg** (ripgrep 14.1 rules, NOT GNU getopt; `RgArgScanTests`, `RgBehaviorTests`; usage errors exit **2**): no long-option abbreviation, attached short values (`-A2 -g*.rs -epat`), counted `-uu`, `--color WHEN` with a REQUIRED value, last of `-i/-s/-S` wins, `-A/-B` beat `-C`, repeatable `-g` (with `!negation`) and `-e` (OR; makes every operand a path). ripgrep options the internal engine does not run (`-t -m -j --json -P -r -f -q -H ...`) are refused with exit 2 instead of being silently dropped (the old scan ignored every unknown bundle letter). Exit status is now ripgrep's (0 match / 1 none / 2 error; it was always 0). **Native passthrough** (`PSBASH_RG_NATIVE`): runs first and gets the argv verbatim and in order (decoy-bound switches of a direct call are re-injected in front), so every ripgrep flag works there and the internal engine's refusals never apply. No psm1 proxy: `E` joins the `I V C W O A B` decoys, and a REPEATED bare `-e` typed at PowerShell still hits the binder (quote the flags).

- **find** (GNU find 4.9; `FindGnuBehaviorTests`; errors exit **1**): NOT getopt — `[-H|-L|-P] [-D opts] [-Olevel] [PATH...] [EXPRESSION]`. On `OrderedArgCommands` the cmdlet receives the WHOLE expression verbatim and in order (the infix `-o`/`-a`, `!`, `\( \)`, the `-exec` argv), which removed `PsEmitter.FindForceQuoteFlags` and `FindExecArgvIndices`; the cmdlet has no binder-bound parameter, so there are no decoys. The scan now separates leading options, the path list and the expression (a word after the expression began is `paths must precede expression`, it used to become a second search root), reports a value predicate without its argument as `missing argument to `-name'` (also `-exec` without its terminator), validates `-maxdepth`/`-mindepth`/`-type` (`-type f,d` = any of) and words `unknown predicate` like GNU.

**Batch 6 (echo, printf, test / `[`, ls).** The first three are bash BUILTINS, so they follow bash, NOT getopt — GNU semantics would be wrong for them. They still ride the emitter opt-in (every dash word single-quoted, arrives verbatim in `Arguments`) but each has its own pure scanner, unit-tested against `wsl bash` 5.2:
- **echo** — `Args/EchoArgScan`: only a LEADING run of words matching `-[neE]+` are options (`-n` sticky, `-e`/`-E` last-wins, xpg_echo off); the first other word (`-x`, `--`, `-n-`, `--help`, `-`, `-e-n`) is printed literally and ends option parsing. No `--` handling, no abbreviations, no errors, no `--help`. Direct PowerShell calls keep the `E` decoy (case recovered from the invocation line).
- **printf** — `Args/PrintfArgScan`: `-v VAR`/`-vVAR` assigns the caller-scope variable AND `BashVariableStore` (like `read`; `NAME[i]` sets one element of the PS array) instead of printing; `--` ends options; a lone `-` is the format; any other dash-led first word is `printf: -X: invalid option` (X = its first letter; `--help`/`---` report `--`) + `printf: usage: printf [-v var] format [arguments]`, exit 2; `-v` with no name, or no format left, are usage errors (exit 2); an invalid identifier is `` `NAME': not a valid identifier ``, exit 2. A leading `--help` also prints the builtin help on stdout first (bash does). Format and arguments after the options are verbatim (`printf '%s\n' -n -v --` prints them). `V` decoy for a direct bare `-v`.
- **test / `[`** — there are NO options: every word is part of the expression. `BashTestExpr` is a port of bash `test.c` (argument-count shapes for 0..4 words, then `-o` > `-a` > `!` > primary with `( )`; `-n`/`-z`/file ops/`-v`/`-t`; `= == != < >`, `-eq…-ge` with `integer expression expected`, `-nt -ot -ef`; errors `a: unary operator expected`, `x: binary operator expected`, `too many arguments`, `syntax error: `-n' unexpected` for an option-shaped leftover, `argument expected`, `` `)' expected``, exit 2). A trailing unary operator with no operand is a plain word. 276 oracle rows in `BashTestExprOracleTests`. `[` (alias) requires and strips the final `]`; `test` keeps a trailing `]` as an ordinary word. File predicates resolve relative to the PowerShell location; `-w` never creates a missing file; `-L`/`-h` see directory links. Direct-call decoys (`E D W A O`) are re-placed in expression position from the invocation's AST (`RebuildWithDecoys`), falling back to the head. The emitter's own `[ ]` translation is bash-correct for arity-3 lists (the middle operator wins: `[ -f = -f ]`) and runs `Invoke-BashTest` for shapes it does not model (`\( \)`, `-o NAME`) instead of degrading to false.
- **ls** — `LsSpec` on the shared parser (GNU 9.4 long-option table incl. abbreviations and ambiguity order; usage errors exit **2**, but an invalid `--color`/`--classify`/`--sort` word is exit **1** with GNU's "Valid arguments are:" block). Implemented: `-a -A -l -h -R -S -t -r -1 -p -d -F -i -s`, `--all --almost-all --human-readable --recursive --reverse --directory --size --inode --classify[=WHEN] --color[=WHEN] --sort=size|time --group-directories-first`. Conflicts are last-wins by walking the tokens (`-tS` vs `-St`, `-pF` vs `-Fp`, `--classify=never`, `--color=never`); `-1` never cancels `-l` (GNU: `-l1` and `-1l` are both long). `-i` prints the inode (`FileIdentity.TryGetInode`: NTFS file index on Windows, `stat %i` on Unix) and `-s` the allocated size in 1 KiB units (`BlocksK`: whole 4 KiB allocation units, dir 4, symlink 0, empty file 0 — the true figure is filesystem-specific), both right-aligned per block and placed before the mode column. Every other GNU option is valid-but-unsupported (exit 2) — they change the output (columns `-x -C -m`, quoting `-Q -b`, `-Z`, `--full-time`, other sort keys `-U -u -c -v -X`, `--sort=none|extension|version|width`...) — rather than becoming file operands. `ls nosuch` reports through the cmdlet's own error stream (`2>/dev/null` works). **Layout** (GNU 9.4, oracle-checked in `LsGnuLayoutTests`): `-a` lists `.` and `..` (typed `LsEntry` objects named `.`/`..`; `-A` does not); non-directory and `-d` operands print first as one block, then each directory operand is its own section; a section gets a `dir:` header when more than one operand was given or `-R` is on, sections are separated by a blank line, and `-l`/`-s` sections start with `total N` (1 KiB blocks of the listed entries incl. `.`/`..`; `total 16K` under `-h`); `-R` recurses depth first in listing order, child headers are `parent/child` (no doubled slash), symlinked directories are not followed. `total`, headers and blank lines are bare text records; entries stay `PsBash.LsEntry`. Known gaps: `-l` columns are not width-aligned per listing (size is padded to 8), link count is always 1.

**Batch 7 (du, tree, column, gzip, tar, md5sum/sha1sum/sha256sum).** Same `Plan`/`ScanArgs` seam (`<Cmd>ArgScanTests`; `ChecksumBehaviorTests`, tar behaviour tests in `InvokeBashTarCommandTests`). Usage-error exit status is per tool and oracle-checked: **1** for du, column (util-linux), gzip and the checksums; **64** (EX_USAGE) for tar *parse* errors while tar's *semantic* errors (no mode, two modes, empty archive, bad `--strip-components`) are **2**; tree exits 1. `tree` is the Steve Baker package and is NOT installed in the WSL oracle, so its table follows tree(1) 2.1 (documented divergence risk). Valid-but-unsupported is always 2. Notable rules and fixes:
- **du**: the per-letter bundle decoder used to swallow every unknown letter (`du -z d` ran) and ignored a dangling/non-numeric `-d`; now GNU usage errors. `-k -m -b` size units (the last of `-h/-k/-m/-b` wins), `-x -l --apparent-size` accepted no-ops, `-a` with `-s` and `-s` with a non-zero `-d` are GNU's usage errors, `-d -1` = unlimited; `-B -D -H -L -P -S -t -X -0 --si --time --time-style --inodes --files0-from --exclude-from` refused.
- **tree** (not getopt_long: long options do not abbreviate): `-L` below 1 / non-numeric is "Invalid level, must be greater than 0." (exit 1), `-I` joined/repeated/`|` alternatives, every directory operand is walked (only the first was), `-n` no-op; the rest of tree's option set refused.
- **column** (util-linux 2.39): `-s` is a SET of delimiter characters, `-o/--output-separator`, `-l/--table-columns-limit`, `-L/--keep-empty-lines` implemented; blank lines dropped unless `-L`; a short row is completed with empty cells (`x      y      `, it used to end early); ambiguity lists in util-linux table order (incl. `--columns`, `--table-*`, `--tree*`). Non-table mode stays a passthrough (util-linux fills terminal-width columns: divergence).
- **gzip**: the `long_options[]` order is the ambiguity order (`--s` = '--stdout' '--silent' '--synchronous' '--suffix'); `-h/-H/-V/-L` info options (`-L` used to compress silently), `-m/-M` accepted, dangling `-S` an error, `-10` = `-1` then the invalid `-0`; `-Z/--lzw/-b/--bits` refused. gunzip/zcat aliases unchanged.
- **tar**: every tar 1.35 long option is registered (per-letter `--X` oracle lists concatenated = argp order) so abbreviations and ambiguity match; everything not implemented is valid-but-unsupported. **Old-style first word** (`tar czf a.tgz d`, `xvf`, `cfC a.tar d f`) is normalised by `NormalizeOldStyle` before the scan (value letters `b C f F g H I K L N T V X` consume the following words in order; a bound direct-call decoy disables it). **`-C` is positional** and now works on create (`tar cf a.tar -C d f` archives `f`). Direct-call decoy `C` is `-c` when no mode is on the line, else `-C` with the first operand as DIR (Pester's `tar -xf $a -C $out`); from the transpiler every flag is single-quoted, so `-c` vs `-C` is case-exact.
- **md5sum/sha1sum/sha256sum** (`ChecksumEngine`): silent wrong output fixed — the printed name was the resolved ABSOLUTE path (now as typed; `/` for `\` on Windows; POSIX names with `\`/newline are `\`-escaped like coreutils), `printf x | md5sum` hashed `x\n` (stdin is now `RecordStreamText`: exact bytes), `--tag` and `-z` (NUL, no newline) were missing. Check mode follows coreutils 9.4: text/binary/BSD-tag lists, per-algorithm hash length, blank and `#` lines skipped, improper lines counted (`--warn` per line, `--strict` fails), `FAILED open or read` plus the stderr cause, `WARNING:` summaries, `--ignore-missing`, `no properly formatted checksum lines found`, list from stdin. Option-combination errors in GNU wording (`--warn` only when verifying, `--tag`/`-b`/`-t`/`-z` with `-c`).
**Migrating a command:**

1. Declare `static readonly string[]` valid-but-unsupported names (a `string[]` field keeps the
   collision guard's classifier scan working) and a `static readonly OptSpecSet`
   (`allowAbbrev: true, gnuInfoOptions: true` for GNU tools).
2. Add `internal static ParsedArgs ScanArgs(string[] args)` as the test seam.
3. In the cmdlet: `PrependDecoys` → keep the exact `--help`/`--version` early exits →
   `ScanArgs` → `TryWriteParseError` → `TryHandleInfoOptions` → read `Has/Last/Operands`.
   A value-bearing decoy (`touch`'s `D` for `-d`) is re-injected as the two elements `"-d", value`.
4. Before deleting the old scan, diff old vs new over an argv corpus (bundles, `--`, long forms,
   abbreviations, unknown and unsupported flags, lone `-`, dash operands after `--`) and check
   every divergence against GNU (`wsl bash`); pin the result as an expected-value table
   (`TeeArgScanTests`, `CpArgScanTests`, `MvArgScanTests`, `RmArgScanTests`, `MkdirArgScanTests`,
   `RmdirArgScanTests`, `LnArgScanTests`, `TouchArgScanTests`). Check the exit status too: set
   `usageExitCode:` only for tools whose GNU usage status is not 1. **Oracle-check the option
   TABLE itself** — batch 2 found ln and touch had no classifier at all (flags became link targets
   / file names) and that rmdir's "unsupported" `-Z`/`--context` do not exist in GNU 9.4.
5. Add the command to `OrderedArgCommands` and the guard map; add emitter tests.

## Escape Sequence Handling

ONE left-to-right scanner, `BashEscapes.Expand(text, EscapeDialect[, out stopped])`
(`src/PsBash.Cmdlets/BashEscapes.cs`), serves every builtin; the psm1
`Expand-EscapeSequences` / `BashRuntime.ExpandEscapeSequences` are the Echo-dialect wrapper.
The dialects differ exactly where bash's builtins do (oracle-checked, bash 5.2):

| Dialect (used by) | Octal | Also | `\c` |
|---|---|---|---|
| `Echo` (`echo -e`) | `\0NNN` only (0 + up to 3 digits); `\101` stays literal | `\xHH \uHHHH \UHHHHHHHH \e \E`; `\"` stays literal | stops ALL output incl. the newline |
| `PrintfB` (`printf %b` arg) | `\0NNN` and `\NNN` | as Echo | stops all output, including the rest of the format |
| `PrintfFormat` (printf format) | `\NNN` = 1-3 digits INCLUDING the first (`\0101` = `\010` + `1`) | `\xHH \u \U \e \" \' \?` | literal (not special) |

All dialects: `\\ \a \b \f \n \r \t \v`; an unknown escape keeps its backslash. `\0` yields a
real NUL char, which survives pipes, `tee` and `>` (`printf 'x\0' > f` is 2 bytes). `\xHH` / `\NNN` name
BYTES: a run of bytes >= 0x80 that is valid UTF-8 becomes that character (`EscapedTextBuilder` in
Transpiler; `printf '\xe2\x82\xac'` is U+20AC and every output boundary writes E2 82 AC, exactly bash's
bytes). A run that is not valid UTF-8 (a lone `\xe9`/`\351`, overlong, truncated) becomes one
escaped-byte marker per byte (U+DC80..U+DCFF), written back as the single original byte by every output
boundary — see "Raw bytes" below. `$'…'` is expanded by the
emitter's own `ExpandAnsiCEscapes` (Transpiler cannot reference Cmdlets; both go through the same
`EscapedTextBuilder`); it truncates the word at the first NUL, as bash's C strings do.

`tr` SETs are not a dialect of `Expand`: `BashEscapes.ExpandTrSet` does escapes, `[:class:]` and `a-z`
ranges in ONE pass so an escaped `-`/`[` stays a literal. `\NNN` is 1-3 octal digits, every other unknown
escape (`\q \x \e \c`) DROPS the backslash (oracle: GNU tr 9.4 — `tr '\q' X` translates `q`), and a lone
trailing backslash is literal plus `tr: warning: an unescaped backslash at end of string is not portable`
on stderr. How `tr` sees the record terminator is in `runtime-command-reference.md` (tr row).

`printf %b` reads the RAW argument text (not the int/double coercion used by `%d`), so `\0101`
keeps its leading zero.

### Raw bytes (escaped-byte markers) — implemented

ps-bash text is .NET `string` (UTF-16) end to end, but the data it carries is BYTES (`printf '\351'`,
`cat binary`, a native tool's output). One shared codec, `PsBash.Core.RawBytes`
(`src/PsBash.Transpiler/RawBytes.cs`, in the leaf assembly every layer references), makes the string a
lossless carrier — Python `surrogateescape` / Cygwin style:

- **Decode** (bytes to string): valid UTF-8 decodes normally; **every byte of an invalid sequence** (a lone
  `E9`, overlong `C0 80`, truncated `E2 82`, stray continuation byte, `FF`, a UTF-8-encoded surrogate)
  becomes one marker char `U+DC00 + byte`, i.e. `U+DC80..U+DCFF` (lone LOW surrogates).
- **Encode** (string to bytes): a marker char becomes the single original byte; everything else is UTF-8.
  `decode` then `encode` is the identity on ANY byte sequence (`RawBytesTests`: all 256 values, every
  two-byte pair, random and mostly-valid-UTF-8 inputs, arbitrary chunk boundaries).
- **Why a low surrogate, not the private-use area:** valid UTF-8 can never decode to a surrogate, so a marker
  cannot come from valid input and cannot be confused with text a user typed. The only residual case is a
  marker char that directly follows a HIGH surrogate: by definition that is a (valid) surrogate pair, a
  supplementary-plane character, and it encodes as such (U+10080 is `D800 DC80`, not byte 80). A *lone* high
  surrogate is not binary data and gets the standard U+FFFD. A marker can therefore only appear when some
  decoder produced it, or when string surgery splits an emoji — documented, not defended.
- The decoder is hand-written (`Utf8.ToUtf16` + `Rune.DecodeFromUtf8` for the maximal invalid subsequence):
  .NET refuses lone surrogates from a `DecoderFallback` ("String contains invalid Unicode code points").
  `RawBytes.Encoding` is the same codec as a normal `System.Text.Encoding` (code page 65001, stateful
  `Decoder`/`Encoder` across chunk boundaries, no preamble) for `StreamReader`/`StreamWriter`, `Console`,
  `ProcessStartInfo.Standard*Encoding` and PowerShell's `$OutputEncoding`.

The pipeline stays strings. Filters, transformers and the fused line-stream cores need no change: a marker is
an ordinary non-ASCII char to them (`LineStreamRawBytesParityTests` pins fused vs unfused), and valid UTF-8 text
is byte-for-byte what it was.

**Boundary list — converted** (each reads through `RawBytes` decode, or writes through `RawBytes` encode):

| Boundary | Where |
|---|---|
| File readers | `BashFileSystem.OpenRawReader` (no BOM sniffing, for byte-exact reads) and `OpenDocumentReader` (UTF-8 BOM skipped, UTF-16 BOM honoured — the long-standing "BOM is transparent" policy of the line tools and whole-document parsers); `ReadTextLines/ReadLines/ReadAllText*`; `head`/`tail` readers, `wc`, `strings`, `base64 -d FILE`, `tar -O` |
| `cat` | Reads a **binary** file (NUL in the first 8 KB, the grep/rg heuristic) byte-exactly — no CRLF rewrite, no BOM strip — via `ReadTextLines(exactIfBinary: true)`; a text file keeps the CRLF/BOM-transparent policy. The fused `CatFileStage` makes the same call. `split` always reads exactly |
| Stdin of a simple command (`cmd < file`) | `Invoke-BashCat file \| cmd` (exact records, a missing final newline kept; a native / unmapped consumer gets `ForEach-Object { Get-BashText $_ }` text). `Get-Content` remains for compound redirects (`while read … done < f`, `$(<f)`) where text lines are the contract |
| Script text | script file and piped-stdin script (`Program.cs`), `TranspileCache` (hash key, disk entries), IPC request bodies |
| Escape producers | `BashEscapes` (`printf` format / `%b`, `echo -e`), the transpiler's `$'…'` (`EscapedTextBuilder`), `tr` SET `\NNN` (a range such as `\200-\377` works: markers are consecutive) |
| Native children | `Console.OutputEncoding = RawBytes.Encoding` in the host (PowerShell decodes a native command's stdout with it, so `findstr . bin` keeps its bytes; also `$(...)` of native output); `$OutputEncoding` set in the runspace (`SdkRunspace`) for text piped INTO a native stdin; `BashRuntime.RunChildProcess` (`StandardOutput/ErrorEncoding`, used by `bash -c` and the tool wrappers) |
| IPC | `HostProtocol` frames (stdout, stderr, request body/path/argv/ENV, sentinels): every base64 payload is `RawBytes.GetBytes` / `GetString` |
| Console | `RawConsole.Install()` (host + launcher): `Console.OutputEncoding` = the codec, so `Console.Write(line)` writes a marker as its byte. The PTY byte pump already moved raw bytes |
| File writers | `Invoke-BashRedirect` (`>`, `>>`), `tee`, `split`, `sort -o`, `uniq OUTPUT`, `sed -i`, the `less` temp file, the psm1 process-substitution temp file and `Write-BashFileText` (via `BashRuntime.WriteRawText`), the failure-tee log |
| Byte consumers | `wc -c` (`RawBytes.GetByteCount`), `cut -b`, `head -c` / `tail -c`, `base64` encode and decode (`-d` writes the decoded bytes exactly — it used to drop a final newline), `md5sum`/`sha1sum`/`sha256sum` (stdin), `gzip` (see below), `jq @base64`/`@base64d` |
| `gzip` | `-c` emits the EXACT compressed bytes (the old base64 detour is gone), `-d`/`zcat` output is the exact decompressed bytes, and with no file operand the pipeline is the input (`gzip -c f \| gzip -dc`) |

**`wc` follows GNU in a UTF-8 locale** (oracle-checked): an invalid byte counts toward `-c` only — it is not a
character (`-m`/`-L`) and neither starts nor ends a word (`printf 'a\xffb c' \| wc` = 2 words, 4 chars,
5 bytes; `printf '\xe9\n' \| wc -w` = 0). A UTF-8 BOM is counted (`wc -c` is the file's size).

**Deliberately left, with reason:**

- **argv and environment of a native child.** The OS passes UTF-16 (Windows) or re-encodes (Unix); a marker
  cannot become a raw byte there. Values with markers are not honoured by native children.
- **File names with invalid bytes**, same reason.
- **The launcher's own stdin is not forwarded** into a `-c` command (pre-existing); only a piped *script* is
  read, as bytes.
- **Windows record terminator.** The host serializer ends each record with `Environment.NewLine`, so the
  launcher's stdout carries `\r\n` on Windows (`echo hi` = `68 69 0D 0A`); a record's own bytes are exact, only
  the boundary is translated. `Invoke-BashRedirect`/`tee` write `\n`.
- **Regex `.` matches a marker as one character**; GNU in a UTF-8 locale does not match an invalid byte.
  `printf '%5s'` / `${#x}` count a marker as one character.
- **`$HOME/.psbashrc`, history, config, AI assist** — text, not data; unchanged.
- **`read` / interactive typing** — keyboard text; the interactive PTY path is a raw byte pump already.
- **`sed -i` / whole-document parsers** (`jq`, `yq`) keep the BOM-tolerant document reader.

Tests: `RawBytesTests` (codec), `HostProtocolRawBytesTests` (frames), `RawBytesCmdletTests` (round trips,
counters, gzip/base64/split), `LineStreamRawBytesParityTests` (fused lane), `RawBytesEndToEndTests` (the
built `ps-bash.exe` writes bytes; native child `findstr`), `RawBytesDifferentialTests` (41 bash-oracle cases,
cassette-replayed).
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

> **Default: write a binary cmdlet**, not a psm1 function — `src/PsBash.Cmdlets/InvokeBash{Name}Command.cs`,
> using the `BashRuntime` helpers below. Reach for a psm1 function only when the command
> needs runspace/script scope a static cmdlet cannot hold (the job-control case above).
> Either way you still add the emitter switch case (`PsEmitter.TryEmitMappedCommand`),
> the `Set-Alias` in the psm1, the `BashFlagSpecs.json` entry, and the reference-table row.
> Check the colliding-flag rules in
> [runtime-migrated-cmdlets.md](./runtime-migrated-cmdlets.md) **before** picking flag
> letters — a bare `-e`/`-i`/`-o`/`-p`/`-w` hard-crashes the binder and `-c`/`-d`/`-v` is
> silently swallowed.

The psm1-function recipe (for the rare case that is the right answer):

1. **Define the function** following the naming convention `Invoke-Bash{Name}`.

2. **Choose an arg parsing strategy**:
   - Simple boolean flags: use `ConvertFrom-BashArgs` with `New-FlagDefs`.
   - Value-bearing flags: use the manual while loop pattern.

3. **Collect pipeline input** at the top of the function:
   ```powershell
   $Arguments = [string[]]$args
   $pipelineInput = @($input)
   ```

4. **Pick the record kind**: a filter passes original objects through (preserving typed
   properties like LsEntry.Name); a transformer emits fresh text. Split multi-line items
   defensively and honor the missing final newline (see Pipeline record kinds).

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
