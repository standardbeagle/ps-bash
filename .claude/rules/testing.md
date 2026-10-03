---
paths:
  - "**/*.Tests/**"
  - "tests/**"
---

# TESTING. Full QA bar (directives cited in code as "qa-rubric Directive N"): `docs/testing/qa-rubric.md` — read on demand.

文言：用tman；神諭先斷言後；負例為主；勿眠；改錯必附回歸測試。

## RUN
`tman build | tman test | tman test-proj src/X.Tests --filter "..." | tman pester` — never bare dotnet. Don't run the full Cmdlets.Tests to verify a targeted change; `--filter` the affected classes.

## ORACLE FIRST
Bash is the oracle. Prefer differential tests (real bash vs ps-bash: stdout+stderr+exit; goldens under `CanonicalEnv` via `AssertOracle.GoldenAsync`, regenerate with `UPDATE_GOLDENS=1`). Hand-written asserts only for ps-bash-specific surface; say why in one line.
Mutating commands (cp/mv/rm/tee…): `FsStateOracle.EqualAsync(setup, command)` (`Differential.Tests/Oracle/`); record with `PSBASH_ORACLE_RECORD=1`.

## MUST
- Bug fix = regression test that fails pre-fix, at the right layer (lexer/parser/emitter/transpiler/e2e/cmdlet).
- Negative cases are primary: missing file/cmd, broken pipe, child crash, alias loop, unquoted var with spaces/globs, IFS mutation.
- Var expansion/exec features: injection probes (`;`, `$(…)`, `{}`, backtick, heredoc with `"`/`$`).
- No `Thread.Sleep`/`Task.Delay`; PTY tests wait for prompt with a fixed env and a 5 s timeout.
- Platform-locked tests declare `[Trait("Platform",…)]`/`[SkippableFact]` with a reason; never silently no-op.
- Cmdlet tests: `CmdResult.Run(pwsh, script)` then `.AssertSuccess()` / `.AssertFailed(exit, "stderr")` — never output-only.
- Binary cmdlet with a common-param-colliding short flag (`-e -i -o -p -w -c -d -v`) needs a DIRECT-invocation test (`Invoke-BashFoo -e …`): Pester (release gate) calls cmdlets directly; xunit force-quotes and hides the break.

## RELEASE GATE
Pester (`tests/PsBash.Tests.ps1`) + Core.Tests block publish; Cmdlets.Tests is continue-on-error. Run `/pester` before tagging.

## NAMING
`Transpile_{Input}_{ExpectedBehavior}`.
