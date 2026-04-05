# Validate Specs Against Source Code

Detect drift between the three spec documents and the actual source code.

## When to Use

Run this skill after making changes to the parser, emitter, or runtime module
to ensure the spec documents still accurately describe the code.

## Validation Steps

### 1. BashTokenKind Enum vs parser-grammar.md Token Table

Extract every member from the `BashTokenKind` enum in
`src/PsBash.Core/Parser/BashToken.cs`. Extract every token name from the
"Token Reference" table in `docs/specs/parser-grammar.md` (the `BashTokenKind`
column). Compare the two sets -- they must be identical.

```
Source: grep for enum members in src/PsBash.Core/Parser/BashToken.cs
Spec:   grep for rows in the Token Reference table in docs/specs/parser-grammar.md
Match:  every enum member appears in the spec, every spec row exists in the enum
```

### 2. TryEmitMappedCommand Cases vs emitter-strategy.md Command Table

Extract every `case "..."` string from the `TryEmitMappedCommand` method in
`src/PsBash.Core/Parser/PsEmitter.cs`. Extract every bash command name from the
"Mapped Commands" table in `docs/specs/emitter-strategy.md`. Compare the two
sets -- they must be identical.

Also verify that the `PsBuiltinAliases` set documented in the "Standalone
Mapping" section matches the actual `PsBuiltinAliases` HashSet in PsEmitter.cs.

```
Source: grep for case statements in TryEmitMappedCommand in PsEmitter.cs
Spec:   grep for rows in the Mapped Commands table in docs/specs/emitter-strategy.md
Match:  every case has a spec row, every spec row has a case
```

### 3. Invoke-Bash* Functions vs runtime-functions.md Command Reference

Extract every `function Invoke-Bash*` declaration from
`src/PsBash.Module/PsBash.psm1`. Extract every function name from the "Command
Reference" table in `docs/specs/runtime-functions.md`. Compare the two sets.

Internal helpers (like `Invoke-BashChecksum`) that are not direct command
mappings may appear in the psm1 but not in the spec table -- that is acceptable
as long as they are mentioned in the relevant command rows.

```
Source: grep for "^function Invoke-Bash" in PsBash.psm1
Spec:   grep for Invoke-Bash entries in the Command Reference table
Match:  every user-facing function has a spec row
```

### 4. Alias Registrations vs runtime-functions.md

Extract every `Set-Alias` at the bottom of `PsBash.psm1`. Verify each alias
name appears somewhere in the spec (either in the Command Reference table or in
the "Additional aliases" note).

```
Source: grep for "^Set-Alias" in PsBash.psm1
Spec:   the Command column in the Command Reference table + Additional aliases line
```

## Reporting

For each validation step, report one of:
- **PASS** -- sets match exactly
- **DRIFT** -- list the specific items that are in source but not in spec, or
  in spec but not in source

If any step reports DRIFT, update the relevant spec file to match the source
code. The source code is the authority; specs must follow.

## Files Involved

- `src/PsBash.Core/Parser/BashToken.cs`
- `src/PsBash.Core/Parser/PsEmitter.cs`
- `src/PsBash.Module/PsBash.psm1`
- `docs/specs/parser-grammar.md`
- `docs/specs/emitter-strategy.md`
- `docs/specs/runtime-functions.md`
