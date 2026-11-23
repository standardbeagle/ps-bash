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
| `AmpGreat`      | `&>`                 | `&>file`        |
| `AmpDGreat`     | `&>>`                | `&>>file`       |
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
| `WordPart.BracedVarSub` | `Name: string`, `Suffix: string?` | `${foo}`, `${foo:-default}`, `${#arr[@]}`, `${!arr[@]}`; a special parameter followed by an operator is that parameter (`${!:+pid}`, `${?:-q}`, `${#:-z}`, `${$%1}`: `BashParser.IsSpecialParameterWithOperator`), `${#!}` `${#$}` `${#?}` `${#-}` are its length (`#L`) |
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

**Leftover-token rule (every statement list).** A statement is an `and_or` plus an
optional `&`. After it only a terminator (`;` / newline), end of input, or the
enclosing construct's own closing token may follow: `}` in a brace group, `)` in a
subshell, the body's stop word (`fi`/`elif`/`else`, `done`) in if/loop bodies, and
`;;` / `;&` / `;;&` / `esac` in a case arm. A `&` is itself a terminator, so anything
may follow it (also inside bodies: `{ cmd & }`). Any other token means the statement
parser stopped early, and `ParseListStatement` throws a `ParseException` naming the
token and its line/column. The top-level loops (`ParseList`,
`ParseTopLevelWithPositions`) and all body loops share this one helper. Before it,
the top level silently dropped the rest of the script and a body re-read the
leftover as a new command (`rm -f !(keep).txt` → `rm -f !`, a subshell, `.txt`).

Constructs that still end in this error by design (unsupported grammar, loud rather
than truncated): the `time` / `coproc` keywords before a compound (`time { …; }`),
extglob `!(…)` / `@(…)` in a command word, and a `}` argument (`echo hi }` — bash
prints `hi }`, but the lexer splits `}x` and the emitter does not quote a bare `}`).

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

redirect     -> fd_prefix? ( file_op WORD | '<<<' WORD | ('<<' | '<<-') DELIMITER )
fd_prefix    -> IO_NUMBER | NAMED_FD            # `3`, `{name}` - not before '&>' / '&>>'
file_op      -> '<' | '>' | '>|' | '>>' | '<>' | '<&' | '>&' | '&>' | '&>>'
```

#### Redirections (one table)

`RedirectOperators` (Transpiler `Parser/RedirectOperators.cs`) is THE list of redirection operators. The lexer asks it
which digit word before an operator is its descriptor (`IsRedirectKind` -> `TakesFdPrefix`), and the parser asks it which
tokens start a redirection everywhere one may appear: among a simple command's words, after a compound command's
closing keyword (`done`, `}`, `fi`, `)`, ... via `ParseTrailingRedirects`) and among a declaration builtin's operands.
INVARIANTS:
- every operator takes an optional `N` or `{name}` prefix on every kind of command (`&>` / `&>>` take none: they name
  stdout and stderr both, and bash reads `2&>f` as the word `2` then `&>f`);
- there is ONE production, `ParseRedirect`, for every command kind; a here-document (Op `<<`, for `<<` and `<<-`) or
  here-string (Op `<<<`) carries its text in `Redirect.Here`. On a simple command a here-text on fd 0 with no `{name}` is
  the command's stdin (`Command.Simple.HereDocs`, the last one wins); on any other descriptor it is a `Redirect` that
  opens that descriptor (`read -u 3 l 3<<< x`, `exec {hd}<<EOF`), emitted by `PsEmitter.FdOpenStep` as text
  (`Invoke-BashExecFd N '<<<' (text)`);
- `<>` is a `Less` token whose `Redirect.ReadWrite` is set (`OpenOperator` = `<>`); `N>&M-` / `N<&M-` (a move) is the
  dup operator with the target word `M-` (`PsEmitter.MovedFd`).

The lists used to be separate and disagreed: `3<<<` was not an IO number (`read -u 3 b 3<<< x` passed `3` as a name),
`{v}<<<` on a simple command ran `{v}`, a compound command's `3<<< x` / `3<<EOF` did not parse.
Guard: Host.Tests `RedirectOperatorTableGuardTests` (every operator x no prefix / `N` / `{name}` x simple / group / loop /
`exec`: a redirection on the AST and valid PowerShell, generated from the table).

Special handling:
- `export` / `local` followed by `ASSIGNMENT_WORD` produces `ShAssignment` (with `IsLocal` flag for `local`).
- If only assignments appear with no command words, the result is `ShAssignment` instead of `Simple`.
- Array assignments (`arr=(a b c)`) are detected when `ASSIGNMENT_WORD` has no value and is followed by `LParen`. **Newlines inside `( )` are whitespace**, so the common multi-line form parses as one literal; breaking on the newline used to close the array early and read the next element as a command. An assignment-shaped element (`arr=(k=v other)`) is an ordinary element. Any other token before the `)` (`arr=(a ; b)`, end of input) throws.

**Declaration builtins** (`export`, `local`, `declare`, `typeset`, `readonly`) share
`ParseDeclarationPairs`, which consumes an interleaved run of `NAME=VAL`, `NAME=(array)`,
and bare `NAME` operands:
- attribute flags (`-a`, `-r`, `-i`, `--`) are SKIPPED, never taken as variable names
  (taking them as names emitted the junk statement `$-a = $-a`);
- an operand that is not a valid bash NAME makes the whole path DECLINE, so the caller
  rewinds to the general command path. `export PATH \`envsubst -v "$1"\`` exports names
  computed at runtime — un-modelable statically — and taking the raw token as a name
  emitted unparseable PowerShell, poisoning the whole file;
