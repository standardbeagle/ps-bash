---
paths:
  - "src/PsBash.Core/Parser/**"
---

# Parser Conventions

Reference: @docs/specs/parser-grammar.md

## Lexer (BashLexer.cs)

- Multi-character operators must be checked longest-first: `<<<` before `<<-` before `<<`
- IoNumber reclassification requires adjacency check — only reclassify digit word when `token.Position + token.Value.Length == redirectPos` (no whitespace gap)
- Process substitution `<(` and `>(` must be detected before single `<`/`>` operators
- Brace expansion `{a,b,c}` is detected by `IsBraceExpansion()` before `{` becomes `LBrace`

## Parser (BashParser.cs)

- `ParseSimple` is the core — it collects words, redirects, heredocs, here-strings, and env pairs in a single loop
- Here-string `<<<` sets `hereDoc` directly (word becomes body); heredoc `<<` sets `heredocDelimiter` for post-loop body collection
- Reserved words (`if`, `then`, `do`, `done`, `fi`, etc.) break the word loop via `IsCompoundDelimiter`
- New grammar: add token kind → update lexer → add AST node → add parser production → add emitter case → add tests at each layer

## AST (Ast/*.cs)

- All AST nodes extend `BashNode` and are immutable records
- `CompoundWord` wraps `ImmutableArray<WordPart>` — never use raw strings for parsed words
- `Command.Simple` carries `HereDocs: ImmutableArray<HereDoc>` — supports multiple `<<` heredocs and `<<<` here-strings; emitter uses the last entry for stdin
