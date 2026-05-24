---
name: qa-audit
description: Audit one bash feature against the QA rubric and write its section into docs/testing/interactive-parity-audit.md
---

# QA AUDIT. ONE FEATURE PER RUN. Ref: @.claude/rules/qa-rubric.md (D10 = TEMPLATE).

文言：審一feature——尋測試、核15失敗軸、核6模式、核oracle、核已知患，依D10模板寫一節；勿寫測、勿改碼。

INPUT: $ARGUMENTS = feature (e.g. "pipes", "if/elif/else", "command substitution").

## STEPS
1. RESOLVE → TEST FILES. grep `src/**/*.Tests/**` + `PsBash.psm1` (if runtime). List file:test. No prose.
2. FAILURE-SURFACE: each of D3's 15 axes → YES/NO/PARTIAL (justify skip, 1 line).
3. MODE: each of D4's M1..M6 → YES/NO/PARTIAL.
4. ORACLE: grep tests for differential harness (Phase 0 fixture). None → NO + why (D1 exceptions).
5. KNOWN BUGS: grep `docs/solutions/` + Dart (`tags: bug-fix` / feature). List file:line or Dart ID.
6. WRITE one section, D10 template EXACTLY → append `docs/testing/interactive-parity-audit.md`. No prose outside template. Priority gaps: top 3, by user impact.
7. SUMMARY to user: feature, gap count, P1 gap (1 line), link.

## BARS
One new section, ≤60 md lines, template-conformant. `grep "### FEATURE:" … | wc -l` == audited count. Consistent columns.

## DO NOT
No writing tests. No fixing bugs. No editing other sections (only refresh one feature).
