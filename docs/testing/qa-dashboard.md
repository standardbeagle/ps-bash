# QA Dashboard

<!-- CI updates this file on every main-branch push. Do not edit the table rows by hand. -->

**Last Updated:** _updated by CI_

---

## Current Coverage

Coverage is collected via `coverlet.collector` with `--collect "XPlat Code Coverage"` on every CI run.
Bars per QA rubric Directive 2: PsEmitter >= 80% branch, BashParser >= 80% branch, psm1 >= 70% branch per function.

| Assembly | Line % | Branch % | Status |
|---|---|---|---|
| PsBash.Core (PsEmitter.cs) | _pending_ | _pending_ | needs >= 80% branch |
| PsBash.Core (BashParser.cs) | _pending_ | _pending_ | needs >= 80% branch |
| PsBash.Module (PsBash.psm1) | _pending_ | _pending_ | needs >= 70% branch per function |

To generate locally:

```bash
PSBASH_COVERAGE=1 ./scripts/test.sh
# or for a full HTML report:
./scripts/coverage-report.sh --open
```

Coverage XML artifact: `coverage-<os>` uploaded per CI run (see Actions > workflow run > Artifacts).

---

## Mutation Score

_Deferred: Stryker.NET integration is planned for a future phase._

Bar: >= 60% mutation score on PsBash.Core runtime (BashParser + PsEmitter).

---

## Flake Budget

**Bar: ZERO flakes over 100 re-runs on the CI matrix before merge (QA rubric Directive 2).**

| Status | Count |
|---|---|
| Active flakes | 0 |
| Quarantined (7+ days) | 0 |
| Fixed this sprint | 0 |

Policy:
- One flake = quarantine with `[Trait("Quarantined", "DART-XXXX")]` (see below). Never disable, never ignore.
- Quarantined > 7 days = open bug, assign, fix or delete.

---

## Quarantine Register

Tests that are flaky or blocked by a known bug are quarantined, not disabled.

| Dart ID | Test (file:name) | Reason | Quarantined At |
|---|---|---|---|
| _(empty)_ | | | |

### Quarantine Convention

Tag a flaky or blocked test with:

```csharp
[Trait("Quarantined", "DART-XXXX")]
// Quarantined: DART-XXXX — <one-line reason> — <ISO date>
[Fact]
public void MyFlakyTest() { ... }
```

Rules:
1. The `[Trait("Quarantined", "DART-XXXX")]` must reference an open Dart task.
2. Add the test name and reason to the table above.
3. Quarantined tests still run in CI — the quarantine report step counts them.
4. After 7 days without a fix, the Dart task must be escalated to High priority.
5. A test may only be deleted (not quarantined) if the feature it covers is also deleted.

CI quarantine report command:

```bash
dotnet test --filter "Quarantined=notEmpty" --list-tests
```

---

## Diff-Coverage Gate

**Bar: >= 90% line coverage on every PR touching `src/PsBash.Core` or `src/PsBash.Module` (QA rubric Directive 2).**

The gate runs automatically on PRs via `.github/workflows/publish.yml`.
To run locally:

```bash
# After running tests with coverage:
PSBASH_COVERAGE=1 ./scripts/test.sh
bash scripts/diff-coverage.sh coverage/coverage.xml origin/main
```

If the gate fails, add tests covering the changed lines before merging.

---

## References

- QA rubric: `.claude/rules/qa-rubric.md`
- Testing conventions: `.claude/rules/testing.md`
- Coverage scripts: `scripts/coverage-report.sh`, `scripts/diff-coverage.sh`
- Parity audit: `docs/testing/interactive-parity-audit.md`
