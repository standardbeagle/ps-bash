# Parser Grammar Specification (v2 AST)

This document specifies the lexer, grammar, and AST of the ps-bash v2 parser.
The design follows the [Oils/OSH `syntax.asdl`](https://github.com/oilshell/oil/blob/master/frontend/syntax.asdl) model.

Source files:
- `src/PsBash.Transpiler/Parser/BashToken.cs` -- token kinds
- `src/PsBash.Transpiler/Parser/BashLexer.cs` -- tokenization
- `src/PsBash.Transpiler/Parser/BashParser.cs` -- recursive-descent grammar
- `src/PsBash.Transpiler/Parser/Ast/Commands.cs` -- command AST nodes
- `src/PsBash.Transpiler/Parser/Ast/Words.cs` -- word-part AST nodes
- `src/PsBash.Transpiler/Parser/Ast/Redirects.cs` -- redirect, heredoc, assignment nodes

---

## 1. Token Reference

| BashTokenKind   | Matches              | Example         |
|-----------------|----------------------|-----------------|
| `Word`          | Unquoted/quoted word | `hello`, `"$x"` |
| `AssignmentWord`| `NAME=val`, `NAME+=val`, `NAME[k]=val` | `x=1`, `arr+=('a')` |
| `Newline`       | `\n` or `\r\n`       | newline         |
| `Semi`          | `;`                  | `;`             |
| `Amp`           | `&`                  | `&`             |
| `Pipe`          | `\|`                 | `\|`            |
| `PipeAmp`       | `\|&`                | `\|&`           |
| `AndIf`         | `&&`                 | `&&`            |
| `OrIf`          | `\|\|`               | `\|\|`          |
| `LParen`        | `(`                  | `(`             |
| `RParen`        | `)`                  | `)`             |
| `LBrace`        | `{`                  | `{`             |
| `RBrace`        | `}`                  | `}`             |
| `Less`          | `<`                  | `<`             |
| `Great`         | `>`                  | `>`             |
| `DLess`         | `<<`                 | `<<`            |
| `DGreat`        | `>>`                 | `>>`            |
| `LessAnd`       | `<&`                 | `<&`            |
| `GreatAnd`      | `>&`                 | `>&`            |
| `DLessDash`     | `<<-`                | `<<-`           |
| `TLess`         | `<<<`                | `<<<`           |
| `Bang`          | `!`                  | `!`             |
| `IoNumber`      | Digit word reclassified before redirect | `2` in `2>` |
| `Eof`           | End of input         |                 |

Reserved words are recognized contextually by the parser, not as distinct token kinds:
`if`, `then`, `else`, `elif`, `fi`, `do`, `done`, `case`, `esac`, `while`, `until`, `for`, `in`, `function`.

---

## 2. AST Node Reference

### 2.1 Command Variants

| Node | Fields | Description |
|------|--------|-------------|
| `Command.Simple` | `Words: CompoundWord[]`, `EnvPairs: EnvPair[]`, `Redirects: Redirect[]`, `HereDocs: HereDoc[]` | Simple command with optional env prefix, redirects, and heredocs |
| `Command.Pipeline` | `Commands: Command[]`, `Ops: string[]` (`"\|"` or `"\|&"`), `Negated: bool` | Pipeline of commands |
| `Command.AndOrList` | `Commands: Command[]`, `Ops: string[]` (`"&&"` or `"\|\|"`) | And-or list |
| `Command.CommandList` | `Commands: Command[]` | Sequential commands separated by `;` or newline |
| `Command.ShAssignment` | `Pairs: Assignment[]`, `IsLocal: bool` | Bare assignment (`x=1`) or `local`/`export` assignment |
| `Command.If` | `Arms: IfArm[]`, `ElseBody: Command?` | If/elif/else construct |
| `Command.BoolExpr` | `Inner: CompoundWord[]`, `Extended: bool` | `[ ... ]` or `[[ ... ]]` test expression |
| `Command.ForIn` | `Var: string`, `List: CompoundWord[]`, `Body: Command` | `for x in a b; do ...; done` |
| `Command.ForArith` | `Init: ArithmeticSyntax?`, `Cond: ArithmeticSyntax?`, `Step: ArithmeticSyntax?`, `Body: Command` | `for ((i=0; i<n; i++)); do ...; done` |
| `Command.While` | `IsUntil: bool`, `Cond: Command`, `Body: Command` | `while`/`until` loop |
| `Command.Case` | `Expr: CompoundWord`, `Arms: CaseArm[]` | `case $x in ...) ;; esac` |
| `Command.ArithCommand` | `Expr: ArithmeticSyntax` | Standalone `(( expr ))` |
| `Command.ShFunction` | `Name: string`, `Body: Command` | `function f { ... }` or `f() { ... }` |
| `Command.Subshell` | `Body: Command`, `Redirects: Redirect[]` | `( cmd1; cmd2 )` with optional trailing redirects |
| `Command.BraceGroup` | `Body: Command` | `{ cmd1; cmd2; }` |

### 2.2 Supporting Types

| Node | Fields | Description |
|------|--------|-------------|
| `IfArm` | `Cond: Command`, `Body: Command` | Single if/elif arm |
| `CaseArm` | `Patterns: string[]`, `Body: Command` | Single case arm with `\|`-separated patterns |
| `Redirect` | `Op: string`, `Fd: int`, `Target: CompoundWord` | Redirect operation (default fd: 0 for `<`, 1 for `>`) |
| `HereDoc` | `Body: string`, `Expand: bool`, `StripTabs: bool` | Here-document or here-string body |
| `Assignment` | `Name: string`, `Op: AssignOp`, `Value: CompoundWord?`, `ArrayValue: ArrayWord?` | Variable assignment |
| `EnvPair` | `Name: string`, `Value: CompoundWord?` | Env prefix for simple commands |
| `ArrayWord` | `Elements: CompoundWord[]` | Array literal `(a b c)` |
| `AssignOp` | `Equal` or `PlusEqual` | `=` vs `+=` |

### 2.3 WordPart Variants

| Node | Fields | Description |
|------|--------|-------------|
| `WordPart.Literal` | `Value: string` | Unquoted literal text |
| `WordPart.EscapedLiteral` | `Value: string` | Backslash-escaped character: `\$` -> `$` |
| `WordPart.SingleQuoted` | `Value: string` | Content inside single quotes (no expansion) |
| `WordPart.AnsiCQuoted` | `Value: string` | `$'...'` ANSI-C quoting; raw inner text, C-escapes (`\n`, `\t`, `\xHH`, `\nnn`, `\uHHHH`, `\cX`) expanded by the emitter |
| `WordPart.DoubleQuoted` | `Parts: WordPart[]` | Content inside double quotes (with expansion) |
| `WordPart.SimpleVarSub` | `Name: string` | `$foo`, `$?`, `$!`, `$#`, `$$`, `$@`, `$*`, `$-`, `$0`-`$9`, `$SECONDS`, `$PPID`, `$BASH_VERSION` |
| `WordPart.BracedVarSub` | `Name: string`, `Suffix: string?` | `${foo}`, `${foo:-default}`, `${#arr[@]}`, `${!arr[@]}` |
| `WordPart.CommandSub` | `Body: BashNode` | `$(cmd)` or `` `cmd` `` -- body is recursively parsed |
| `WordPart.ArithSub` | `Expr: ArithmeticSyntax` | `$(( x + 1 ))` |
| `WordPart.TildeSub` | `User: string?` | `~` (null user = current) or `~user` |
| `WordPart.GlobPart` | `Pattern: string` | `*`, `?`, `[abc]`, `+(*.py\|*.js)` |
| `WordPart.BracedTuple` | `Items: string[]` | `{a,b,c}` |
| `WordPart.BracedRange` | `Start: int`, `End: int`, `ZeroPad: int`, `Step: int` | `{1..10}`, `{01..05}`, `{1..10..2}` |
| `WordPart.ProcessSub` | `Body: BashNode`, `IsInput: bool` | `<(cmd)` (IsInput=true) or `>(cmd)` (IsInput=false) |

---

## 3. Grammar Productions

The parser is a hand-rolled recursive-descent parser consuming the flat token list from `BashLexer`.

### 3.1 Top-Level

```
input       -> list EOF
list        -> and_or (';' and_or)* ';'?
and_or      -> pipeline (('&&' | '||') pipeline)*
pipeline    -> compound_or_simple (('|' | '|&') compound_or_simple)*
```

`|&` (stderr-merge pipe) is a distinct `PipeAmp` token; the ops array stores `"|&"`.

### 3.2 Compound-or-Simple Dispatch

`ParseCompoundOrSimple` dispatches on the current token:

```
compound_or_simple ->
    | 'if'       -> if_command
    | 'for'      -> for_command
    | 'while'    -> while_command
    | 'until'    -> while_command   (IsUntil=true)
    | 'case'     -> case_command
    | 'function' -> function_def
    | '[' | '[[' -> test_expr
    | WORD '(' ')' -> parens_function_def
    | '(' '('    -> arith_command
    | '('        -> subshell
    | '{'        -> brace_group
    | _          -> simple_command
```

### 3.3 Simple Command

```
simple_command -> assignment_prefix* word_or_redirect*

assignment_prefix -> ASSIGNMENT_WORD ('(' word* ')')?
word_or_redirect  -> WORD | redirect | here_string | heredoc_operator

redirect     -> IO_NUMBER? redirect_op WORD
redirect_op  -> '<' | '>' | '>>' | '<&' | '>&' | '<<' | '<<-'
here_string  -> '<<<' WORD
heredoc_op   -> ('<<' | '<<-') DELIMITER
```

Special handling:
- `export` / `local` followed by `ASSIGNMENT_WORD` produces `ShAssignment` (with `IsLocal` flag for `local`).
- If only assignments appear with no command words, the result is `ShAssignment` instead of `Simple`.
- Array assignments (`arr=(a b c)`) are detected when `ASSIGNMENT_WORD` has no value and is followed by `LParen`. **Newlines inside `( )` are whitespace**, so the common multi-line form parses as one literal; breaking on the newline used to close the array early and read the next element as a command.

**Declaration builtins** (`export`, `local`, `declare`, `typeset`, `readonly`) share
`ParseDeclarationPairs`, which consumes an interleaved run of `NAME=VAL`, `NAME=(array)`,
and bare `NAME` operands:
- attribute flags (`-a`, `-r`, `-i`, `--`) are SKIPPED, never taken as variable names
  (taking them as names emitted the junk statement `$-a = $-a`);
- an operand that is not a valid bash NAME makes the whole path DECLINE, so the caller
  rewinds to the general command path. `export PATH \`envsubst -v "$1"\`` exports names
  computed at runtime — un-modelable statically — and taking the raw token as a name
  emitted unparseable PowerShell, poisoning the whole file;
- `declare` / `typeset` / `readonly` only route here when a `NAME=(` initializer is
  actually present; every other form stays on the emitter's `TryEmitDeclare` attribute
  path (`-i`, `-A`, `-p`).

### 3.4 If / Elif / Else

```
if_command -> 'if' if_arm ('elif' if_arm)* ('else' body)? 'fi'
if_arm     -> and_or TERM 'then' TERM body
body       -> and_or (TERM and_or)*
TERM       -> ';' | NEWLINE
```

### 3.5 For Loops

```
for_in     -> 'for' WORD ('in' word*)? TERM 'do' body 'done'
for_arith  -> 'for' '(' '(' clause ';' clause ';' clause ')' ')' TERM 'do' body 'done'
```

When `in` is absent, `List` is empty (implicit `$@`).

### 3.6 While / Until

```
while_command -> ('while' | 'until') and_or TERM 'do' body 'done'
```

### 3.7 Case

```
case_command -> 'case' WORD TERM 'in' TERM case_arm* 'esac'
case_arm     -> '('? pattern ('|' pattern)* ')' body ';;'?
pattern      -> token*   (collected as raw text until '|' or ')')
```

The `;;` terminator is detected as two consecutive `Semi` tokens.

### 3.8 Function Definition

```
function_def     -> 'function' WORD '(' ')'? TERM brace_group
parens_function  -> WORD '(' ')' TERM brace_group
brace_group      -> '{' body '}'
```

Both forms produce `Command.ShFunction`.

### 3.9 Subshell and Brace Group

```
subshell    -> '(' body ')' redirect*
brace_group -> '{' body '}'
```

### 3.10 Test Expression

```
test_expr -> '[' inner_word* ']'
           | '[[' inner_word* ']]'
```

Inside `[[ ]]`, `&&` and `||` tokens are consumed as literal words (logical operators, not shell operators). `<`, `>`, and `!` are also consumed as literal words inside both forms. `!=` is assembled from `Bang` + `Word` starting with `=`.

**The `=~` right-hand side is a regex, not a token stream.** The lexer is context-free
and splits `^(a|b)$` into `LParen` / `Pipe` / `RParen`, so consuming tokens would stop at
the `(`. `ParseTestExpr` instead re-reads the word after `=~` straight from the source
(`ConsumeRegexOperand`) as one whitespace-delimited, quote-aware word, then skips the
tokens inside that span. This mirrors bash, where `(` groups in a `[[ ]]` condition
*except* after `=~`.

That word is decomposed by `DecomposeRegexWord`, not the ordinary word decomposer:

- backslashes pass through verbatim (`\.` must stay an escaped dot — the ordinary
  decomposer dropped it, silently widening the pattern to "any character");
- a bare `$` is a regex anchor — `$` starts an expansion only before `{`, `(`, or a name
  character, so `(\s|$)` survives while `$re` still expands;
- POSIX bracket classes are translated for .NET (`TranslatePosixClassesForRegex`):
  `[[:digit:]]` → `[0-9]`, `[[:space:]]` → `[\s]`. .NET regex has no POSIX classes and
  would read `[[:digit:]]` as the set `[:digt`. Unknown class names are left alone.

The emitter passes a **sole** expansion RHS through bare (`$x -match $env:re`) rather
than single-quoting it, since `re='^a.b$'; [[ $x =~ $re ]]` is the idiomatic bash form.

### 3.11 Arithmetic Command

```
arith_command -> '(' '(' arithmetic_expr ')' ')'
```

The expression between `((` and `))` is parsed into `ArithmeticSyntax`, whose
typed `ArithmeticExpr` root preserves precedence, associativity, assignments,
and lazy conditional/logical branches. `ArithmeticSyntax.Source` retains the
original text for the single `Invoke-BashArith` runtime handoff shared with
arithmetic `for` clauses and `$((...))` substitution.

The typed arithmetic parameter subset supports unbraced named parameters,
single-digit positionals and special parameters (`$x`, `$0`-`$9`, `$#`, `$?`,
`$@`, `$*`, `$$`, `$!`) plus their simple braced forms (`${x}`, `${10}`,
`${?}`, and so on). Bash's unbraced rule is retained: `$10` expands `$1` and
then the literal `0`, whereas `${10}` addresses positional parameter 10.
Compound/default, length, indirect, substring, and array parameter expansions
are outside this typed arithmetic subset.

### 3.12 Word Decomposition

`DecomposeWord` sub-parses a single `Word` token's raw text into `WordPart` nodes:

```
compound_word -> word_part+
word_part     -> tilde_sub         (only at word start)
              | single_quoted      'content'
              | ansi_c_quoted      $'content with \escapes'
              | double_quoted      "content with $expansion"
              | escaped_literal    \c
              | arith_sub          $((expr))
              | command_sub        $(cmd)  or  `cmd`
              | braced_var_sub     ${name...}
              | simple_var_sub     $name  or  $?  $!  etc.
              | process_sub        <(cmd)  or  >(cmd)
              | glob_part          *  ?  [class]  +(extglob)
              | brace_expansion    {a,b,c}  or  {1..10}
              | literal            plain text
```

---

## 4. Special Bash Variables

The parser recognizes bash special variables and maps them to PowerShell equivalents during emission:

| Bash Variable | PowerShell Emission | Notes |
|---------------|---------------------|-------|
| `$?` | `$LASTEXITCODE` | Exit status of last command |
| `$@` / `$*` | `$(if ($global:BashPositional) { $global:BashPositional } else { $args })` | Positional parameters (with `set --` support) |
| `$#` | `$(if ($global:BashPositional) { $global:BashPositional.Count } else { $args.Count })` | Number of positional parameters |
| `$0` | `$MyInvocation.MyCommand.Name` | Script or function name |
| `$$` / `$!` | `$PID` / `$global:BashBgLastPid` | Current/background process ID |
| `$-` | `$global:BashFlags` | Shell flags |
| `$_` | `$global:BashLastArg` | Last argument of previous command |
| `$RANDOM` | `$(Get-Random -Maximum 32768)` | Random number 0-32767 |
| `$SECONDS` | `$([math]::Floor(([DateTime]::UtcNow - $global:BashStartTime).TotalSeconds))` | Seconds since shell started |
| `$PPID` | `(Get-Process -Id $PID -ErrorAction SilentlyContinue).Parent.Id` | Parent process ID |
| `$BASH_VERSION` | `$global:BashVersion` | Bash version string |
| `$BASH_VERSINFO` | `$global:BashVersionInfo` | Bash version array |
| `$HOME` | `$HOME` | Home directory (kept as-is) |
| `$PWD` | `$PWD` | Current directory (kept as-is) |
| `$1`-`$9` | `$(if ($global:BashPositional) { $global:BashPositional[N-1] } else { $args[N-1] })` | Positional parameters |
| Other names | `$env:NAME` | Environment variables |

The `set --` command (`set -- a b c`) resets positional parameters via `$global:BashPositional`, which is checked before falling back to PowerShell's `$args`.

---

## 6. Lexer Edge Cases

### 6.1 IoNumber Adjacency

A digit-only `Word` token is reclassified to `IoNumber` **only** when immediately adjacent to a redirect operator (zero whitespace between the token's end position and the operator's start position).

```bash
2>file     # IoNumber(2) Great Target(file) -- fd redirect
2 >file    # Word(2) Great Target(file) -- "2" is an argument, stdout redirect
```

Implementation: `TryReclassifyIoNumber` checks `last.Position + last.Value.Length == redirectPos`.

### 6.2 Here-string vs Heredoc vs Heredoc-Strip

The lexer checks three-character `<<<` before two-character prefixes:

```bash
cat <<< "hello"     # TLess -- here-string, word becomes body directly
cat <<EOF            # DLess -- heredoc, body collected until EOF line
cat <<-EOF           # DLessDash -- heredoc with leading-tab stripping
```

Heredoc delimiter quoting controls expansion:
- Unquoted `<<EOF` -> `Expand=true` (variables expanded)
- Quoted `<<'EOF'` or `<<"EOF"` -> `Expand=false` (literal body)

### 6.3 Brace Expansion vs Literal Braces

`{` is classified as a brace expansion word (not `LBrace`) when `IsBraceExpansion` detects content with `,` or `..` before the closing `}` with no unquoted whitespace inside:

```bash
echo {a,b,c}   # Word("{a,b,c}") -- brace expansion
echo {1..5}    # Word("{1..5}")   -- range expansion
{ cmd; }       # LBrace ... RBrace -- brace group (no comma/dotdot)
echo '{a,b}'   # Word("'{a,b}'") -- single-quoted, braces are literal
```

### 6.4 Process Substitution vs Redirect

`<(` and `>(` are detected **before** the single-character `<`/`>` operator check. The entire `<(...)` or `>(...)` is consumed as a single `Word` token, then decomposed into `WordPart.ProcessSub` during word parsing.

```bash
diff <(cmd1) <(cmd2)   # Two Word tokens containing process substitutions
cmd > file             # Great + Word("file") -- normal redirect
```

### 6.5 Double-Quote Backslash Rules

Inside double quotes, backslash is only special before `$`, `` ` ``, `"`, `\`, and newline. Before any other character, the backslash is literal:

```bash
echo "hello\nworld"    # Literal(\) Literal(n) -- backslash is preserved
echo "price: \$5"      # Literal($) Literal(5) -- backslash escapes $
```

---

## 7. Oils ASDL Gap Analysis

Oils `syntax.asdl` features that are absent, represented differently, or safely degraded:

| Oils Feature | ASDL Type | Status |
|-------------|-----------|--------|
| Coproc | `command.CoProcess` | No dedicated node or grammar; `coproc` is parsed as an ordinary `Command.Simple` |
| `select` loop | `command.Select` | Parsed as `Command.Select`; emitter safely degrades the unsupported interactive menu loop to a comment |
| `time` prefix | `command.TimeBlock` | No dedicated node or prefix grammar; `time` is parsed as an ordinary `Command.Simple` |
| Arithmetic expressions | `arith_expr`, `command.ForExpr` | Represented by typed `ArithmeticSyntax`/`ArithmeticExpr` nodes for `ForArith`, `ArithCommand`, and `ArithSub`; original source is retained for the shared runtime evaluator handoff |
| Typed array `declare -a`/`declare -A` | `command.Declare` | No dedicated node; `declare` is parsed as `Command.Simple`. Bare/local array assignments use `Command.ShAssignment` with `ArrayWord` |
| Extended test operators such as `[[ ... =~ ... ]]` | `BoolExpr` with typed ops | Parsed as `Command.BoolExpr`, but inner operands/operators are stored as `CompoundWord[]`; the emitter interprets `=~` |
| `trap` / `exec` | special builtins | Parsed as `Command.Simple`, with builtin-specific emitter handling where supported; no dedicated AST nodes |
| Oil/YSH-specific syntax (`var`, `const`, `proc`, `func`) | various | Not applicable (bash only) |
| Here-doc with multiple heredocs per line | `Redir[]` | Supported: `HereDocs` is an array; emitter uses last for stdin |

---

## 8. Adding New Grammar

Steps to add a new token or grammar production:

1. **Add the token kind** in `BashToken.cs` -- add a new member to `BashTokenKind`.

2. **Teach the lexer** in `BashLexer.cs`:
   - Multi-char operators: add to the two/three-character operator section (longer matches first).
   - If it is a redirect operator, add it to `IsRedirectKind` so `TryReclassifyIoNumber` fires.
   - If it is a word variant, adjust `ClassifyWord` or `ScanWord`.

3. **Add the AST node** in the appropriate file under `Ast/`:
   - Command variant: add a `sealed record` inside `Command` in `Commands.cs`.
   - Word variant: add inside `WordPart` in `Words.cs`.
   - Supporting type: add in `Redirects.cs` or a new file.

4. **Add the parser production** in `BashParser.cs`:
   - Register dispatch in `ParseCompoundOrSimple` for compound commands.
   - Add a `ParseXxx` method implementing the production.
   - Use `Expect(word)` for reserved-word terminals, `Advance()` for operator tokens.
   - Use `ParseCompoundBody(stopWords)` for bodies terminated by keywords.

5. **Add the emitter** in the corresponding `Emit` visitor to generate PowerShell output.

6. **Write tests** covering the new syntax:
   - Lexer test: verify token sequence in `BashLexerTests`.
   - Parser test: verify AST shape in `BashParserTests`.
   - Round-trip test: verify PowerShell output in the integration suite.
   - Run all tests via `./scripts/test.sh`.
