---
paths:
  - "**/*.Tests/**"
---

# TESTING. QA bar: @.claude/rules/qa-rubric.md (overrides this on conflict).

文言：用scripts/test.sh不用裸dotnet test；分五層；改錯必附回歸測試；命名Transpile_輸入_預期。

## RUN
ALWAYS `scripts/test.sh` — NEVER bare `dotnet test` (script kills MSBuild nodes + testhost).
`./scripts/test.sh` · `--filter "MyTest"` · `src/PsBash.Core.Tests` (project). Don't put `|` in a `--filter` unless quoted.

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

## BUG FIX = REGRESSION TEST (mandatory)
Repro test (fails pre-fix) → fix → passes → add at the right layer (PsEmitterTests for transpile, psm1 for runtime).

## NAMING
`Transpile_{Input}_{ExpectedBehavior}` — e.g. `Transpile_XargsWithBraces_QuotesBracesToPreventScriptBlockParsing`.
