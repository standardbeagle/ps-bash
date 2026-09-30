---
paths:
  - "**/*.Tests/**"
---

# TESTING. QA bar: @.claude/rules/qa-rubric.md (overrides this on conflict).

文言：用tman不用裸dotnet test；分五層；改錯必附回歸測試；命名Transpile_輸入_預期。

## RUN
ALWAYS `tman` (see CLAUDE.md "Running Tests") — NEVER bare `dotnet build`/`dotnet test` (leaks MSBuild nodes + testhost).
`tman build` · `tman test` · `tman test-proj src/PsBash.Core.Tests --filter "..."` · `tman pester`.
Test verbs BUILD FIRST in the same job (no stale-binary runs). Quote a `--filter` containing `|`.
`scripts/test.sh` = legacy (Stress split/coverage); its cleanup is scoped to this checkout's `src/*/bin`.

## LAYERS
1. BashLexerTests — tokens. 2. BashParserTests — AST shape. 3. PsEmitterTests — `PsEmitter.Transpile()` output.
4. BashTranspilerTests — end-to-end transpile. 5. ProgramEndToEndTests — spawn ps-bash.exe, check stdout/stderr/exit.

## PUBLISH GATE ≠ XUNIT (release-blocking, easy to miss)
The release gate is the **Pester** suite (`tests/PsBash.Tests.ps1`) + **Core.Tests**, NOT the
xunit Cmdlets.Tests (which is `continue-on-error` in publish.yml). Pester calls cmdlets
DIRECTLY — `Invoke-BashEcho -e '...'` — exercising bare-flag binder collisions that the
transpiler's force-quoting hides from every xunit test (xunit passes `-e` as a force-quoted
Arguments string). So a binary `Invoke-Bash*` with a common-param-colliding short flag
(`-e -i -o -p -w`, `-c -d -v`; see `os-interface`/the collision guard) MUST have a
**direct-invocation** test (`Invoke-BashFoo -e ...`), not only a force-quoted-arg one — or the
break only shows up in the publish Pester gate. Manifest invariants (ReleaseNotes ≤10600) live
in Core.Tests. Run Pester locally before tagging — see the release-pester-gate-local memory.

## CMDLET TESTS: ASSERT THE EXIT, NOT JUST THE OUTPUT
Run scripts via `CmdResult.Run(pwsh, script)` (`Cmdlets.Tests/CmdResult.cs`: Stdout, Stderr, ExitCode, Errors).
Success test → `.AssertSuccess()` (exit 0 + no error records). Failure test → `.AssertFailed(exit, "stderr fragment")`.
A string-only helper lets a FAILING command pass any test that just checks the filesystem. `PwshTestFixture` fails fast
(throws) on missing module files / failed import — never continue on a bare runspace.

## MUTATING COMMANDS (cp mv rm tee ...): DIFF THE FILESYSTEM vs GNU
Use `FsStateOracle.EqualAsync(setup, command)` (`Differential.Tests/Oracle/FsStateOracle.cs`, cases in
`FsStateDifferentialTests`): same fixture tree, real bash vs ps-bash, compares stdout + exit + resulting tree
(paths, file/dir, exact bytes). Snapshot is stdout → cassette → replays without WSL. Record: `PSBASH_ORACLE_RECORD=1`.
Fixture files end in one `\n` (`Tree` writes them with `printf '%s\n'`).

## BUG FIX = REGRESSION TEST (mandatory)
Repro test (fails pre-fix) → fix → passes → add at the right layer (PsEmitterTests for transpile, psm1 for runtime).

## NAMING
`Transpile_{Input}_{ExpectedBehavior}` — e.g. `Transpile_XargsWithBraces_QuotesBracesToPreventScriptBlockParsing`.
