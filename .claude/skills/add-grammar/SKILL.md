---
name: add-grammar
description: Add new grammar production to the bash parser (new token, AST node, or syntax)
disable-model-invocation: true
---

Add grammar support for: $ARGUMENTS

Reference: @docs/specs/parser-grammar.md

## Steps

1. **Add token kind** (if new operator/keyword): In `BashToken.cs`, add to `BashTokenKind` enum

2. **Update lexer**: In `BashLexer.cs`:
   - Multi-char operators: check longest-first (e.g., `<<<` before `<<`)
   - Remember adjacency rules for IoNumber reclassification
   - Add lexer test in `BashLexerTests.cs`

3. **Add AST node** (if new syntax construct): In `Ast/Commands.cs` or `Ast/Words.cs`:
   - Extend `Command` or `WordPart` with a new sealed record
   - Use `ImmutableArray<T>` for collections
   - Add XML doc comment referencing Oils ASDL equivalent if applicable

4. **Add parser production**: In `BashParser.cs`:
   - Add handling in the appropriate parse method (`ParseSimple`, `ParseCompound`, etc.)
   - For new compound commands, add the keyword to `IsCompoundDelimiter`
   - Add parser test in `BashParserTests.cs`

5. **Add emitter case**: In `PsEmitter.cs`:
   - Add case to the `Emit` switch for new `Command.*` type
   - Add case to `EmitWordPart` for new `WordPart.*` type
   - Add emitter test in `PsEmitterTests.cs`

6. **Run full test suite**: `./scripts/test.sh`
