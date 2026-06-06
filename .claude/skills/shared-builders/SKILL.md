---
name: shared-builders
description: Use (or extend) the shared PsBuild PowerShell-text builder and the OS-interface FileSystemHelpers/RunChildProcess helpers instead of hand-rolling PS strings or platform branches
disable-model-invocation: true
---

文言：勿手綴PS文、勿裸毀滅檔操或裸生成。PS文經PsBuild，毀滅檔經FileSystemHelpers.Delete*Force，子程序經RunChildProcess。新片段→加助手+測試，勿內聯。

Route this work through the shared libraries — don't hand-roll: $ARGUMENTS

These two libraries exist because every recent one-off fix had unfixed siblings (the `if !`
no-`[void]` wrong-branch bug; the cp/mv/find read-only-delete crashes). Centralize → fix once.

## A. Emitting PowerShell text → `PsBuild` (`src/PsBash.Transpiler/Parser/PsBuild.cs`)
NEVER hand-concatenate quoting / exit-code / `[void]` / subshell / splat / null-safe fragments in
`PsEmitter`. Pick the primitive:
- string literal → `PsBuild.SingleQuote` / `DoubleQuote` / `EscapeForDoubleQuote` (backtick-FIRST).
- a command as an `if`/`while` condition (test EXIT CODE) → `PsBuild.ExitCodeTest(cmd, negate)` — ALWAYS `[void]`-wraps; `negate:true` = bash `! cmd`.
- `&&`/`||` chain exit-code → `PsBuild.SetExitFromBool` (boolexpr / negated) or append `PsBuild.SignalFailIfNonZero()` (plain pipeline, keeps stdout); standalone `[ ]` statement → `PsBuild.SilentExitFromBool`.
- suppress output → `PsBuild.Void` / `VoidStatement` (statement-list-safe); isolate → `Subshell` / `Subexpr`.
- unquoted-var word-split splat (RC-7) → `PsBuild.WordSplitArray`; pipeline text extract → `PsBuild.NullSafeBashText`.
- Exit-code scope is ALWAYS `$global:LASTEXITCODE`.

## B. Destructive FS / child process → `PsBash.Cmdlets` shared helpers
- force-delete (rm/cp/mv/find) → `FileSystemHelpers.DeleteDirectoryForce` / `DeleteFileForce` / `DeleteEntryForce(path,isDir)` — read-only-aware. `ClearReadOnly` for non-recursive cases. NEVER raw `Directory.Delete`/`File.Delete` on a force path (throws on Windows read-only descendants).
- buffered shell-out → `BashRuntime.RunChildProcess(psi[, timeout])` (timeout + kill-tree). Raw `Process.Start` only for streaming/interactive (traceroute/less) that handle `Stopping` themselves.
- path normalization → `FileSystemHelpers.NormalizeOperandPath`/`WindowsPath`; file reads → `BashFileSystem`; `--version` → `TryHandleVersion`; exit code → `SetLastExitCode`.

## STEPS to ADD a new shared primitive (when a fragment/op repeats)
1. **Add the method** to `PsBuild.cs` (PS-text) or `FileSystemHelpers.cs` (OS) with an XML-doc note on the load-bearing detail (why the `[void]`, the read-only fallback, the escape order).
2. **Unit-test it** — `PsBash.Core.Tests/Parser/PsBuildTests.cs` (escaping/exit-code/void/splat axes) or `PsBash.Cmdlets.Tests/InvokeBashFileSystemMutatorTests.cs` (read-only theory).
3. **Route ALL call sites** to it — grep for the hand-built form and replace; check for siblings (the bug you're fixing usually has them).
4. **Update expectations** — emitter test strings shift; run `dotnet test src/PsBash.Core.Tests -f net10.0 --filter PsEmitter`.
5. **Build + verify e2e** via the launcher; **run** the relevant `*.Tests`.
6. **Document** — one line in `.claude/rules/emitter.md` (PS-text) or `os-interface.md` (OS), and update memory [[psbuild-ps-text-builder]] / [[os-interface-shared-helpers]] if the convention changed.
