---
paths:
  - "src/PsBash.Module/**"
---

# Runtime Conventions

Reference: @docs/specs/runtime-functions.md

## BashObject Contract

- All output goes through `New-BashObject -BashText "text"` 
- `Get-BashText -InputObject $item` extracts text from any pipeline object
- `Set-BashDisplayProperty` configures ToString() for Out-String formatting

## Multi-line BashText Splitting

When pipeline items contain embedded newlines, commands MUST split into individual records:

```powershell
$allLines = [System.Collections.Generic.List[string]]::new()
foreach ($item in $pipelineInput) {
    $text = Get-BashText -InputObject $item
    $text = $text -replace "`n$", ''
    foreach ($subLine in $text.Split("`n")) {
        $allLines.Add($subLine)
    }
}
```

This applies to: awk, wc, head, and any line-oriented command.

## Arg Parsing Pattern

All `Invoke-Bash*` functions use:
```powershell
$Arguments = [string[]]$args
$pipelineInput = @($input)
```

Then a manual `while ($i -lt $Arguments.Count)` loop for flag parsing. Use `ConvertFrom-BashArgs` for boolean-only flags, manual loop for value-bearing flags like `-n N` or `-d CHAR`.

## Escape Sequences

`Expand-EscapeSequences` uses a sentinel pattern: `\\` → NUL marker → expand `\n`/`\t`/etc → restore marker to `\`. Used by tr, echo -e, printf.