- redirects among the operands (`export PATH 2>/dev/null`, `local x=1 >/dev/null`)
  are consumed and dropped. A successful declaration writes nothing, and a value's
  command substitution is expanded before redirections, so its stderr is not covered
  (oracle-checked). Known gap: bash still creates/truncates a `> file` target;
- `declare` / `typeset` / `readonly` only route here when a `NAME=(` initializer is
  actually present; every other form stays on the emitter's `TryEmitDeclare` attribute
  path (`-i`, `-A`, `-p`).

### 3.4 If / Elif / Else

```
if_command -> 'if' if_arm ('elif' if_arm)* ('else' body)? 'fi'
if_arm     -> and_or TERM 'then' TERM body
body       -> and_or (TERM and_or)* '&'?
TERM       -> ';' | NEWLINE | '&'        ('&' also backgrounds the preceding and_or)
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

After the pattern's `)` the parser skips **newlines only**, never semicolons. An arm
with an EMPTY body (`x) ;;`) is legal bash, and skipping terminators there ate the `;;`
— the parser then read the NEXT arm's pattern as a command word and threw on its `)`.
A lone `;` immediately after `)` is a bash syntax error, so nothing else is skippable.

An empty pattern (`case x in ) esac`, or an empty alternative `a|)`) is rejected rather
than becoming a match-nothing arm.

### 3.8 Function Definition

```
function_def     -> 'function' WORD '(' ')'? TERM brace_group
parens_function  -> WORD '(' ')' TERM brace_group
brace_group      -> '{' body '}'
```

Both forms produce `Command.ShFunction`. The body may be any compound command. A
redirect after a `{ … }` body (`f() { …; } >/dev/null`) belongs to the definition and
applies on every call; the body then becomes a `Command.BraceGroup` carrying those
redirects (other compound bodies consume their own trailing redirects).

### 3.9 Subshell and Brace Group

```
subshell    -> '(' body ')' redirect*
brace_group -> '{' body '}'
```

Every compound's trailing redirects come from the ONE `ParseTrailingRedirects`, which also
accepts a here-string `<<< word` and a here-document `<< DELIM` (`( cat ) <<< hi`,
`{ sort; } <<EOF`): both become a `Redirect` with `Fd = 0` and `Here` = the body
(`Op` `"<<<"` / `"<<"`), the compound's stdin like `< file`. (The subshell used to carry a
private copy of the loop that knew neither, so they were parse errors.)

### 3.10 Test Expression

```
test_expr -> '[' inner_word* ']'
           | '[[' inner_word* ']]'
