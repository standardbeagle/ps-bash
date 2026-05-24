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

## BUG FIX = REGRESSION TEST (mandatory)
Repro test (fails pre-fix) → fix → passes → add at the right layer (PsEmitterTests for transpile, psm1 for runtime).

## NAMING
`Transpile_{Input}_{ExpectedBehavior}` — e.g. `Transpile_XargsWithBraces_QuotesBracesToPreventScriptBlockParsing`.
