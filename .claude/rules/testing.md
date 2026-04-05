---
paths:
  - "**/*.Tests/**"
---

# Testing Conventions

## Running Tests

ALWAYS use `scripts/test.sh` — never bare `dotnet test`. The script cleans up MSBuild server nodes and testhost processes.

```bash
./scripts/test.sh                          # all tests
./scripts/test.sh --filter "MyTest"        # specific test
./scripts/test.sh src/PsBash.Core.Tests    # specific project
```

## Test Layers

1. **BashLexerTests** — token-level: verify tokenization of specific input strings
2. **BashParserTests** — AST-level: verify parse tree structure for bash input
3. **PsEmitterTests** — transpile-level: verify `PsEmitter.Transpile()` output string
4. **BashTranspilerTests** — integration: verify `BashTranspiler.Transpile()` end-to-end
5. **ProgramEndToEndTests** — full e2e: spawn ps-bash.exe process, check stdout/stderr/exit code

## Bug Fix Pattern

Every bug fix MUST include a regression test:
1. Write a test that reproduces the bug (expected to fail before fix)
2. Fix the bug
3. Verify the test passes
4. Add the test at the appropriate layer (usually PsEmitterTests for transpile bugs, psm1 for runtime bugs)

## Test Naming

Use `Transpile_{Input}_{ExpectedBehavior}` for emitter tests, e.g.:
- `Transpile_XargsWithBraces_QuotesBracesToPreventScriptBlockParsing`
- `Transpile_HeadWithHeredoc_ParsesCorrectly`
