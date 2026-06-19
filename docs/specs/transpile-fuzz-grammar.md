# Transpile Fuzz Grammar & Parseability Contract

How ps-bash proves the transpiler **never emits broken PowerShell**. This is the spec
that drives the two generative fuzzers and the curated regression corpus in
`src/PsBash.Host.Tests/Transpiler/`.

Why this matters: Claude Code's Bash tool wraps **every** command it runs through ps-bash.
The host hands the transpiler's output to PowerShell's parser. If the emitted PowerShell is
syntactically invalid, the command fails *before it runs* with `ps-bash: parse error: …` —
so a single emitter gap silently breaks an entire class of agent commands. The differential /
oracle suites check *what the output is*; this layer checks the weaker, broader, and
release-critical property that **the output is always syntactically valid PowerShell** (or a
clean, well-formed error).

---

## 1. The Parseability Contract (the one invariant)

For **any** input string `s`, `BashTranspiler.Transpile(s)` must do exactly one of:

- **(A) Succeed** → return PowerShell that `System.Management.Automation.Language.Parser
  .ParseInput` accepts with **zero** `ParseError`s, **or**
- **(B) Reject cleanly** → throw a `ParseException` whose message is *high quality*:
  - `Message` is non-empty and not a bare runtime-exception string,
  - `Line >= 1` and `Column >= 1` (the error is positioned in the bash source),
  - `Rule` names the grammar production that failed (non-empty).

It must **never**:

- throw any exception other than `ParseException` (a raw `NullReferenceException`,
  `IndexOutOfRangeException`, `ArgumentException`, … is a crash bug), or
- return PowerShell that fails to parse (a *silent* broken-emission bug — the worst kind,
  because it reaches PowerShell and fails opaquely).

This single contract covers both well-formed bash (mostly branch A — exercises the emitter)
and garbage (mostly branch B — exercises error quality). Both fuzzers assert it verbatim via
`ParseabilityContract.Assert(input)`.

---

## 2. Test surfaces

| Surface | File | Role |
|---|---|---|
| Contract helper | `ParseabilityContract.cs` | The `Assert(input)` oracle (§1). Single source. |
| Wrapper regression | `WrapperParseabilityTests.cs` | The exact Claude Code Bash-tool wrapper shapes (external, changing input). |
| Weak-spot corpus | `TranspileParseabilityCorpusTests.cs` | Curated constructs, one per row, organized by §3 categories — every bug this layer ever caught becomes a permanent row. |
| Grammar fuzzer | `TranspileGrammarFuzzTests.cs` | Seeded generator of **valid** bash from the §3 grammar → must hit branch (A). |
| Garbage fuzzer | `TranspileGarbageFuzzTests.cs` | Seeded generator of **malformed** input (noise, mutation, structural torture) → must hit (A) or (B), never a crash / broken PS. |

All five are **ultra-fast and pure in-process**: `Transpile` + `Parser.ParseInput`, no host
spawn, no bash oracle, no IPC. The whole layer runs in well under a second for tens of
thousands of cases. Determinism (qa-rubric Directive 6): every fuzzer uses a **fixed seed**;
a failure prints the seed + the exact input + the emitted PowerShell + the parse error, so it
reproduces verbatim. No `Random()` without a seed, no time-based input.

---

## 3. The construct grammar (what the grammar fuzzer generates)

The generator is a recursive producer over these productions. Each is a documented weak
surface drawn from the bash spec, the Oils `syntax.asdl` model (see `parser-grammar.md`), and
— crucially — the bug history (every row below maps to at least one real fixed bug or a
plausible future one).

### 3.1 Words & quoting seams
- single `'…'`, double `"…"`, ANSI-C `$'…\n…'`, adjacent-quote concatenation `"a"'b'"c"`
- escaped literals `\$ \" \\ \``, literal `#` mid-word, backslash-space `a\ b`
- a `$var` / `"$var"` / `${var}` reference embedded in a larger word

### 3.2 Parameter expansion (the richest weak surface)
- defaults `${x:-w}` `${x:=w}` `${x:+w}` `${x:?m}` and colon-less `${x-w}` `${x=w}` `${x+w}` `${x?m}`
- length `${#x}`; case `${x^^}` `${x,,}` `${x^}` `${x,}`; `@`-transforms `${x@Q|U|L|u|l}`
- suffix/prefix removal `${x%p}` `${x%%p}` `${x#p}` `${x##p}`
- substitution `${x/a/b}` `${x//a/b}` `${x/#a/b}` `${x/%a/b}`
- substring `${x:o}` `${x:o:l}` `${x: -o}` (negative/space offsets)
- **nesting**: the argument word may itself be any expansion — `${a:-${b:-$(c)}}`
  (the class that broke pre-2026-06: an inner `($env:b ?? "z")` carried raw `"` into the
  surrounding `"…"`). Generated to bounded depth.

### 3.3 Arrays
- literal `arr=(a b c)`, append `arr+=(d)`, assoc `declare -A m; m[k]=v`
- `${arr[0]}` `${arr[@]}` `${arr[*]}` `${#arr[@]}` `${!arr[@]}` `${arr[@]:1:2}`
- quoted `"${arr[@]}"` in a `for … in` head

### 3.4 Command substitution
- `$(cmd)`, backtick `` `cmd` ``, nested `$(echo $(echo x))`
- **compound bodies**: `$(case … esac)` `$(for … done)` `$(if … fi)` `$(while … done)`
  — these emit a PowerShell *statement* (`switch`/`foreach`/`if`) that cannot head a
  pipeline; the emitter wraps them `& { … }` (the "empty pipe element" class).

