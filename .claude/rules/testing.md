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
- WALL-CLOCK bound on a pipeline (Cmdlets.Tests) = `BoundedPipeline.Invoke` + class in `[Collection(WallClockBoundCollection.Name)]` (runs alone): beside parallel classes every script→.NET method call (`$steppable.Process($_)` per record) waits on Windows' process-wide AMSI lock held by their script compiles — 1 s became 60 s (guard `WallClockBoundGuardTests`).
- SKIPS need `[SkippableFact]`/`[SkippableTheory]`: under plain `[Fact]`/`[Theory]` a `Skip.If`/`IfNot`/`SkipException` FAILS — also when reached via a helper (`AssertOracle.EqualAsync`), lambda, async body or ctor. Guard: `PsBash.Testing.SkipUsageGuard` (IL walk) + `SkipUsageGuardTests` in every test project (add one to a new project).
- PLATFORM FACTS are derived, never guessed: `\`→`/` is Windows-only (`docs/specs/paths.md`) → `OperatingSystem.IsWindows() ? "C:/x" : <bash result>` (guard `Core.Tests/WindowsPathExpectationGuardTests`); dir size/block counts come from the host off Windows (`FindPredicateTests.FsFact` → GNU find), not "ext4 = 4096".
- A time bound that detects a HANG sits far short of the hang: the held child lives ≥2× the longest bound (`ProcessTreeStopTests`: hold 120 s, bound budget+40 s). A bound a few seconds under the hold fails correct code on a loaded host (g45, load 58: `timeout 12` took 26 s vs 22 s, child held 30 s).
- A file ANOTHER process writes during the test (pid file, marker) is read with `PsBash.Testing.PublishedFile` (`TryRead`/`ReadWhenReady`): `File.Exists` then `File.ReadAllText` races the writer and throws a sharing violation (guard `Core.Tests/PublishedFileGuardTests`).
- Platform-locked tests declare `[Trait("Platform",…)]`/`[SkippableFact]` with a reason; never silently no-op.
- Cmdlet tests: `CmdResult.Run(pwsh, script)` then `.AssertSuccess()` / `.AssertFailed(exit, "stderr")` — never output-only.
- Binary cmdlet with a common-param-colliding short flag (`-e -i -o -p -w -c -d -v`) needs a DIRECT-invocation test (`Invoke-BashFoo -e …`): Pester (release gate) calls cmdlets directly; xunit force-quotes and hides the break.

## SPEC CASE ISOLATION
A spec case must not observe or affect another case or the checkout (`docs/specs/spec-suite.md` 3.4). Own per case: cwd (`CaseDir`; the launcher process STARTS there), shell state (in-host `BashShellState` restore; launcher = new process). Shared, so such a case runs alone: `/tmp`, `~`, `$HOME`, interactive history (`CaseIsolation.UsesSharedLocation`). New per-case state goes into those primitives, never a `cd`/`export` glued in front of the script. No absolute writes elsewhere (`> /zz` works elevated on Windows), no timing races against nested-shell start (`timeout 0.5 $SH`). Guards: `Spec.Tests/CaseIsolationTests`, `SharedLocationGateTests`. Junk in a checkout root (`a[`, `F`, `foo.txt`) = a snippet run WITHOUT a case dir (`tman dev -c` in the repo root): `cd "$(mktemp -d)"` first.

## RELEASE GATE
Pester (`tests/PsBash.Tests.ps1`) + Core.Tests block publish; Cmdlets.Tests is continue-on-error. Run `/pester` before tagging.

## NAMING
`Transpile_{Input}_{ExpectedBehavior}`.
