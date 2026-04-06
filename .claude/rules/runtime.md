---
paths:
  - "src/PsBash.Module/**"
---

# Runtime Conventions

Reference: @docs/specs/runtime-functions.md

## BashObject Contract

Two output functions — use the right one:

- **`Emit-BashLine -Text "text"`** — for stdout-like text output. Splits on newlines and emits one BashObject per line. Matches bash semantics where `\n` is a record boundary. Use this for printf, echo -e, and any command that produces text with embedded newlines.
- **`New-BashObject -BashText "text"`** — for typed/structured output. Does NOT split. Use for typed objects (LsEntry, CatLine, PsEntry) that are inherently single-line.
- `Get-BashText -InputObject $item` extracts text from any pipeline object
- `Set-BashDisplayProperty` configures ToString() for Out-String formatting

## Pipeline Object Preservation

Consumer commands (grep, sed, tail, etc.) should pass original objects through the pipeline, NOT create new BashObjects. This preserves typed properties (e.g., LsEntry.Name, CatLine.Content) through pipe chains.

For single-line items (the common case from ls, cat, find): pass through directly.
For multi-line edge cases: use defensive split as safety net:

```powershell
foreach ($item in $pipelineInput) {
    $text = Get-BashText -InputObject $item
    if ($text -match "`n" -and $text -ne "`n") {
        # Multi-line edge case: split into new BashObjects
        foreach ($subLine in ($text -replace "`n$",'' -split "`n")) {
            New-BashObject -BashText "$subLine`n"
        }
    } else {
        # Single-line: pass original object (preserves type)
        $item  # process directly
    }
}
```

**DO NOT** unconditionally split all pipeline items into `$allLines` — this destroys typed objects.

## Arg Parsing Pattern

All `Invoke-Bash*` functions use:
```powershell
$Arguments = [string[]]$args
$pipelineInput = @($input)
```

Then a manual `while ($i -lt $Arguments.Count)` loop for flag parsing. Use `ConvertFrom-BashArgs` for boolean-only flags, manual loop for value-bearing flags like `-n N` or `-d CHAR`.

## Escape Sequences

`Expand-EscapeSequences` uses a sentinel pattern: `\\` → NUL marker → expand `\n`/`\t`/etc → restore marker to `\`. Used by tr, echo -e, printf.