```

Inside `[[ ]]`, `&&` and `||` tokens are consumed as literal words (logical operators, not shell operators). `<`, `>`, and `!` are also consumed as literal words inside both forms. `!=` is assembled from `Bang` + `Word` starting with `=` only when the two tokens are ADJACENT (no whitespace): `[ a != b ]` is the operator, but `[ ! = x ]` / `[ ! = ! ]` keep three words (negation/operand, binary `=`, operand — bash reads the middle word as the operator).

**Grouping parens.** Inside `[[ ]]`, `(` / `)` are consumed as literal words
(`[[ ! -e $f || ( -f $f && ! -L $f ) ]]`). The emitter's `SplitLogical` only breaks on
`&&` / `||` at paren **depth 0**, so a group stays one operand and its association is
preserved, and `TryUnwrapTestGroup` strips one fully-enclosing pair before recursing.
Previously the parser broke on the paren, dropping the group's operands so the clause
silently collapsed to a constant.

**The `=~` right-hand side is a regex, not a token stream.** The lexer is context-free
and splits `^(a|b)$` into `LParen` / `Pipe` / `RParen`, so consuming tokens would stop at
the `(`. `ParseTestExpr` instead re-reads the word after `=~` straight from the source
(`ConsumeRegexOperand`) as one whitespace-delimited, quote-aware word, then skips the
tokens inside that span. This mirrors bash, where `(` groups in a `[[ ]]` condition
*except* after `=~`. Because it reads source, not tokens, the scan itself refuses the shell
operators bash still sees there: an unquoted `;` / `&` at its start and an unquoted `<` / `>`
anywhere outside a regex group (`[[ x =~ <> ]]`, `[[ x =~ a<b ]]`: "unexpected token", status 2;
`(<)`, `\<`, `'a<'` are regex text).

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

**Command substitution as an operand** (`$(( $(date +%s) + 60 ))`, and the
backtick spelling) parses to an opaque `ArithmeticExpr.CommandSub` node — the
same "keep an unevaluable form for the runtime handoff" treatment as
`${#arr[@]}`. It is opaque because bash expands the substitution to TEXT and only
then evaluates, so the command must run before the arithmetic evaluator sees the
expression at all. The emitter therefore splices each substitution's runtime value
into the string it hands to `Invoke-BashArith` (see `emitter-strategy.md` §6);
a `CommandSub` node reaching `BashArith.Evaluate` is an error, not a silent zero.
`$((…))` nested inside arithmetic is NOT a command substitution: the lexer drops
the `$`, leaving `((…))` for the existing paren grouping.

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

A `$"…"` (locale quoting) or `$'…'` anywhere in a word is that quoting, not a literal `$`: `a$"*"` is `a*`,
`a$'\x41'b` is `aAb`. Inside `"${v:-…}"` a `$'…'` keeps its ANSI-C meaning (bash `extquote`, on by default).

#### Parameter expansion extent

**Invariant: one scanner, `BashLexer.DollarBraceClose`, decides where a `${…}` ends**, for the lexer's words, the word
parser (`ParseBracedVar` reads only inside that extent), assignment-value `:` splitting, subscripts, heredoc bodies,
`$( )` bodies and arithmetic. bash's rule (parse_matched_pair, P_FIRSTCLOSE): the FIRST unquoted, unescaped `}` closes
it; a bare `{` in the operand does not nest, only a nested `${…}` does; quotes, `\x`, `$'…'`, `$( )`, `$(( ))` and
backticks are taken whole. So `${x/ /{1,2\}}` is one expansion (replacement `{1,2}`), `${u:-{a,b}}` is `${u:-{a,b}`
then a literal `}`, and `$(echo ${u:-)})` prints `)`. A single quote in the operand quotes even inside double quotes
or a heredoc (`${u:-it's}` there has no closing `}`; bash fails that heredoc's command with a bad substitution,
ps-bash still prints the body with the `${` literal: open, the heredoc emitter has no command abandon for it). Brace expansion is different: its scan (bash brace_gobbler)
counts a `${`'s braces as nesting, so `{a,${u:-{b}}` has no closing brace and stays literal. Guard
`DollarBraceExtentGuardTests` (lexer extent = parser extent = expected, and the text after the expansion is untouched,
for generated operands with braces, escapes and quotes).

#### Tilde prefixes

`~`, `~/x`, `~+`, `~-` expand at the start of a word and, in an assignment value, after each unquoted `:`
(`w=a:~+:~-` is `a:$PWD:$OLDPWD`). `~user` is that login's home directory from the passwd database
(`Platform/UserHome`, `/etc/passwd`), a quoted text (not split or globbed); with no entry it stays `~user`. Windows has
no passwd database: there only the current user's name resolves, to `$HOME` (docs/specs/intentional-differences.md).

---

## 4. Special Bash Variables

The parser recognizes bash special variables and maps them to PowerShell equivalents during emission:

| Bash Variable | PowerShell Emission | Notes |
|---------------|---------------------|-------|
| `$?` | `$LASTEXITCODE` | Exit status of last command |
| `$@` / `$*` | `$(if ($global:BashPositional) { $global:BashPositional } else { $args })` | Positional parameters (with `set --` support) |
| `$#` | `$(if ($global:BashPositional) { $global:BashPositional.Count } else { $args.Count })` | Number of positional parameters |
| `$0` | `$(if ($global:BashPositional0) { $global:BashPositional0 } else { 'bash' })` | The `-c CMD NAME` name or script path exactly as given (launcher sets `BashPositional0`); `bash` when absent; unchanged inside functions |
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

The body is sliced from the raw source by the parser (`BashParser.HereDocument`, from the span the lexer stamped on the
delimiter token), which also applies `<<-` tab stripping there — `HereDoc.Body` is final; the emitter does not
strip again. A body delimited by end of input (no delimiter line, no trailing newline)
still gets the final `\n` bash gives every body line.

**Line continuation (unquoted delimiter).** bash reads an unquoted here-document's lines with backslash-newline removed:
before the `<<-` tab strip (which then strips only the joined line's start), before the delimiter match and before
expansion. So `a $x\` + `b` is `a $xb` (`$xb` unset: `a `), `<<-` `TAB t1\` + `TAB t2` is `t1 TAB t2`, and `a\` then
`EOF` is the body line `aEOF`, not the end. A backslash takes the next character with it: `\\` before a newline is a
literal backslash and the newline stays. Of the other escapes only `\$`, `` \` `` and `\\` are special (left for
expansion); `\a`, `\"`, `\'`, `\{` keep their backslash. A quoted delimiter (`'EOF'`, `"EOF"`, `\EOF`) keeps every
backslash-newline. ONE rule: `BashLexer.JoinHereDocContinuations`, used by the lexer's delimiter match
(`SkipHeredocBody`, also inside `$( )`) and by the parser's body. Tests: Core.Tests `HereDocContinuationTests`,
Differential `RedirectOperatorDifferentialTests.HereDocument_BackslashNewline_IsALineContinuation`.

### 6.3 Brace Expansion vs Literal Braces

`{` is classified as a brace expansion word (not `LBrace`) when `IsBraceExpansion` detects content with `,` or `..` before the closing `}` with no unquoted whitespace inside:

```bash
echo {a,b,c}   # Word("{a,b,c}") -- brace expansion
echo {1..5}    # Word("{1..5}")   -- range expansion
{ cmd; }       # LBrace ... RBrace -- brace group (no comma/dotdot)
echo '{a,b}'   # Word("'{a,b}'") -- single-quoted, braces are literal
```

**Brace sequence expressions.** In the word parser a `{…}` expands only when it holds a top-level `,` or IS a sequence
expression `{x..y[..incr]}`; any other pair is literal text while the rest of the word expands (`n=3; echo {1..$n}`
prints `{1..3}`, `{1..3..$n}` prints `{1..3..3}`, `{x{a,b}y}` is `{xay} {xby}`). **Invariant: one expander,
`Parser/BraceSequence.cs`, decides whether a body is a sequence and makes its words**, for the top-level form
(`BracedRange`, whose words the emitter takes from it too) and a nested one (`{x,{1..10..3}}`, `{a,b{0001..3}}`). bash
(braces.c expand_seqterm): both ends integers (optional sign) or both single ASCII letters; the increment is an integer,
its sign ignored and 0 meaning 1; an end written with a leading zero (`01`, `-05`) pads every word to the widest end,
sign included (`{-01..2}` is `-01 000 001 002`). A letter sequence runs through the ASCII characters between its ends
(`{Z..b..3}` is `Z ] ``), a generated `\` leaves an empty word, and a generated backtick with text after it in its word
(`-{z..A}-`) is a bad substitution. Guard `BraceSequenceGuardTests` (nested = top-level = oracle for a table of
sequences).

**Empty words.** An item's quotes are removed at parse time, so `{"",a}` and `{,a}` both hold an empty item;
`BracedTuple.QuotedEmpty` records which empty items were quoted. **Invariant: a word brace expansion produces is
dropped when it has no quoted part and expands to nothing**, as bash removes an empty unquoted word: `{a,}` is one
word, `{,}` none, `$e{a,}` none with `e` unset, while `{"",a}`, `''{a,}` and `{"$e",b}` keep their empty word. One
rule, `PsEmitter.DropsEmptyBraceWord`, for every path of `EmitBraceExpandedWord`: a literal word is dropped at transpile
time, a word holding an expansion when it runs. Tests: `PsEmitterTests.Transpile_EmptyBraceWord_DroppedUnlessQuoted`,
`GlobBraceExpansionDifferentialTests.Differential_BraceTuple_TrailingComma_EmptyItem`.
### 6.4 Process Substitution vs Redirect

`<(` and `>(` are detected **before** the single-character `<`/`>` operator check. The entire `<(...)` or `>(...)` is consumed as a single `Word` token, then decomposed into `WordPart.ProcessSub` during word parsing.

```bash
diff <(cmd1) <(cmd2)   # Two Word tokens containing process substitutions
cmd > file             # Great + Word("file") -- normal redirect
```

### 6.5 Array Subscripts

A subscript ends at the `]` that closes its `[`, found by ONE scanner, `BashLexer.SubscriptClose` (bash
`skipsubscript`): brackets nest, and quoted text (`'…'`, `"…"`, `$'…'`), a backslash-escaped character and
`$( )` / `${ }` / `` `…` `` are taken whole, so a `]` or `=` inside them is subscript text. Its readers: the lexer
(`OpenSubscriptEnd`, `ClassifyWord`), the parser (`FindAssignmentEquals` for `NAME[sub]=v`, the `[KEY]=` split of
an array-literal element in `ParseAssignmentWordWithArray`) and the emitter, which reads an element's parts through
`ArraySubscript.TrySplit` (literal and glob text scanned, every other part one opaque character) and a
`declare a[x]=y` operand through `TrySplitElementInitializer`.

```bash
declare -A m=(['a]=b']=c)   # key a]=b, value c   (the first `]=` gave key 'a, value b']=c)
m['x]=y']=1                 # key x]=y
```

Guard: `ArraySubscriptTests.Transpiler_FindsNoSubscriptEndOutsideTheOneScanner` (no `"]="` search, no
`…+\]=` regex in transpiler code).

### 6.6 Double-Quote Backslash Rules

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
