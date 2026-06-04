---
name: common-parameter-flag-collisions
title: Bash short flags eaten by the PowerShell common-parameter binder (find -o/-a, grep -o, wc -c, diff -c, jq -c)
description: Every Invoke-Bash* binary cmdlet is an advanced PSCmdlet, so it inherits PowerShell common parameters. A bare bash short flag that prefixes one of them is consumed by the binder before reaching the cmdlet's Arguments — hard-crash for -e/-i/-o/-p/-w, silent-loss for -c/-d/-v. Found and fixed five cases plus the find expression-operator feature.
tags: [cmdlets, powershell-binder, common-parameters, find, grep, wc, diff, jq, emitter]
date: 2026-06-04
status: FIXED — 5 cmdlet decoys + emitter force-quote for find's infix -o/-a + find boolean-expression evaluator; regression tests at emitter + cmdlet layers
---

# Bash short flags eaten by the PowerShell common-parameter binder

## Symptom

Surfaced via dogfooding (the Bash tool routes through ps-bash — see
[[fix-bash-tool-dogfood-bugs-as-they-happen]]):

```text
$ find . -name a -o -name b
ps-bash: ... Parameter cannot be processed because the parameter name 'o' is ambiguous.
         Possible matches include: -OutVariable -OutBuffer.
```

A whole class of bare short flags either hard-crash or silently produce wrong output.

## Root cause

Every `InvokeBash*Command` is a `PSCmdlet` (advanced cmdlet), so PowerShell gives it the
**common parameters**: `-Verbose -Debug -ErrorAction -ErrorVariable -WarningAction
-WarningVariable -InformationAction -InformationVariable -OutVariable -OutBuffer
-PipelineVariable -ProgressAction -WhatIf -Confirm`. The binder matches a bare `-x` token
against parameter names by **case-insensitive prefix BEFORE** it falls through to
`[Parameter(ValueFromRemainingArguments)] Arguments`. So an undeclared single-letter bash
flag that prefixes a common parameter never reaches the cmdlet's manual scan:

| Flag | Resolves to | Result |
|------|-------------|--------|
| `-e -i -o -p -w` | ≥2 common params (ambiguous) | **hard crash** |
| `-c` | `-Confirm` (unique) | **silent-loss** — flag dropped, no error |
| `-d` | `-Debug` (unique) | silent-loss |
| `-v` | `-Verbose` (unique) | silent-loss |

A flag can ALSO collide with the cmdlet's **own** `-Arguments` parameter: find's `-a` (AND)
prefix-matches `-Arguments` and binds it as named, swallowing the next token.

## The hunt (which commands were broken)

A systematic audit of all `InvokeBash*Command.cs` against the colliding set found:

| Command | Flag | Failure | Meaning |
|---|---|---|---|
| `find` | `-o` | hard-crash (-OutVariable/-OutBuffer) | OR operator |
| `find` | `-a` | wrong (binds -Arguments) | AND operator |
| `grep` | `-o` | hard-crash | only-matching |
| `wc` | `-c` | silent wrong output | byte count |
| `diff` | `-c` | silent | context format |
| `jq` | `-c` | silent | compact output |
| `md5sum`/`sha1sum`/`sha256sum` | `-c` | silent (also unimplemented) | verify checksum file |

The first manual audit missed `find -a` and the three checksum `-c` cases; the **guard test**
(below) found those by reflection. The cmdlets' own audit doc-comments were **not trustworthy** — grep/diff/wc each falsely
claimed `-o`/`-c` "has no collision." `rg` already declared `O` for `-o` correctly; grep
just never mirrored it.

## Why there is no single silver-bullet transform

Verified empirically with synthetic binder tests:

- **Array-splat** (`Cmd @args`) sends every element positional and bypasses ALL binding —
  but it also bypasses *declared* switches, so every cmdlet that reads a bound switch
  (sort/cut/du/tr/uniq/gzip declare and read `-c`) would break.
- **Universal emitter-quoting** of a letter (e.g. `-c`) breaks the cmdlets that declare and
  bind it. The same letter is bound in some cmdlets and Arguments-parsed in others.

So the fix is necessarily **per-cmdlet**, plus an emitter quote for the one position-critical
infix case.

## Fixes

