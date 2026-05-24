---
paths:
  - "src/PsBash.Transpiler/Parser/**"
---

# PARSER. Ref: @docs/specs/parser-grammar.md

文言：詞法多字符長先匹，IoNumber須緊鄰，<( >( 先於 < >，{a,b} 先於 LBrace；AST皆不可變record，CompoundWord不用裸字串。

## LEXER (BashLexer.cs)
- multi-char ops longest-first: `<<<` > `<<-` > `<<`.
- IoNumber reclass needs adjacency: only when `token.Position + token.Value.Length == redirectPos` (no gap).
- process-sub `<(` `>(` detected BEFORE single `<`/`>`.
- brace-expansion `{a,b,c}` via `IsBraceExpansion()` before `{` → `LBrace`.

## PARSER (BashParser.cs)
- `ParseSimple` is core: collects words + redirects + heredocs + here-strings + env pairs in ONE loop.
- here-string `<<<` sets `hereDoc` directly (word = body); heredoc `<<` sets `heredocDelimiter` for post-loop body collection.
- reserved words (`if then do done fi` …) break the word loop via `IsCompoundDelimiter`.
- NEW GRAMMAR pipeline: token kind → lexer → AST node → parser production → emitter case → tests at EACH layer.

## AST (Ast/*.cs)
- all nodes extend `BashNode`, immutable records.
- `CompoundWord` wraps `ImmutableArray<WordPart>`; never raw strings for parsed words.
- `Command.Simple.HereDocs` is `ImmutableArray<HereDoc>` (multiple `<<` + `<<<`); emitter uses the LAST for stdin.
