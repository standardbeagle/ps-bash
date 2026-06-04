---
name: double-quote-embedded-quote-backtick-not-escaped
title: Escaped " and ` inside bash double quotes leaked unescaped into PowerShell (parse error)
description: A bash double-quoted string containing an escaped double-quote (\") or backtick (\`) transpiled to invalid PowerShell because the emitter's double-quote inner-literal escaper only handled $, not " or `. PowerShell then rejected the emitted string with "The string is missing the terminator". Root-caused and fixed in PsEmitter.AppendDoubleQuotedInner.
tags: [emitter, quoting, double-quote, powershell-parse-error, dogfood]
date: 2026-06-04
status: FIXED (PsEmitter.cs AppendDoubleQuotedInner); regression tests at emitter + differential layers
---

# Escaped `"` / `` ` `` inside bash double quotes leaked unescaped into PowerShell

## Symptom

Any bash command with an escaped double-quote (or backtick) **inside** a double-quoted
string failed at the PowerShell layer:

```text
$ echo "a\"b"
ps-bash: parse error: The string is missing the terminator: ".
```

Expected (bash oracle): `a"b`. Same shape for `` echo "a\`b" `` (expected `` a`b ``).

This surfaced via dogfooding — the Claude Code Bash tool routes through ps-bash, and a
routine `grep -n "... => \"" file.cs` command (an escaped `"` inside the double-quoted
grep pattern) tripped it. See [[bash_tool_is_psbash_dogfood]].

## Root cause

The error string `The string is missing the terminator` is **not** emitted by ps-bash's
own C# — it is a PowerShell parser (`ParseException`) error. So the lexer/parser handled
the input fine; the **emitter produced invalid PowerShell**.

Trace:

1. **Parser** — `BashParser.Words.ParseDoubleQuoted` (`src/PsBash.Transpiler/Parser/BashParser.Words.cs:244`)
   correctly consumes the backslash for the bash-special set `$ \` " \ newline` and stores
   the escaped char as a bare `WordPart.Literal`: `\"` → `Literal("\"")`, `` \` `` → ``Literal("`")``,
   `\$` → `Literal("$")`. The backslash is gone by the time the emitter runs.

2. **Emitter** — `PsEmitter.AppendDoubleQuotedInner`
   (`src/PsBash.Transpiler/Parser/PsEmitter.cs`) writes those literals **inside a PowerShell
   double-quoted string** but escaped only `$`:

   ```csharp
   // BEFORE
   sb.Append(lit.Value.Replace("$", "`$"));
   ```

   In a PowerShell double-quoted string, `"` ends the string and backtick is the escape
   character — both must be backtick-escaped, just like `$`. They were not. So
   `Literal("\"")` emitted a bare `"` that terminated the PS string early:

   ```text
   bash:  echo "a\"b"
   emit:  Invoke-BashEcho "a"b"      # PS sees "a" + dangling  b"  -> missing terminator
   ```

The `\$` case had a test (`Differential_Backslash_EscapesDollarInDoubleQuotes`) and worked;
`\"` and `` \` `` were never tested and were broken. This is the same class of
"escape one delimiter, forget the siblings" gap noted in [[transpiler-port-gaps]].

## Fix

Escape all three PowerShell-special characters in the inner literal, backtick **first**
(it is introduced by the other two replacements). Mirrors the already-correct
`SingleQuoted` branch of `FlattenPartsToDoubleQuotedString`.

```csharp
// AFTER
sb.Append(lit.Value.Replace("`", "``").Replace("$", "`$").Replace("\"", "`\""));
```

Emitted output after the fix (all parse cleanly under
`System.Management.Automation.Language.Parser.ParseInput`):

| bash | PowerShell | evaluates to |
|------|-----------|--------------|
| `echo "a\"b"`     | `Invoke-BashEcho "a` + `` `" `` + `b"` | `a"b` |
| `` echo "a\`b" `` | `Invoke-BashEcho "a` + ` `` ` + `b"`   | `` a`b `` |
| `echo "price: \$5"` | `Invoke-BashEcho "price: ` + `` `$ `` + `5"` | `price: $5` |

## Regression tests

- Emitter layer — `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs`:
  `Transpile_EscapedQuoteInsideDoubleQuotes_BacktickEscapesForPowerShell`,
  `Transpile_EscapedBacktickInsideDoubleQuotes_DoublesBacktickForPowerShell`.
- Oracle/differential layer — `src/PsBash.Differential.Tests/QuotingDifferentialTests.cs`:
  `Differential_Backslash_EscapesDoubleQuoteInDoubleQuotes`,
  `Differential_Backslash_EscapesBacktickInDoubleQuotes`.

## Affected scope

Any double-quoted operand containing an escaped `"` or `` ` `` — across **every** mapped
command, not just `echo` (the bug was in shared double-quote emission). Single-part
double-quoted words (`EmitDoubleQuoted`) and multi-part adjacency words both route through
`AppendDoubleQuotedInner`, so both are covered by the one-line fix.

## Files

- Fix: `src/PsBash.Transpiler/Parser/PsEmitter.cs` (`AppendDoubleQuotedInner`)
- Parser reference: `src/PsBash.Transpiler/Parser/BashParser.Words.cs:244`
- Spec: `docs/specs/parser-grammar.md` §6.5 (double-quote backslash rules)
