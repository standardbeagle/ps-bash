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

## PIPELINE RECORD KINDS (filter vs transformer)
文言：濾者透傳原物件，變者出新文本；末行換行以源為準。
Producers (ls/find/ps/stat/du/date/ping…) emit typed objects. Consumers split by what they do to a line:
- FILTER — output line IS an input line (grep plain, head/tail -n, sort, uniq plain, tac, shuf, cat no-flags, tee, less/more, rg plain): PASS THE ORIGINAL object through. `ls | grep .txt` stays `PsBash.LsEntry`.
- TRANSFORMER — text changes (sed, tr, cut, awk, rev, nl, grep -o/-n/-H, uniq -c, cat -n/-E/-T, head/tail -c): emit FRESH text (`BashRuntime.TextRecord`), never the upstream object.
- Multi-line record (`printf 'b\na'` = one object): split it into text lines; the LAST piece inherits the missing final newline.
- Terminator: `BashRuntime.IsUnterminated(item)` (NoTrailingNewline AND text not ending `\n`). byte-copy tools (head/tail/cat/tee/rev/tr/sed/more/less) keep it; always-terminating tools (grep/sort/uniq/shuf/cut/nl/awk) pass originals through `BashRuntime.PassTerminated` so a stale flag cannot glue lines. tac glues (`printf 'b\na'|tac` = `ab\n`) — reversed originals give that for free.
- NEVER flatten all input into `$allLines` — destroys typed objects of filters.

## ARG PARSING
Top of every `Invoke-Bash*`: `$Arguments=[string[]]$args; $pipelineInput=@($input)`.
Then manual `while ($i -lt $Arguments.Count)` loop. `ConvertFrom-BashArgs` = boolean-only flags; manual loop = value flags (`-n N`, `-d CHAR`).

## ESCAPES
`Expand-EscapeSequences`: `\\`→NUL sentinel→expand `\n`/`\t`/…→restore `\`. Used by tr / echo -e / printf.

## PSM1 INVARIANTS (each learned via a bug)
- jq/yq truthiness → `Test-JqTruthy` (ONLY `$null` / `[bool]$false` are falsy). `$x -ne $false` mis-coerces `0` and the string `"false"` to falsy → `// 99` and `select(.n)` broke on 0.
- `Show-BashHelp` returns ONE `New-BashObject` (whole help in `.BashText`). Do NOT per-line it — breaks `(Show-BashHelp x).BashText -match 'Usage'` and every command's `--help` test. Consumers split multi-line BashText themselves.
- glob detection = `[*?[]` (a `[..]` char class counts), not `[*?]`.
- `fg %N` / `wait %N` = positional JOB NUMBER (the `[N]` from `jobs`), NOT the synthetic `$!` id (which starts at 1000).