### 3.5 Arithmetic
- `$((expr))` over `+ - * / % ** << >> & | ^ < <= > >= == != && || ?: , = ++ --`, hex `0x`,
  the `(( … ))` command form, and the C-style `for (( … ; … ; … ))` header.

### 3.6 Test expressions
- `[ … ]` and `[[ … ]]`: unary file tests (`-e -f -d -r -w -x -s -h -L …`), string `-z -n`,
  binary `= == != -eq -ne -lt -le -gt -ge`, regex `=~`, glob RHS, negation `!`
- POSIX combinators `[ a = b -a c = d ]` / `-o` (the multi-clause split class), and `[[ ]]`
  logical `&&` / `||`.

### 3.7 Brace expansion
- tuples `{a,b,c}`, ranges `{1..5}` `{01..10}` `{1..10..2}` `{a..e}`, adjacent cross-multiply
  `{a,b}{1,2}`, prefix/suffix `pre{a,b}post`, nesting `{a,{b,c},d}`.

### 3.8 Redirections
- `> >> < <<< 2>&1 &> >| 1>&2 2>/dev/null`, fd numbers, process sub `<(…)` `>(…)`.

### 3.9 Heredocs
- expanding `<<EOF`, literal `<<'EOF'`, tab-strip `<<-EOF`, backslash-quoted `<<\EOF`,
  bodies containing `" ' $(…) ` PowerShell-hostile chars, and a **trailing command after
  the terminator** (`<<EOF … EOF && echo after`).

### 3.10 Control flow & lists
- `if/elif/else/fi`, `for … in`, C-style `for (( ))`, `while`/`until`, `case` with `|`-patterns,
  functions `f() { }` / `function f { }`, subshell `( )`, brace group `{ ; }`
- pipelines `| |&`, negation `! cmd`, and-or `&& ||`, sequence `;`, background `&`.

### 3.11 Special parameters & env prefix
- `$? $@ $# $1..$9 ${10} $$ $! $- $_ $0 $RANDOM $HOME`, env prefix `FOO=bar BAZ=qux cmd`,
  `export` / `local` / `read -r` / `read -ra`.

---

## 4. Known gaps — malformed input that emits broken PowerShell (tracked)

These are inputs where the transpiler currently emits PowerShell that does **not** parse,
instead of throwing a clean `ParseException`. They are **all truncated / structurally
malformed bash** — input a real script never contains (it would be a bash syntax error too).
Because they are not valid bash, the grammar fuzzer never generates them and the curated corpus
never asserts them; they are exercised only by the garbage fuzzer, which asserts the SAFETY
floor (no crash / no hang) — so a future regression that turns one of these into a *crash* is
still caught. The strong "valid bash ⇒ valid PowerShell" guarantee already holds across the
entire covered construct grammar (grammar fuzzer + corpus, both fully green).

Tracked classes (the parser accepts the incomplete construct; the emitter then trusts it):

| Class | Example | Emits (broken) | Right fix |
|---|---|---|---|
| Empty / unterminated test | `[ `, `[[`, `[ $x =` | `$(if (()) …)` | require `]`/`]]` + complete predicate |
| Unterminated command-sub | `echo $(`, `` ` `` | `… $( \| ForEach-Object …` | require closing `)` / `` ` `` |
| Unterminated brace-var | `${`, `${#` | partial `$…` | require closing `}` |
| Truncated heredoc | `<<EOF` (no terminator) | `@"…"@ \| Emit-BashLine \|` | require the delimiter line |
| Dangling redirect | `> > x`, `2>&` | `… -Path >` | require a redirect target |
| Nameless function | `function` | `function  { … }` | require a function name |
| Literal brace in a bare word | `grep -e p f}ile` | `… f}ile` (bare `}`) | quote a literal-brace operand |
| Adjacent bare expansions | `$$$$` (`$$`+`$$`) | `$PID$PID` | join adjacent expansions in one string |

**Perf gap (tracked):** some deeply-nested metachar soup (`${[(…` chains) hits catastrophic
backtracking in word decomposition — a single ~24-char garbage string can take hundreds of ms
to transpile. It always *terminates* (no true infinite loop after the EOF-cursor fix), so it is
not a crash; the garbage fuzzer caps soup length at 20 and case counts so the suite stays fast.
A real fix is to bound the word-decomposition scanners. The `--blame-hang-timeout` backstop in CI
catches any genuine infinite loop regression.

When one is fixed: change the parser/emitter, then move a representative input from the garbage
torture list into `TranspileParseabilityCorpusTests` as a full-contract row (it then proves the
clean rejection / valid emission).

Fixed while introducing this layer (were broken, now pass the full contract): six `PsEmitter`
bugs (`$(case/for/if/while …)` compound command-sub, nested `${x:-${y:-z}}`, `[ … -a/-o … ]`
combinators, `declare -i n=5`), the `${VAR##pat}` single-quote escape (a grammar-fuzz find),
three EOF token-cursor RAW CRASHES (`for` / `function` / `2>&`), dangling `&&`/`||`/`|` operands,
and `true`/`false` as a redirected pipe target.

---

## 5. Adding to the corpus

1. A real transpile bug (Bash-tool break, differential failure, or a fuzzer find) → add one
   `[InlineData]` row to `TranspileParseabilityCorpusTests.cs` under its §3 category, with the
   bug in a comment. It must fail before the fix and pass after (qa-rubric Directive 7,
   testing.md "BUG FIX = REGRESSION TEST").
2. A whole new construct family the emitter learns → add a §3 production + teach the grammar
   generator one new branch + a corpus row.
3. Never weaken `ParseabilityContract.Assert` (the full contract) to make a *valid-bash* case
   pass — fix the emitter. Malformed-input broken-PS belongs in §4 and is held only to the
   `AssertNoCrash` safety floor until the parser learns to reject it cleanly.
