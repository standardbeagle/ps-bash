---
paths:
  - "src/PsBash.Transpiler/Parser/PsEmitter.cs"
  - "src/PsBash.Transpiler/Parser/PsBuild.cs"
---

# EMITTER. Ref: @docs/specs/emitter-strategy.md

文言：透傳為本——映命名、轉全參，旗由運行時解。勿譯旗、勿抽旗、勿臆旗、勿映原生cmdlet。逗號花括號之旗須引號。
PS文須經PsBuild：引號轉義、退碼測試(必[void])、抑輸出、splat、空安全皆一源；勿手綴。

## PASSTHROUGH PRINCIPLE
Map bash command name → `Invoke-Bash*`, forward ALL args unchanged. Runtime parses flags.

NEVER in the emitter:
- translate bash flag → PS named param (`-d` → `-Delimiter`).
- extract + re-emit specific flags (`-n N` out of head).
- assume which flags a command supports.
- map to native PS cmdlets (`Select-Object`/`Measure-Object`/`Sort-Object`).

## PIPE-TARGET MAPPING
- all pipe targets in `TryEmitMappedCommand` use `EmitPassthrough`.
- add a command: case in `TryEmitMappedCommand` → `EmitPassthrough("Invoke-BashFoo", args)`. No custom `EmitFoo` unless quoting needs it.

## QUOTING (NeedsPassthroughQuoting)
Quote a flag arg containing `,` (PS array sep) or `{`/`}` (PS scriptblock): emit `"-F,"`, `"-I{}"`.

## PS-TEXT VIA PsBuild (one source — `Parser/PsBuild.cs`)
Recurring PowerShell fragments drift when hand-built (the negated-pipeline condition once
omitted the `[void]` its sibling had → wrong branch). Build them through `PsBuild`, never inline:
- string literals → `PsBuild.SingleQuote` / `DoubleQuote` / `EscapeForDoubleQuote` (backtick-FIRST).
- a command as a boolean condition → `PsBuild.ExitCodeTest(cmd, negate)` (ALWAYS `[void]`-wraps).
- `&&`/`||` chain exit-code → `SetExitFromBool` / `SignalFailIfNonZero`; standalone `[ ]` → `SilentExitFromBool`.
- suppress output → `Void` / `VoidStatement`; isolate → `Subshell` / `Subexpr`.
- unquoted-var word-split splat → `WordSplitArray`; pipeline text extract → `NullSafeBashText`.
Exit-code scope is ALWAYS `$global:LASTEXITCODE`. New repeated fragment → add a `PsBuild` primitive + unit test, don't inline it.

## SEAMS (route through these; don't re-derive — each has a regression test)
- numeric `[ ]`/`[[ ]]` compare (`-eq -ne -lt -le -gt -ge`) → `[long](lhs) op [long](rhs)`. Bare = string compare (`'10' -gt 9` is FALSE).
- multi-statement condition (`cd` emits a `;`-list) → `ExitCodeTest`→`VoidStatement` = `[void]$(…)`, NEVER `[void](…)` (grouping parens can't hold a stmt list → "Missing closing ')'").
- command-sub: QUOTED / ASSIGNMENT context → `EmitCommandSubString` (newline-preserved, trailing-stripped); bare UNQUOTED arg → `EmitCommandSub` (array → PS arg-splat = bash word-split). Do NOT swap.
- case pattern → `NormalizeCasePattern` (strip quotes; a glob metachar INSIDE quotes → backtick-escape). Raw slice kept `"foo"` literal → never matched.
- array/`${x:off:len}` slice with a dynamic or out-of-range bound → runtime-clamped scriptblock, ALWAYS wrapped `$(& { … })` — a bare `& {}` is not a valid command arg (`&` parses as the background op).
- passthrough force-quote: single-quote via `TryGetStaticArgValue` ONLY when the emitted word already contains a quote (`-F","`→`'-F,'`); else keep the historical `"…"`. Don't blanket-requote.
