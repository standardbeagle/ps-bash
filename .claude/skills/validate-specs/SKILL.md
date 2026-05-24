---
name: validate-specs
description: Validate specs against source code (detect drift)
---

# VALIDATE SPECS vs SOURCE.

文言：四核——token枚舉↔grammar表、TryEmitMappedCommand↔mapped表、Invoke-Bash*↔command表、Set-Alias↔alias；漂移則改spec（碼為準）。

Run after parser/emitter/runtime changes. Source is authority; specs follow.

## CHECKS — each PASS, or DRIFT (list items in source-not-spec / spec-not-source)
1. **Tokens**: `BashTokenKind` enum (`src/PsBash.Transpiler/Parser/BashToken.cs`) == Token Reference table in `docs/specs/parser-grammar.md`.
2. **Mapped commands**: `case "..."` in `TryEmitMappedCommand` (`src/PsBash.Transpiler/Parser/PsEmitter.cs`) == Mapped Commands table in `emitter-strategy.md`. Also `PsBuiltinAliases` HashSet == its Standalone-Mapping doc.
3. **Runtime fns**: `^function Invoke-Bash*` in `PsBash.psm1` == Command Reference table in `runtime-command-reference.md`. Internal helpers (e.g. `Invoke-BashChecksum`) may be absent if named in a row.
4. **Aliases**: each `^Set-Alias` in `PsBash.psm1` appears in `runtime-command-reference.md` (table or Additional-aliases line).

## ON DRIFT
Update the spec to match source.