1. **Standalone flags — declare a single-letter decoy `[Parameter]` and read it** (an exact
   name match beats the common-param prefix match). The flag is already implemented in the
   cmdlet's manual scan; the decoy just lets it bind:
   - `grep`: `[Parameter] SwitchParameter O` → `outputMatchOnly = O.IsPresent`.
   - `wc`: `[Parameter] SwitchParameter C` → `bytesOnly = parsed.Flags["-c"] || C.IsPresent`.
   - `diff`: `[Parameter] SwitchParameter C` → `context = C.IsPresent`.
   - `jq`: `[Parameter] SwitchParameter C` → `compact = C.IsPresent`.

2. **find's infix `-o`/`-a` — emitter force-quote** (`PsEmitter.FindForceQuoteFlags = {-o,-a}`).
   A switch decoy would resolve the crash but **lose the operator's position**, which an infix
   boolean operator can't tolerate. Quoting routes the literal token to `Arguments` in place.
   The emitter change is a new optional `forceQuoteFlags` set on `EmitPassthrough`, used only
   by the `find` case.

3. **Checksum check mode** (`md5sum`/`sha1sum`/`sha256sum` `-c`): declare a `C` decoy and emit a
   policy-compliant "recognized but not supported" message (the binary `ChecksumEngine` never
   implemented verify mode — previously `-c` was silently dropped and the checksum file hashed as
   data). `--check` (no collision) is caught the same way. Implementing real verify mode is a
   separate feature task.

4. **find boolean-expression evaluator** (the feature this unblocked): `Invoke-BashFind` now
   parses `-a`/`-and`, `-o`/`-or`, `-not`/`!`, `( )` grouping and `-true`/`-false` into a
   per-item predicate expression (GNU precedence `! > AND > OR`) instead of a flat implicit-AND
   filter. Each leaf predicate is compiled as it is parsed; an empty expression matches
   everything (so `find .` is unchanged). Implicit-AND of sequential predicates is byte-identical
   to the old behavior.

5. **Parser `!` fix** (`BashParser.Simple.cs`): a `!` *after* the command word is now kept as a
   literal argument (`find . ! -name x`, `test ! -f y`) instead of breaking the command and
   re-parsing the tail as a separate negated pipeline. Per bash, `!` is the negation reserved
   word only at pipeline start, which `ParsePipeline` already consumes — so any `!` reaching the
   simple-command word loop (`words.Count > 0`) is an operand. A leading `!` still negates.

## Regression tests

- Emitter: `PsEmitterTests` `Transpile_FindOrOperator_*`, `Transpile_FindAndOperator_*`,
  `Transpile_FindDashOInOtherCommands_NotQuoted`.
- Cmdlet: `InvokeBashFindCommandTests` `Find_OrOperator_*`, `Find_NotOperator_*`,
  `Find_BangOperator_*`, `Find_Grouping_*`, `Find_ExplicitAnd_*`, `Find_ImplicitAnd_*`,
  `Find_TrueFalse_*`; `Grep_OnlyMatching_DashO_*`; `Wc_Pipeline_BytesOnlyFlag_DashC_*`;
  `Diff_ContextFormat_DashC_*`; existing `Jq_Identity_RoundTripsSimpleObject_Compact`.

## Build-gate guard (done)

`PsBash.Cmdlets.Tests/CommonParameterCollisionGuardTests.cs` cross-references `BashFlagSpecs.json`
(each command's documented flags) against every binary cmdlet's declared `[Parameter]`/`[Alias]`
single-letter names plus `PsEmitter.FindForceQuoteFlags`, and fails if a documented short flag in
`{a,c,d,e,i,o,p,v,w}` is left unguarded. This turns the whole class into a compile gate — it is
how `find -a` and the three checksum `-c` cases were found after the manual audit missed them. The
guard's `EmitterForceQuoted` map must stay in sync with `PsEmitter.FindForceQuoteFlags`.

## Remaining real follow-up

Checksum **verify mode** (`md5sum -c FILE`) is now binder-safe but still unimplemented (emits
"recognized but not supported"). Implementing it (parse the checksum file, recompute, print
`path: OK`/`FAILED`) is a separate feature task. See [[powershell-common-param-flag-collision]].
