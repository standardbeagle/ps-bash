---
paths:
  - "src/PsBash.Transpiler/Parser/**"
---

# PARSER. Ref: `docs/specs/parser-grammar.md`

文言：詞法多字符長先匹，IoNumber須緊鄰，<( >( 先於 < >，{a,b} 先於 LBrace；AST皆不可變record，CompoundWord不用裸字串。

## LEXER (BashLexer.cs)
- multi-char ops longest-first: `<<<` > `<<-` > `<<`.
- IoNumber reclass needs adjacency: only when `token.Position + token.Value.Length == redirectPos` (no gap).
- process-sub `<(` `>(` detected BEFORE single `<`/`>`.
- brace-expansion `{a,b,c}` via `IsBraceExpansion()` before `{` → `LBrace`.

## PARSER (BashParser.cs)
- `ParseSimple` is core: collects words + redirects + heredocs + here-strings + env pairs in ONE loop.
- redirection operators = ONE table `RedirectOperators` (lexer IoNumber + parser); ONE production `ParseRedirect` on every command kind, `N`/`{name}` prefix on every operator. Here-text on fd 0 (no `{name}`) of a simple command -> `HereDocs`; on any other fd -> `Redirect.Here`. `BashParser.HereDocument` slices the body from the `[BodyStart, BodyEnd)` span the LEXER stamped on the delimiter token (`BashLexer.SkipHeredocBodies`); unquoted -> `BashLexer.JoinHereDocContinuations` (backslash-newline). Never re-derive the body from the token cursor.
- reserved words (`if then do done fi` …) break the word loop via `IsCompoundDelimiter`.
- NEW GRAMMAR pipeline: token kind → lexer → AST node → parser production → emitter case → tests at EACH layer.

## AST (Ast/*.cs)
- all nodes extend `BashNode`, immutable records.
- `CompoundWord` wraps `ImmutableArray<WordPart>`; never raw strings for parsed words.
- `Command.Simple.HereDocs` is `ImmutableArray<HereDoc>` (multiple `<<` + `<<<`); emitter uses the LAST for stdin.
