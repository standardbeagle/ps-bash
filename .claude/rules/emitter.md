---
paths:
  - "src/PsBash.Core/Parser/PsEmitter.cs"
---

# Emitter Conventions

Reference: @docs/specs/emitter-strategy.md

## The Passthrough Principle

The emitter maps bash command names to `Invoke-Bash*` functions and forwards ALL arguments unchanged. The runtime functions handle flag parsing.

**NEVER** do any of these in the emitter:
- Translate bash flags to PowerShell named parameters (`-d` → `-Delimiter`)
- Extract and re-emit specific flags (`-n N` from head)
- Assume which flags a command supports
- Map to native PowerShell cmdlets (`Select-Object`, `Measure-Object`, `Sort-Object`)

## Pipe Target Mapping

All pipe targets in `TryEmitMappedCommand` should use `EmitPassthrough`.

When adding a new command:
1. Add a case to `TryEmitMappedCommand` switch
2. Use `EmitPassthrough("Invoke-BashFoo", args)` 
3. Do NOT create a custom `EmitFoo` method unless quoting requires it

## Quoting (NeedsPassthroughQuoting)

Quote flag arguments containing:
- `,` — PowerShell interprets as array separator
- `{` or `}` — PowerShell interprets as scriptblock

The flag gets wrapped in double quotes: `"-F,"`, `"-I{}"`.
