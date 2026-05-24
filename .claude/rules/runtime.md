---
paths:
  - "src/PsBash.Module/**"
---

# RUNTIME. Ref: @docs/specs/runtime-functions.md (table: runtime-command-reference.md; migrations: runtime-migrated-cmdlets.md)

文言：Emit-BashLine分行、New-BashObject存型不分；消費者透傳原物件勿展平；$args手解旗；轉義用哨兵。

## BASHOBJECT — pick the right output fn
- `Emit-BashLine -Text` → stdout-like text; splits on `\n`, one object/line. printf / echo -e / heredoc.
- `New-BashObject -BashText` → typed single-line (LsEntry/CatLine/PsEntry). Does NOT split.
- `Get-BashText -InputObject` → text from any pipeline object. `Set-BashDisplayProperty` → ToString() for Out-String.

## PIPELINE PRESERVATION
Consumers (grep/sed/tail…) PASS ORIGINAL objects through — keep typed props.
- single-line (ls/cat/find): pass directly.
- multi-line edge: defensive split — split THAT item into `New-BashObject` lines; pass single-line items unchanged.
- NEVER flatten all input into `$allLines` — destroys typed objects.

## ARG PARSING
Top of every `Invoke-Bash*`: `$Arguments=[string[]]$args; $pipelineInput=@($input)`.
Then manual `while ($i -lt $Arguments.Count)` loop. `ConvertFrom-BashArgs` = boolean-only flags; manual loop = value flags (`-n N`, `-d CHAR`).

## ESCAPES
`Expand-EscapeSequences`: `\\`→NUL sentinel→expand `\n`/`\t`/…→restore `\`. Used by tr / echo -e / printf.
