---
name: add-grammar
description: Add new grammar production to the bash parser (new token, AST node, or syntax)
disable-model-invocation: true
---

文言：token→詞法→AST record→parser production→emitter case→各層測試。皆在 PsBash.Transpiler/Parser。

Add grammar for: $ARGUMENTS · Ref: @docs/specs/parser-grammar.md · all files under `src/PsBash.Transpiler/Parser/`

## STEPS
1. **Token** (new op/keyword): add to `BashTokenKind` in `BashToken.cs`.
2. **Lexer** (`BashLexer.cs`): multi-char ops longest-first (`<<<` before `<<`); mind IoNumber adjacency. Test `BashLexerTests.cs`.
3. **AST** (`Ast/Commands.cs` or `Ast/Words.cs`): new sealed record on `Command`/`WordPart`; `ImmutableArray<T>` collections; XML doc cites Oils ASDL if any.
4. **Parser** (`BashParser.cs`): handle in the right parse method (`ParseSimple`/`ParseCompound`…); new compound keyword → `IsCompoundDelimiter`. Test `BashParserTests.cs`.
5. **Emitter** (`PsEmitter.cs`): case in `Emit` for new `Command.*`; case in `EmitWordPart` for new `WordPart.*`. Test `PsEmitterTests.cs`.
6. **Run**: `./scripts/test.sh`.
